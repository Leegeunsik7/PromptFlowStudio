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
    /// <summary>I2V 첫 프레임 참조 이미지 경로(메타 <c>referenceImagePath</c>) — T2V 산출물이면 null.</summary>
    public string? ReferenceImagePath { get; init; }
    /// <summary>폴더가 비었을 때 노출하는 기본 템플릿(파일 없음 · 삭제는 목록에서만).</summary>
    public bool IsSample { get; init; }
    public bool IsVideo => !IsImagePath(FilePath);

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

    public IReadOnlyList<GalleryItem> Items { get { lock (_lock) return _items.ToArray(); } }
    public bool HasPendingRestore => Volatile.Read(ref _pendingRestore) is not null;

    public IReadOnlyList<GalleryItem> ScanGalleryItems(string directoryPath)
    {
        var found = new List<GalleryItem>();
        string? root = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
            {
                root = Path.GetFullPath(directoryPath);
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!IsMediaFile(file) || IsInUploads(root, file)) continue;
                    var item = TryLoadItem(file);
                    if (item is not null) found.Add(item);
                }
            }
        }
        catch (Exception ex) { Program.Log("GalleryMedia.Scan", ex); }   // [부분 구제] 열거 중단 전까지 수집분 유지

        found.Sort(static (a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        if (found.Count > MaxItems) found.RemoveRange(MaxItems, found.Count - MaxItems);
        if (found.Count == 0) found.AddRange(CreateSampleItems());

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
        if (target is null) return false;

        if (!target.IsSample)
        {
            if (root is null || !IsUnderRoot(root, target.FilePath)) return false;
            try
            {
                if (File.Exists(target.FilePath)) File.Delete(target.FilePath);
                var meta = MetaPathFor(target.FilePath);
                if (File.Exists(meta)) File.Delete(meta);
            }
            catch (Exception ex) { Program.Log("GalleryMedia.Delete", ex); return false; }
        }

        lock (_lock) return _items.Remove(target);
    }

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

    private static GalleryItem? TryLoadItem(string file)
    {
        try
        {
            var info = new FileInfo(file);
            string title = Path.GetFileNameWithoutExtension(file), prompt = "", thumb = "", resolution = "", reference = "";
            long seed = 0;
            float duration = 0;
            DateTime created = info.LastWriteTime;

            var metaPath = MetaPathFor(file);
            if (File.Exists(metaPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                    var r = doc.RootElement;
                    if (r.ValueKind == JsonValueKind.Object)
                    {
                        title = Str(r, "title") is { Length: > 0 } t ? t : title;
                        prompt = Str(r, "prompt");
                        thumb = Str(r, "thumbnail");
                        resolution = Str(r, "resolution");
                        reference = Str(r, "referenceImagePath");
                        if (r.TryGetProperty("seed", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sv)) seed = sv;
                        if (r.TryGetProperty("renderDurationSec", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetSingle(out var dv)) duration = dv;
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
                ReferenceImagePath = reference.Length > 0 ? reference : null,
            };
        }
        catch (Exception ex) { Program.Log("GalleryMedia.Item", ex); return null; }
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
}
