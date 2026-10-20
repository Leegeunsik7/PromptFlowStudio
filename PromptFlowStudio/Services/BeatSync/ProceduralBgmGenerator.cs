namespace PromptFlowStudio.Services.BeatSync;

/// <summary>
/// 절차적 BGM 생성기(48 kHz 스테레오 · 무할당) — 4/4 킥(다운비트 강세) · 2/4 스네어 · 8 분 하이햇 · 베이스(근음 8 분) · 패드(3 화음 · 2 마디 진행).
/// 정답 비트 그리드(<see cref="BeatTimeSec"/>)를 제공해 트래커를 검증한다. WAV 를 불러오면 그 루프로 대체(비트 정답 없음).
/// </summary>
public sealed class ProceduralBgmGenerator
{
    public const int Fs = 48000;
    private static readonly float[] Roots = { 55f, 43.65f, 49f, 41.2f };
    private float[]? _wavL, _wavR;
    private long _n, _wavPos;
    private uint _rng = 0x1234567u;

    public double Bpm { get; private set; } = 120;
    public double OffsetSec { get; private set; } = 0.1;
    public float Level { get; set; } = 0.5f;
    public bool HasWav => _wavL is not null;
    public long Samples => _n;
    public double BeatPeriod => 60.0 / Bpm;
    public double BeatTimeSec(long k) => OffsetSec + k * BeatPeriod;

    public void SetTempo(double bpm, double offsetSec = 0.1) { Bpm = Math.Clamp(bpm, 40, 220); OffsetSec = offsetSec; }
    public void Reset() { _n = 0; _wavPos = 0; _rng = 0x1234567u; }

    /// <summary>WAV(48 kHz 이외는 선형 리샘플) 루프 적재(비핫패스) — null 이면 절차적 모드.</summary>
    public void LoadWav(float[]? left, float[]? right, int rate)
    {
        if (left is null) { _wavL = _wavR = null; return; }
        right ??= left;
        if (rate != Fs)
        {
            var len = (int)((long)left.Length * Fs / rate);
            var l2 = new float[len]; var r2 = new float[len];
            for (var i = 0; i < len; i++)
            {
                var s = (double)i * rate / Fs; var a = (int)s; var f = (float)(s - a); var b = Math.Min(a + 1, left.Length - 1);
                l2[i] = left[a] + (left[b] - left[a]) * f; r2[i] = right[a] + (right[b] - right[a]) * f;
            }
            left = l2; right = r2;
        }
        _wavL = left; _wavR = right; _wavPos = 0;
    }

    private float Noise() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return (_rng >> 8) * (2f / 16777216f) - 1f; }

    public void Render(Span<float> l, Span<float> r)
    {
        if (_wavL is not null)
        {
            for (var i = 0; i < l.Length; i++) { var p = (int)(_wavPos % _wavL.Length); l[i] = _wavL[p] * Level * 2; r[i] = _wavR![p] * Level * 2; _wavPos++; }
            _n += l.Length;
            return;
        }
        var bp = BeatPeriod * Fs;
        var off = OffsetSec * Fs;
        for (var i = 0; i < l.Length; i++, _n++)
        {
            var t = (_n - off) / bp;           // 비트 단위 위치
            float k = 0, sn = 0, hh = 0, bs = 0, pad = 0;
            if (t >= 0)
            {
                var beat = (long)Math.Floor(t); var inBeat = (t - beat) * BeatPeriod;  // 초
                var bar = beat / 4; var pos = (int)(beat % 4);
                var ts = (float)inBeat;
                var accent = pos == 0 ? 1f : 0.55f;
                k = accent * MathF.Exp(-ts / 0.09f) * MathF.Sin(2f * MathF.PI * (52f * ts + 90f * 0.012f * (1f - MathF.Exp(-ts / 0.012f)))) + (ts < 0.002f ? 0.6f * accent : 0f);
                if (pos is 1 or 3) sn = 0.35f * MathF.Exp(-ts / 0.07f) * (0.6f * Noise() + 0.4f * MathF.Sin(2f * MathF.PI * 190f * ts));
                var half = (t - beat) >= 0.5 ? (t - beat - 0.5) * BeatPeriod : inBeat;
                hh = 0.08f * MathF.Exp(-(float)half / 0.025f) * Noise();
                var root = Roots[(int)(bar / 2 % 4)];
                var tt = _n / (float)Fs;
                bs = 0.22f * MathF.Exp(-(float)((t * 2 - Math.Floor(t * 2)) * BeatPeriod / 2) / 0.18f) * MathF.Sin(2f * MathF.PI * root * tt);
                pad = 0.05f * (MathF.Sin(2f * MathF.PI * root * 4f * tt) + MathF.Sin(2f * MathF.PI * root * 5.04f * tt) + MathF.Sin(2f * MathF.PI * root * 6f * tt));
            }
            var m = Level * (k + sn + bs);
            l[i] = m + Level * (hh * 0.8f + pad * 1.1f);
            r[i] = m + Level * (hh * 1.2f + pad * 0.9f);
        }
    }
}
