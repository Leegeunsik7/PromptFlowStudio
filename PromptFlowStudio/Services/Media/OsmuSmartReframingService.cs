using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.15.0 OSMU 출력 대상.</summary>
public enum OsmuTarget { YouTubeShorts, InstagramReels, TikTok, InstagramFeed, Pinterest, NaverClip }

/// <summary>v16.15.0 재구성 방식 — 블러 배경(원본 전체 보존 + 흐린 확대 배경) · 센터 프레이밍(초점 기준 꽉 찬 크롭).</summary>
public enum OsmuFraming { BlurBackground, CenterCrop }

/// <summary>v16.15.0 대상 규격 — 폴더/파일 키 · 표시명 · 해상도 · 종횡비 · 메타데이터 채널.</summary>
public sealed record OsmuTargetSpec(OsmuTarget Target, string Key, string Label, string Icon, int Width, int Height, string Aspect, SocialChannel Channel);

/// <summary>
/// [PromptFlowStudio] v16.15.0 🌐 OSMU 스마트 멀티 종횡비 재구성 엔진 — 완성 영상 1개 → 9:16 / 1:1 / 4:5 플랫폼 규격(ffmpeg filter_complex · 순수 BCL).
///   ▸ BlurBackground: split → 배경(1/4 축소 → 꽉 찬 크롭 → gblur → 원 해상도 확대 · 살짝 어둡게) + 전경(비율 유지 축소) → 중앙 overlay.
///     축소 후 블러라 1080p 블러 비용 1/16(실시간 근접).
///   ▸ CenterCrop: 꽉 차게 확대 후 초점(FocusX/FocusY 0~1, 기본 중앙·살짝 위 0.45 — 얼굴/주체 상단 편중 보정) 기준 크롭.
///   ▸ 출력: libx264 CRF 20 · yuv420p · 원본 오디오 aac 재인코딩(없으면 생략, <c>0:a?</c>) · +faststart. 인자는 리스트(공백 경로 안전).
/// </summary>
public sealed class OsmuSmartReframingService
{
    public const double DefaultFocusY = 0.45;

    public static readonly IReadOnlyList<OsmuTargetSpec> Specs = new[]
    {
        new OsmuTargetSpec(OsmuTarget.YouTubeShorts,  "youtube_shorts",  "YouTube Shorts",  "▶️", 1080, 1920, "9:16", SocialChannel.YouTube),
        new OsmuTargetSpec(OsmuTarget.InstagramReels, "instagram_reels", "Instagram Reels", "📸", 1080, 1920, "9:16", SocialChannel.Instagram),
        new OsmuTargetSpec(OsmuTarget.TikTok,         "tiktok",          "TikTok",          "🎵", 1080, 1920, "9:16", SocialChannel.TikTok),
        new OsmuTargetSpec(OsmuTarget.InstagramFeed,  "instagram_feed",  "Instagram Feed",  "🟪", 1080, 1080, "1:1",  SocialChannel.Instagram),
        new OsmuTargetSpec(OsmuTarget.Pinterest,      "pinterest",       "Pinterest",       "📌", 1080, 1350, "4:5",  SocialChannel.Pinterest),
        new OsmuTargetSpec(OsmuTarget.NaverClip,      "naver_clip",      "Naver Clip",      "🟢", 1080, 1350, "4:5",  SocialChannel.NaverClip),
    };

    public static OsmuTargetSpec SpecOf(OsmuTarget t)
    {
        foreach (var s in Specs) if (s.Target == t) return s;
        return Specs[0];
    }

    private readonly Func<string?> _ffmpeg;

    public OsmuSmartReframingService(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    /// <param name="ffmpegPath">ffmpeg 경로 공급자(테스트 주입용).</param>
    public OsmuSmartReframingService(Func<string?> ffmpegPath) => _ffmpeg = ffmpegPath;

    public bool IsAvailable => _ffmpeg() is not null;

    /// <summary>재구성 필터 그래프(출력 라벨 [v] · 순수 함수).</summary>
    public static string BuildFilter(int width, int height, OsmuFraming framing, double focusX = 0.5, double focusY = DefaultFocusY)
    {
        var w = Even(width);
        var h = Even(height);
        var sb = new StringBuilder(320);
        if (framing == OsmuFraming.CenterCrop)
        {
            sb.Append("[0:v]scale=").Append(w).Append(':').Append(h).Append(":force_original_aspect_ratio=increase,crop=")
              .Append(w).Append(':').Append(h).Append(":(iw-ow)*").Append(Inv(Math.Clamp(focusX, 0, 1)))
              .Append(":(ih-oh)*").Append(Inv(Math.Clamp(focusY, 0, 1))).Append(",setsar=1,format=yuv420p[v]");
            return sb.ToString();
        }
        var bw = Even(w / 4);
        var bh = Even(h / 4);
        sb.Append("[0:v]split=2[bg][fg];")
          .Append("[bg]scale=").Append(bw).Append(':').Append(bh).Append(":force_original_aspect_ratio=increase,crop=").Append(bw).Append(':').Append(bh)
          .Append(",gblur=sigma=12,scale=").Append(w).Append(':').Append(h).Append(",eq=brightness=-0.06:saturation=0.9[bgb];")
          .Append("[fg]scale=").Append(w).Append(':').Append(h).Append(":force_original_aspect_ratio=decrease,setsar=1[fgs];")
          .Append("[bgb][fgs]overlay=(W-w)/2:(H-h)/2,setsar=1,format=yuv420p[v]");
        return sb.ToString();
    }

    /// <summary>ffmpeg 인자(순수 함수 · 파일 접근 0).</summary>
    public static List<string> BuildArgs(string input, string output, OsmuTargetSpec spec, OsmuFraming framing,
        double focusX = 0.5, double focusY = DefaultFocusY)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("입력 영상이 비었습니다.", nameof(input));
        return new List<string>(32)
        {
            "-y", "-hide_banner", "-nostdin", "-i", input,
            "-filter_complex", BuildFilter(spec.Width, spec.Height, framing, focusX, focusY),
            "-map", "[v]", "-map", "0:a?",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
            "-movflags", "+faststart", "-progress", "pipe:1", output,
        };
    }

    /// <summary>재구성 실행(예외 0). durationSeconds 는 진행률 분모(모르면 0 → 완료 시 1).</summary>
    public async Task<MixResult> ReframeAsync(string input, string output, OsmuTargetSpec spec, OsmuFraming framing,
        double durationSeconds = 0, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var exe = _ffmpeg();
        if (exe is null) return new(false, null, "ffmpeg.exe 를 찾을 수 없습니다.");
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) return new(false, null, "원본 영상이 없습니다.");
        return await FfmpegVideoMixerService.RunAsync(exe, BuildArgs(input, output, spec, framing), output, durationSeconds, progress, ct).ConfigureAwait(false);
    }

    private static int Even(int v) => Math.Max(2, v & ~1);
    private static string Inv(double v) => FfmpegVideoMixerService.Inv(v);
}

/// <summary>v16.15.0 OSMU 패키징 요청.</summary>
public sealed record OsmuExportRequest(
    string MasterVideoPath, IReadOnlyList<OsmuTarget> Targets, OsmuFraming Framing = OsmuFraming.BlurBackground,
    bool Zip = false, bool UseAi = true, string? OutputRoot = null, double DurationSeconds = 0, string? ThemeCategory = null);

/// <summary>v16.15.0 대상 1건 결과.</summary>
public sealed record OsmuExportItem(OsmuTarget Target, bool Ok, string? VideoPath, string? MetadataPath, string Message);

/// <summary>v16.15.0 패키징 결과 — 출력(폴더 또는 ZIP) · 대상별 결과 · 메타데이터 출처.</summary>
public sealed record OsmuExportResult(bool Ok, string? OutputPath, IReadOnlyList<OsmuExportItem> Items, string Message);

/// <summary>
/// [PromptFlowStudio] v16.15.0 📦 OSMU 다중 플랫폼 패키저 — [AI 메타데이터] → [대상별 재구성 MP4 + 메타데이터 .txt] → manifest.json → (선택) ZIP.
///   ▸ 레이아웃: <c>OSMU_yyyyMMdd_HHmmss/{key}_{aspect}/{key}.mp4 · {key}_metadata.txt</c> + 루트 <c>osmu_manifest.json</c>.
///   ▸ 대상 실패는 건너뛰고 계속([부분 구제]) — 1건 이상 성공이면 Ok. ZIP 은 성공 후 폴더를 압축하고 원 폴더 정리.
///   ▸ 동시 실행 1건(재진입 차단) · 취소/실패는 결과 record 로 반환.
/// </summary>
public sealed class OsmuExportPackager(OsmuSmartReframingService reframe, SocialMetadataAiGenerator metadata)
{
    private int _busy;

    public bool IsAvailable => reframe.IsAvailable;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool AiEnabled => metadata.AiEnabled;

    public async Task<OsmuExportResult> ExportAsync(StoryboardProject project, OsmuExportRequest req,
        IProgress<PostProductionProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(req);
        if (Interlocked.Exchange(ref _busy, 1) != 0) return Fail("이미 OSMU 내보내기가 진행 중입니다.");
        string? root = null;
        try
        {
            if (!reframe.IsAvailable) return Fail("ffmpeg.exe 를 찾을 수 없어 OSMU 내보내기를 할 수 없습니다.");
            if (string.IsNullOrWhiteSpace(req.MasterVideoPath) || !File.Exists(req.MasterVideoPath)) return Fail("원본 완성 영상이 없습니다 — 먼저 최종 영상을 내보내세요.");
            var targets = Distinct(req.Targets);
            if (targets.Count == 0) return Fail("내보낼 플랫폼을 1개 이상 선택하세요.");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            root = req.OutputRoot ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(req.MasterVideoPath))!, "OSMU_" + stamp);
            Directory.CreateDirectory(root);

            progress?.Report(new(2, "🏷️ 플랫폼별 메타데이터 생성 중…"));
            var channels = new List<SocialChannel>(targets.Count);
            foreach (var t in targets)
            {
                var c = OsmuSmartReframingService.SpecOf(t).Channel;
                if (!channels.Contains(c)) channels.Add(c);
            }
            var meta = await metadata.GenerateAsync(SocialMetadataAiGenerator.FromProject(project, req.ThemeCategory), channels, project, req.UseAi, ct).ConfigureAwait(false);

            var items = new List<OsmuExportItem>(targets.Count);
            var ok = 0;
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var spec = OsmuSmartReframingService.SpecOf(targets[i]);
                var from = 10 + 85 * i / targets.Count;
                var to = 10 + 85 * (i + 1) / targets.Count;
                var stage = spec.Icon + " " + spec.Label + " " + spec.Aspect + " 재구성 중…";
                progress?.Report(new(from, stage));
                var dir = Path.Combine(root, FolderOf(spec));
                var video = Path.Combine(dir, spec.Key + ".mp4");
                var r = await reframe.ReframeAsync(req.MasterVideoPath, video, spec, req.Framing, req.DurationSeconds, Map(progress, from, to, stage), ct).ConfigureAwait(false);
                if (!r.Ok)
                {
                    if (ct.IsCancellationRequested) break;
                    items.Add(new OsmuExportItem(spec.Target, false, null, null, r.Message));
                    continue;
                }
                var txt = Path.Combine(dir, spec.Key + "_metadata.txt");
                var header = spec.Icon + " " + spec.Label + " · " + spec.Aspect + " · " + spec.Width.ToString(CultureInfo.InvariantCulture) + "x" + spec.Height.ToString(CultureInfo.InvariantCulture);
                await File.WriteAllTextAsync(txt, SocialMetadataAiGenerator.ToText(meta.Posts[spec.Channel], header), new UTF8Encoding(true), ct).ConfigureAwait(false);
                items.Add(new OsmuExportItem(spec.Target, true, video, txt, "완료"));
                ok++;
            }
            if (ct.IsCancellationRequested) return new(false, null, items, "취소됨");
            if (ok == 0) return new(false, null, items, "모든 플랫폼 재구성 실패: " + (items.Count > 0 ? items[0].Message : ""));

            await WriteManifestAsync(root, project, req, items, meta, ct).ConfigureAwait(false);
            var output = root;
            if (req.Zip)
            {
                progress?.Report(new(97, "🗜️ ZIP 패키징 중…"));
                output = root + ".zip";
                if (File.Exists(output)) File.Delete(output);
                ZipFile.CreateFromDirectory(root, output, CompressionLevel.Fastest, includeBaseDirectory: true);
                try { Directory.Delete(root, true); } catch { /* 폴더 정리 실패 무시(ZIP 은 완성) */ }
            }
            progress?.Report(new(100, "✅ OSMU 패키지 완성"));
            var msg = ok.ToString(CultureInfo.InvariantCulture) + "/" + targets.Count.ToString(CultureInfo.InvariantCulture) + " 플랫폼 · 메타데이터 " + meta.Message
                + (req.Zip ? " · ZIP" : " · 폴더");
            return new(true, output, items, msg);
        }
        catch (OperationCanceledException) { return Fail("취소됨"); }
        catch (Exception ex) { Program.Log("OsmuExport.Export", ex); return Fail("OSMU 내보내기 실패: " + ex.Message); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public static string FolderOf(OsmuTargetSpec s) => s.Key + "_" + s.Aspect.Replace(':', 'x');

    private static List<OsmuTarget> Distinct(IReadOnlyList<OsmuTarget>? src)
    {
        var list = new List<OsmuTarget>(src?.Count ?? 0);
        if (src is null) return list;
        foreach (var t in src) if (!list.Contains(t)) list.Add(t);
        return list;
    }

    private static async Task WriteManifestAsync(string root, StoryboardProject project, OsmuExportRequest req,
        List<OsmuExportItem> items, SocialMetadataResult meta, CancellationToken ct)
    {
        var entries = new List<object>(items.Count);
        foreach (var it in items)
        {
            var s = OsmuSmartReframingService.SpecOf(it.Target);
            entries.Add(new
            {
                target = s.Key, label = s.Label, aspect = s.Aspect, width = s.Width, height = s.Height, ok = it.Ok,
                video = it.VideoPath is null ? null : Path.GetRelativePath(root, it.VideoPath),
                metadata = it.MetadataPath is null ? null : Path.GetRelativePath(root, it.MetadataPath),
                title = meta.Posts[s.Channel].Title, hashtags = meta.Posts[s.Channel].Hashtags, message = it.Message,
            });
        }
        var json = JsonSerializer.Serialize(new
        {
            version = "16.15.0", project = project.Title, framing = req.Framing.ToString(), metadataSource = meta.Message,
            aiChannels = meta.AiChannels, createdAt = DateTime.Now.ToString("O", CultureInfo.InvariantCulture), items = entries,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        await File.WriteAllTextAsync(Path.Combine(root, "osmu_manifest.json"), json, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    private static OsmuExportResult Fail(string msg) => new(false, null, Array.Empty<OsmuExportItem>(), msg);

    private static IProgress<double>? Map(IProgress<PostProductionProgress>? p, int from, int to, string stage)
        => p is null ? null : new Progress01(p, from, to, stage);

    private sealed class Progress01(IProgress<PostProductionProgress> inner, int from, int to, string stage) : IProgress<double>
    {
        private int _last = -1;
        public void Report(double value)
        {
            var pct = from + (int)Math.Round(Math.Clamp(value, 0, 1) * (to - from));
            if (pct == Interlocked.Exchange(ref _last, pct)) return;
            inner.Report(new(pct, stage));
        }
    }
}
