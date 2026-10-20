namespace PromptFlowStudio.Services.ViralReels;

public enum ReelDubLang : byte { English, Spanish, Japanese }

/// <summary>
/// [PromptFlow Studio v17.47.0] 📱 스튜디오 파이프라인 결합 AI 숏폼 리믹스 합성기(<see cref="Compose"/> 60 Hz 무할당 · 9:16 72×128).
/// ① v17.40 긴장 아크: 분해 스팬의 세그먼트(12) 훅 점수를 원본 긴장도로 삼아 훅을 0.1초 콜드 오픈으로 당기고(타임라인 회전) 상승 아크 하한을 강제한다.
/// ② v17.32 더빙: EN/ES/JA 언어별 기본 주파수 · 음절 속도의 하모닉 보이스를 긴장도 비례 음량으로 합성(원본 음성 미사용).
/// ③ v17.46 세트 확장 + 피사체 매팅: 피사체 키(루마 &gt; 0.7) 밖을 원근 그리드 바닥 · 그라데이션 하늘의 3D 시네마틱 세트로 치환.
/// ④ v17.39 펀치인 샷 전환(비트 감쇠 줌) · v17.43 아나모픽 수평 플레어 스트릭 · v17.38 모달 폴리 타격음(3 모드 감쇠 사인)을 비트에 결합.
/// 변환 레벨 0 = 단순 리포스트(그레이드만) · 1 = 스튜디오 리믹스 · 2 = + 미러/강한 줌 · 3 = + 리프레임 크롭.
/// </summary>
public sealed class AutonomousReelsStudioRemixSynthesizer
{
    public const int W = RealtimeViralReelsIngestionEngine.W, H = RealtimeViralReelsIngestionEngine.H, Px = W * H, Spf = RealtimeViralReelsIngestionEngine.Spf;
    public const int Segments = 12, Window = RealtimeViralReelsIngestionEngine.WindowFrames, ColdOpenFrame = 6, MaxLevel = 3;
    public const float SubjectKey = 0.7f, Horizon = H * 0.55f;
    private static readonly float[] F0 = { 140f, 165f, 190f }, SylRate = { 4.2f, 5.5f, 7.0f };
    private static readonly float[] ModeHz = { 180f, 410f, 960f }, ModeTau = { 2400f, 1600f, 900f }, ModeAmp = { 1f, 0.6f, 0.35f };
    private readonly float[] _r = new float[Px], _g = new float[Px], _b = new float[Px], _luma = new float[Px], _src = new float[Px], _audio = new float[Spf], _srcAudio = new float[Spf], _voice = new float[Spf], _foley = new float[Spf];
    private readonly float[] _srcArc = new float[Segments], _arc = new float[Segments];
    private readonly bool[] _beat = new bool[Window];
    private int _shift, _beatAge = 1000, _impactAge = int.MaxValue / 2;
    private double _phase;
    private long _sample;
    private float _cx = W * 0.5f, _cy = H * 0.5f;

    public AutonomousReelsStudioRemixSynthesizer()
    {
        for (var s = 0; s < Segments; s++) _arc[s] = 0.35f + 0.65f * s / (Segments - 1);
    }

    public int TransformLevel { get; set; } = 1;
    public ReelDubLang Language { get; set; } = ReelDubLang.English;
    /// <summary>원본 오디오 혼입 비율(0 = 완전 신규 사운드). 가드가 원본 오디오 감지 시 0 으로 강제.</summary>
    public float SourceAudioMix { get; set; }
    public bool FlareEnabled { get; set; } = true;
    public bool FoleyEnabled { get; set; } = true;
    public ReadOnlySpan<float> OutR => _r;
    public ReadOnlySpan<float> OutG => _g;
    public ReadOnlySpan<float> OutB => _b;
    public ReadOnlySpan<float> Luma => _luma;
    public ReadOnlySpan<float> Audio => _audio;
    /// <summary>v17.49 스템 탭 — 더빙 보이스 · 모달 폴리 채널(믹스 전).</summary>
    public ReadOnlySpan<float> VoiceStem => _voice;
    public ReadOnlySpan<float> FoleyStem => _foley;
    /// <summary>현재 리믹스 프레임이 유래한 원본 프레임(루마).</summary>
    public ReadOnlySpan<float> SourceLuma => _src;
    public int SourceIndex { get; private set; }
    public float Tension { get; private set; }
    public float Zoom { get; private set; } = 1f;
    public float FlareAmt { get; private set; }
    public float SetCoverage { get; private set; }
    public float ArcGain { get; private set; } = 1f;
    public float SourceHookMs { get; private set; }
    public float RemixHookMs => TransformLevel >= 1 ? ColdOpenFrame * 1000f / RealtimeViralReelsIngestionEngine.Fps : SourceHookMs;
    public int Impacts { get; private set; }
    public bool ArcReady { get; private set; }
    public double LastComposeUs { get; private set; }
    public float SourceArc(int s) => _srcArc[s];
    public float RemixArc(int s) => _arc[s];
    public static string LangName(ReelDubLang l) => l switch { ReelDubLang.English => "English", ReelDubLang.Spanish => "Español", _ => "日本語" };
    public static string LevelName(int l) => l switch { 0 => "리포스트(그레이드만)", 1 => "스튜디오 리믹스", 2 => "+ 미러 · 강한 줌", _ => "+ 리프레임 크롭" };

    public void Reset()
    {
        ArcReady = false; _shift = 0; _beatAge = 1000; _impactAge = int.MaxValue / 2; Impacts = 0; ArcGain = 1f;
        Array.Clear(_beat);
    }

    /// <summary>3초 창 분해 완료 시 — 원본 긴장 아크 · 비트 맵 · 콜드 오픈 회전량 확정(무할당).</summary>
    public void PrepareArc(RealtimeViralReelsIngestionEngine ing)
    {
        Array.Clear(_srcArc); Array.Clear(_beat);
        Span<int> cnt = stackalloc int[Segments];
        var n = Math.Min(ing.SpanCount, Window);
        for (var i = 0; i < n; i++)
        {
            ref readonly var sp = ref ing.Span(i);
            if (sp.Frame < 0 || sp.Frame >= Window) continue;
            var s = sp.Frame * Segments / Window;
            _srcArc[s] += sp.Hook; cnt[s]++;
            _beat[sp.Frame] = sp.Beat;
        }
        var max = 1e-6f;
        for (var s = 0; s < Segments; s++) { if (cnt[s] > 0) _srcArc[s] /= cnt[s]; max = MathF.Max(max, _srcArc[s]); }
        for (var s = 0; s < Segments; s++) _srcArc[s] /= max;
        var hook = Math.Max(ing.HookFrame, 0);
        SourceHookMs = hook * 1000f / RealtimeViralReelsIngestionEngine.Fps;
        _shift = Math.Max(hook - ColdOpenFrame, 0);
        var shiftSeg = _shift * Segments / Window;
        float ms = 0f, mr = 0f;
        for (var s = 0; s < Segments; s++)
        {
            var v = MathF.Max(_srcArc[(s + shiftSeg) % Segments], 0.35f + 0.65f * s / (Segments - 1));
            _arc[s] = s == 0 ? 1f : v;
            ms += _srcArc[s]; mr += _arc[s];
        }
        ArcGain = ms > 0f ? mr / ms : 1f;
        ArcReady = true;
    }

    /// <summary>리믹스 프레임 합성(무할당) — 원본 프레임은 인제스천 엔진 디코더로 회전 인덱스에서 재생성.</summary>
    public void Compose(RealtimeViralReelsIngestionEngine ing, int frame)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var lv = Math.Clamp(TransformLevel, 0, MaxLevel);
        var idx = lv >= 1 ? (frame + _shift) % Window : frame % Window;
        SourceIndex = idx;
        ing.RenderSource(idx, _src, _srcAudio);
        Tension = _arc[Math.Min(frame % Window * Segments / Window, Segments - 1)];
        if (_beat[idx]) { _beatAge = 0; _impactAge = 0; Impacts++; } else _beatAge++;
        var punch = MathF.Exp(-_beatAge / 6f);
        var zoom = lv == 0 ? 1f : 1f + (lv >= 2 ? 0.12f : 0.04f) + 0.18f * punch * Tension;
        if (lv >= 3) zoom *= 1.35f;
        Zoom = zoom;
        FlareAmt = lv >= 1 && FlareEnabled ? 0.6f * punch * Tension : 0f;
        var mirror = lv >= 2;
        const float cx = (W - 1) * 0.5f, cy = (H - 1) * 0.5f;
        var inv = 1f / zoom;
        float sumX = 0f, sumY = 0f; var subj = 0;
        var fx = _cx; var fy = _cy;
        for (var y = 0; y < H; y++)
        {
            var v = (int)MathF.Round(cy + (y - cy) * inv);
            var row = y * W;
            var dyf = MathF.Abs(y - fy);
            for (var x = 0; x < W; x++)
            {
                var xs = mirror ? W - 1 - x : x;
                var u = (int)MathF.Round(cx + (xs - cx) * inv);
                var l = _src[Math.Clamp(v, 0, H - 1) * W + Math.Clamp(u, 0, W - 1)];
                float r, g, b;
                if (lv == 0) { r = l * 1.02f; g = l; b = l * 0.97f; }
                else if (l > SubjectKey) { r = l; g = l * 0.78f; b = l * 0.66f; sumX += x; sumY += y; subj++; }
                else SetPixel(x, y, frame, out r, out g, out b);
                if (lv >= 1) { r = r * 1.05f + 0.02f; b = b * 0.95f + 0.03f; }
                if (FlareAmt > 0f && dyf < 3f)
                {
                    var k = FlareAmt * MathF.Exp(-MathF.Abs(x - fx) / 25f) * (1f - dyf / 3f);
                    r += 0.3f * k; g += 0.5f * k; b += k;
                }
                _r[row + x] = r; _g[row + x] = g; _b[row + x] = b;
                _luma[row + x] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            }
        }
        if (subj > 0) { _cx = sumX / subj; _cy = sumY / subj; }
        SetCoverage = lv >= 1 ? 1f - (float)subj / Px : 0f;
        MixAudio(lv);
        LastComposeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static void SetPixel(int x, int y, int frame, out float r, out float g, out float b)
    {
        if (y < Horizon)
        {
            var k = y / Horizon;
            r = 0.95f + (0.1f - 0.95f) * (1f - k); g = 0.55f + (0.35f - 0.55f) * (1f - k); b = 0.35f + (0.5f - 0.35f) * (1f - k);
            return;
        }
        var z = 12f / (y - Horizon + 1f);
        var wx = (x - W * 0.5f) * z / 40f + frame * 0.02f; var wz = z + frame * 0.05f;
        var line = wx - MathF.Floor(wx) < 0.08f || wz - MathF.Floor(wz) < 0.08f;
        var fade = 1f / (1f + 0.1f * z);
        if (line) { r = 0.72f * fade; g = 0.48f * fade; b = 0.24f * fade; }
        else { r = 0.08f; g = 0.1f; b = 0.14f; }
    }

    private void MixAudio(int lv)
    {
        var li = (int)Language;
        var f0 = F0[li]; var rate = SylRate[li];
        var amp = 0.12f * (0.6f + 0.4f * Tension);
        var mix = Math.Clamp(SourceAudioMix, 0f, 1f);
        var foley = lv >= 1 && FoleyEnabled;
        for (var i = 0; i < Spf; i++)
        {
            var a = mix * _srcAudio[i];
            float vo = 0f, fo = 0f;
            if (lv >= 1)
            {
                var t = (float)(_sample + i) / RealtimeViralReelsIngestionEngine.SampleRate;
                var env = MathF.Sin(MathF.PI * rate * t); env *= env;
                var ph = (float)_phase;
                vo = amp * env * (MathF.Sin(ph) + 0.5f * MathF.Sin(2f * ph) + 0.25f * MathF.Sin(3f * ph));
                a += vo;
                _phase += 2.0 * Math.PI * f0 / RealtimeViralReelsIngestionEngine.SampleRate;
            }
            if (foley && _impactAge < 8 * Spf)
            {
                var age = _impactAge + i;
                var s = 0f;
                for (var m = 0; m < 3; m++) s += ModeAmp[m] * MathF.Exp(-age / ModeTau[m]) * MathF.Sin(2f * MathF.PI * ModeHz[m] * age / RealtimeViralReelsIngestionEngine.SampleRate);
                fo = 0.35f * Tension * s;
                a += fo;
            }
            _audio[i] = a; _voice[i] = vo; _foley[i] = fo;
        }
        if (_phase > 2.0 * Math.PI * 1e4) _phase %= 2.0 * Math.PI;
        _sample += Spf;
        if (_sample >= 10L * RealtimeViralReelsIngestionEngine.SampleRate) _sample -= 10L * RealtimeViralReelsIngestionEngine.SampleRate; // 10 s × 음절 속도 = 정수 주기
        if (_impactAge < int.MaxValue / 2) _impactAge += Spf;
    }
}
