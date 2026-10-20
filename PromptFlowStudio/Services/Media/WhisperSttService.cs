using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>자막 한 줄(구간) — 인덱스/시작/끝/텍스트.</summary>
public sealed record SubtitleCue(int Index, TimeSpan Start, TimeSpan End, string Text);

/// <summary>STT 결과. 실패 시 Ok=false + Message, Cues 는 빈 목록.</summary>
public sealed record SttResult(bool Ok, string? SrtPath, IReadOnlyList<SubtitleCue> Cues, string Message);

/// <summary>오프라인 STT(자막 추출) 계약 — Faster-Whisper CLI 로 .srt/타임코드 추출 + 스토리보드 컷 동기화.</summary>
public interface IWhisperSttService
{
    bool IsAvailable { get; }
    /// <summary>오디오/비디오에서 자막(.srt)과 타임코드 큐를 추출. 실패는 Graceful.</summary>
    Task<SttResult> TranscribeAsync(string mediaPath, string? language = null, IProgress<string>? status = null, CancellationToken ct = default);
    /// <summary>추출한 큐를 컷 시간창(각 <paramref name="clipSeconds"/>초, 소스 기준)에 분배해 각 <see cref="StoryboardScene.VoiceText"/> 를 채운다.
    ///   <paramref name="playbackSpeed"/>&gt;1 이면 STT 소스가 배속 렌더본이므로 큐 시각을 ×배속으로 소스 시간축에 환산해 정확히 매핑. 채운 컷 수 반환.</summary>
    int DistributeToScenes(IReadOnlyList<SubtitleCue> cues, IReadOnlyList<StoryboardScene> scenes, double clipSeconds = 5.0, double playbackSpeed = 1.0);

    /// <summary>배속 렌더용 타임코드 보정 — 각 큐의 시작/끝에 <c>t_sub = t_whisper / PlaybackSpeed</c> 적용(1.0x 소스 SRT → 배속 표시 시간대). 시간 밀림 제거.</summary>
    IReadOnlyList<SubtitleCue> ScaleForSpeed(IReadOnlyList<SubtitleCue> cues, double playbackSpeed);
}

/// <summary>
/// [PromptFlowStudio] 오프라인 STT — Faster-Whisper(MIT) CLI 연동(외부 NuGet 0, 순수 BCL). 네트워크/유료 API 없음.
///   ▸ 설정 <c>Engines:FasterWhisper:Exe</c>(예: Purfview whisper-faster.exe). 결과 .srt 를 임시 폴더에 생성 → BCL 로 파싱해 큐 목록화.
///   ▸ <see cref="DistributeToScenes"/> 로 타임코드를 컷 5초 창에 매핑 → 기존 자막 번인(drawtext) 파이프라인이 그대로 소비(양방향 동기화).
///   ▸ CLI/모델 부재·실패는 예외 없이 결과 record 로 원인 반환(Graceful).
/// </summary>
public sealed class WhisperSttService(EngineConfigService engines) : IWhisperSttService
{
    private string? Exe => engines.Resolve(EngineKeys.FasterWhisper);

    /// <inheritdoc/>
    public bool IsAvailable => Exe is not null;

    /// <inheritdoc/>
    public async Task<SttResult> TranscribeAsync(string mediaPath, string? language = null, IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            return new(false, null, Array.Empty<SubtitleCue>(), "입력 미디어를 찾을 수 없습니다.");
        var exe = Exe;
        if (exe is null) return new(false, null, Array.Empty<SubtitleCue>(), "Faster-Whisper CLI 가 설정되지 않았습니다.");

        var outDir = Path.Combine(Path.GetTempPath(), "promptflow-stt", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(outDir);
            status?.Report("자막 추출 중… (Faster-Whisper)");

            var model = engines.Extra(EngineKeys.FasterWhisper, "Model", "medium")!;
            var lang  = string.IsNullOrWhiteSpace(language) ? (engines.Extra(EngineKeys.FasterWhisper, "Language", "ko") ?? "ko") : language;
            var template = engines.Args(EngineKeys.FasterWhisper);
            var args = ProcessRunner.Substitute(template, new Dictionary<string, string>
            {
                ["in"] = mediaPath, ["model"] = model, ["lang"] = lang, ["outdir"] = outDir,
            });

            var r = await ProcessRunner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
            if (!r.Started) return new(false, null, Array.Empty<SubtitleCue>(), "Faster-Whisper 실행 실패.");

            // 결과 .srt 찾기(입력파일명.srt 우선, 없으면 폴더 내 아무 .srt).
            var srt = Path.Combine(outDir, Path.GetFileNameWithoutExtension(mediaPath) + ".srt");
            if (!File.Exists(srt))
                srt = Directory.EnumerateFiles(outDir, "*.srt").FirstOrDefault();
            if (srt is null || !File.Exists(srt))
                return new(false, null, Array.Empty<SubtitleCue>(), $"자막 파일이 생성되지 않았습니다. {Tail(r.StdErr)}");

            var cues = ParseSrt(File.ReadAllText(srt, Encoding.UTF8));

            // 임시 srt 를 미디어 폴더 옆으로 보존(원하면 재사용).
            string? keptSrt = null;
            try
            {
                keptSrt = Path.ChangeExtension(mediaPath, ".srt");
                File.Copy(srt, keptSrt, overwrite: true);
            }
            catch { keptSrt = srt; }

            return cues.Count > 0
                ? new(true, keptSrt, cues, $"{cues.Count}개 자막 구간 추출 완료")
                : new(false, keptSrt, cues, "자막 구간을 찾지 못했습니다.");
        }
        catch (OperationCanceledException) { return new(false, null, Array.Empty<SubtitleCue>(), "자막 추출이 취소되었습니다."); }
        catch (Exception ex) { Program.Log("Whisper.Transcribe", ex); return new(false, null, Array.Empty<SubtitleCue>(), "자막 추출 실패: " + ex.Message); }
        finally { try { if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true); } catch { /* 무시 */ } }
    }

    /// <inheritdoc/>
    public int DistributeToScenes(IReadOnlyList<SubtitleCue> cues, IReadOnlyList<StoryboardScene> scenes, double clipSeconds = 5.0, double playbackSpeed = 1.0)
    {
        if (cues is null || cues.Count == 0 || scenes is null || scenes.Count == 0) return 0;
        var spd = Math.Clamp(playbackSpeed <= 0 ? 1.0 : playbackSpeed, 0.5, 2.0);
        var filled = 0;
        for (var i = 0; i < scenes.Count; i++)
        {
            var start = i * clipSeconds;
            var end   = (i + 1) * clipSeconds;
            // 배속 렌더본에서 추출된 큐는 시간이 압축돼 있으므로 ×배속으로 소스 시간축에 환산 후 컷 창과 비교(시간 밀림 보정).
            var text = string.Join(" ", cues
                .Where(c => c.Start.TotalSeconds * spd >= start - 0.001 && c.Start.TotalSeconds * spd < end - 0.001)
                .Select(c => c.Text.Trim())
                .Where(t => t.Length > 0));
            if (text.Length > 0) { scenes[i].VoiceText = text; filled++; }
        }
        return filled;
    }

    /// <inheritdoc/>
    public IReadOnlyList<SubtitleCue> ScaleForSpeed(IReadOnlyList<SubtitleCue> cues, double playbackSpeed)
    {
        if (cues is null || cues.Count == 0) return Array.Empty<SubtitleCue>();
        var spd = Math.Clamp(playbackSpeed <= 0 ? 1.0 : playbackSpeed, 0.5, 2.0);
        if (Math.Abs(spd - 1.0) < 1e-9) return cues;   // 원본 배속이면 그대로
        // t_sub = t_whisper / PlaybackSpeed — 배속(빠름)일수록 자막 표시 시각을 앞당겨 비디오/오디오 템포와 정렬.
        return cues.Select(c => c with
        {
            Start = TimeSpan.FromSeconds(c.Start.TotalSeconds / spd),
            End   = TimeSpan.FromSeconds(c.End.TotalSeconds / spd),
        }).ToList();
    }

    // ── SRT 파서(BCL) — "idx\n hh:mm:ss,mmm --> hh:mm:ss,mmm\n text...\n\n" 블록 ──
    private static List<SubtitleCue> ParseSrt(string content)
    {
        var cues = new List<SubtitleCue>();
        if (string.IsNullOrWhiteSpace(content)) return cues;

        var blocks = content.Replace("\r\n", "\n").Replace('\r', '\n').Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) continue;

            // 첫 줄이 인덱스(숫자)면 건너뛰고 다음 줄이 타임라인.
            var tlIdx = int.TryParse(lines[0].Trim(), out _) ? 1 : 0;
            if (tlIdx >= lines.Length) continue;

            var m = TimeRx.Match(lines[tlIdx]);
            if (!m.Success) continue;
            if (!TryTime(m.Groups[1].Value, out var st) || !TryTime(m.Groups[2].Value, out var en)) continue;

            var text = string.Join(" ", lines.Skip(tlIdx + 1).Select(l => l.Trim())).Trim();
            if (text.Length == 0) continue;
            cues.Add(new SubtitleCue(cues.Count + 1, st, en, text));
        }
        return cues;
    }

    private static bool TryTime(string s, out TimeSpan ts)
    {
        ts = TimeSpan.Zero;
        // hh:mm:ss,mmm (또는 . 소수점)
        var m = Regex.Match(s.Trim(), @"^(\d+):(\d{2}):(\d{2})[.,](\d{1,3})$");
        if (!m.Success) return false;
        try
        {
            ts = new TimeSpan(0,
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))
                + TimeSpan.FromMilliseconds(int.Parse(m.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture));
            return true;
        }
        catch { return false; }
    }

    private static string Tail(string s, int n = 300) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.Trim() : "…" + s[^n..].Trim());

    private static readonly Regex TimeRx = new(@"([\d:.,]+)\s*-->\s*([\d:.,]+)", RegexOptions.Compiled);
}
