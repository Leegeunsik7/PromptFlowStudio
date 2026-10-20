using System.Globalization;
using PromptFlowStudio.Services.World;

namespace PromptFlowStudio.Services.Neuro;

public enum DirectorIntent : byte { Balanced, Thrill, Calm }

/// <summary>미디어 변조 파라미터 인덱스(슬루 제한 배열 · 라벨/단위는 <see cref="BioFeedbackGenerativeMediaModifier.Label"/>).</summary>
public enum ModParam : byte { HueShift, Saturation, Contrast, Exposure, Temperature, Vignette, Tempo, Pitch, Tension, LowPass, Particles, Shake, Bloom }

/// <summary>
/// [PromptFlow Studio v16.56.0] 🎛 바이오피드백 연동 실시간 미디어 변조기(Pure BCL · 무할당 핫패스).
///   ▸ 정책: 연출 의도(균형/스릴/평온)의 목표 몰입·공포 대비 편차 → 13개 파라미터 목표치(감도 슬라이더 배율 · 신뢰도 게이팅 · 공포 상한 안전 완화).
///   ▸ 끊김 없음: 파라미터별 초당 최대 변화량 슬루 제한 → 프레임 간 점프 0.
///   ▸ 영상: 3×256 LUT(대비 · 노출 · 색온도 · 블룸) + 고정소수 색상회전/채도 행렬 + 비네트 맵 + 카메라 셰이크 + 잔불/먼지 입자 가산 합성(RGB24).
///   ▸ 음향: BGM 바리스피드(템포 → 피치 연동) · 긴장 효과음(심박 럽덥 + 노이즈 라이저) · 1극 로우패스.
///   ▸ 분기: v16.54.0 인터랙티브 분기 결정 창에서 감정 → 선택지 친화도(강렬/평온/경이 키워드 태깅) 최고점 자동 선택(세그먼트당 1회 · 체류 틱).
/// </summary>
public sealed class BioFeedbackGenerativeMediaModifier
{
    public const int ParamCount = 13, MaxParticles = 600, MaxChoices = 256, DwellTicks = 8;
    public const float ComfortCap = 0.85f, BaseTempo = 100f;
    private static readonly float[] Neutral = { 0f, 1f, 1f, 0f, 0f, 0.15f, BaseTempo, 0f, 0f, 18000f, 0.3f, 0f, 0.2f };
    private static readonly float[] Rate = { 20f, 0.6f, 0.5f, 0.8f, 0.8f, 0.6f, 30f, 3f, 1.2f, 12000f, 0.8f, 1.5f, 0.6f };

    private readonly object _gate = new();
    private readonly float[] _cur = (float[])Neutral.Clone();
    private readonly float[] _target = (float[])Neutral.Clone();
    private readonly byte[] _lutR = new byte[256], _lutG = new byte[256], _lutB = new byte[256];
    private readonly float[] _lutKey = new float[6];
    private readonly float[] _aff = new float[MaxChoices * 3];
    private readonly int[] _mat = new int[9];
    private ushort[] _vig = Array.Empty<ushort>();
    private byte[] _shakeBuf = Array.Empty<byte>();
    private int _vigW, _vigH, _lastSeg = -1, _dwell, _lastChoice = -1;
    private float _vigKey = -1f, _sensitivity = 0.7f;
    private bool _decided;
    private BranchProgram? _tagProgram;
    private InteractiveVideoBranchingTranspiler? _branch;
    private double _lpState, _beatPhase, _riserPhase;
    private uint _noise = 0x1234567u;
    private long _ticks, _graded, _decisions, _frame;

    public BioFeedbackGenerativeMediaModifier() { }
    public BioFeedbackGenerativeMediaModifier(IServiceProvider sp) => _branch = sp.GetService(typeof(InteractiveVideoBranchingTranspiler)) as InteractiveVideoBranchingTranspiler;

    public event Action? OnChange;

    /// <summary>감정 자율 변조 감도(0 = 변조 없음 · 1 = 최대).</summary>
    public float Sensitivity
    {
        get => Volatile.Read(ref _sensitivity);
        set { Volatile.Write(ref _sensitivity, Math.Clamp(value, 0f, 1f)); LastAction = "🎚 변조 감도 " + Sensitivity.ToString("0.00", CultureInfo.InvariantCulture); Raise(); }
    }

    public DirectorIntent Intent { get; set; } = DirectorIntent.Balanced;
    public bool AutoBranch { get; set; } = true;
    public long Ticks => Interlocked.Read(ref _ticks);
    public long FramesGraded => Interlocked.Read(ref _graded);
    public long BranchDecisions => Interlocked.Read(ref _decisions);
    public EmotionState LastInput { get; private set; } = EmotionState.Neutral;
    public string? LastAction { get; private set; }
    public InteractiveVideoBranchingTranspiler? Branch { get => _branch; set => _branch = value; }

    public float this[ModParam p] => Volatile.Read(ref _cur[(int)p]);
    public float TargetOf(ModParam p) => Volatile.Read(ref _target[(int)p]);
    public static float NeutralOf(ModParam p) => Neutral[(int)p];
    public static float MaxRate(ModParam p) => Rate[(int)p];

    public static string Label(ModParam p) => p switch
    {
        ModParam.HueShift => "색상 회전(°)", ModParam.Saturation => "채도", ModParam.Contrast => "대비", ModParam.Exposure => "노출(EV)",
        ModParam.Temperature => "색온도", ModParam.Vignette => "비네트", ModParam.Tempo => "BGM 템포(BPM)", ModParam.Pitch => "BGM 피치(st)",
        ModParam.Tension => "긴장 효과음", ModParam.LowPass => "로우패스(Hz)", ModParam.Particles => "VFX 입자 밀도", ModParam.Shake => "셰이크", _ => "블룸",
    };

    public string? LastChoiceLabel
    {
        get
        {
            var p = _tagProgram; var c = _lastChoice;
            return p is not null && (uint)c < (uint)p.Choices.Length ? p.Choices[c].Label : null;
        }
    }

    private static float TargetEngagement(DirectorIntent i) => i switch { DirectorIntent.Thrill => 0.8f, DirectorIntent.Calm => 0.5f, _ => 0.7f };
    private static float TargetFear(DirectorIntent i) => i switch { DirectorIntent.Thrill => 0.65f, DirectorIntent.Calm => 0.15f, _ => 0.4f };

    /// <summary>1틱(무할당) — 감정 → 목표치 → 슬루 제한 → 자동 분기.</summary>
    public void Tick(in EmotionState e, double dtSec)
    {
        var dt = (float)Math.Clamp(dtSec, 0.0, 0.5);
        lock (_gate)
        {
            LastInput = e;
            var s = Sensitivity;
            var intent = Intent;
            var te = TargetEngagement(intent); var tf = TargetFear(intent);
            var boost = Math.Clamp((te - e.Engagement) * 2f, -1f, 1f);
            var over = Math.Clamp((e.Fear - ComfortCap) / (1f - ComfortCap), 0f, 1f);
            var dF = e.Fear - tf;
            float a = e.Arousal - 0.5f, v = e.Valence - 0.5f;
            var t = _target;
            var tempo = Math.Clamp(BaseTempo + s * (40f * a + 25f * boost) - 30f * over, 60f, 160f);
            t[(int)ModParam.Tempo] = tempo;
            t[(int)ModParam.Pitch] = 12f * MathF.Log2(tempo / BaseTempo);
            t[(int)ModParam.Contrast] = Math.Clamp(1f + s * (0.25f * a + 0.2f * boost) - 0.15f * over, 0.6f, 1.6f);
            t[(int)ModParam.Saturation] = Math.Clamp(1f + s * (0.5f * v + 0.2f * boost), 0.3f, 1.8f);
            t[(int)ModParam.Temperature] = Math.Clamp(s * (0.6f * v - 0.3f * MathF.Max(0f, dF)) + 0.4f * over, -1f, 1f);
            t[(int)ModParam.HueShift] = Math.Clamp(-12f * s * MathF.Max(0f, dF), -30f, 30f);
            t[(int)ModParam.Exposure] = Math.Clamp(s * (-0.4f * MathF.Max(0f, e.Fear - 0.5f) + 0.2f * v) + 0.3f * over, -1f, 1f);
            t[(int)ModParam.Vignette] = Math.Clamp(0.15f + s * (0.5f * MathF.Max(0f, e.Fear - 0.3f) + 0.2f * boost) - 0.3f * over, 0f, 0.8f);
            t[(int)ModParam.Tension] = Math.Clamp(s * (1.2f * MathF.Max(0f, -dF) + 0.4f * boost) - over, 0f, 1f);
            t[(int)ModParam.LowPass] = Math.Clamp(18000f - s * 16000f * MathF.Max(0f, e.Fear - 0.5f), 2000f, 20000f);
            t[(int)ModParam.Particles] = Math.Clamp(0.3f + s * (0.5f * a + 0.4f * boost) - 0.4f * over, 0f, 1f);
            t[(int)ModParam.Shake] = Math.Clamp(s * (0.6f * MathF.Max(0f, a) + 0.3f * MathF.Max(0f, e.Fear - 0.5f)) - over, 0f, 1f);
            t[(int)ModParam.Bloom] = Math.Clamp(0.2f + s * 0.5f * MathF.Max(0f, v), 0f, 1f);
            var gate = Math.Clamp(e.Confidence / 0.3f, 0f, 1f);
            for (var i = 0; i < ParamCount; i++)
            {
                var goal = Neutral[i] + (t[i] - Neutral[i]) * gate;
                t[i] = goal;
                var step = Rate[i] * dt;
                _cur[i] += Math.Clamp(goal - _cur[i], -step, step);
            }
            if (AutoBranch) DecideBranch(e, te, tf);
        }
        if ((Interlocked.Increment(ref _ticks) & 15) == 0) Raise();
    }

    /// <summary>선택지 라벨 → 친화도(강렬 · 평온 · 경이) 태깅. 프로그램 변경 시 1회.</summary>
    private void TagChoices(BranchProgram p)
    {
        _tagProgram = p;
        var n = Math.Min(p.Choices.Length, MaxChoices);
        for (var c = 0; c < n; c++)
        {
            var l = p.Choices[c].Label;
            float intense = 0, calm = 0, wonder = 0;
            if (Has(l, "💥") || Has(l, "충격") || Has(l, "공포") || Has(l, "추격") || Has(l, "폭발") || Has(l, "밀어") || Has(l, "전투") || Has(l, "chase") || Has(l, "fight") || Has(l, "scare") || Has(l, "push")) intense = 1f;
            if (Has(l, "🌙") || Has(l, "달") || Has(l, "평온") || Has(l, "잔잔") || Has(l, "휴식") || Has(l, "calm") || Has(l, "moon") || Has(l, "slow")) calm = 1f;
            if (Has(l, "🎥") || Has(l, "오비트") || Has(l, "경이") || Has(l, "풍경") || Has(l, "orbit") || Has(l, "reveal") || Has(l, "wonder")) wonder = 1f;
            if (intense + calm + wonder == 0) wonder = 0.5f;
            _aff[c * 3] = intense; _aff[c * 3 + 1] = calm; _aff[c * 3 + 2] = wonder;
        }
    }

    private static bool Has(string s, string k) => s.Contains(k, StringComparison.OrdinalIgnoreCase);

    private void DecideBranch(in EmotionState e, float te, float tf)
    {
        var b = _branch;
        var p = b?.Program;
        if (b is null || p is null || !b.Playing) return;
        if (!ReferenceEquals(p, _tagProgram)) TagChoices(p);
        var seg = b.CurrentSegment;
        if (seg != _lastSeg) { _lastSeg = seg; _decided = false; _dwell = 0; }
        if (_decided || e.Confidence < 0.35f || ++_dwell < DwellTicks) return;
        var sg = p.Segments[seg];
        var over = Math.Clamp((e.Fear - ComfortCap) / (1f - ComfortCap), 0f, 1f);
        var intense = MathF.Max(0f, te - e.Engagement) * 2f + MathF.Max(0f, tf - e.Fear) - 2f * over;
        var calm = 2f * over + MathF.Max(0f, e.Fear - tf) + MathF.Max(0f, e.Arousal - 0.75f);
        var wonder = e.Valence + 0.3f * e.Engagement - 0.5f;
        int best = -1; var bestScore = float.MinValue;
        for (var c = 0; c < sg.ChoiceCount; c++)
        {
            var gi = sg.ChoiceStart + c;
            if (gi >= MaxChoices || !b.IsWindowOpen(c)) continue;
            var score = intense * _aff[gi * 3] + calm * _aff[gi * 3 + 1] + wonder * _aff[gi * 3 + 2];
            if (score > bestScore) { bestScore = score; best = c; }
        }
        if (best < 0) return;
        b.Choose(best);
        _decided = true;
        _lastChoice = sg.ChoiceStart + best;
        Interlocked.Increment(ref _decisions);
    }

    private void RebuildLut()
    {
        float con = _cur[(int)ModParam.Contrast], ev = _cur[(int)ModParam.Exposure], tmp = _cur[(int)ModParam.Temperature], bloom = _cur[(int)ModParam.Bloom];
        float hue = _cur[(int)ModParam.HueShift], sat = _cur[(int)ModParam.Saturation];
        if (_lutKey[0] == con && _lutKey[1] == ev && _lutKey[2] == tmp && _lutKey[3] == bloom && _lutKey[4] == hue && _lutKey[5] == sat) return;
        _lutKey[0] = con; _lutKey[1] = ev; _lutKey[2] = tmp; _lutKey[3] = bloom; _lutKey[4] = hue; _lutKey[5] = sat;
        var gain = MathF.Pow(2f, ev);
        float gr = 1f + 0.15f * tmp, gb = 1f - 0.15f * tmp;
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255f;
            var y = ((x - 0.5f) * con + 0.5f) * gain;
            y += bloom * MathF.Max(0f, x - 0.7f) * 0.5f;
            _lutR[i] = (byte)Math.Clamp((int)(y * gr * 255f + 0.5f), 0, 255);
            _lutG[i] = (byte)Math.Clamp((int)(y * 255f + 0.5f), 0, 255);
            _lutB[i] = (byte)Math.Clamp((int)(y * gb * 255f + 0.5f), 0, 255);
        }
        // 휘도 보존 색상 회전(W3C feHueRotate) + 채도: M = L + S·(H − L) — 고정소수 ×1024
        var th = hue * MathF.PI / 180f;
        float c = MathF.Cos(th), sn = MathF.Sin(th);
        Span<float> hm = stackalloc float[9];
        hm[0] = 0.213f + c * 0.787f - sn * 0.213f; hm[1] = 0.715f - c * 0.715f - sn * 0.715f; hm[2] = 0.072f - c * 0.072f + sn * 0.928f;
        hm[3] = 0.213f - c * 0.213f + sn * 0.143f; hm[4] = 0.715f + c * 0.285f + sn * 0.140f; hm[5] = 0.072f - c * 0.072f - sn * 0.283f;
        hm[6] = 0.213f - c * 0.213f - sn * 0.787f; hm[7] = 0.715f - c * 0.715f + sn * 0.715f; hm[8] = 0.072f + c * 0.928f + sn * 0.072f;
        for (var k = 0; k < 9; k++)
        {
            var l = (k % 3) switch { 0 => 0.213f, 1 => 0.715f, _ => 0.072f };
            _mat[k] = (int)MathF.Round((l + sat * (hm[k] - l)) * 1024f);
        }
    }

    private void EnsureFrameBuffers(int w, int h, float vig)
    {
        if (_vigW != w || _vigH != h)
        {
            _vig = new ushort[w * h];        // 해상도 변경 시 1회 재할당
            _shakeBuf = new byte[w * h * 3];
            _vigW = w; _vigH = h; _vigKey = -1f;
        }
        if (_vigKey == vig) return;
        _vigKey = vig;
        float cx = (w - 1) * 0.5f, cy = (h - 1) * 0.5f, inv = 1f / (cx * cx + cy * cy);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                float dx = x - cx, dy = y - cy;
                _vig[y * w + x] = (ushort)(Math.Clamp(1f - vig * (dx * dx + dy * dy) * inv * 1.6f, 0f, 1f) * 256f);
            }
    }

    /// <summary>RGB24 프레임 컬러 그레이딩 + 셰이크 + 입자(무할당 · 해상도 변경 시만 버퍼 재할당).</summary>
    public void GradeFrame(Span<byte> rgb, int w, int h)
    {
        if (w <= 0 || h <= 0 || rgb.Length < w * h * 3) return;
        lock (_gate)
        {
            RebuildLut();
            EnsureFrameBuffers(w, h, _cur[(int)ModParam.Vignette]);
            var frame = Interlocked.Increment(ref _frame);
            var shake = _cur[(int)ModParam.Shake];
            if (shake > 0.02f)
            {
                int sx = (int)MathF.Round(shake * 4f * MathF.Sin(frame * 1.7f)), sy = (int)MathF.Round(shake * 3f * MathF.Cos(frame * 2.3f));
                var n = w * h * 3;
                rgb[..n].CopyTo(_shakeBuf);
                if (PromptFlowStudio.Services.Synergy.EngineHyperAccelerationOptimizer.Enabled) PromptFlowStudio.Services.Synergy.EngineHyperAccelerationOptimizer.ShiftClampRgb(_shakeBuf, rgb, w, h, sx, sy); // v16.62.0 행 블록 복사(비트 동일)
                else for (var y = 0; y < h; y++)
                {
                    var syy = Math.Clamp(y - sy, 0, h - 1);
                    for (var x = 0; x < w; x++)
                    {
                        var sxx = Math.Clamp(x - sx, 0, w - 1);
                        int d = (y * w + x) * 3, s = (syy * w + sxx) * 3;
                        rgb[d] = _shakeBuf[s]; rgb[d + 1] = _shakeBuf[s + 1]; rgb[d + 2] = _shakeBuf[s + 2];
                    }
                }
            }
            var mt = _mat;
            for (var i = 0; i < w * h; i++)
            {
                var o = i * 3;
                int r = _lutR[rgb[o]], g = _lutG[rgb[o + 1]], b = _lutB[rgb[o + 2]];
                int nr = (mt[0] * r + mt[1] * g + mt[2] * b) >> 10, ng = (mt[3] * r + mt[4] * g + mt[5] * b) >> 10, nb = (mt[6] * r + mt[7] * g + mt[8] * b) >> 10;
                int vg = _vig[i];
                rgb[o] = (byte)Math.Clamp((nr * vg) >> 8, 0, 255);
                rgb[o + 1] = (byte)Math.Clamp((ng * vg) >> 8, 0, 255);
                rgb[o + 2] = (byte)Math.Clamp((nb * vg) >> 8, 0, 255);
            }
            var count = (int)(_cur[(int)ModParam.Particles] * MaxParticles);
            var warm = _cur[(int)ModParam.Temperature] >= 0f;
            for (var p = 0; p < count; p++)
            {
                var hsh = Hash((uint)p);
                var x = (int)(hsh % (uint)w);
                var speed = 1 + (int)((hsh >> 12) & 3);
                var y = h - 1 - (int)(((hsh >> 16) + (ulong)frame * (ulong)speed) % (ulong)h);
                var o = (y * w + x) * 3;
                var a = 60 + (int)((hsh >> 24) & 127);
                rgb[o] = (byte)Math.Min(255, rgb[o] + (warm ? a : a / 3));
                rgb[o + 1] = (byte)Math.Min(255, rgb[o + 1] + a / 2);
                rgb[o + 2] = (byte)Math.Min(255, rgb[o + 2] + (warm ? a / 4 : a));
            }
        }
        Interlocked.Increment(ref _graded);
    }

    private static uint Hash(uint x) { x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16; return x; }

    /// <summary>BGM 바리스피드 재생(템포 비율 = 피치 연동 · 선형 보간 · 루프 · 무할당). position 은 소스 샘플 위치(호출 간 유지).</summary>
    public int RenderBgm(ReadOnlySpan<float> source, ref double position, Span<float> dst)
    {
        if (source.Length < 2) { dst.Clear(); return dst.Length; }
        var rate = this[ModParam.Tempo] / BaseTempo;
        for (var i = 0; i < dst.Length; i++)
        {
            var i0 = (int)position;
            var f = (float)(position - i0);
            dst[i] = source[i0] * (1f - f) + source[(i0 + 1) % source.Length] * f;
            position += rate;
            if (position >= source.Length) position -= source.Length;
        }
        return dst.Length;
    }

    /// <summary>긴장 효과음 합성 + 로우패스(모노 버퍼에 가산 · 무할당).</summary>
    public void MixTension(Span<float> mono, int sampleRate)
    {
        float tension = this[ModParam.Tension], lp = this[ModParam.LowPass];
        var bpm = 60f + 70f * tension;
        var beatInc = bpm / 60.0 / sampleRate;
        var a = 1.0 - Math.Exp(-2.0 * Math.PI * lp / sampleRate);
        for (var i = 0; i < mono.Length; i++)
        {
            _beatPhase += beatInc;
            if (_beatPhase >= 1.0) _beatPhase -= 1.0;
            var ph = _beatPhase;
            var env = ph < 0.08 ? Math.Sin(ph / 0.08 * Math.PI) : ph is > 0.22 and < 0.30 ? 0.7 * Math.Sin((ph - 0.22) / 0.08 * Math.PI) : 0.0;
            _riserPhase += 2.0 * Math.PI * 55.0 / sampleRate;
            if (_riserPhase > 2.0 * Math.PI) _riserPhase -= 2.0 * Math.PI;
            var x = _noise; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _noise = x;
            var noise = ((x & 0xFFFF) / 32768.0 - 1.0) * 0.15 * tension;
            var s = mono[i] + tension * (0.5 * env * Math.Sin(_riserPhase) + noise);
            _lpState += (s - _lpState) * a;
            mono[i] = (float)_lpState;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Neutral.AsSpan().CopyTo(_cur); Neutral.AsSpan().CopyTo(_target);
            _lastSeg = -1; _decided = false; _lastChoice = -1;
        }
        LastAction = "♻ 변조 초기화";
        Raise();
    }

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Neuro.Modifier.OnChange", ex); }
    }
}
