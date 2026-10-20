namespace PromptFlowStudio.Services.BeatSync;

/// <summary>
/// [PromptFlow Studio v17.53.0] 🎙 지능형 음성 인식 사이드체인 컴프레서 &amp; 더킹 합성기(<see cref="Process"/> 무할당 · 블록 임의 길이 · 60 fps 블록 800).
/// <b>검출</b>: 보컬 스템 10 ms(480 샘플) 슬라이딩 RMS &gt; −32 dBFS → 발화. 음절 사이 끊김에 펌핑하지 않도록 120 ms 홀드.
/// <b>게인</b>: dB 영역 1 차 지수 곡선 — 어택 τ = 15 ms / ln 14(검출 후 15 ms 에 목표의 1 dB 이내 도달) · 릴리스 τ = 250 ms / ln 14(250 ms 에 0 dB 의 1 dB 이내 복귀).
/// 깊이 −6 ~ −24 dB(기본 −14). BGM 에만 적용 — 보컬 · v17.38 물리 폴리는 감쇄 없이 그대로 합산한다(폴리 마스킹 없음).
/// 출력 = 보컬(센터) + 폴리(스테레오) + BGM × 게인(스테레오).
/// </summary>
public sealed class VoiceActivatedSidechainDuckingSynthesizer
{
    public const int Fs = 48000, Win = 480;
    private readonly float[] _sq = new float[Win];
    private int _sqHead;
    private double _sum;
    private float _gDb, _holdLeft;
    private float _aCoef, _rCoef;

    public VoiceActivatedSidechainDuckingSynthesizer() => SetTimes(15f, 250f);

    public float ThresholdDbfs { get; set; } = -32f;
    public float DepthDb { get; set; } = -14f;
    public float HoldMs { get; set; } = 120f;
    public float AttackMs { get; private set; }
    public float ReleaseMs { get; private set; }
    public bool Enabled { get; set; } = true;
    public float VoiceDbfs { get; private set; } = -120f;
    public bool VoiceActive { get; private set; }
    public float GainReductionDb => -_gDb;
    public float MaxGainReductionDb { get; private set; }
    public long ActiveSamples { get; private set; }
    public long Samples { get; private set; }
    public double LastProcessUs { get; private set; }

    /// <summary>어택 · 릴리스(ms) — 지정 시간에 목표의 1 dB 이내 도달하도록 τ = t / ln 14.</summary>
    public void SetTimes(float attackMs, float releaseMs)
    {
        AttackMs = attackMs; ReleaseMs = releaseMs;
        _aCoef = 1f - MathF.Exp(-1f / (attackMs / MathF.Log(14f) * 1e-3f * Fs));
        _rCoef = 1f - MathF.Exp(-1f / (releaseMs / MathF.Log(14f) * 1e-3f * Fs));
    }

    public void Reset() { Array.Clear(_sq); _sqHead = 0; _sum = 0; _gDb = 0; _holdLeft = 0; VoiceActive = false; MaxGainReductionDb = 0; ActiveSamples = Samples = 0; }

    /// <summary>블록 처리(무할당) — bgm 은 제자리에서 더킹되고 outL/outR 에 최종 믹스가 기록된다.</summary>
    public void Process(ReadOnlySpan<float> voice, ReadOnlySpan<float> foleyL, ReadOnlySpan<float> foleyR, Span<float> bgmL, Span<float> bgmR, Span<float> outL, Span<float> outR)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = outL.Length;
        var thr = MathF.Pow(10f, ThresholdDbfs / 10f) * Win; // 평균 제곱 임계 × 창
        var holdSamples = HoldMs * 1e-3f * Fs;
        var target = Enabled ? Math.Clamp(DepthDb, -24f, 0f) : 0f;
        for (var i = 0; i < n; i++)
        {
            var v = i < voice.Length ? voice[i] : 0f;
            var q = v * v;
            _sum += q - _sq[_sqHead]; _sq[_sqHead] = q; _sqHead = (_sqHead + 1) % Win;
            if (_sum < 0) _sum = 0;
            var on = _sum > thr;
            if (on) _holdLeft = holdSamples; else if (_holdLeft > 0) _holdLeft--;
            var active = on || _holdLeft > 0;
            var want = active ? target : 0f;
            _gDb += (want - _gDb) * (want < _gDb ? _aCoef : _rCoef);
            var g = MathF.Pow(10f, _gDb / 20f);
            var bl = bgmL[i] * g; var br = bgmR[i] * g;
            bgmL[i] = bl; bgmR[i] = br;
            outL[i] = v + (i < foleyL.Length ? foleyL[i] : 0f) + bl;
            outR[i] = v + (i < foleyR.Length ? foleyR[i] : 0f) + br;
            if (active) ActiveSamples++;
        }
        Samples += n;
        VoiceActive = _holdLeft > 0 || _sum > thr;
        VoiceDbfs = 10f * MathF.Log10((float)Math.Max(_sum / Win, 1e-12));
        if (-_gDb > MaxGainReductionDb) MaxGainReductionDb = -_gDb;
        LastProcessUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
