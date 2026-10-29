using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Audio;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Commercial;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Render;

/// <summary>합본/후처리 결과 — IsVirtual=true 면 ffmpeg 부재로 실제 인코딩 없이 가상 처리(매니페스트 기록 또는 원본 유지).
/// SubtitlePath = 합본 옆 <c>.srt</c>(없으면 null) · AudioMuxed = 대본 음성 AAC 트랙 믹싱 완료.</summary>
public sealed record VideoStitchResult(bool Ok, bool IsVirtual, string? OutputPath, int PartCount, string Message, string? SubtitlePath = null, bool AudioMuxed = false);

/// <summary>합본 컷 1개 — 로컬 영상 경로 · 재생 길이(초) · 대본(TTS/자막 원천).</summary>
public sealed record StitchCut(string VideoPath, double DurationSec, string? Script);

/// <summary>ffmpeg 실행기(테스트 주입) — 정상 종료(코드 0) 여부. 취소 시 프로세스 트리 종료 후 false.</summary>
public delegate Task<bool> FfmpegRunner(string ffmpegExe, IReadOnlyList<string> args, CancellationToken ct);

/// <summary>컷 스트림 정보 — 첫 비디오 스트림 해상도 · 오디오 스트림 유무.</summary>
public sealed record StitchProbe(int Width, int Height, bool HasAudio);

/// <summary>컷 프로브(테스트 주입) — 실패/불명은 null.</summary>
public delegate Task<StitchProbe?> FfmpegProber(string ffmpegExe, string videoPath, CancellationToken ct);

/// <summary>
/// [PromptFlowStudio] 멀티 컷 최종 합본 + 커머셜 로고/디스클레이머 번인 — 순수 BCL <c>ffmpeg</c> 외부 프로세스.
///   ▸ 합본: 같은 그룹(<see cref="MetaGroup"/>) 렌더 작업이 전부 Completed 면 CutIndex 순 concat demuxer(<c>-c copy</c> 무손실)로 <c>{project}_full.mp4</c>.
///   ▸ 스마트 Concat: 프로브 해상도 불일치(OOM 하향 컷 혼재) 또는 <c>-c copy</c> 실패 시 최대 해상도 기준 레터박스(<c>scale+pad</c>) <c>libx264/aac</c> 재인코딩 폴백.
///   ▸ ffmpeg 부재(테스트·오프라인): 가상 병합 — <c>{output}.stitch.json</c> 매니페스트(파트 순서)만 원자적 기록.
///   ▸ 오디오: 컷 대본(<see cref="MetaScript"/>) → <see cref="ITtsSynthesisService"/> WAV → 컷 길이로 패드/트림·concat → <c>-c:v copy -c:a aac -shortest</c> 믹싱(실패 시 무음 합본 유지).
///   ▸ 자막: 컷 길이 누적 타임코드로 <c>{project}_full.srt</c> 를 합본 옆에 기록(<see cref="SubtitleGeneratorService"/>).
///   ▸ 번인: <see cref="CommercialBrandService.BuildOverlayArgs"/> 필터(로고 overlay 세이프존 + drawtext 디스클레이머) — 실패/부재 시 원본 유지.
///   ▸ 취소는 <see cref="ProcessRunner"/> 가 프로세스 트리 종료.
/// </summary>
public sealed class FFmpegVideoStitchService
{
    /// <summary>렌더 작업 메타 — 합본 그룹 키(스토리보드 일괄 전송 · 숏폼 세션 단위).</summary>
    public const string MetaGroup = "stitch.group";
    /// <summary>렌더 작업 메타 — 컷 대본(TTS 음성 · SRT 자막 원천).</summary>
    public const string MetaScript = "stitch.script";
    public const string FullSuffix = "_full.mp4";
    public const string VirtualManifestExt = ".stitch.json";
    /// <summary>대본 TTS 임시 폴더 접두(%TEMP%) — 캐시 정리 대상.</summary>
    public const string TtsTempPrefix = "pfs_tts_";

    private readonly Func<string?> _resolveFfmpeg;
    private readonly FfmpegRunner _run;
    private readonly FfmpegProber _probe;

    /// <summary>프로브 불명 시 재인코딩 기준 해상도.</summary>
    public const int FallbackWidth = 1920, FallbackHeight = 1080;

    public FFmpegVideoStitchService() : this(null, null) { }

    public FFmpegVideoStitchService(Func<string?>? resolveFfmpeg, FfmpegRunner? runner) : this(resolveFfmpeg, runner, null) { }

    /// <param name="resolveFfmpeg">ffmpeg 경로 조회(null 반환 = 부재 → 가상 병합). null 이면 <see cref="FfmpegLocator"/>(tools 동봉 우선 → PATH).</param>
    /// <param name="runner">ffmpeg 실행기. null 이면 <see cref="ProcessRunner"/>.</param>
    /// <param name="probe">컷 프로브. null 이면 기본 실행기일 때만 ffprobe(주입 실행기는 프로브 생략 → copy 실패 시 폴백).</param>
    public FFmpegVideoStitchService(Func<string?>? resolveFfmpeg, FfmpegRunner? runner, FfmpegProber? probe)
    {
        _resolveFfmpeg = resolveFfmpeg ?? DefaultResolveFfmpeg;
        _run = runner ?? DefaultRunAsync;
        _probe = probe ?? (runner is null ? DefaultProbeAsync : static (_, _, _) => Task.FromResult<StitchProbe?>(null));
    }

    public bool IsFfmpegAvailable => SafeResolve() is not null;

    public static string NewGroupId() => Guid.NewGuid().ToString("N");

    /// <summary>기존 메타 복사 + 그룹 키 부여.</summary>
    public static Dictionary<string, string> WithGroup(IReadOnlyDictionary<string, string>? meta, string groupId)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (meta is not null) foreach (var kv in meta) d[kv.Key] = kv.Value;
        d[MetaGroup] = groupId;
        return d;
    }

    /// <summary>그룹 진행 — 소속 작업 수 · Completed 수.</summary>
    public static (int Total, int Completed) GroupProgress(IReadOnlyList<BatchRenderJob> jobs, string? groupId)
    {
        int total = 0, done = 0;
        if (string.IsNullOrEmpty(groupId)) return (0, 0);
        for (var i = 0; i < jobs.Count; i++)
        {
            if (!InGroup(jobs[i], groupId)) continue;
            total++;
            if (jobs[i].Status == BatchJobStatus.Completed) done++;
        }
        return (total, done);
    }

    /// <summary>
    /// 그룹 완성 판정 — 소속 작업 1건 이상 · 전부 Completed · 전부 로컬 산출물 실존. CutIndex 오름차순 파트 경로.
    /// 취소/실패/진행 중 작업이 하나라도 있거나 산출물이 원격 URL·누락이면 false.
    /// </summary>
    public static bool TryCollectCompletedGroup(IReadOnlyList<BatchRenderJob> jobs, string? groupId, out string[] parts)
    {
        parts = Array.Empty<string>();
        if (!TryCollectCompletedCuts(jobs, groupId, out var cuts)) return false;
        parts = new string[cuts.Length];
        for (var i = 0; i < parts.Length; i++) parts[i] = cuts[i].VideoPath;
        return true;
    }

    /// <summary><see cref="TryCollectCompletedGroup"/> + 컷 길이·대본(<see cref="MetaScript"/>) — 오디오 믹싱/자막용.</summary>
    public static bool TryCollectCompletedCuts(IReadOnlyList<BatchRenderJob> jobs, string? groupId, out StitchCut[] cuts)
    {
        cuts = Array.Empty<StitchCut>();
        if (string.IsNullOrEmpty(groupId)) return false;
        var picked = new List<BatchRenderJob>(8);
        for (var i = 0; i < jobs.Count; i++)
        {
            var j = jobs[i];
            if (!InGroup(j, groupId)) continue;
            if (j.Status != BatchJobStatus.Completed || LocalOutputOf(j) is null) return false;
            picked.Add(j);
        }
        if (picked.Count == 0) return false;
        picked.Sort(static (a, b) => (a.CutIndex ?? int.MaxValue).CompareTo(b.CutIndex ?? int.MaxValue));
        cuts = new StitchCut[picked.Count];
        for (var i = 0; i < cuts.Length; i++)
        {
            var j = picked[i];
            var script = j.Metadata is { } m && m.TryGetValue(MetaScript, out var sc) ? sc : null;
            cuts[i] = new StitchCut(LocalOutputOf(j)!, j.DurationSec, script);
        }
        return true;
    }

    /// <summary>기존 메타 복사 + 그룹 키 + 대본(빈 대본은 키 생략).</summary>
    public static Dictionary<string, string> WithGroup(IReadOnlyDictionary<string, string>? meta, string groupId, string? script)
    {
        var d = WithGroup(meta, groupId);
        if (!string.IsNullOrWhiteSpace(script)) d[MetaScript] = script.Trim();
        return d;
    }

    /// <summary>작업 산출물 로컬 경로(OutputPath 우선 → ResultPath) — 실존 파일만.</summary>
    public static string? LocalOutputOf(BatchRenderJob job)
    {
        var p = !string.IsNullOrWhiteSpace(job.OutputPath) && File.Exists(job.OutputPath) ? job.OutputPath
              : !string.IsNullOrWhiteSpace(job.ResultPath) && !job.ResultPath.Contains("://", StringComparison.Ordinal) && File.Exists(job.ResultPath) ? job.ResultPath
              : null;
        return p is null ? null : Path.GetFullPath(p);
    }

    /// <summary><c>{dir}\{project}_full.mp4</c> — 파일명 금지 문자는 '_' 치환, 빈 이름은 "storyboard".</summary>
    public static string BuildFullOutputPath(string dir, string? projectName)
    {
        var name = string.IsNullOrWhiteSpace(projectName) ? "storyboard" : projectName.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return Path.Combine(dir, sb.ToString() + FullSuffix);
    }

    /// <summary>concat demuxer 목록 — <c>file 'C:/a/b.mp4'</c>(작은따옴표는 <c>'\''</c> 이스케이프).</summary>
    public static string BuildConcatList(IReadOnlyList<string> parts)
    {
        var sb = new StringBuilder(parts.Count * 64);
        for (var i = 0; i < parts.Count; i++)
            sb.Append("file '").Append(parts[i].Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
        return sb.ToString();
    }

    public static string[] BuildConcatArgs(string listPath, string outputPath) =>
    [
        "-y", "-hide_banner", "-loglevel", "error",
        "-f", "concat", "-safe", "0", "-i", listPath,
        "-c", "copy", "-movflags", "+faststart", outputPath,
    ];

    /// <summary>
    /// 이기종 해상도 재인코딩 합본 인자 — 컷별 <c>scale=W:H:force_original_aspect_ratio=decrease,pad=W:H:(ow-iw)/2:(oh-ih)/2,setsar=1</c>
    /// 레터박스 후 concat 필터 → <c>libx264</c>/<c>aac</c>. <paramref name="includeAudio"/>=false 면 <c>anullsrc</c> 무음 트랙(aac) 부여.
    /// </summary>
    public static string[] BuildTranscodeConcatArgs(IReadOnlyList<string> parts, int width, int height, bool includeAudio, string outputPath)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var w = (width & ~1).ToString(inv);
        var h = (height & ~1).ToString(inv);
        var args = new List<string>(32 + parts.Count * 2) { "-y", "-hide_banner", "-loglevel", "error" };
        for (var i = 0; i < parts.Count; i++) { args.Add("-i"); args.Add(parts[i]); }
        if (!includeAudio) { args.Add("-f"); args.Add("lavfi"); args.Add("-i"); args.Add("anullsrc=r=44100:cl=stereo"); }

        var f = new StringBuilder(parts.Count * 160);
        for (var i = 0; i < parts.Count; i++)
        {
            var n = i.ToString(inv);
            f.Append('[').Append(n).Append(":v]scale=").Append(w).Append(':').Append(h).Append(":force_original_aspect_ratio=decrease,pad=")
             .Append(w).Append(':').Append(h).Append(":(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p[v").Append(n).Append("];");
            if (includeAudio)
                f.Append('[').Append(n).Append(":a]aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo[a").Append(n).Append("];");
        }
        for (var i = 0; i < parts.Count; i++)
        {
            var n = i.ToString(inv);
            f.Append("[v").Append(n).Append(']');
            if (includeAudio) f.Append("[a").Append(n).Append(']');
        }
        f.Append("concat=n=").Append(parts.Count.ToString(inv)).Append(":v=1:a=").Append(includeAudio ? '1' : '0').Append("[vout]");
        if (includeAudio) f.Append("[aout]");
        args.Add("-filter_complex"); args.Add(f.ToString());
        args.AddRange(new[] { "-map", "[vout]", "-map", includeAudio ? "[aout]" : parts.Count.ToString(inv) + ":a" });
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k" });
        if (!includeAudio) args.Add("-shortest");
        args.AddRange(new[] { "-movflags", "+faststart", outputPath });
        return args.ToArray();
    }

    /// <summary>재인코딩 기준 — 최대 해상도(픽셀 면적) 컷 · 오디오는 전 컷 보유 시만 · 프로브 불명이면 1920x1080 무음.
    /// Mixed = 알려진 해상도가 2종 이상(사전 감지 → copy 생략).</summary>
    public static (int Width, int Height, bool AllAudio, bool Mixed) PlanTranscode(IReadOnlyList<StitchProbe?> probes)
    {
        int w = 0, h = 0, fw = 0, fh = 0;
        var allAudio = probes.Count > 0;
        var mixed = false;
        for (var i = 0; i < probes.Count; i++)
        {
            var p = probes[i];
            if (p is null || p.Width <= 0 || p.Height <= 0) { allAudio = false; continue; }
            if (!p.HasAudio) allAudio = false;
            if (fw == 0) { fw = p.Width; fh = p.Height; }
            else if (p.Width != fw || p.Height != fh) mixed = true;
            if ((long)p.Width * p.Height > (long)w * h) { w = p.Width; h = p.Height; }
        }
        return w == 0 ? (FallbackWidth, FallbackHeight, false, false) : (w, h, allAudio, mixed);
    }

    /// <summary>ffprobe <c>-show_entries stream=codec_type,width,height -of csv=p=0</c> 출력 파싱 — 첫 video 해상도 · audio 유무.</summary>
    public static StitchProbe? ParseProbeOutput(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        int w = 0, h = 0;
        var audio = false;
        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = raw.Split(',', StringSplitOptions.TrimEntries);
            if (t[0] == "audio") audio = true;
            else if (t[0] == "video" && w == 0 && t.Length >= 3
                && int.TryParse(t[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pw)
                && int.TryParse(t[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ph)
                && pw > 0 && ph > 0) { w = pw; h = ph; }
        }
        return w == 0 ? null : new StitchProbe(w, h, audio);
    }

    /// <summary>파트들을 단일 MP4 로 무손실 병합. ffmpeg 부재면 가상 병합 매니페스트.</summary>
    public async Task<VideoStitchResult> StitchAsync(IReadOnlyList<string> parts, string outputPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (string.IsNullOrWhiteSpace(outputPath)) return new(false, false, null, 0, "출력 경로가 비어 있습니다.");
        if (parts.Count == 0) return new(false, false, null, 0, "합칠 컷이 없습니다.");
        for (var i = 0; i < parts.Count; i++)
            if (string.IsNullOrWhiteSpace(parts[i]) || !File.Exists(parts[i]))
                return new(false, false, null, parts.Count, "컷 파일 누락: " + parts[i]);

        var full = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var ffmpeg = SafeResolve();
        if (ffmpeg is null) return WriteVirtualManifest(parts, full);

        var list = full + ".concat.txt";
        var count = parts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var probes = new StitchProbe?[parts.Count];
            for (var i = 0; i < probes.Length; i++) probes[i] = await SafeProbeAsync(ffmpeg, parts[i], ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return new(false, false, null, parts.Count, "취소됨");
            var plan = PlanTranscode(probes);

            if (!plan.Mixed)
            {
                File.WriteAllText(list, BuildConcatList(parts), new UTF8Encoding(false));
                var ok = await _run(ffmpeg, BuildConcatArgs(list, full), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return new(false, false, null, parts.Count, "취소됨");
                if (ok && File.Exists(full) && new FileInfo(full).Length > 0)
                    return new(true, false, full, parts.Count, count + "컷 합본 완료");
                TryDelete(full);
            }

            // 해상도/코덱 불일치 — 최대 해상도 레터박스 재인코딩 폴백
            var re = await _run(ffmpeg, BuildTranscodeConcatArgs(parts, plan.Width, plan.Height, plan.AllAudio, full), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return new(false, false, null, parts.Count, "취소됨");
            if (!re || !File.Exists(full) || new FileInfo(full).Length == 0)
                return new(false, false, null, parts.Count, "ffmpeg 합본 실패(스트림 복사·재인코딩 모두 실패).");
            return new(true, false, full, parts.Count, count + "컷 합본 완료(해상도 통일 재인코딩 " + (plan.Width & ~1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" + (plan.Height & ~1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("Stitch.Concat", ex);
            return new(false, false, null, parts.Count, "합본 실패: " + ex.Message);
        }
        finally { TryDelete(list); }
    }

    /// <summary>
    /// 컷 합본 + 대본 음성 믹싱 + 자막 — 합본(<see cref="StitchAsync"/>) 성공 후, 실제 합본이고 대본이 하나라도 있으면 컷별 TTS WAV 를
    /// 컷 길이에 맞춰 AAC 트랙으로 믹싱(원본 자리 교체 · 실패 시 무음 합본 유지), 합본 옆 <c>.srt</c> 기록(가상 병합 포함).
    /// </summary>
    public async Task<VideoStitchResult> StitchCutsAsync(IReadOnlyList<StitchCut> cuts, string outputPath, ITtsSynthesisService? tts, SubtitleGeneratorService? subtitles, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        var parts = new string[cuts.Count];
        for (var i = 0; i < parts.Length; i++) parts[i] = cuts[i].VideoPath;
        var r = await StitchAsync(parts, outputPath, ct).ConfigureAwait(false);
        if (!r.Ok || ct.IsCancellationRequested) return r;

        var muxed = false;
        if (!r.IsVirtual && tts is not null && r.OutputPath is not null && HasAnyScript(cuts))
            muxed = await MuxNarrationAsync(r.OutputPath, cuts, tts, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return r with { Ok = false, Message = "취소됨" };

        string? srt = null;
        if (subtitles is not null)
        {
            var subCuts = new SubtitleCut[cuts.Count];
            for (var i = 0; i < subCuts.Length; i++) subCuts[i] = new SubtitleCut(cuts[i].DurationSec, cuts[i].Script);
            srt = subtitles.WriteSrt(subCuts, SubtitleGeneratorService.SrtPathFor(Path.GetFullPath(outputPath)));
        }
        var msg = r.Message;
        if (muxed) msg += " · 대본 음성 믹싱(AAC)";
        if (srt is not null) msg += " · 자막 " + Path.GetFileName(srt);
        return r with { Message = msg, SubtitlePath = srt, AudioMuxed = muxed };
    }

    /// <summary>
    /// 오디오 믹싱 인자 — 입력 0 = 합본 영상, 1..N = 컷 WAV. 컷별 <c>apad,atrim=0:{길이}</c> 로 컷 경계 정렬 후 concat →
    /// <c>-map 0:v:0 -map [aout] -c:v copy -c:a aac -shortest</c>(영상 무손실).
    /// </summary>
    public static string[] BuildAudioMuxArgs(string videoPath, IReadOnlyList<string> wavs, IReadOnlyList<double> durations, string outputPath)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var args = new List<string>(24 + wavs.Count * 2) { "-y", "-hide_banner", "-loglevel", "error", "-i", videoPath };
        for (var i = 0; i < wavs.Count; i++) { args.Add("-i"); args.Add(wavs[i]); }
        var f = new StringBuilder(wavs.Count * 112);
        for (var i = 0; i < wavs.Count; i++)
        {
            var d = durations[i] > 0 ? durations[i] : 0.1;
            f.Append('[').Append((i + 1).ToString(inv)).Append(":a]aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=0:")
             .Append(d.ToString("0.###", inv)).Append(",asetpts=N/SR/TB[a").Append(i.ToString(inv)).Append("];");
        }
        for (var i = 0; i < wavs.Count; i++) f.Append("[a").Append(i.ToString(inv)).Append(']');
        f.Append("concat=n=").Append(wavs.Count.ToString(inv)).Append(":v=0:a=1[aout]");
        args.Add("-filter_complex"); args.Add(f.ToString());
        args.AddRange(new[] { "-map", "0:v:0", "-map", "[aout]", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", outputPath });
        return args.ToArray();
    }

    private static bool HasAnyScript(IReadOnlyList<StitchCut> cuts)
    {
        for (var i = 0; i < cuts.Count; i++) if (!string.IsNullOrWhiteSpace(cuts[i].Script)) return true;
        return false;
    }

    /// <summary>컷별 TTS(대본 없음/실패 → 컷 길이 무음) → ffmpeg 믹싱 → 합본 자리 교체. 실패 격리(false · 원본 유지).</summary>
    private async Task<bool> MuxNarrationAsync(string videoPath, IReadOnlyList<StitchCut> cuts, ITtsSynthesisService tts, CancellationToken ct)
    {
        var ffmpeg = SafeResolve();
        if (ffmpeg is null) return false;
        var tmpDir = Path.Combine(Path.GetTempPath(), TtsTempPrefix + Guid.NewGuid().ToString("N"));
        var muxOut = videoPath + ".mux.mp4";
        try
        {
            Directory.CreateDirectory(tmpDir);
            var wavs = new string[cuts.Count];
            var durs = new double[cuts.Count];
            for (var i = 0; i < cuts.Count; i++)
            {
                durs[i] = cuts[i].DurationSec > 0 ? cuts[i].DurationSec : 0.1;
                wavs[i] = Path.Combine(tmpDir, "cut_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".wav");
                var ok = !string.IsNullOrWhiteSpace(cuts[i].Script) && await tts.SynthesizeToFileAsync(cuts[i].Script!, wavs[i], ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return false;
                if (!ok && !LocalTtsSynthesisService.WriteMockWav(wavs[i], durs[i])) return false;
            }
            var ran = await _run(ffmpeg, BuildAudioMuxArgs(videoPath, wavs, durs, muxOut), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || !ran || !File.Exists(muxOut) || new FileInfo(muxOut).Length == 0) return false;
            File.Move(muxOut, videoPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("Stitch.AudioMux", ex);
            return false;
        }
        finally
        {
            TryDelete(muxOut);
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 커머셜 로고 PNG overlay(세이프존) + 디스클레이머 drawtext 번인 → <paramref name="outputPath"/>.
    /// 로고·디스클레이머 모두 없으면 무동작(Ok=false). ffmpeg 부재면 IsVirtual=true(원본 유지).
    /// </summary>
    public async Task<VideoStitchResult> BurnInOverlayAsync(string inputPath, string? logoPath, string? disclaimer, WatermarkPlacement placement, string outputPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)) return new(false, false, null, 0, "원본 영상 없음");
        var logo = !string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath) ? Path.GetFullPath(logoPath) : null;
        var text = string.IsNullOrWhiteSpace(disclaimer) ? null : disclaimer.Trim();
        if (logo is null && text is null) return new(false, false, null, 0, "합성할 로고/디스클레이머 없음");

        var ffmpeg = SafeResolve();
        if (ffmpeg is null) return new(false, true, inputPath, 1, "ffmpeg 부재 — 로고/자막 합성 생략(원본 유지)");

        var full = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // drawtext 는 textfile 로 전달 — 따옴표·콜론·% 이스케이프 회피.
        var textFile = text is null ? null : Path.Combine(Path.GetTempPath(), "pfs_disclaimer_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            if (textFile is not null) File.WriteAllText(textFile, text, new UTF8Encoding(false));
            var args = CommercialBrandService.BuildOverlayArgs(Path.GetFullPath(inputPath), logo, textFile, PromptFlowStudio.Services.Media.FontResolver.FindKoreanFont(bold: true), placement, full);
            var ok = await _run(ffmpeg, args, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return new(false, false, null, 1, "취소됨");
            if (!ok || !File.Exists(full) || new FileInfo(full).Length == 0) return new(false, false, null, 1, "ffmpeg 로고/자막 합성 실패");
            return new(true, false, full, 1, "로고/디스클레이머 번인 완료");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("Stitch.Overlay", ex);
            return new(false, false, null, 1, "합성 실패: " + ex.Message);
        }
        finally { if (textFile is not null) TryDelete(textFile); }
    }

    private static VideoStitchResult WriteVirtualManifest(IReadOnlyList<string> parts, string outputPath)
    {
        var manifest = outputPath + VirtualManifestExt;
        try
        {
            var json = JsonSerializer.Serialize(new { output = outputPath, parts, createdAt = DateTimeOffset.Now, reason = "ffmpeg-unavailable" });
            var tmp = manifest + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, manifest, overwrite: true);
            return new(true, true, manifest, parts.Count, "ffmpeg 부재 — 가상 병합 매니페스트 기록(" + parts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "컷)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("Stitch.Virtual", ex);
            return new(false, true, null, parts.Count, "가상 병합 실패: " + ex.Message);
        }
    }

    private static bool InGroup(BatchRenderJob job, string groupId)
        => job.Metadata is { } m && m.TryGetValue(MetaGroup, out var g) && string.Equals(g, groupId, StringComparison.Ordinal);

    private string? SafeResolve()
    {
        try { return _resolveFfmpeg(); }
        catch (Exception ex) { Program.Log("Stitch.Resolve", ex); return null; }
    }

    /// <summary><see cref="FfmpegLocator"/> — tools/ffmpeg/bin → tools → 앱 폴더 → <see cref="CliPathResolver"/>(Tools 번들 → 로컬앱데이터 → PATH).</summary>
    private static string? DefaultResolveFfmpeg() => FfmpegLocator.Locate().Path;

    private async Task<StitchProbe?> SafeProbeAsync(string ffmpeg, string part, CancellationToken ct)
    {
        try { return await _probe(ffmpeg, part, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { Program.Log("Stitch.Probe", ex); return null; }
    }

    /// <summary>ffmpeg 옆 ffprobe(없으면 PATH) — 실패/부재는 null(copy 시도 → 실패 시 재인코딩 폴백).</summary>
    private static async Task<StitchProbe?> DefaultProbeAsync(string ffmpeg, string part, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(ffmpeg);
        var name = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        var probe = !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : name;
        var r = await ProcessRunner.RunAsync(probe, ["-v", "error", "-show_entries", "stream=codec_type,width,height", "-of", "csv=p=0", part], ct: ct).ConfigureAwait(false);
        return r.Ok ? ParseProbeOutput(r.StdOut) : null;
    }

    private static async Task<bool> DefaultRunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
        return r.Ok && r.ExitCode == 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
