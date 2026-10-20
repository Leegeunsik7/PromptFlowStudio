using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()
using PromptFlowStudio.Services.Engines;

namespace PromptFlowStudio.Services.Media;

/// <summary>완전 오프라인 로컬 TTS 합성 계약(Kokoro-82M / F5-TTS CLI). 실패/미설치는 null(Graceful).</summary>
public interface ILocalTtsService
{
    /// <summary>사용 가능한 로컬 TTS 엔진이 설정되어 있는지.</summary>
    bool IsAvailable { get; }
    /// <summary>활성 엔진 이름(표시용): "Kokoro-82M" / "F5-TTS" / "(미설치)".</summary>
    string EngineName { get; }
    /// <summary>텍스트를 로컬에서 WAV 로 합성해 경로 반환(캐시). 네트워크/유료 API 없음. 실패 시 null.</summary>
    Task<string?> SynthesizeAsync(string text, string? outPath = null, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] 오프라인 TTS — Kokoro-82M(Apache 2.0) 우선, 없으면 F5-TTS(MIT) 폴백. 순수 BCL <see cref="ProcessRunner"/> CLI 연동(외부 NuGet 0).
///   ▸ 설정: appsettings <c>Engines:Kokoro:Exe</c> / <c>Engines:F5Tts:Exe</c>(비면 해당 엔진 비활성). Args 는 인자 템플릿({textfile}{out}{voice}).
///   ▸ 입력 텍스트를 임시 UTF-8 파일로 넘겨(인라인 이스케이프/한글 회피) WAV 를 생성 → <c>VoiceoverService</c> 와 동일하게 <c>Media/_tts/</c> 에
///     콘텐츠 해시 캐시. 결과 WAV 는 기존 병합/자막 파이프라인이 그대로 소비(ffmpeg 가 wav/mp3 무관 처리).
///   ▸ 미설치/실패는 예외 없이 null — 호출부(JobManager)가 온라인 Edge TTS 로 폴백하거나 나레이션 없이 진행.
/// </summary>
public sealed class LocalTtsService(EngineConfigService engines, IVideoMergeService merge) : ILocalTtsService
{
    private string TtsDir { get; } = Path.Combine(LicenseService.DataAreaRoot(), "Media", "_tts");

    // 동적 설정(EngineConfigService)에서 라이브로 읽어 저장 즉시 반영(Kokoro 우선).
    private string? KokoroExe => engines.Resolve(EngineKeys.Kokoro);
    private string? F5Exe     => engines.Resolve(EngineKeys.F5Tts);

    /// <inheritdoc/>
    public bool IsAvailable => KokoroExe is not null || F5Exe is not null;

    /// <inheritdoc/>
    public string EngineName => KokoroExe is not null ? "Kokoro-82M" : F5Exe is not null ? "F5-TTS" : "(미설치)";

    /// <inheritdoc/>
    public async Task<string?> SynthesizeAsync(string text, string? outPath = null, CancellationToken ct = default)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;

        var useKokoro = KokoroExe is not null;
        var exe = useKokoro ? KokoroExe : F5Exe;
        if (exe is null) return null;

        string? txtFile = null;
        try
        {
            Directory.CreateDirectory(TtsDir);
            var voice = engines.Extra(EngineKeys.Kokoro, "Voice", "af_heart") ?? "af_heart";
            var dest = outPath ?? Path.Combine(TtsDir, "loc_" + Hash((useKokoro ? "k|" + voice : "f5") + "|" + t) + ".wav");
            // 캐시 히트 — 44.1kHz·스테레오 정규화본 반환(24kHz 모노 → 규격화, 덕킹 믹싱 안전).
            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                return await AudioNormalizer.ToStereo44kAsync(merge.ResolvedFfmpegPath, dest, ct).ConfigureAwait(false);

            // 입력 텍스트 임시 파일(UTF-8 · BOM 없음).
            txtFile = Path.Combine(TtsDir, $"_in_{Guid.NewGuid():N}.txt");
            File.WriteAllText(txtFile, t, new UTF8Encoding(false));

            var name = useKokoro ? EngineKeys.Kokoro : EngineKeys.F5Tts;
            var template = engines.Args(name);
            var args = ProcessRunner.Substitute(template, new Dictionary<string, string>
            {
                ["textfile"] = txtFile, ["out"] = dest, ["text"] = t, ["voice"] = voice,
            });

            var r = await ProcessRunner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
            if (!r.Ok || !File.Exists(dest) || new FileInfo(dest).Length == 0) return null;
            // 생성 직후 44.1kHz·스테레오 정규화(-ar 44100 -ac 2). ffmpeg 없으면 원본 반환(Graceful).
            return await AudioNormalizer.ToStereo44kAsync(merge.ResolvedFfmpegPath, dest, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Program.Log("LocalTts.Synthesize", ex); return null; }
        finally { try { if (txtFile is not null && File.Exists(txtFile)) File.Delete(txtFile); } catch { /* 무시 */ } }
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
