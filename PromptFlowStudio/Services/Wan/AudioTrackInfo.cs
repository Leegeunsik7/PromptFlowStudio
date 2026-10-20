using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlow Studio v6.0.2] 분석된 오디오 트랙 — 총 길이 + 구간별 볼륨 강도(엔벨로프).
///   ▸ <see cref="Envelope"/> 는 0.0~1.0 정규화 버킷 배열(파형 바 하나당 한 값 · 시간 균등 분할).
///   ▸ WAV(PCM)는 실측 RMS, 그 외(MP3 등)·오프라인/스텁 업로드는 순수 BCL 모의 엔벨로프로 폴백(크로스 플랫폼).
///   ⚠ 100% 결정적 값 객체(record) — OS 종속 코덱 없음(제2헌장).
/// </summary>
public sealed record AudioTrackInfo(string FileName, double DurationSec, IReadOnlyList<double> Envelope)
{
    /// <summary>파형 바 개수(=엔벨로프 버킷 수).</summary>
    public int Buckets => Envelope.Count;

    /// <summary>버킷 하나가 대표하는 시간(초).</summary>
    public double SecondsPerBucket => Buckets <= 0 ? 0 : DurationSec / Buckets;

    /// <summary>전체 평균 강도(0~1).</summary>
    public double AverageIntensity => Envelope.Count == 0 ? 0 : Envelope.Average();

    /// <summary>지정 시각(초)의 강도(0~1) — 범위를 벗어나면 0.</summary>
    public double IntensityAt(double tSec)
    {
        if (Buckets == 0 || DurationSec <= 0) return 0;
        var idx = (int)(tSec / DurationSec * Buckets);
        return idx >= 0 && idx < Buckets ? Envelope[idx] : 0;
    }

    /// <summary>[startSec, endSec) 구간의 평균 강도(0~1) — 컷 구간 다이내믹 판정.</summary>
    public double AverageIntensityBetween(double startSec, double endSec)
    {
        if (Buckets == 0 || DurationSec <= 0 || endSec <= startSec) return 0;
        var from = Math.Clamp((int)(startSec / DurationSec * Buckets), 0, Buckets - 1);
        var to = Math.Clamp((int)Math.Ceiling(endSec / DurationSec * Buckets), from + 1, Buckets);
        double sum = 0; var n = 0;
        for (var i = from; i < to; i++) { sum += Envelope[i]; n++; }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>빈 트랙(업로드 전 기본).</summary>
    public static AudioTrackInfo Empty { get; } = new("(없음)", 0, Array.Empty<double>());
}

/// <summary>
/// [PromptFlow Studio v6.0.2] 오디오 파형 분석기 — 순수 C# BCL(외부 코덱/JS 0).
///   ▸ <see cref="AnalyzeFile"/>: WAV(PCM) 파일이면 실측 RMS 엔벨로프, 그 외/실패는 <see cref="Mock"/> 폴백.
///   ▸ <see cref="Mock"/>: 파일명 해시 시드로 재현 가능한 '노래형' 엔벨로프(벌스/코러스 다이내믹) 생성 — 브라우저 WebAudio 불가 환경 보장(insight 1).
/// </summary>
public static class AudioWaveformAnalyzer
{
    /// <summary>파형 바 기본 개수(모바일 컴팩트 뷰 기준).</summary>
    public const int DefaultBuckets = 64;

    /// <summary>파일 경로 분석 — .wav 는 실측, 그 외/실패는 파일명 기반 모의 엔벨로프.</summary>
    public static AudioTrackInfo AnalyzeFile(string? path, int buckets = DefaultBuckets)
    {
        var name = string.IsNullOrWhiteSpace(path) ? "track.wav" : Path.GetFileName(path);
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)
                && Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var wav = AnalyzeWavBytes(File.ReadAllBytes(path), name, buckets);
                if (wav is not null) return wav;
            }
        }
        catch (Exception ex) { Program.Log("AudioWaveform.AnalyzeFile", ex); }   // 실패는 조용히 모의 폴백(Graceful)
        return Mock(name, EstimateDurationFromName(name), buckets);
    }

    /// <summary>
    /// WAV(RIFF/PCM) 바이트 → 실측 RMS 엔벨로프. 16/8/32비트 PCM 및 32비트 float 지원. 파싱 실패 시 null(폴백 유도).
    /// </summary>
    public static AudioTrackInfo? AnalyzeWavBytes(byte[]? bytes, string fileName, int buckets = DefaultBuckets)
    {
        if (bytes is null || bytes.Length < 44) return null;
        buckets = Math.Clamp(buckets, 8, 256);
        try
        {
            if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return null;
            if (bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E') return null;

            int channels = 0, sampleRate = 0, bits = 0, fmtTag = 1, dataOff = -1, dataLen = 0;
            var pos = 12;
            while (pos + 8 <= bytes.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                var size = BitConverter.ToInt32(bytes, pos + 4);
                var body = pos + 8;
                if (size < 0 || body + size > bytes.Length) size = Math.Max(0, bytes.Length - body);
                if (id == "fmt ")
                {
                    fmtTag = BitConverter.ToInt16(bytes, body);
                    channels = BitConverter.ToInt16(bytes, body + 2);
                    sampleRate = BitConverter.ToInt32(bytes, body + 4);
                    bits = BitConverter.ToInt16(bytes, body + 14);
                }
                else if (id == "data") { dataOff = body; dataLen = size; }
                pos = body + size + (size & 1);   // 청크는 2바이트 정렬
            }

            if (dataOff < 0 || channels <= 0 || sampleRate <= 0 || bits <= 0) return null;
            var bytesPerSample = bits / 8;
            var frame = bytesPerSample * channels;
            if (frame <= 0) return null;
            var frames = dataLen / frame;
            if (frames <= 0) return null;

            var duration = frames / (double)sampleRate;
            var env = new double[buckets];
            var counts = new long[buckets];
            for (long f = 0; f < frames; f++)
            {
                var b = dataOff + (int)(f * frame);   // 버퍼 인덱스는 int 범위(byte[] 상한)
                var sample = ReadMonoSample(bytes, b, bits, fmtTag, channels, bytesPerSample);   // -1..1(첫 채널)
                var bucket = (int)(f * (long)buckets / frames);
                if (bucket >= buckets) bucket = buckets - 1;
                env[bucket] += sample * sample;   // RMS 누적(제곱합)
                counts[bucket]++;
            }
            for (var i = 0; i < buckets; i++)
                env[i] = counts[i] == 0 ? 0 : Math.Sqrt(env[i] / counts[i]);

            Normalize(env);
            return new AudioTrackInfo(fileName, duration, env);
        }
        catch (Exception ex) { Program.Log("AudioWaveform.Wav", ex); return null; }
    }

    /// <summary>
    /// [순수 BCL 폴백] 파일명 시드로 재현 가능한 '노래형' 모의 엔벨로프 생성 — 벌스(낮음)/코러스(높음) 다이내믹 + 결정적 지터.
    /// </summary>
    public static AudioTrackInfo Mock(string fileName, double durationSec, int buckets = DefaultBuckets)
    {
        buckets = Math.Clamp(buckets, 8, 256);
        durationSec = durationSec <= 0 ? 30 : Math.Clamp(durationSec, 3, 600);
        var seed = StableHash(fileName);
        var rng = new Random(seed);

        // 곡 구조: 2~3개의 코러스(고강도) 구간을 시드로 배치, 그 사이는 벌스(중저강도).
        var chorusCount = 2 + (seed % 2);
        var env = new double[buckets];
        for (var i = 0; i < buckets; i++)
        {
            var t = i / (double)(buckets - 1);                       // 0..1 진행
            var breathe = 0.5 + 0.28 * Math.Sin(t * Math.PI * 6 + seed % 7);   // 완만한 호흡
            var chorus = 0.0;
            for (var c = 0; c < chorusCount; c++)
            {
                var center = (c + 1.0) / (chorusCount + 1.0);        // 균등 배치
                var d = Math.Abs(t - center);
                chorus += 0.55 * Math.Exp(-(d * d) / 0.006);          // 가우시안 피크(코러스 고조)
            }
            var jitter = (rng.NextDouble() - 0.5) * 0.22;            // 비트 단위 결정적 흔들림
            env[i] = breathe + chorus + jitter;
        }
        // 인트로/아웃트로 페이드(자연스러운 시작·끝).
        var fade = Math.Max(1, buckets / 12);
        for (var i = 0; i < fade; i++)
        {
            var k = i / (double)fade;
            env[i] *= k;
            env[buckets - 1 - i] *= k;
        }
        Normalize(env);
        return new AudioTrackInfo(fileName, durationSec, env);
    }

    // ── 내부 헬퍼 ──

    private static double ReadMonoSample(byte[] b, int off, int bits, int fmtTag, int channels, int bytesPerSample)
    {
        // 첫 채널만 사용(모노 근사). fmtTag 3 = IEEE float.
        try
        {
            if (fmtTag == 3 && bits == 32) return Math.Clamp(BitConverter.ToSingle(b, off), -1, 1);
            switch (bits)
            {
                case 16: return BitConverter.ToInt16(b, off) / 32768.0;
                case 8: return (b[off] - 128) / 128.0;                 // 8비트는 부호 없는 오프셋(0~255)
                case 32: return BitConverter.ToInt32(b, off) / 2147483648.0;
                case 24:
                    var raw = b[off] | (b[off + 1] << 8) | (b[off + 2] << 16);
                    if ((raw & 0x800000) != 0) raw -= 0x1000000;       // 24비트 두 보수 부호 확장
                    return raw / 8388608.0;
                default: return 0;
            }
        }
        catch { return 0; }
    }

    private static void Normalize(double[] env)
    {
        var max = 0.0;
        for (var i = 0; i < env.Length; i++) { if (env[i] < 0) env[i] = 0; if (env[i] > max) max = env[i]; }
        if (max <= 0) { for (var i = 0; i < env.Length; i++) env[i] = 0; return; }
        for (var i = 0; i < env.Length; i++) env[i] = Math.Clamp(env[i] / max, 0, 1);
    }

    /// <summary>파일명에 붙은 숫자(초/분)를 러프하게 읽어 길이 추정 — 없으면 이름 해시로 20~45초.</summary>
    private static double EstimateDurationFromName(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        if (digits.Length is >= 2 and <= 4 && int.TryParse(digits, out var n) && n is >= 5 and <= 600) return n;
        return 20 + StableHash(name) % 26;   // 20~45초
    }

    /// <summary>플랫폼·런타임 무관 결정적 해시(문자열 → 음이 아닌 int).</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 2166136261;
            foreach (var ch in s ?? "") { h ^= ch; h *= 16777619; }
            return (int)(h & 0x7FFFFFFF);
        }
    }
}
