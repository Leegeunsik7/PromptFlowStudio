using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.StreamGateway;

namespace PromptFlowStudio.Services.GlobalDubbing;

/// <summary>v17.64.0 송출 오디오 소비자(fMP4 먹서 · 라이브 인코더) — 플랫폼별 언어 트랙 PCM 스팬 전달(무할당).</summary>
public interface IDubAudioSink
{
    void Write(ChatPlatform platform, DubLanguage language, ReadOnlySpan<float> pcm, long samplePos);
}

/// <summary>
/// [PromptFlow Studio v17.64.0] 다국어 동시 음성 합성 &amp; 멀티 스트림 라우터(48 kHz · 800 샘플/틱 · 무할당 핫패스).
///   ▸ 레인 0 = 한국어 원본 음성(v17.32 엔진) → 화자 d-vector(<see cref="RealtimeNeuralVoiceCloningDubbingEngine.PushSource"/>) → 레인 1~3 대상 언어가 같은 F0 · 성도 · 기식으로 수렴(제로샷 음색 근사).
///   ▸ v17.60 아바타 감정(각성 → 이득 · 정서가 → 고역 틸트)을 언어 독립 운율로 모든 레인에 동일 적용.
///   ▸ 플랫폼(Twitch · YouTube · 치지직)별 언어 트랙 → 2 s 라이브 송출 링 + <see cref="IDubAudioSink"/>(v17.62 게이트웨이 연결 상태 = On-Air).
/// </summary>
public sealed class MultilingualVoiceSynthAndStreamRouter : ITranslationChunkSink
{
    public const int Fs = RealtimeNeuralVoiceCloningDubbingEngine.Fs, TickSamples = Fs / 60, Lanes = RealtimeNeuralVoiceCloningDubbingEngine.MaxLanes, Platforms = 3, LiveSamples = Fs * 2;

    private readonly StreamGatewayClipperStudioRunner? _gateway;
    private readonly DubLanguage[] _platLang = { DubLanguage.En, DubLanguage.Es, DubLanguage.Ko };
    private readonly DubLanguage[] _laneLang = { DubLanguage.Ko, DubLanguage.En, DubLanguage.Es, DubLanguage.Ja };
    private readonly bool[] _laneUsed = new bool[Lanes];
    private readonly float[][] _pcm = new float[Lanes][];
    private readonly float[][] _live = new float[Platforms][];
    private readonly long[] _livePos = new long[Platforms], _routed = new long[Platforms];
    private readonly float[] _peak = new float[Lanes], _rms = new float[Lanes], _tilt = new float[Lanes];
    private readonly long[] _chunksIn = new long[Lanes];

    public MultilingualVoiceSynthAndStreamRouter(StreamGatewayClipperStudioRunner? gateway = null)
    {
        _gateway = gateway;
        for (var i = 0; i < Lanes; i++) _pcm[i] = new float[TickSamples];
        for (var p = 0; p < Platforms; p++) _live[p] = new float[LiveSamples];
        Remap();
    }

    public RealtimeNeuralVoiceCloningDubbingEngine Voice { get; } = new();
    public IDubAudioSink? Sink { get; set; }
    public bool GatewayAvailable => _gateway is not null;
    /// <summary>v17.60 각성(0~1) · 정서가(−1~1).</summary>
    public float Arousal { get; set; } = 0.4f;
    public float Valence { get; set; }
    public float MasterGain { get; set; } = 1f;
    public long Ticks { get; private set; }
    public long Remaps { get; private set; }
    public long SourceLines { get; private set; }
    public double LastRenderUs { get; private set; }
    public double MaxRenderUs { get; private set; }

    public DubLanguage PlatformLanguage(ChatPlatform p) => _platLang[(int)p];
    public DubLanguage LaneLanguage(int lane) => _laneLang[lane];
    public bool LaneActive(int lane) => _laneUsed[lane];
    public int LaneOf(DubLanguage l) { for (var i = 0; i < Lanes; i++) if (_laneUsed[i] && _laneLang[i] == l) return i; return -1; }
    public int LaneOfPlatform(ChatPlatform p) => LaneOf(_platLang[(int)p]);
    public float PeakDb(int lane) => Db(_peak[lane]);
    public float RmsDb(int lane) => Db(_rms[lane]);
    public float Level(int lane) => Math.Clamp((Db(_rms[lane]) + 60f) / 60f, 0f, 1f);
    public long ChunksIn(int lane) => _chunksIn[lane];
    public long Routed(ChatPlatform p) => _routed[(int)p];
    public bool OnAir(ChatPlatform p) => _gateway is not null && _gateway.Gateway.Session(p).State == GatewayLinkState.Connected;
    /// <summary>레인 출력 ↔ 원본 화자 음색 코사인 유사도.</summary>
    public float Similarity(int lane) => _laneUsed[lane] ? Voice.Similarity(lane) : 0f;

    /// <summary>번역기 대상 비트마스크(한국어 제외 · 플랫폼 지정 언어).</summary>
    public int TranslationMask
    {
        get
        {
            var m = 0;
            foreach (var l in _platLang) { var i = RealtimeMultilingualTranslationPhonemeEngine.IndexOf(l); if (i >= 0) m |= 1 << i; }
            return m;
        }
    }

    private static float Db(float a) => a > 1e-6f ? 20f * MathF.Log10(a) : -120f;

    // ══════════════════════ 라우팅 설정(비핫패스) ══════════════════════
    /// <summary>플랫폼 출력 언어 지정 → 레인 재배치(변경 레인만 초기화). 지원 외 언어는 거부.</summary>
    public bool SetPlatformLanguage(ChatPlatform p, DubLanguage l)
    {
        if (l != DubLanguage.Ko && RealtimeMultilingualTranslationPhonemeEngine.IndexOf(l) < 0) return false;
        _platLang[(int)p] = l;
        Remap();
        return true;
    }

    private void Remap()
    {
        Span<bool> keep = stackalloc bool[Lanes];
        _laneUsed[0] = true; _laneLang[0] = DubLanguage.Ko; keep[0] = true;
        for (var i = 1; i < Lanes; i++)
        {
            var needed = false;
            foreach (var l in _platLang) if (l == _laneLang[i] && l != DubLanguage.Ko) needed = true;
            if (_laneUsed[i] && needed) keep[i] = true;
        }
        foreach (var l in _platLang)
        {
            if (l == DubLanguage.Ko) continue;
            var have = false;
            for (var i = 1; i < Lanes; i++) if (keep[i] && _laneLang[i] == l) have = true;
            if (have) continue;
            for (var i = 1; i < Lanes; i++)
            {
                if (keep[i]) continue;
                _laneLang[i] = l; keep[i] = true;
                Voice.ResetLane(i); _peak[i] = _rms[i] = _tilt[i] = 0f;
                break;
            }
        }
        for (var i = 1; i < Lanes; i++) _laneUsed[i] = keep[i];
        Remaps++;
    }

    public void Reset()
    {
        for (var i = 0; i < Lanes; i++) { Voice.ResetLane(i); _peak[i] = _rms[i] = _tilt[i] = 0f; _chunksIn[i] = 0; }
        Voice.ResetCloning();
        for (var p = 0; p < Platforms; p++) { Array.Clear(_live[p]); _livePos[p] = 0; _routed[p] = 0; }
        Ticks = SourceLines = 0; MaxRenderUs = 0;
    }

    // ══════════════════════ 번역 소비(ITranslationChunkSink) ══════════════════════
    public void OnSourceLine(ReadOnlySpan<char> korean)
    {
        Voice.Enqueue(0, DubLanguage.Ko, korean);
        _chunksIn[0]++; SourceLines++;
    }

    public void OnChunk(in TranslationChunk chunk, ReadOnlySpan<char> text)
    {
        var lane = LaneOf(chunk.Lang);
        if (lane <= 0) return;
        Voice.Enqueue(lane, chunk.Lang, text);
        _chunksIn[lane]++;
    }

    // ══════════════════════ 60 Hz 렌더 · 라우팅(무할당 핫패스) ══════════════════════
    public void Render()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var gain = MasterGain * (0.8f + 0.4f * Math.Clamp(Arousal, 0f, 1f));
        var tilt = 0.35f * Math.Clamp(Valence, -1f, 1f);
        for (var lane = 0; lane < Lanes; lane++)
        {
            if (!_laneUsed[lane]) continue;
            var buf = _pcm[lane].AsSpan();
            Voice.Render(lane, buf);
            if (lane == 0) Voice.PushSource(buf);
            float prev = _tilt[lane], pk = 0f, e = 0f;
            for (var i = 0; i < buf.Length; i++)
            {
                var x = buf[i];
                var y = gain * (x + tilt * (x - prev));
                prev = x;
                y = y > 1f ? 1f : y < -1f ? -1f : y;
                buf[i] = y;
                var a = MathF.Abs(y); if (a > pk) pk = a;
                e += y * y;
            }
            _tilt[lane] = prev;
            _peak[lane] = MathF.Max(pk, _peak[lane] * 0.92f);
            _rms[lane] = MathF.Sqrt(e / buf.Length);
        }
        for (var p = 0; p < Platforms; p++)
        {
            var lane = LaneOf(_platLang[p]);
            if (lane < 0) continue;
            var src = _pcm[lane].AsSpan();
            var live = _live[p];
            var o = (int)(_livePos[p] % LiveSamples);
            var first = Math.Min(src.Length, LiveSamples - o);
            src[..first].CopyTo(live.AsSpan(o));
            if (first < src.Length) src[first..].CopyTo(live);
            Sink?.Write((ChatPlatform)p, _platLang[p], src, _livePos[p]);
            _livePos[p] += src.Length; _routed[p] += src.Length;
        }
        Ticks++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastRenderUs = us; if (us > MaxRenderUs && Ticks > 5) MaxRenderUs = us;
    }

    /// <summary>플랫폼 라이브 링의 최근 <paramref name="dst"/> 길이 샘플 복사(송출 인코더 · UI 파형).</summary>
    public int ReadLive(ChatPlatform p, Span<float> dst)
    {
        var live = _live[(int)p]; var pos = _livePos[(int)p];
        var n = (int)Math.Min(Math.Min(dst.Length, LiveSamples), pos);
        for (var i = 0; i < n; i++) dst[i] = live[(int)((pos - n + i) % LiveSamples)];
        return n;
    }
}
