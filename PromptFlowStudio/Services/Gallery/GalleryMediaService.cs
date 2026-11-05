using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PromptFlowStudio.Services.Gallery;

public enum AspectCategory { Vertical9x16, Landscape16x9, Square1x1 }

/// <summary>갤러리 미디어 종류 — Still = <c>MediaRoot/_stills</c> 캡처 스냅샷(PNG).</summary>
public enum GalleryMediaType { Video, Image, Still }

public sealed class GalleryItem
{
    public Guid ItemId { get; init; }
    public string Title { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string ThumbnailUrl { get; init; } = "";
    public string Prompt { get; init; } = "";
    public long Seed { get; init; }
    public string Resolution { get; init; } = "1280x720";
    public AspectCategory AspectCategory { get; init; }
    public DateTime CreatedAt { get; init; }
    public float RenderDurationSec { get; init; }
    /// <summary>⏱️ 영상 재생시간(메타 <c>durationSeconds</c> · 트림 시 <see cref="GalleryMediaService.NotifyMediaMetadataChanged"/> 로 즉시 갱신) — 미상 null.</summary>
    public double? DurationSeconds { get; internal set; }
    /// <summary>I2V 첫 프레임 참조 이미지 경로(메타 <c>referenceImagePath</c>) — T2V 산출물이면 null.</summary>
    public string? ReferenceImagePath { get; init; }
    /// <summary>폴더가 비었을 때 노출하는 기본 템플릿(파일 없음 · 삭제는 목록에서만).</summary>
    public bool IsSample { get; init; }
    /// <summary>🧪 Mock 더미 산출물 — 메타 <c>isMock</c>/<c>engine=Mock</c> 또는 <c>testsrc</c> 컬러바 흔적.</summary>
    public bool IsMock { get; init; }
    /// <summary>카드 태그 — <c>🧪 Mock</c> · <c>🎬 AI Render</c>(샘플은 null).</summary>
    public string? AssetTag => IsSample ? null : IsStill ? "🖼️ 스냅샷" : IsMock ? "🧪 Mock" : IsExported ? "📦 내보냄" : "🎬 AI Render";
    /// <summary>⭐ 즐겨찾기 — 메타 <c>isFavorite</c>(토글은 <see cref="GalleryMediaService.SetFavorite"/>).</summary>
    public bool IsFavorite { get; internal set; }
    /// <summary>📦 내보낸 산출물 — 메타 <c>isExported:true</c>(멀티 패키지 익스포터 기록).</summary>
    public bool IsExported { get; init; }
    /// <summary>정적 첫 프레임 포스터(<c>{이름}.poster.jpg</c>) — 없으면 null(CSS 스틸).</summary>
    public string? PosterPath { get; init; }
    /// <summary>파일 크기(바이트) — 스토리지 대시보드 집계용(샘플 0).</summary>
    public long SizeBytes { get; init; }
    public bool IsVideo => !IsImagePath(FilePath);
    /// <summary>🖼️ <c>_stills/</c> 폴더 PNG 스냅샷.</summary>
    public bool IsStill { get; init; }
    public GalleryMediaType MediaType => IsStill ? GalleryMediaType.Still : IsVideo ? GalleryMediaType.Video : GalleryMediaType.Image;

    internal static bool IsImagePath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Workspace 프롬프트 입력 복원 번들 — <see cref="WorkspaceSize"/>는 Workspace 사이즈 셀렉터 값(<c>"1280*720"</c>) · <see cref="ReferenceImagePath"/> 가 있으면 I2V 복원.</summary>
public sealed record GalleryRestoreContext(Guid SourceItemId, string Title, string Prompt, long Seed, string Resolution, string WorkspaceSize, string? ReferenceImagePath = null);

/// <summary>
/// [PromptFlowStudio] 갤러리 메타데이터 아카이브 — 출력 폴더 미디어 탐색 · 사이드카 메타 파싱 · 안전 삭제 · Workspace 복원 핸드오프.
///   ▸ 메타데이터 규격: 미디어 옆 <c>{이름}.meta.json</c>(title/prompt/seed/resolution/createdAt/renderDurationSec/thumbnail) — 없으면 파일명·PNG 헤더로 추정.
///   ▸ 폴더가 없거나 비면 기본 샘플 세트(쇼츠 · 시네마틱 · 커머셜 · 정사각) 반환.
///   ▸ 삭제는 마지막 스캔 루트 하위 파일만 허용(경로 이탈 차단) · 샘플은 목록에서만 제거.
///   ▸ 복원 번들은 <see cref="QueueRestore"/> → Workspace 가 <see cref="TakePendingRestore"/> 로 1회 소비.
/// </summary>
public sealed class GalleryMediaService
{
    public const string MetaSuffix = ".meta.json";
    private const int MaxItems = 500;
    private static readonly string[] MediaExtensions = { ".mp4", ".webm", ".mov", ".png", ".jpg", ".jpeg", ".webp" };

    private readonly object _lock = new();
    private readonly List<GalleryItem> _items = new();
    private string? _root;
    private GalleryRestoreContext? _pendingRestore;

    private readonly Func<string?> _exportTargetDir;

    public GalleryMediaService() : this(null) { }

    /// <param name="exportTargetDirProvider">StockSuite 에셋 폴더 공급자 — 스캔 시 역방향 치유(이미 내보낸 원본에 <c>isExported</c> 보정) 대상.</param>
    public GalleryMediaService(Func<string?>? exportTargetDirProvider)
        => _exportTargetDir = exportTargetDirProvider ?? (static () => null);

    public IReadOnlyList<GalleryItem> Items { get { lock (_lock) return _items.ToArray(); } }
    public bool HasPendingRestore => Volatile.Read(ref _pendingRestore) is not null;
    /// <summary>마지막 스캔에서 역방향 치유로 <c>isExported</c> 가 보정된 원본 수.</summary>
    public int LastHealedCount { get; private set; }

    public IReadOnlyList<GalleryItem> ScanGalleryItems(string directoryPath)
    {
        var found = new List<GalleryItem>();
        string? root = null;
        var healed = 0;
        try
        {
            if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
            {
                root = Path.GetFullPath(directoryPath);
                var exported = ExportedTargetIndex.TryBuild(SafeTargetDir());
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!IsMediaFile(file) || IsInUploads(root, file) || file.EndsWith(PosterSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    // 🩹 역방향 치유 — StockSuite 산출물과 엄격 일치(출처 마커 경로 · 매니페스트 SHA-256 · 내용 SHA-256)할 때만 메타 보정. 파일명 매칭 금지(오탐).
                    if (exported?.Match(file) is { } target && !Export.ExportMetadata.IsMarkedExported(file)
                        && Export.ExportMetadata.MarkExported(file, new[] { target }, DateTimeOffset.UtcNow)) healed++;
                    var item = TryLoadItem(file, IsStillSnapshot(root, file));
                    if (item is not null) found.Add(item);
                }
            }
        }
        catch (Exception ex) { Program.Log("GalleryMedia.Scan", ex); }   // [부분 구제] 열거 중단 전까지 수집분 유지

        found.Sort(static (a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        if (found.Count > MaxItems) found.RemoveRange(MaxItems, found.Count - MaxItems);
        if (found.Count == 0) found.AddRange(CreateSampleItems());

        LastHealedCount = healed;
        lock (_lock)
        {
            _root = root;
            _items.Clear();
            _items.AddRange(found);
            return _items.ToArray();
        }
    }

    public bool DeleteGalleryItem(Guid itemId)
    {
        GalleryItem? target = null;
        string? root;
        lock (_lock)
        {
            root = _root;
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].ItemId == itemId) { target = _items[i]; break; }
        }
        if (target is null || !TryDeleteFiles(root, target)) return false;
        lock (_lock) return _items.Remove(target);
    }

    /// <summary>체크박스 다중 선택 삭제 — 성공 개수 반환([부분 구제] 실패 항목은 목록 유지).</summary>
    public int DeleteGalleryItems(IEnumerable<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = new HashSet<Guid>(itemIds);
        if (ids.Count == 0) return 0;
        return DeleteWhere(it => ids.Contains(it.ItemId));
    }

    /// <summary>🧹 Mock 더미 일괄 삭제 — <see cref="GalleryItem.IsMock"/> 미디어 + 사이드카 메타 영구 삭제. 성공 개수 반환.</summary>
    public int DeleteMockAssets() => DeleteWhere(static it => it.IsMock && !it.IsSample);

    public int MockCount
    {
        get
        {
            lock (_lock)
            {
                int n = 0;
                for (int i = 0; i < _items.Count; i++) if (_items[i].IsMock && !_items[i].IsSample) n++;
                return n;
            }
        }
    }

    private int DeleteWhere(Predicate<GalleryItem> match)
    {
        GalleryItem[] targets;
        string? root;
        lock (_lock)
        {
            root = _root;
            targets = _items.FindAll(match).ToArray();
        }
        var deleted = new List<GalleryItem>(targets.Length);
        foreach (var t in targets)
            if (TryDeleteFiles(root, t)) deleted.Add(t);
        if (deleted.Count > 0)
            lock (_lock) foreach (var d in deleted) _items.Remove(d);
        return deleted.Count;
    }

    private static bool TryDeleteFiles(string? root, GalleryItem target)
    {
        if (target.IsSample) return true;
        if (root is null || !IsUnderRoot(root, target.FilePath)) return false;
        try
        {
            if (File.Exists(target.FilePath)) File.Delete(target.FilePath);
            var meta = MetaPathFor(target.FilePath);
            if (File.Exists(meta)) File.Delete(meta);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("GalleryMedia.Delete", ex);
            return false;
        }
    }

    /// <summary>
    /// Mock 판정 — 메타 <c>isMock:true</c> · <c>engine/engineKind == "Mock"</c> · <c>metadata</c> 값/프롬프트/제목에 <c>testsrc</c> 포함.
    /// </summary>
    public static bool IsMockMeta(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("isMock", out var m) && m.ValueKind == JsonValueKind.True) return true;
        if (Str(root, "engine").Equals("Mock", StringComparison.OrdinalIgnoreCase)
            || Str(root, "engineKind").Equals("Mock", StringComparison.OrdinalIgnoreCase)) return true;
        if (HasTestSrc(Str(root, "prompt")) || HasTestSrc(Str(root, "title"))) return true;
        if (root.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
            foreach (var p in md.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && HasTestSrc(p.Value.GetString())) return true;
        return false;
    }

    private static bool HasTestSrc(string? s) => s is not null && s.Contains("testsrc", StringComparison.OrdinalIgnoreCase);

    public GalleryRestoreContext PackageRestoreContext(GalleryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (w, h) = ParseResolution(item.Resolution) ?? (1280, 720);
        var size = w.ToString(CultureInfo.InvariantCulture) + "*" + h.ToString(CultureInfo.InvariantCulture);
        var reference = string.IsNullOrWhiteSpace(item.ReferenceImagePath) ? null : item.ReferenceImagePath;
        return new GalleryRestoreContext(item.ItemId, item.Title, item.Prompt ?? "", item.Seed, item.Resolution, size, reference);
    }

    /// <summary>마지막 스캔 결과에서 미디어 경로(전체 경로 · 대소문자 무시)로 항목 조회 — 렌더 큐 딥링크 포커스용.</summary>
    public GalleryItem? FindByPath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return null;
        string full;
        try { full = Path.GetFullPath(mediaPath); } catch { return null; }
        lock (_lock)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.IsSample || it.FilePath.Length == 0) continue;
                try { if (string.Equals(Path.GetFullPath(it.FilePath), full, StringComparison.OrdinalIgnoreCase)) return it; }
                catch { }
            }
        }
        return null;
    }

    public void QueueRestore(GalleryRestoreContext ctx) => Volatile.Write(ref _pendingRestore, ctx ?? throw new ArgumentNullException(nameof(ctx)));
    public GalleryRestoreContext? TakePendingRestore() => Interlocked.Exchange(ref _pendingRestore, null);

    public static string MetaPathFor(string mediaPath) => Path.ChangeExtension(mediaPath, null) + MetaSuffix;
    public const string PosterSuffix = ".poster.jpg";
    public static string PosterPathFor(string mediaPath) => Path.ChangeExtension(mediaPath, null) + PosterSuffix;

    /// <summary>
    /// ⭐ 즐겨찾기 설정 — 사이드카 <c>.meta.json</c> 의 <c>isFavorite</c> 갱신(기존 필드 보존 · 없으면 생성 · 임시파일 원자 교체).
    /// 샘플은 메모리에서만 토글. 대상 없음/루트 이탈/쓰기 실패 시 false(상태 불변).
    /// </summary>
    public bool SetFavorite(Guid itemId, bool favorite)
    {
        GalleryItem? target = null;
        string? root;
        lock (_lock)
        {
            root = _root;
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].ItemId == itemId) { target = _items[i]; break; }
        }
        if (target is null) return false;
        if (!target.IsSample)
        {
            if (root is null || !IsUnderRoot(root, target.FilePath)) return false;
            var meta = MetaPathFor(target.FilePath);
            try
            {
                System.Text.Json.Nodes.JsonObject obj;
                try { obj = File.Exists(meta) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta)) as System.Text.Json.Nodes.JsonObject ?? new() : new(); }
                catch (JsonException) { obj = new(); }   // 손상 메타 → 새로 작성
                obj["isFavorite"] = favorite;
                var tmp = meta + ".~tmp";
                File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, meta, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Program.Log("GalleryMedia.Favorite", ex);
                return false;
            }
        }
        lock (_lock) target.IsFavorite = favorite;
        return true;
    }

    /// <summary>🔔 미디어 메타 변경 통지(전체 경로) — 갤러리/스토리보드 뷰가 구독해 재생시간 뱃지 등을 즉시 갱신. 핸들러 예외 격리.</summary>
    public event Action<string>? MediaMetadataChanged;

    /// <summary>
    /// ✂️ 트림 등으로 미디어가 바뀌었음을 통지 — 스캔 목록의 항목 재생시간을 즉시 갱신(재스캔 없이)하고 <see cref="MediaMetadataChanged"/> 발행.
    ///   반환 = 메모리 항목 갱신 여부(목록에 없으면 false · 이벤트는 항상 발행 → 새 클립은 구독자가 재스캔).
    /// </summary>
    public bool NotifyMediaMetadataChanged(string mediaPath, double? durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return false;
        var item = FindByPath(mediaPath);
        if (item is not null && durationSeconds is { } d && d > 0) lock (_lock) item.DurationSeconds = d;
        var h = MediaMetadataChanged;
        if (h is not null)
        {
            foreach (var del in h.GetInvocationList())
            {
                try { ((Action<string>)del)(mediaPath); }
                catch (Exception ex) { Program.Log("GalleryMedia.MetaChanged", ex); }
            }
        }
        return item is not null;
    }

    /// <summary>재생시간 뱃지 문구 — <c>m:ss.f</c>(1분 미만 <c>s.f s</c>) · 미상 null.</summary>
    public static string? FormatDurationBadge(double? seconds)
    {
        if (seconds is not { } s || s <= 0 || double.IsNaN(s) || double.IsInfinity(s)) return null;
        if (s < 60) return s.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        var m = (int)(s / 60);
        return m.ToString(CultureInfo.InvariantCulture) + ":" + (s - m * 60).ToString("00.0", CultureInfo.InvariantCulture);
    }

    public bool ToggleFavorite(Guid itemId)
    {
        GalleryItem? t = null;
        lock (_lock)
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].ItemId == itemId) { t = _items[i]; break; }
        return t is not null && SetFavorite(itemId, !t.IsFavorite);
    }

    public static AspectCategory Classify(int width, int height)
    {
        if (width <= 0 || height <= 0) return AspectCategory.Landscape16x9;
        if (height * 10 > width * 11) return AspectCategory.Vertical9x16;
        if (width * 10 > height * 11) return AspectCategory.Landscape16x9;
        return AspectCategory.Square1x1;
    }

    /// <summary><c>"1280x720"</c> · <c>"720*1280"</c> · <c>"960×960"</c> → (w, h).</summary>
    public static (int W, int H)? ParseResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution)) return null;
        var p = resolution.Split('x', 'X', '*', '×');
        return p.Length == 2
            && int.TryParse(p[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(p[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            && w > 0 && h > 0 ? (w, h) : null;
    }

    // ── 스캔 내부 ──
    private static bool IsMediaFile(string path)
    {
        var ext = Path.GetExtension(path);
        foreach (var e in MediaExtensions)
            if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsInUploads(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file);
        return rel.StartsWith("_uploads", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>스냅샷 저장 폴더명(<c>MediaRoot/_stills</c>).</summary>
    public const string StillsFolder = "_stills";

    /// <summary><paramref name="root"/> 하위 <c>_stills/</c> 의 PNG 인지.</summary>
    public static bool IsStillSnapshot(string root, string file)
    {
        try
        {
            if (!Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)) return false;
            var rel = Path.GetRelativePath(root, file);
            return rel.StartsWith(StillsFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith(StillsFolder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private static bool IsUnderRoot(string root, string file)
    {
        try
        {
            var full = Path.GetFullPath(file);
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static GalleryItem? TryLoadItem(string file, bool isStill)
    {
        try
        {
            var info = new FileInfo(file);
            string title = Path.GetFileNameWithoutExtension(file), prompt = "", thumb = "", resolution = "", reference = "";
            long seed = 0;
            float duration = 0;
            double? playDur = null;
            DateTime created = info.LastWriteTime;
            bool isMock = HasTestSrc(title), isFav = false, isExported = false;

            var metaPath = MetaPathFor(file);
            if (File.Exists(metaPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                    var r = doc.RootElement;
                    if (r.ValueKind == JsonValueKind.Object)
                    {
                        isMock |= IsMockMeta(r);
                        isFav = r.TryGetProperty("isFavorite", out var fv) && fv.ValueKind == JsonValueKind.True;
                        isExported = r.TryGetProperty("isExported", out var ev) && ev.ValueKind == JsonValueKind.True;
                        title = Str(r, "title") is { Length: > 0 } t ? t : title;
                        prompt = Str(r, "prompt");
                        thumb = Str(r, "thumbnail");
                        resolution = Str(r, "resolution");
                        reference = Str(r, "referenceImagePath");
                        if (r.TryGetProperty("seed", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sv)) seed = sv;
                        if (r.TryGetProperty("renderDurationSec", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetSingle(out var dv)) duration = dv;
                        if (r.TryGetProperty("durationSeconds", out var pd) && pd.ValueKind == JsonValueKind.Number && pd.TryGetDouble(out var pdv) && pdv > 0) playDur = pdv;
                        if (r.TryGetProperty("createdAt", out var c) && c.ValueKind == JsonValueKind.String && c.TryGetDateTime(out var cv)) created = cv;
                    }
                }
                catch (Exception ex) { Program.Log("GalleryMedia.Meta", ex); }   // 메타 손상 → 파일 정보로 폴백
            }

            var dims = ParseResolution(resolution) ?? ReadPngSize(file) ?? ParseResolutionToken(title);
            var (w, h) = dims ?? (1280, 720);
            if (dims is null || string.IsNullOrEmpty(resolution))
                resolution = w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(thumb) && GalleryItem.IsImagePath(file)) thumb = file;
            var poster = PosterPathFor(file);

            return new GalleryItem
            {
                ItemId = StableId(Path.GetFullPath(file)),
                Title = title,
                FilePath = file,
                ThumbnailUrl = thumb,
                Prompt = prompt,
                Seed = seed,
                Resolution = resolution,
                AspectCategory = Classify(w, h),
                CreatedAt = created,
                RenderDurationSec = duration,
                DurationSeconds = playDur,
                ReferenceImagePath = reference.Length > 0 ? reference : null,
                IsMock = isMock,
                IsFavorite = isFav,
                IsExported = isExported,
                IsStill = isStill,
                PosterPath = File.Exists(poster) ? poster : null,
                SizeBytes = info.Length,
            };
        }
        catch (Exception ex) { Program.Log("GalleryMedia.Item", ex); return null; }
    }

    private string? SafeTargetDir()
    {
        try { var d = _exportTargetDir(); return string.IsNullOrWhiteSpace(d) ? null : d.Trim().Trim('"'); }
        catch (Exception ex) { Program.Log("GalleryMedia.ExportDir", ex); return null; }
    }

    /// <summary>
    /// StockSuite 대상 폴더 산출물 색인 — 출처 마커(<c>.pfs-src</c>) 경로 · <c>.stocksuite_manifest.json</c> SHA-256(렌더 ID/크기 후보) · 내용 크기+SHA-256 순 엄격 매칭.
    /// 단순 파일명(<c>bg_ambient.mp4</c>) 일치는 오탐 원인이라 사용하지 않으며, 백업(<c>*.backup.*</c>)은 활성 에셋이 아니므로 제외.
    /// </summary>
    private sealed class ExportedTargetIndex
    {
        private readonly string _dir;
        private readonly Dictionary<string, string> _bySource = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, List<string>> _bySize = new();
        private Export.StockSuiteManifest? _manifest;
        private byte[]? _manifestSha;
        private string? _manifestTarget;
        private int _count;

        private ExportedTargetIndex(string dir) => _dir = dir;

        public static ExportedTargetIndex? TryBuild(string? dir)
        {
            if (dir is null) return null;
            try
            {
                var full = Path.GetFullPath(dir);
                if (!Directory.Exists(full)) return null;
                var idx = new ExportedTargetIndex(full);
                foreach (var t in Directory.EnumerateFiles(full, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsMediaFile(t) || t.Contains(".~pfs", StringComparison.OrdinalIgnoreCase) || Export.StockSuiteExporter.IsBackupAsset(t)) continue;
                    idx._count++;
                    var marker = t + Export.StockSuiteExporter.SourceMarkerSuffix;
                    if (File.Exists(marker))
                    {
                        var payload = File.ReadAllText(marker);
                        var bar = payload.IndexOf('|');
                        var src = (bar >= 0 ? payload[..bar] : payload).Trim();
                        if (src.Length > 0) idx._bySource[src] = t;
                    }
                    var len = new FileInfo(t).Length;
                    if (!idx._bySize.TryGetValue(len, out var l)) idx._bySize[len] = l = new List<string>(1);
                    l.Add(t);
                }
                if (Export.StockSuiteExporter.TryReadManifest(full) is { } m && m.SourceSha256.Length == 64)
                {
                    var active = Path.Combine(full, m.ActiveAsset);
                    try
                    {
                        if (File.Exists(active)) { idx._manifest = m; idx._manifestSha = Convert.FromHexString(m.SourceSha256); idx._manifestTarget = active; }
                    }
                    catch (FormatException) { }   // 손상 해시 → 매니페스트 매칭 비활성
                }
                return idx._count == 0 ? null : idx;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Program.Log("GalleryMedia.ExportIndex", ex);
                return null;
            }
        }

        /// <summary>일치 대상 경로 — 없으면 null. 대상 폴더 내부 파일 자신은 제외.</summary>
        public string? Match(string file)
        {
            try
            {
                var full = Path.GetFullPath(file);
                if (IsUnderRoot(_dir, full)) return null;
                if (_bySource.TryGetValue(full, out var t)) return t;
                var len = new FileInfo(full).Length;
                if (len == 0) return null;
                // 🧾 매니페스트 — 렌더 ID 또는 바이트 수가 후보일 때만 해시, SHA-256 일치해야 확정(렌더 ID 단독 일치는 불충분).
                var m = _manifest;
                var manifestCand = m is not null && (len == m.SourceBytes || string.Equals(Path.GetFileNameWithoutExtension(full), m.SourceRenderId, StringComparison.OrdinalIgnoreCase));
                var sizeCand = _bySize.TryGetValue(len, out var cands);
                if (!manifestCand && !sizeCand) return null;
                byte[] h;
                using (var a = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan)) h = SHA256.HashData(a);
                if (manifestCand && CryptographicOperations.FixedTimeEquals(h, _manifestSha!)) return _manifestTarget;
                if (!sizeCand) return null;
                foreach (var c in cands!)
                {
                    using var b = new FileStream(c, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan);
                    if (CryptographicOperations.FixedTimeEquals(h, SHA256.HashData(b))) return c;
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Program.Log("GalleryMedia.ExportMatch", ex);
                return null;
            }
        }
    }

    private static string Str(JsonElement r, string name)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>PNG IHDR(16~23바이트) 폭·높이 — 비 PNG 는 null.</summary>
    private static (int W, int H)? ReadPngSize(string file)
    {
        if (!Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)) return null;
        Span<byte> head = stackalloc byte[24];
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Read(head) < 24 || head[0] != 0x89 || head[1] != (byte)'P') return null;
        int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
        int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
        return w > 0 && h > 0 ? (w, h) : null;
    }

    /// <summary>파일명 내 <c>720x1280</c> 류 토큰 추정.</summary>
    private static (int W, int H)? ParseResolutionToken(string name)
    {
        foreach (var tok in name.Split('_', '-', ' ', '.'))
            if (tok.Length >= 7 && ParseResolution(tok) is { } d && d.W >= 100 && d.H >= 100) return d;
        return null;
    }

    private static Guid StableId(string key)
    {
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant()), hash);
        return new Guid(hash);
    }

    // ── 기본 샘플 세트 ──
    private static GalleryItem[] CreateSampleItems()
    {
        var now = DateTime.Now;
        return new[]
        {
            Sample("sample-shorts", "AI 쇼츠 · 네온 스트리트 댄스", "720x1280", 20240901, 42.5f, now.AddMinutes(-12), "#ff3d9a", "#7c5cff",
                "A street dancer spinning under neon signs at night, vertical 9:16, dynamic handheld camera, rain reflections, cinematic lighting"),
            Sample("sample-cinematic", "시네마틱 · 황금빛 사막 대지", "1280x720", 77031, 96.0f, now.AddHours(-3), "#f59e0b", "#ef4444",
                "Epic aerial drone shot over golden desert dunes at sunset, slow dolly forward, anamorphic lens flare, 16:9 cinematic"),
            Sample("sample-commercial", "커머셜 · 프리미엄 향수 런칭", "1280x720", 5150, 64.2f, now.AddDays(-1), "#22d3ee", "#3b82f6",
                "Luxury perfume bottle rotating on black marble, soft rim light, water droplets, macro product shot, commercial grade"),
            Sample("sample-square", "피드 · 미니멀 카페 라떼아트", "960x960", 3141592, 38.7f, now.AddDays(-2), "#a3e635", "#10b981",
                "Top-down shot of latte art being poured in a minimalist cafe, square 1:1, warm morning light, shallow depth of field"),
        };
    }

    private static GalleryItem Sample(string key, string title, string res, long seed, float dur, DateTime at, string c1, string c2, string prompt)
    {
        var (w, h) = ParseResolution(res)!.Value;
        return new GalleryItem
        {
            ItemId = StableId("sample://" + key),
            Title = title,
            FilePath = "",
            ThumbnailUrl = SvgThumb(w, h, c1, c2),
            Prompt = prompt,
            Seed = seed,
            Resolution = res,
            AspectCategory = Classify(w, h),
            CreatedAt = at,
            RenderDurationSec = dur,
            IsSample = true,
        };
    }

    private static string SvgThumb(int w, int h, string c1, string c2)
    {
        var ws = w.ToString(CultureInfo.InvariantCulture);
        var hs = h.ToString(CultureInfo.InvariantCulture);
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 " + ws + " " + hs + "'>"
            + "<defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='" + c1 + "'/><stop offset='1' stop-color='" + c2 + "'/></linearGradient></defs>"
            + "<rect width='" + ws + "' height='" + hs + "' fill='url(#g)'/></svg>";
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    // ── 🔎 스마트 검색 · 💽 스토리지 대시보드 ──

    /// <summary>복합 검색 — 키워드(공백 분리 AND · 제목/프롬프트/파일명/티커) × 화면비 × 해상도 × 코덱 칩.</summary>
    public IReadOnlyList<GalleryItem> Search(GallerySearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snap = Items;
        var res = new List<GalleryItem>(snap.Count);
        for (var i = 0; i < snap.Count; i++)
            if (MatchesSearch(snap[i], query)) res.Add(snap[i]);
        return res;
    }

    public static bool MatchesSearch(GalleryItem it, GallerySearchQuery q)
    {
        ArgumentNullException.ThrowIfNull(it);
        ArgumentNullException.ThrowIfNull(q);
        if (q.Aspect != GalleryAspectChip.Any && ClassifyAspectChip(it) != q.Aspect) return false;
        if (q.Resolution != GalleryResolutionChip.Any && ClassifyResolutionChip(it) != q.Resolution) return false;
        if (q.Codec != GalleryCodecChip.Any && ClassifyCodecChip(it.FilePath) != q.Codec) return false;
        return MatchesText(it, q.Text);
    }

    /// <summary>키워드 전부 포함(대소문자 무시) — 제목 · 프롬프트 · 파일명 대상. 빈 질의 = 통과.</summary>
    public static bool MatchesText(GalleryItem it, string? text)
    {
        ArgumentNullException.ThrowIfNull(it);
        if (string.IsNullOrWhiteSpace(text)) return true;
        var span = text.AsSpan().Trim();
        var file = Path.GetFileName(it.FilePath ?? "");
        while (span.Length > 0)
        {
            var sp = span.IndexOfAny(' ', '\t');
            var tok = sp < 0 ? span : span[..sp];
            span = sp < 0 ? ReadOnlySpan<char>.Empty : span[(sp + 1)..].TrimStart();
            if (tok.Length == 0) continue;
            if (!it.Title.AsSpan().Contains(tok, StringComparison.OrdinalIgnoreCase)
                && !it.Prompt.AsSpan().Contains(tok, StringComparison.OrdinalIgnoreCase)
                && !file.AsSpan().Contains(tok, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>화면비 칩 분류 — 세로(9:16) · 16:9(1.6~2.0) · 21:9(2.2~2.6) · 32:9(3.2 이상). 그 외 Any.</summary>
    public static GalleryAspectChip ClassifyAspectChip(GalleryItem it)
    {
        ArgumentNullException.ThrowIfNull(it);
        if (ParseResolution(it.Resolution) is not { } d || d.W <= 0 || d.H <= 0) return GalleryAspectChip.Any;
        var r = (double)d.W / d.H;
        return r < 0.8 ? GalleryAspectChip.V9x16
             : r >= 1.6 && r < 2.0 ? GalleryAspectChip.W16x9
             : r >= 2.2 && r < 2.6 ? GalleryAspectChip.W21x9
             : r >= 3.2 ? GalleryAspectChip.W32x9
             : GalleryAspectChip.Any;
    }

    /// <summary>해상도 칩 분류(짧은 변 기준 — 울트라와이드 3840×1080 은 1080p) — 2160 이상 4K · 1080 이상 1080p.</summary>
    public static GalleryResolutionChip ClassifyResolutionChip(GalleryItem it)
    {
        ArgumentNullException.ThrowIfNull(it);
        if (ParseResolution(it.Resolution) is not { } d) return GalleryResolutionChip.Any;
        var shortSide = Math.Min(d.W, d.H);
        return shortSide >= 2160 ? GalleryResolutionChip.Uhd4K : shortSide >= 1080 ? GalleryResolutionChip.Fhd1080 : GalleryResolutionChip.Any;
    }

    public static GalleryCodecChip ClassifyCodecChip(string? path)
    {
        var ext = Path.GetExtension(path ?? "");
        return ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) ? GalleryCodecChip.WebM
             : ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? GalleryCodecChip.Mp4
             : GalleryCodecChip.Any;
    }

    /// <summary>스토리지 점유율 — 총량 · 4K UHD · 1080p MP4 · WebM · 기타 + 디스크 여유(샘플 제외).</summary>
    public GalleryStorageBreakdown ComputeStorage()
    {
        string? root;
        lock (_lock) root = _root;
        return ComputeStorage(Items, root);
    }

    public static GalleryStorageBreakdown ComputeStorage(IReadOnlyList<GalleryItem> items, string? root)
    {
        ArgumentNullException.ThrowIfNull(items);
        long total = 0, uhd = 0, fhd = 0, webm = 0, other = 0;
        var count = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.IsSample || it.SizeBytes <= 0) continue;
            count++;
            total += it.SizeBytes;
            var codec = ClassifyCodecChip(it.FilePath);
            var res = ClassifyResolutionChip(it);
            if (codec == GalleryCodecChip.WebM) webm += it.SizeBytes;
            else if (res == GalleryResolutionChip.Uhd4K) uhd += it.SizeBytes;
            else if (res == GalleryResolutionChip.Fhd1080 && codec == GalleryCodecChip.Mp4) fhd += it.SizeBytes;
            else other += it.SizeBytes;
        }
        return new GalleryStorageBreakdown(count, total, uhd, fhd, webm, other, FreeBytesOf(root));
    }

    private static long FreeBytesOf(string? root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root)) return -1;
            var drive = Path.GetPathRoot(Path.GetFullPath(root));
            return string.IsNullOrEmpty(drive) ? -1 : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("GalleryMedia.FreeSpace", ex);
            return -1;
        }
    }

    /// <summary>검색 결과 일괄 내보내기 — 미디어 + 사이드카 메타 복사([부분 구제] 실패 건 제외). 반환 (성공 수, 대상 폴더).</summary>
    public static (int Copied, string Directory) ExportItems(IReadOnlyList<GalleryItem> items, string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        Directory.CreateDirectory(targetDirectory);
        var copied = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.IsSample || !File.Exists(it.FilePath)) continue;
            try
            {
                var dest = Path.Combine(targetDirectory, Path.GetFileName(it.FilePath));
                File.Copy(it.FilePath, dest, overwrite: true);
                var meta = MetaPathFor(it.FilePath);
                if (File.Exists(meta)) File.Copy(meta, MetaPathFor(dest), overwrite: true);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("GalleryMedia.Export", ex); }
        }
        return (copied, targetDirectory);
    }

    /// <summary>기본 내보내기 폴더 — 내 비디오\PromptFlowStudio Exports\search_{시각}(스캔 루트 밖 · 중복 색인 방지).</summary>
    public static string DefaultExportDirectory(DateTime now)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "PromptFlowStudio Exports", "search_" + now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
    }
}

/// <summary>화면비 칩.</summary>
public enum GalleryAspectChip { Any = 0, W16x9, W21x9, W32x9, V9x16 }
/// <summary>해상도 칩.</summary>
public enum GalleryResolutionChip { Any = 0, Uhd4K, Fhd1080 }
/// <summary>코덱(컨테이너) 칩.</summary>
public enum GalleryCodecChip { Any = 0, Mp4, WebM }

/// <summary>복합 검색 질의.</summary>
public sealed record GallerySearchQuery(string? Text, GalleryAspectChip Aspect = GalleryAspectChip.Any,
    GalleryResolutionChip Resolution = GalleryResolutionChip.Any, GalleryCodecChip Codec = GalleryCodecChip.Any);

/// <summary>스토리지 점유율 스냅샷(FreeBytes 음수 = 조회 불가).</summary>
public sealed record GalleryStorageBreakdown(int Count, long TotalBytes, long Uhd4KBytes, long Fhd1080Mp4Bytes, long WebMBytes, long OtherBytes, long FreeBytes)
{
    /// <summary>총량 대비 비중(0~100).</summary>
    public double Percent(long part) => TotalBytes <= 0 ? 0 : Math.Clamp(part * 100.0 / TotalBytes, 0, 100);
}
