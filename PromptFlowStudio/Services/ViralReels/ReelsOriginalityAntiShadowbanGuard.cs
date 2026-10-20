using System.Numerics;
namespace PromptFlowStudio.Services.ViralReels;

public enum ReelsPublishVerdict : byte { Warming, Ready, SimilarityBlocked, AudioBlocked, CreditMissing }

/// <summary>
/// [PromptFlow Studio v17.47.0] 📱 오리지널리티 &amp; 저작권 오디오 가드(<see cref="Evaluate"/> 60 Hz 무할당).
/// <b>시각</b>: 리믹스 루마와 그 프레임이 유래한 원본 루마의 8×8 블록 SSIM 평균(EWMA)이 0.60 을 넘으면 15 프레임 연속 시
/// 합성기 변환 레벨을 한 단계 올려(세트 치환 → 미러/줌 → 리프레임) 실질적 재창작을 강제한다.
/// <b>오디오</b>: 32 대역 Goertzel 로그 에너지의 시간 · 주파수 2차 차분 부호(Haitsma-Kalker 31-bit)로 프레임 지문을 만들고, 최근 8 프레임 블록을
/// 3초 창 원본 지문의 모든 오프셋과 비교한 최소 비트 오류율(시프트 불변)의 EWMA &lt; 0.25 이면 원본 오디오 재사용으로 판정해 원본 혼입을 0 으로 끊고
/// 신규 더빙 · v17.38 폴리 레이어만 남긴다(지문 변조로 탐지를 우회하지 않음 — 원본 오디오를 제거).
/// 발행 판정: 유사도 · 오디오 · 원작 크레딧 표기 모두 통과 시 Ready.
/// </summary>
public sealed class ReelsOriginalityAntiShadowbanGuard
{
    public const int W = RealtimeViralReelsIngestionEngine.W, H = RealtimeViralReelsIngestionEngine.H, Block = 8, Bands = 32, Window = RealtimeViralReelsIngestionEngine.WindowFrames;
    public const float SsimLimit = 0.60f, MatchBer = 0.25f, C1 = 0.01f * 0.01f, C2 = 0.03f * 0.03f;
    public const int EscalateFrames = 15, WarmFrames = 30, FpBlock = 8, FpBits = Bands - 1;
    private static readonly float[] Coef = BuildCoef();
    private readonly uint[] _srcFp = new uint[Window], _rmxFp = new uint[FpBlock];
    private readonly bool[] _srcValid = new bool[Window];
    private readonly float[] _srcBands = new float[Bands], _rmxBands = new float[Bands];
    private int _over, _frames, _rmxHead, _rmxCount;
    private bool _collision;

    public bool AutoEscalate { get; set; } = true;
    public bool AutoProtectAudio { get; set; } = true;
    public bool CreditAttached { get; set; } = true;
    public bool UseSimd { get; set; } = true;
    public float Ssim { get; private set; }
    public float SsimAvg { get; private set; } = 1f;
    public float MaxSsimAfterWarm { get; private set; }
    public float Ber { get; private set; } = 0.5f;
    public float BerAvg { get; private set; } = 0.5f;
    public bool AudioCollision => _collision;
    public long Escalations { get; private set; }
    public long AudioBlocks { get; private set; }
    public long Frames => _frames;
    public ReelsPublishVerdict Verdict { get; private set; }
    public double LastGuardUs { get; private set; }
    public static string VerdictName(ReelsPublishVerdict v) => v switch
    {
        ReelsPublishVerdict.Ready => "발행 가능",
        ReelsPublishVerdict.SimilarityBlocked => "원본 유사도 초과",
        ReelsPublishVerdict.AudioBlocked => "원본 오디오 감지",
        ReelsPublishVerdict.CreditMissing => "원작 크레딧 누락",
        _ => "검증 중",
    };

    public void Reset()
    {
        Array.Clear(_srcValid); Array.Clear(_srcBands); Array.Clear(_rmxBands); _rmxHead = 0; _rmxCount = 0;
        _over = 0; _frames = 0; _collision = false; SsimAvg = 1f; BerAvg = 0.5f; MaxSsimAfterWarm = 0f; Verdict = ReelsPublishVerdict.Warming;
    }

    private static float[] BuildCoef()
    {
        var c = new float[Bands];
        for (var b = 0; b < Bands; b++)
        {
            var f = 80f * MathF.Pow(4000f / 80f, b / (Bands - 1f));
            c[b] = 2f * MathF.Cos(2f * MathF.PI * f / RealtimeViralReelsIngestionEngine.SampleRate);
        }
        return c;
    }

    /// <summary>31-bit 오디오 지문 — bit b: (E[b] − E[b+1]) − (prevE[b] − prevE[b+1]) &gt; 0(로그 에너지). prev 는 갱신된다.</summary>
    public static uint Fingerprint(ReadOnlySpan<float> audio, Span<float> prev)
    {
        Span<float> e = stackalloc float[Bands];
        for (var b = 0; b < Bands; b++)
        {
            float s1 = 0f, s2 = 0f; var c = Coef[b];
            for (var i = 0; i < audio.Length; i++) { var s0 = audio[i] + c * s1 - s2; s2 = s1; s1 = s0; }
            e[b] = MathF.Log(MathF.Max(s1 * s1 + s2 * s2 - c * s1 * s2, 0f) + 1e-9f);
        }
        uint fp = 0;
        for (var b = 0; b < FpBits; b++) if (e[b] - e[b + 1] - (prev[b] - prev[b + 1]) > 0f) fp |= 1u << b;
        e.CopyTo(prev);
        return fp;
    }

    /// <summary>8×8 블록 SSIM 평균 — SIMD(Vector&lt;float&gt;.Count == 8 행 단위) / 스칼라.</summary>
    public static float BlockSsim(ReadOnlySpan<float> a, ReadOnlySpan<float> b, bool simd = true)
    {
        var vec = simd && Vector.IsHardwareAccelerated && Vector<float>.Count == Block;
        double acc = 0;
        const int bw = W / Block, bh = H / Block;
        const float inv = 1f / (Block * Block);
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                float sx, sy, sxx, syy, sxy;
                if (vec)
                {
                    Vector<float> vx = default, vy = default, vxx = default, vyy = default, vxy = default;
                    for (var r = 0; r < Block; r++)
                    {
                        var o = (by * Block + r) * W + bx * Block;
                        var x = new Vector<float>(a.Slice(o, Block)); var y = new Vector<float>(b.Slice(o, Block));
                        vx += x; vy += y; vxx += x * x; vyy += y * y; vxy += x * y;
                    }
                    sx = Vector.Sum(vx); sy = Vector.Sum(vy); sxx = Vector.Sum(vxx); syy = Vector.Sum(vyy); sxy = Vector.Sum(vxy);
                }
                else
                {
                    sx = sy = sxx = syy = sxy = 0f;
                    for (var r = 0; r < Block; r++)
                    {
                        var o = (by * Block + r) * W + bx * Block;
                        for (var k = 0; k < Block; k++) { var x = a[o + k]; var y = b[o + k]; sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y; }
                    }
                }
                var mx = sx * inv; var my = sy * inv;
                var vx2 = MathF.Max(sxx * inv - mx * mx, 0f); var vy2 = MathF.Max(syy * inv - my * my, 0f); var cxy = sxy * inv - mx * my;
                acc += (2f * mx * my + C1) * (2f * cxy + C2) / ((mx * mx + my * my + C1) * (vx2 + vy2 + C2));
            }
        return (float)(acc / (bw * bh));
    }

    /// <summary>프레임 검증(무할당) — 원본 지문 적재 · 리믹스 SSIM/지문 비교 · 레벨 상향 · 원본 오디오 차단 · 발행 판정.</summary>
    public void Evaluate(int srcFrame, ReadOnlySpan<float> srcAudio, AutonomousReelsStudioRemixSynthesizer synth)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _frames++;
        var fi = srcFrame % Window;
        _srcFp[fi] = Fingerprint(srcAudio, _srcBands);
        _srcValid[fi] = true;
        _rmxFp[_rmxHead] = Fingerprint(synth.Audio, _rmxBands);
        _rmxHead = (_rmxHead + 1) % FpBlock;
        if (_rmxCount < FpBlock) _rmxCount++;
        var best = FpBlock * FpBits;
        if (_rmxCount == FpBlock)
            for (var o = 0; o < Window; o++)
            {
                var d = 0;
                for (var k = 0; k < FpBlock && d < best; k++)
                {
                    var si = (o - k + Window) % Window;
                    if (!_srcValid[si]) { d = int.MaxValue; break; }
                    d += BitOperations.PopCount(_rmxFp[(_rmxHead - 1 - k + 2 * FpBlock) % FpBlock] ^ _srcFp[si]);
                }
                if (d < best) best = d;
            }
        Ber = _rmxCount < FpBlock || RealtimeViralReelsIngestionEngine.MeanSquare(synth.Audio, UseSimd) < 1e-8f ? 0.5f : best / (float)(FpBlock * FpBits); // 무음 = 지문 없음
        BerAvg += (Ber - BerAvg) / 30f;

        Ssim = BlockSsim(synth.Luma, synth.SourceLuma, UseSimd);
        SsimAvg += 0.1f * (Ssim - SsimAvg);
        if (_frames > WarmFrames && SsimAvg > MaxSsimAfterWarm) MaxSsimAfterWarm = SsimAvg;
        if (SsimAvg > SsimLimit) _over++; else _over = 0;
        if (AutoEscalate && _over >= EscalateFrames && synth.TransformLevel < AutonomousReelsStudioRemixSynthesizer.MaxLevel)
        {
            synth.TransformLevel++;
            Escalations++;
            _over = 0;
        }

        var hit = _frames > WarmFrames && BerAvg < MatchBer;
        if (hit && !_collision && AutoProtectAudio)
        {
            synth.SourceAudioMix = 0f;
            synth.FoleyEnabled = true;
            if (synth.TransformLevel < 1) synth.TransformLevel = 1;
            AudioBlocks++;
        }
        _collision = hit;

        Verdict = _frames <= WarmFrames ? ReelsPublishVerdict.Warming
            : SsimAvg > SsimLimit ? ReelsPublishVerdict.SimilarityBlocked
            : _collision ? ReelsPublishVerdict.AudioBlocked
            : !CreditAttached ? ReelsPublishVerdict.CreditMissing
            : ReelsPublishVerdict.Ready;
        LastGuardUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
