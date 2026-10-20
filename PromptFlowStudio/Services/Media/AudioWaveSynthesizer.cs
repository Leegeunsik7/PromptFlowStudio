using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Inference;   // LocalModelLoader(가속/가중치 게이트)
using PromptFlowStudio.Services.Wan;         // AudioTrackInfo · AudioWaveformAnalyzer

namespace PromptFlowStudio.Services.Media;

/// <summary>가이드 파형 합성 결과 — WAV 바이트 + 엔벨로프 + 파형 SVG + 백엔드.</summary>
public sealed record WaveSynthResult(
    bool Ok, byte[] Wav, AudioTrackInfo Envelope, string SvgDataUrl, InferenceBackend Backend, string Message);

/// <summary>
/// [PromptFlow Studio v7.2.0] 순수 BCL 오디오 파형 합성기(외부 C++ DLL/NuGet 0).
///   ▸ 텍스트/길이로 <b>음절 버스트 근사</b> 스피치형 PCM 을 생성하고 <see cref="MemoryStream"/> + RIFF WAV(44.1kHz·16bit 모노) 헤더로 봉인.
///   ▸ 생성 파형은 <see cref="AudioWaveformAnalyzer"/> 로 엔벨로프화 → 보컬 트랙 파형 SVG + <see cref="VocalSyncNode"/> 입력으로 전달.
///   ▸ ONNX TTS 결선점(<see cref="TrySynthesizeOnnxAsync"/>): 가속 HW + 가중치 로드 시 고음질 합성으로 <b>즉시 승격</b>, 미탑재/예외는 BCL 가이드로 Graceful 폴백.
/// </summary>
public sealed class AudioWaveSynthesizer
{
    private const int SampleRate = 44100;   // 44.1kHz(과제 규격)
    private const int MaxSeconds = 90;      // 가이드 파형 상한(메모리 방어)
    private readonly LocalModelLoader _loader;

    public AudioWaveSynthesizer(LocalModelLoader loader) { _loader = loader; }

    /// <summary>네이티브 TTS(가속+가중치) 준비 여부. 현재 가중치 미탑재 → false → BCL 가이드.</summary>
    public bool NativeReady => _loader.IsAccelerated && _loader.HasLoadedWeights;

    /// <summary>가이드 파형 합성 — 네이티브 TTS 우선, 미가용/예외 시 BCL 스피치형 파형으로 폴백.</summary>
    public async Task<WaveSynthResult> SynthesizeAsync(string? text, double durationSec, CancellationToken ct = default)
    {
        if (NativeReady)
        {
            try { return await TrySynthesizeOnnxAsync(text, durationSec, ct).ConfigureAwait(false); }
            catch (OutOfMemoryException ex) { Program.Log("WaveSynth.OOM", ex); }
            catch (NotSupportedException) { /* 가중치 미탑재 → BCL */ }
            catch (Exception ex) { Program.Log("WaveSynth.Native", ex); }
        }
        return BuildBclGuide(text, durationSec);
    }

    /// <summary>[결선 지점] ONNX TTS 고음질 합성 — 현재 가중치 미탑재라 NotSupported(상위 BCL 폴백). 결선 시 이 메서드만 교체.</summary>
    private static Task<WaveSynthResult> TrySynthesizeOnnxAsync(string? text, double durationSec, CancellationToken ct)
        => throw new NotSupportedException("ONNX TTS 가중치 미탑재 — BCL 가이드 파형 폴백");

    /// <summary>BCL 스피치형 가이드 파형 생성(음절 버스트 × 보이스 톤 + 결정적 지터). 실패는 빈 결과(Graceful).</summary>
    private WaveSynthResult BuildBclGuide(string? text, double durationSec)
    {
        try
        {
            var dur = Math.Clamp(durationSec <= 0 ? 5 : durationSec, 0.5, MaxSeconds);
            var total = (int)(dur * SampleRate);
            var pcm = new short[total];

            // 음절 수 근사: 공백 제외 글자수/2.2(한국어 음절 대략), 최소 dur*1.5.
            var chars = string.IsNullOrWhiteSpace(text) ? 0 : text!.Count(c => !char.IsWhiteSpace(c));
            var syllables = Math.Max((int)(dur * 1.5), Math.Max(1, (int)(chars / 2.2)));
            var seed = StableHash(text ?? "guide");
            var rng = new Random(seed);
            var syllLen = (double)total / syllables;
            const double baseFreq = 155.0;   // 보이스 근사 기본 주파수

            for (var i = 0; i < syllables; i++)
            {
                var startS = (int)(i * syllLen);
                var endS = (int)Math.Min(total, (i + 1) * syllLen);
                var len = endS - startS;
                if (len <= 0) continue;
                // 음절별 살짝 다른 피치(억양) + 진폭.
                var f = baseFreq * (0.9 + 0.35 * rng.NextDouble());
                var amp = 0.35 + 0.5 * rng.NextDouble();
                for (var s = startS; s < endS; s++)
                {
                    var p = (s - startS) / (double)len;            // 0..1 음절 진행
                    var window = Math.Sin(Math.PI * p);            // raised-sine 음절 창(자연 감쇠)
                    var t = s / (double)SampleRate;
                    var tone = Math.Sin(2 * Math.PI * f * t) + 0.3 * Math.Sin(2 * Math.PI * f * 2 * t);   // 기음 + 배음
                    var val = amp * window * tone * 0.9;
                    pcm[s] = (short)Math.Clamp(val * short.MaxValue, short.MinValue, short.MaxValue);
                }
            }

            var wav = BuildRiffWav(pcm, SampleRate);
            var env = AudioWaveformAnalyzer.AnalyzeWavBytes(wav, "guide.wav") ?? AudioTrackInfo.Empty;
            var svg = EnvelopeSvgDataUrl(env);
            return new WaveSynthResult(true, wav, env, svg, InferenceBackend.Bcl, $"BCL 가이드 파형 · {syllables}음절 · {dur:0.#}s");
        }
        catch (Exception ex)
        {
            Program.Log("WaveSynth.Bcl", ex);
            return new WaveSynthResult(false, Array.Empty<byte>(), AudioTrackInfo.Empty, "", InferenceBackend.Bcl, "합성 실패");
        }
    }

    /// <summary>16bit 모노 PCM → RIFF/WAVE 바이트(순수 BCL · MemoryStream + BinaryWriter).</summary>
    public static byte[] BuildRiffWav(short[] samples, int sampleRate)
    {
        samples ??= Array.Empty<short>();
        var dataBytes = samples.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        // RIFF 헤더
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
        // fmt 청크(PCM)
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);   // PCM · 모노
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        // data 청크
        w.Write("data"u8.ToArray()); w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>엔벨로프 → 보컬 트랙용 미니 파형 SVG DataURL(외부 에셋 0 · URL 이스케이프).</summary>
    public static string EnvelopeSvgDataUrl(AudioTrackInfo env)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256);
        sb.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 32' preserveAspectRatio='none'>");
        var n = Math.Max(1, env.Buckets);
        var bw = 100.0 / n;
        for (var i = 0; i < n; i++)
        {
            var v = env.Buckets == 0 ? 0 : env.Envelope[i];
            var h = Math.Clamp(v, 0, 1) * 28 + 2;
            var x = i * bw;
            var y = (32 - h) / 2;
            sb.Append("<rect x='").Append(x.ToString("0.##", ci)).Append("' y='").Append(y.ToString("0.##", ci))
              .Append("' width='").Append(Math.Max(0.4, bw - 0.3).ToString("0.##", ci)).Append("' height='").Append(h.ToString("0.##", ci))
              .Append("' rx='0.4' fill='#a855f7'/>");
        }
        sb.Append("</svg>");
        return "data:image/svg+xml," + Uri.EscapeDataString(sb.ToString());
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 2166136261;
            foreach (var ch in s) { h ^= ch; h *= 16777619; }
            return (int)(h & 0x7FFFFFFF);
        }
    }
}
