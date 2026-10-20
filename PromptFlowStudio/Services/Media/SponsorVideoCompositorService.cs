using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.19.0 제품 배치 앵커(세이프존 기준).</summary>
public enum ProductAnchor { BottomRight, BottomLeft, BottomCenter, Center, Right, Left }

/// <summary>v16.19.0 세이프존 사각형(px).</summary>
public readonly record struct SafeRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
}

/// <summary>v16.19.0 브랜드 오버레이 — 로고 워터마크 · PPL 고지 · CTA 문구/색 · CTA 노출 비율(끝에서부터).</summary>
public sealed record BrandOverlaySpec(
    string? LogoPath = null, string? PplText = "유료 광고 포함", string? CtaText = "지금 만나 보세요", string CtaColor = "FF4F8B",
    double LogoOpacity = 0.85, double CtaTailRatio = 0.4);

/// <summary>v16.19.0 장면 1건 합성 요청 — 장면(테마) 영상 · 누끼 PNG · 출력 · 해상도 · 배치/크기 · 그림자/플로팅 · 오디오 교체(선택).</summary>
public sealed record SponsorSceneRequest(
    string SceneVideoPath, string ProductCutoutPath, string OutputPath, int Width = 1080, int Height = 1920,
    ProductAnchor Anchor = ProductAnchor.BottomRight, double ProductScale = 0.38, double DurationSeconds = 0,
    bool DropShadow = true, bool FloatMotion = true, string? AudioPath = null, string? ThemeKey = null);

/// <summary>v16.19.0 합성 레이아웃(순수 계산 결과).</summary>
public readonly record struct SponsorLayout(SafeRect Safe, int ProductW, int CtaY, int CtaH, int LogoW, int FontPpl, int FontCta);

/// <summary>
/// [PromptFlowStudio] v16.19.0 🎬 협찬 비디오 합성 &amp; 브랜드 오버레이 — 204+ 테마 장면 + 제품 누끼 + 로고 워터마크 + PPL 고지 + CTA(ffmpeg filter_complex · 순수 BCL).
///   ▸ 세이프존: 세로(9:16) 상 12% · 하 22%(숏폼 UI 버튼/캡션 회피) · 좌우 6%, 가로/정사각 5%. 모든 오버레이는 세이프존 내부.
///   ▸ 제품: 폭 = 세이프존 × ProductScale · 0.3초 알파 페이드인 · 플로팅(±6px 3초 주기 sin) · 드롭 섀도(흑색 45% gblur 오프셋).
///   ▸ 로고: 세이프존 우상단 · 폭 18% · 불투명도 조절. PPL: 좌상단 반투명 박스 고지(협찬 표시 의무). CTA: 끝 40% 구간 하단 컬러 밴드 + 중앙 문구.
///   ▸ 문구는 임시 UTF-8 textfile(한글/특수문자 이스케이프 회피) · 폰트는 <see cref="FontResolver"/> · 인자는 리스트(공백 경로 안전).
///   ▸ <see cref="BuildFilter"/>/<see cref="BuildArgs"/> 순수 함수(파일 접근 0) · 실행 실패는 <see cref="MixResult"/>(예외 0).
/// </summary>
public sealed class SponsorVideoCompositorService
{
    private readonly Func<string?> _ffmpeg;

    public SponsorVideoCompositorService(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    /// <param name="ffmpegPath">ffmpeg 경로 공급자(테스트 주입용).</param>
    public SponsorVideoCompositorService(Func<string?> ffmpegPath) => _ffmpeg = ffmpegPath;

    public bool IsAvailable => _ffmpeg() is not null;

    /// <summary>해상도별 커머셜 세이프존.</summary>
    public static SafeRect SafeZoneOf(int width, int height)
    {
        var w = Even(width);
        var h = Even(height);
        if (h > w)
        {
            var sx = Even((int)(w * 0.06));
            var top = Even((int)(h * 0.12));
            var bottom = Even((int)(h * 0.22));
            return new SafeRect(sx, top, w - sx * 2, h - top - bottom);
        }
        var m = Even((int)(Math.Min(w, h) * 0.05));
        return new SafeRect(m, m, w - m * 2, h - m * 2);
    }

    /// <summary>레이아웃 계산(순수).</summary>
    public static SponsorLayout LayoutOf(int width, int height, double productScale)
    {
        var safe = SafeZoneOf(width, height);
        var ctaH = Even(Math.Max(48, (int)(safe.H * 0.09)));
        var fontCta = Math.Max(18, ctaH * 42 / 100);
        var fontPpl = Math.Max(14, Even(height) * 22 / 1000);
        return new SponsorLayout(safe, Even((int)(safe.W * Math.Clamp(productScale, 0.1, 0.9))), safe.Bottom - ctaH, ctaH,
            Even(safe.W * 18 / 100), fontPpl, fontCta);
    }

    /// <summary>테마 시네마틱 → 권장 앵커(카메라 무빙 방향의 반대편/하단 배치로 주체 가림 최소화).</summary>
    public static ProductAnchor SuggestAnchor(ThemeCinematics? c) => c?.Camera switch
    {
        CameraMotion.CraneUp or CameraMotion.DollyZoom => ProductAnchor.BottomCenter,
        CameraMotion.SmoothPan => ProductAnchor.Right,
        CameraMotion.Orbit => ProductAnchor.BottomLeft,
        _ => ProductAnchor.BottomRight,
    };

    /// <summary>CTA 시작 시각(초) — 길이를 모르면 0(전 구간).</summary>
    public static double CtaStartOf(double duration, double tailRatio)
        => duration > 0 ? Math.Round(duration * (1 - Math.Clamp(tailRatio, 0.05, 1)), 2) : 0;

    /// <summary>
    /// 합성 필터 그래프(출력 라벨 [v] · 순수). 입력: 0=장면, 1=누끼, 2=로고(있을 때). textfile 경로가 null 이면 해당 문구 생략.
    /// </summary>
    public static string BuildFilter(SponsorSceneRequest req, BrandOverlaySpec overlay, bool hasLogo,
        string? pplTextFile, string? ctaTextFile, string? fontFile)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(overlay);
        var w = Even(req.Width);
        var h = Even(req.Height);
        var L = LayoutOf(w, h, req.ProductScale);
        var s = L.Safe;
        var hasCta = ctaTextFile is not null;
        var bottom = hasCta ? L.CtaY - 12 : s.Bottom;
        var (x, y) = req.Anchor switch
        {
            ProductAnchor.BottomLeft => (Inv(s.X), "(" + Inv(bottom) + "-h)"),
            ProductAnchor.BottomCenter => ("((W-w)/2)", "(" + Inv(bottom) + "-h)"),
            ProductAnchor.Center => ("((W-w)/2)", "((H-h)/2)"),
            ProductAnchor.Right => ("(" + Inv(s.Right) + "-w)", "((H-h)/2)"),
            ProductAnchor.Left => (Inv(s.X), "((H-h)/2)"),
            _ => ("(" + Inv(s.Right) + "-w)", "(" + Inv(bottom) + "-h)"),
        };
        var yExpr = req.FloatMotion ? y + "+6*sin(2*PI*t/3)" : y;

        var sb = new StringBuilder(1400);
        sb.Append("[0:v]scale=").Append(w).Append(':').Append(h).Append(":force_original_aspect_ratio=increase,crop=")
          .Append(w).Append(':').Append(h).Append(",setsar=1,format=yuv420p[bg];")
          .Append("[1:v]format=rgba,scale=").Append(L.ProductW).Append(":-2:flags=lanczos,fade=t=in:st=0.3:d=0.6:alpha=1");
        string cur;
        if (req.DropShadow)
        {
            sb.Append(",split=2[pd][ps];")
              .Append("[ps]colorchannelmixer=rr=0:gg=0:bb=0:aa=0.45,gblur=sigma=14[sh];")
              .Append("[bg][sh]overlay=x='").Append(x).Append("+18':y='").Append(yExpr).Append("+24':format=auto:shortest=1[b0];")
              .Append("[b0][pd]");
        }
        else sb.Append("[pd];[bg][pd]");
        sb.Append("overlay=x='").Append(x).Append("':y='").Append(yExpr).Append("':format=auto:shortest=1[b1];");
        cur = "b1";
        if (hasLogo)
        {
            sb.Append("[2:v]format=rgba,scale=").Append(L.LogoW).Append(":-2,colorchannelmixer=aa=")
              .Append(Inv(Math.Clamp(overlay.LogoOpacity, 0.1, 1))).Append("[lg];")
              .Append('[').Append(cur).Append("][lg]overlay=x=").Append(s.Right).Append("-w:y=").Append(s.Y).Append(":format=auto:shortest=1[b2];");
            cur = "b2";
        }
        sb.Append('[').Append(cur).Append(']');
        var chain = false;
        if (pplTextFile is not null)
        {
            DrawText(sb, pplTextFile, fontFile, L.FontPpl, "white", Inv(s.X), Inv(s.Y), "box=1:boxcolor=black@0.45:boxborderw=8", null);
            chain = true;
        }
        if (hasCta)
        {
            if (chain) sb.Append(',');
            var start = CtaStartOf(req.DurationSeconds, overlay.CtaTailRatio);
            var enable = start > 0 ? "gte(t," + Inv(start) + ")" : null;
            sb.Append("drawbox=x=").Append(s.X).Append(":y=").Append(L.CtaY).Append(":w=").Append(s.W).Append(":h=").Append(L.CtaH)
              .Append(":color=0x").Append(Hex(overlay.CtaColor)).Append("@0.92:t=fill");
            if (enable is not null) sb.Append(":enable='").Append(enable).Append('\'');
            sb.Append(',');
            DrawText(sb, ctaTextFile!, fontFile, L.FontCta, "white", "(w-text_w)/2", Inv(L.CtaY) + "+(" + Inv(L.CtaH) + "-text_h)/2", "shadowcolor=black@0.35:shadowx=2:shadowy=2", enable);
            chain = true;
        }
        if (!chain) sb.Append("null");
        sb.Append(",format=yuv420p[v]");
        return sb.ToString();
    }

    /// <summary>ffmpeg 인자(순수 · 파일 접근 0).</summary>
    public static List<string> BuildArgs(SponsorSceneRequest req, BrandOverlaySpec overlay, string? pplTextFile, string? ctaTextFile, string? fontFile)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.SceneVideoPath)) throw new ArgumentException("장면 영상이 비었습니다.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.ProductCutoutPath)) throw new ArgumentException("제품 누끼가 비었습니다.", nameof(req));
        var hasLogo = !string.IsNullOrWhiteSpace(overlay.LogoPath);
        var args = new List<string>(40) { "-y", "-hide_banner", "-nostdin", "-i", req.SceneVideoPath, "-loop", "1", "-i", req.ProductCutoutPath };
        var next = 2;
        if (hasLogo) { args.Add("-loop"); args.Add("1"); args.Add("-i"); args.Add(overlay.LogoPath!); next++; }
        var audioIdx = -1;
        if (!string.IsNullOrWhiteSpace(req.AudioPath)) { args.Add("-i"); args.Add(req.AudioPath!); audioIdx = next; }
        args.Add("-filter_complex"); args.Add(BuildFilter(req, overlay, hasLogo, pplTextFile, ctaTextFile, fontFile));
        args.Add("-map"); args.Add("[v]");
        args.Add("-map"); args.Add(audioIdx >= 0 ? audioIdx.ToString(CultureInfo.InvariantCulture) + ":a" : "0:a?");
        args.AddRange(new[]
        {
            "-shortest", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart", "-progress", "pipe:1", req.OutputPath,
        });
        return args;
    }

    /// <summary>장면 1건 합성 실행(예외 0).</summary>
    public async Task<MixResult> CompositeAsync(SponsorSceneRequest req, BrandOverlaySpec overlay,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(overlay);
        var exe = _ffmpeg();
        if (exe is null) return new(false, null, "ffmpeg.exe 를 찾을 수 없습니다.");
        if (!File.Exists(req.SceneVideoPath)) return new(false, null, "장면 영상이 없습니다.");
        if (!File.Exists(req.ProductCutoutPath)) return new(false, null, "제품 누끼 PNG 가 없습니다.");
        var ov = overlay.LogoPath is not null && !File.Exists(overlay.LogoPath) ? overlay with { LogoPath = null } : overlay;
        var tmp = Path.Combine(Path.GetTempPath(), "pfs_sponsor_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            var ppl = await WriteTextAsync(tmp, "ppl.txt", ov.PplText, ct).ConfigureAwait(false);
            var cta = await WriteTextAsync(tmp, "cta.txt", ov.CtaText, ct).ConfigureAwait(false);
            var font = FontResolver.FindKoreanFont(bold: true);
            var args = BuildArgs(req, ov,
                ppl is null ? null : FontResolver.EscapeDrawPath(ppl),
                cta is null ? null : FontResolver.EscapeDrawPath(cta),
                font is null ? null : FontResolver.EscapeDrawPath(font));
            return await FfmpegVideoMixerService.RunAsync(exe, args, req.OutputPath, req.DurationSeconds, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(false, null, "취소됨"); }
        catch (Exception ex) { Program.Log("SponsorCompositor.Composite", ex); return new(false, null, "협찬 합성 실패: " + ex.Message); }
        finally { try { Directory.Delete(tmp, true); } catch { /* 임시 폴더 정리 실패 무시 */ } }
    }

    private static async Task<string?> WriteTextAsync(string dir, string name, string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var p = Path.Combine(dir, name);
        await File.WriteAllTextAsync(p, text.Trim(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        return p;
    }

    private static void DrawText(StringBuilder sb, string textFile, string? fontFile, int size, string color, string x, string y, string extra, string? enable)
    {
        sb.Append("drawtext=");
        if (fontFile is not null) sb.Append("fontfile='").Append(fontFile).Append("':");
        sb.Append("textfile='").Append(textFile).Append("':fontsize=").Append(size).Append(":fontcolor=").Append(color)
          .Append(":x=").Append(x).Append(":y=").Append(y).Append(':').Append(extra);
        if (enable is not null) sb.Append(":enable='").Append(enable).Append('\'');
    }

    /// <summary>CTA 색(#RRGGBB/RRGGBB) 정규화 — 무효면 기본 핑크.</summary>
    public static string Hex(string? c)
    {
        var s = (c ?? "").Trim().TrimStart('#');
        if (s.Length != 6) return "FF4F8B";
        foreach (var ch in s) if (!char.IsAsciiHexDigit(ch)) return "FF4F8B";
        return s.ToUpperInvariant();
    }

    private static int Even(int v) => Math.Max(2, v & ~1);
    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Inv(double v) => FfmpegVideoMixerService.Inv(v);
}

/// <summary>v16.19.0 1-Click 캠페인 요청.</summary>
public sealed record BrandCampaignRequest(
    string Name, string ProductImagePath, IReadOnlyList<string> SceneVideos, AdProductBrief Brief, BrandOverlaySpec Overlay,
    AdDuration Duration = AdDuration.Sec15, AdTone Tone = AdTone.Warm, int Width = 1080, int Height = 1920,
    ProductAnchor Anchor = ProductAnchor.BottomRight, double ProductScale = 0.38, ThemeLighting? Lighting = null,
    bool UseAi = true, bool UseScriptCta = true, string? OutputRoot = null, CommercialAdScript? Script = null);

/// <summary>v16.19.0 캠페인 장면 결과.</summary>
public sealed record BrandCampaignItem(string SceneVideo, bool Ok, string? OutputPath, string Message);

/// <summary>v16.19.0 캠페인 결과(이력 항목).</summary>
public sealed record BrandCampaignResult(
    Guid Id, string Name, bool Ok, string? OutputFolder, string? CutoutPath, CommercialAdScript? Script,
    IReadOnlyList<BrandCampaignItem> Items, DateTime At, string Message)
{
    public int Succeeded { get { var n = 0; foreach (var i in Items) if (i.Ok) n++; return n; } }
}

/// <summary>
/// [PromptFlowStudio] v16.19.0 🛍️ 커머셜 캠페인 매니저 — [누끼+조명 매칭] → [CM 카피] → [장면별 협찬 합성] → 대본/manifest 저장 1-Click.
///   ▸ 레이아웃: <c>Brand_{name}_yyyyMMdd_HHmmss/product_cutout.png · ad_script.txt · scene_NN.mp4 · campaign_manifest.json</c>.
///   ▸ 장면 실패는 건너뛰고 계속([부분 구제]) — 1건 이상 성공이면 Ok. 동시 실행 1건(재진입 차단) · 이력 상한 30 · OnChange(예외 격리).
/// </summary>
public sealed class CommercialBrandCampaignService(
    CommercialProductSegmenterService segmenter, SponsorVideoCompositorService compositor, CommercialAdScriptAiGenerator scripts)
{
    public const int HistoryCap = 30;

    private readonly object _gate = new();
    private readonly List<BrandCampaignResult> _history = new(HistoryCap + 1);
    private int _busy;

    public event Action? OnChange;

    public bool IsAvailable => segmenter.IsAvailable && compositor.IsAvailable;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool AiEnabled => scripts.AiEnabled;
    public int TotalCampaigns { get; private set; }
    public int TotalScenes { get; private set; }
    public string? LastStage { get; private set; }

    public CommercialProductSegmenterService Segmenter => segmenter;
    public CommercialAdScriptAiGenerator Scripts => scripts;

    public IReadOnlyList<BrandCampaignResult> History
    {
        get { lock (_gate) return _history.ToArray(); }
    }

    public BrandCampaignResult? Last
    {
        get { lock (_gate) return _history.Count > 0 ? _history[0] : null; }
    }

    public async Task<BrandCampaignResult> RunAsync(BrandCampaignRequest req, IProgress<PostProductionProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (Interlocked.Exchange(ref _busy, 1) != 0) return Fail(req, "이미 캠페인 출력이 진행 중입니다.", record: false);
        Raise();
        string? root = null;
        try
        {
            if (!IsAvailable) return Fail(req, "ffmpeg.exe 를 찾을 수 없어 광고 비디오를 출력할 수 없습니다.");
            if (string.IsNullOrWhiteSpace(req.ProductImagePath) || !File.Exists(req.ProductImagePath)) return Fail(req, "제품 사진을 먼저 업로드하세요.");
            var scenes = new List<string>(req.SceneVideos.Count);
            foreach (var v in req.SceneVideos) if (!string.IsNullOrWhiteSpace(v) && File.Exists(v) && !scenes.Contains(v)) scenes.Add(v);
            if (scenes.Count == 0) return Fail(req, "합성할 테마 장면 영상을 1개 이상 선택하세요.");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            root = req.OutputRoot ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scenes[0]))!, "Brand_" + Safe(req.Name) + "_" + stamp);
            Directory.CreateDirectory(root);

            Stage(progress, 3, "✂️ 제품 누끼 추출 · 조명 매칭 중…");
            var cutout = Path.Combine(root, "product_cutout.png");
            var seg = await segmenter.SegmentAsync(req.ProductImagePath, cutout, scenes[0], req.Lighting, null, 0.6, ct).ConfigureAwait(false);
            if (!seg.Ok) return Fail(req, "누끼 실패: " + seg.Message);

            Stage(progress, 15, "✍️ CM 카피 · 징글 생성 중…");
            var script = req.Script ?? await scripts.GenerateAsync(req.Brief, req.Duration, req.Tone, req.UseAi, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(root, "ad_script.txt"), CommercialAdScriptAiGenerator.ToText(script, req.Brief), new UTF8Encoding(true), ct).ConfigureAwait(false);
            var overlay = req.UseScriptCta && !string.IsNullOrWhiteSpace(script.Cta) ? req.Overlay with { CtaText = script.Cta } : req.Overlay;

            var items = new List<BrandCampaignItem>(scenes.Count);
            for (var i = 0; i < scenes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var from = 20 + 75 * i / scenes.Count;
                var to = 20 + 75 * (i + 1) / scenes.Count;
                var stage = "🎬 장면 " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + scenes.Count.ToString(CultureInfo.InvariantCulture) + " 협찬 합성 중…";
                Stage(progress, from, stage);
                var outPath = Path.Combine(root, "scene_" + (i + 1).ToString("00", CultureInfo.InvariantCulture) + ".mp4");
                var sr = new SponsorSceneRequest(scenes[i], cutout, outPath, req.Width, req.Height, req.Anchor, req.ProductScale, script.TotalSeconds);
                var r = await compositor.CompositeAsync(sr, overlay, Map(progress, from, to, stage), ct).ConfigureAwait(false);
                if (!r.Ok && ct.IsCancellationRequested) break;
                items.Add(new BrandCampaignItem(scenes[i], r.Ok, r.Ok ? outPath : null, r.Ok ? "완료" : r.Message));
            }
            if (ct.IsCancellationRequested) return Fail(req, "취소됨");
            var ok = 0;
            foreach (var it in items) if (it.Ok) ok++;
            await WriteManifestAsync(root, req, seg, script, items, ct).ConfigureAwait(false);
            Stage(progress, 100, ok > 0 ? "✅ 광고 비디오 출력 완료" : "❌ 모든 장면 합성 실패");
            var msg = ok.ToString(CultureInfo.InvariantCulture) + "/" + items.Count.ToString(CultureInfo.InvariantCulture) + " 장면 · 카피 " + script.Message + " · " + seg.Message;
            var result = new BrandCampaignResult(Guid.NewGuid(), req.Name, ok > 0, root, cutout, script, items, DateTime.Now,
                ok > 0 ? msg : "모든 장면 합성 실패: " + (items.Count > 0 ? items[0].Message : ""));
            Record(result, ok);
            return result;
        }
        catch (OperationCanceledException) { return Fail(req, "취소됨"); }
        catch (Exception ex) { Program.Log("BrandCampaign.Run", ex); return Fail(req, "캠페인 출력 실패: " + ex.Message); }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Raise();
        }
    }

    public void ClearHistory()
    {
        lock (_gate) _history.Clear();
        Raise();
    }

    private void Record(BrandCampaignResult r, int scenes)
    {
        lock (_gate)
        {
            _history.Insert(0, r);
            if (_history.Count > HistoryCap) _history.RemoveAt(_history.Count - 1);
            TotalCampaigns++;
            TotalScenes += scenes;
        }
        LastStage = r.Message;
    }

    private BrandCampaignResult Fail(BrandCampaignRequest req, string msg, bool record = true)
    {
        var r = new BrandCampaignResult(Guid.NewGuid(), req.Name, false, null, null, null, Array.Empty<BrandCampaignItem>(), DateTime.Now, msg);
        if (record) { lock (_gate) { _history.Insert(0, r); if (_history.Count > HistoryCap) _history.RemoveAt(_history.Count - 1); } LastStage = msg; }
        return r;
    }

    private void Stage(IProgress<PostProductionProgress>? p, int pct, string stage)
    {
        LastStage = stage;
        p?.Report(new(pct, stage));
        Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("BrandCampaign.OnChange", ex); }
    }

    private static async Task WriteManifestAsync(string root, BrandCampaignRequest req, ProductSegmentResult seg, CommercialAdScript script,
        List<BrandCampaignItem> items, CancellationToken ct)
    {
        var scenes = new List<object>(items.Count);
        foreach (var it in items)
            scenes.Add(new { scene = Path.GetFileName(it.SceneVideo), ok = it.Ok, output = it.OutputPath is null ? null : Path.GetRelativePath(root, it.OutputPath), message = it.Message });
        var json = JsonSerializer.Serialize(new
        {
            version = "16.19.0", campaign = req.Name, brand = req.Brief.Brand, product = req.Brief.ProductName,
            duration = (int)script.Duration, headline = script.Headline, cta = script.Cta, aiScript = script.AiUsed,
            bgm = new { script.Bgm.Mood, script.Bgm.Bpm, script.Bgm.Instruments, script.Bgm.Jingle },
            mask = seg.Mask.Mode.ToString(), lighting = seg.BackgroundSource, ppl = req.Overlay.PplText,
            resolution = req.Width.ToString(CultureInfo.InvariantCulture) + "x" + req.Height.ToString(CultureInfo.InvariantCulture),
            compliance = script.ComplianceNotes, createdAt = DateTime.Now.ToString("O", CultureInfo.InvariantCulture), scenes,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        await File.WriteAllTextAsync(Path.Combine(root, "campaign_manifest.json"), json, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    public static string Safe(string? name)
    {
        var s = string.IsNullOrWhiteSpace(name) ? "Campaign" : name.Trim();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || char.IsWhiteSpace(c) ? '_' : c);
        return sb.Length > 40 ? sb.ToString(0, 40) : sb.ToString();
    }

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
