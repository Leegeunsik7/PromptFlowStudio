using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Services.Audio;

/// <summary>
/// [PromptFlowStudio] 오프라인 대본 TTS — 외부 NuGet 0.
///   ▸ 1순위: <see cref="ILocalTtsService"/>(Kokoro-82M / F5-TTS CLI) 구성 시 그 WAV 를 출력 경로로 복사.
///   ▸ 2순위(Windows): SAPI 5 COM(<c>SAPI.SpVoice</c> + <c>SAPI.SpFileStream</c>) 늦은 바인딩 — System.Speech 패키지 없이 OS 내장 음성.
///   ▸ 폴백: Mock — 대본 길이 추정 시간만큼 무음 RIFF WAV(44.1kHz·16bit 모노) 기록(합본 타이밍 보존 · 잡음 0).
///   ▸ 모든 실패 격리(false) · 원자적 기록(.tmp → Move).
/// </summary>
public sealed class LocalTtsSynthesisService : ITtsSynthesisService
{
    public const string BackendMock = "Mock";
    public const string BackendSapi = "SAPI";
    private const int MockSampleRate = 44100;
    private const double SecondsPerChar = 0.12;   // 한국어 낭독 근사(≈8자/초)
    private const double MaxSeconds = 90;

    private readonly ILocalTtsService? _local;
    private readonly bool _useSapi;
    private volatile string _lastBackend = BackendMock;

    public LocalTtsSynthesisService(ILocalTtsService? local) : this(local, useSapi: true) { }

    /// <param name="useSapi">false 면 SAPI 생략(테스트 결정론) — 로컬 엔진 → Mock 순.</param>
    public LocalTtsSynthesisService(ILocalTtsService? local, bool useSapi)
    {
        _local = local;
        _useSapi = useSapi;
    }

    public string LastBackend => _lastBackend;

    /// <summary>대본 낭독 예상 길이(초) — 공백 제외 글자 × <see cref="SecondsPerChar"/>, 0.5~90 클램프.</summary>
    public static double EstimateSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0.5;
        var n = 0;
        foreach (var c in text) if (!char.IsWhiteSpace(c)) n++;
        return Math.Clamp(n * SecondsPerChar, 0.5, MaxSeconds);
    }

    public async Task<bool> SynthesizeToFileAsync(string text, string outputPath, CancellationToken ct)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || string.IsNullOrWhiteSpace(outputPath)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(outputPath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("Tts.Prepare", ex);
            return false;
        }

        if (_local is { IsAvailable: true } local)
        {
            try
            {
                var wav = await local.SynthesizeAsync(t, full, ct).ConfigureAwait(false);
                if (wav is not null && File.Exists(wav))
                {
                    if (!string.Equals(Path.GetFullPath(wav), full, StringComparison.OrdinalIgnoreCase)) File.Copy(wav, full, overwrite: true);
                    _lastBackend = local.EngineName;
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (Exception ex) { Program.Log("Tts.Local", ex); }
        }
        if (ct.IsCancellationRequested) return false;

        if (_useSapi && OperatingSystem.IsWindows())
        {
            try
            {
                if (await Task.Run(() => TrySapi(t, full), ct).ConfigureAwait(false)) { _lastBackend = BackendSapi; return true; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (Exception ex) { Program.Log("Tts.Sapi", ex); }
        }
        if (ct.IsCancellationRequested) return false;

        if (!WriteMockWav(full, EstimateSeconds(t))) return false;
        _lastBackend = BackendMock;
        return true;
    }

    /// <summary>Mock — 지정 길이 무음 WAV 원자적 기록.</summary>
    public static bool WriteMockWav(string path, double seconds)
    {
        var tmp = path + ".tmp";
        try
        {
            var samples = new short[(int)(Math.Clamp(seconds, 0.1, MaxSeconds) * MockSampleRate)];
            File.WriteAllBytes(tmp, AudioWaveSynthesizer.BuildRiffWav(samples, MockSampleRate));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("Tts.Mock", ex);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>SAPI 5 늦은 바인딩 — SpFileStream(SSFMCreateForWrite=3) 을 AudioOutputStream 으로 지정 후 동기 Speak.</summary>
    private static bool TrySapi(string text, string path)
    {
#if WINDOWS
        var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
        var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream");
        if (voiceType is null || streamType is null) return false;
        object? voice = null, stream = null;
        var tmp = path + ".sapi.wav";
        try
        {
            voice = Activator.CreateInstance(voiceType);
            stream = Activator.CreateInstance(streamType);
            if (voice is null || stream is null) return false;
            streamType.InvokeMember("Open", BindingFlags.InvokeMethod, null, stream, new object[] { tmp, 3, false });
            voiceType.InvokeMember("AudioOutputStream", BindingFlags.PutRefDispProperty, null, voice, new[] { stream });
            voiceType.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, new object[] { text, 0 });
            streamType.InvokeMember("Close", BindingFlags.InvokeMethod, null, stream, null);
            if (!File.Exists(tmp) || new FileInfo(tmp).Length <= 44) return false;
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        finally
        {
            if (stream is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream);
            if (voice is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(voice);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
#else
        return false;
#endif
    }
}
