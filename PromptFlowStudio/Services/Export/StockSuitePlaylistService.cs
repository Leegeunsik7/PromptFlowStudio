using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Export;

/// <summary>📅 장중 스케줄 슬롯 — Key: <c>morning</c>|<c>midday</c>|<c>closing</c>|<c>night</c> · Time: <c>HH:mm-HH:mm</c>(자정 넘김 허용).</summary>
public sealed record PlaylistSlot(string Key, string Time, string Label, string File);

/// <summary>📅 <c>playlist.json</c> 모델.</summary>
public sealed record StockSuitePlaylist(bool Enabled, IReadOnlyList<PlaylistSlot> Slots);

/// <summary>📅 스케줄 발행 결과 — Published = 슬롯 키 순서 보존 대상 경로 · [부분 구제] 실패 슬롯은 Failed.</summary>
public sealed record PlaylistPublishResult(bool Ok, string? PlaylistPath, IReadOnlyList<string> Published, IReadOnlyList<string> Failed, bool NeedsPathSetup, string Message);

/// <summary>
/// [PromptFlowStudio] 📅 StockSuite 장중 시간대별 배경 자동 순환 스케줄러 — 에셋 폴더의 <c>playlist.json</c> 생성/관리.
///   ▸ 슬롯 영상은 <see cref="StockSuiteExporter"/>(무음·샤프닝·60fps·원자적 교체·메타 표준) 경유 복사 → 슬롯별 고정 이름(<c>bg_morning.mp4</c> 등).
///   ▸ 발행 완료 후 <c>playlist.json</c> 원자적 기록 + <c>hotreload.trigger</c>(file=playlist.json) 1회 신호.
///   ▸ 미지정 슬롯은 대상 폴더에 기존 파일이 있으면 유지, 없으면 목록에서 제외.
/// </summary>
public sealed class StockSuitePlaylistService
{
    public const string PlaylistFileName = "playlist.json";
    private readonly StockSuiteExporter _exporter;

    public StockSuitePlaylistService(StockSuiteExporter exporter)
        => _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));

    /// <summary>기본 4슬롯(장초반 · 정규장 · 장마감 · 야간).</summary>
    public static IReadOnlyList<PlaylistSlot> DefaultSlots { get; } =
    [
        new("morning", "08:30-10:00", "장초반 개장 랠리", "bg_morning.mp4"),
        new("midday", "10:00-14:30", "정규장 퀀트 앰비언트", "bg_midday.mp4"),
        new("closing", "14:30-16:00", "동시호가/장마감", "bg_closing.mp4"),
        new("night", "16:00-08:30", "야간 글로벌 시황", "bg_night.mp4"),
    ];

    public string? ConfiguredDirectory => _exporter.ConfiguredDirectory;

    /// <summary><c>HH:mm-HH:mm</c> 파싱 — 실패 시 null.</summary>
    public static (TimeOnly Start, TimeOnly End)? ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        var dash = range.IndexOf('-');
        if (dash <= 0) return null;
        return TimeOnly.TryParseExact(range[..dash].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s)
            && TimeOnly.TryParseExact(range[(dash + 1)..].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e)
            && s != e ? (s, e) : null;
    }

    /// <summary>현재 시각 활성 슬롯 — 자정 넘김 구간(<c>16:00-08:30</c>) 지원 · 반열린 구간 [start, end).</summary>
    public static PlaylistSlot? ResolveActiveSlot(IReadOnlyList<PlaylistSlot> slots, TimeOnly now)
    {
        ArgumentNullException.ThrowIfNull(slots);
        foreach (var s in slots)
        {
            if (ParseRange(s.Time) is not { } r) continue;
            var inRange = r.Start < r.End ? now >= r.Start && now < r.End : now >= r.Start || now < r.End;
            if (inRange) return s;
        }
        return null;
    }

    /// <summary>정규 JSON(<c>{ enabled, slots:[{time,label,file}] }</c> · 들여쓰기).</summary>
    public static string BuildPlaylistJson(StockSuitePlaylist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        var buf = new System.Buffers.ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteBoolean("enabled", playlist.Enabled);
            w.WriteStartArray("slots");
            foreach (var s in playlist.Slots)
            {
                w.WriteStartObject();
                w.WriteString("time", s.Time);
                w.WriteString("label", s.Label);
                w.WriteString("file", s.File);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>스키마 즉시 검증 파싱 — 손상/스키마 불일치 시 null. 슬롯 Key 는 기본 슬롯 시간대로 역추정.</summary>
    public static StockSuitePlaylist? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("slots", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var enabled = !r.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
            var slots = new List<PlaylistSlot>(arr.GetArrayLength());
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                var time = Str(e, "time");
                var file = Str(e, "file");
                if (ParseRange(time) is null || file.Length == 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                slots.Add(new(KeyForTime(time), time, Str(e, "label"), file));
            }
            return new(enabled, slots);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>대상 폴더의 기존 <c>playlist.json</c> 로드(없음/손상 = null).</summary>
    public StockSuitePlaylist? Load(string? targetDir = null)
    {
        var dir = ResolveDir(targetDir);
        if (dir is null) return null;
        try
        {
            var p = Path.Combine(Path.GetFullPath(dir), PlaylistFileName);
            return File.Exists(p) ? TryParse(File.ReadAllText(p)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.Playlist.Load", ex);
            return null;
        }
    }

    /// <summary><c>playlist.json</c> 원자적 기록(임시 → 교체). 반환 = 기록 경로.</summary>
    public static string WritePlaylist(string directory, StockSuitePlaylist playlist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.Combine(Path.GetFullPath(directory), PlaylistFileName);
        var tmp = path + ".~pfs";
        try
        {
            File.WriteAllText(tmp, BuildPlaylistJson(playlist));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return path;
    }

    /// <summary>
    /// [StockSuite 스케줄 발행] — 슬롯 키 → 갤러리 원본 경로 지정분을 슬롯 고정 이름으로 내보낸 뒤 <c>playlist.json</c> + <c>hotreload.trigger</c> 동시 갱신.
    /// 개별 실패 격리([부분 구제]). 절대 throw 하지 않음(인자 오류 제외).
    /// </summary>
    public async Task<PlaylistPublishResult> PublishAsync(IReadOnlyDictionary<string, string?> assignments, string? targetDir = null, bool enabled = true, CancellationToken ct = default, bool? sharpen = null, bool? smooth60 = null)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        var none = Array.Empty<string>();
        var dir = ResolveDir(targetDir);
        if (dir is null) return new(false, null, none, none, true, "StockSuite 에셋 경로가 설정되지 않았습니다 — 환경설정에서 경로를 지정하세요.");

        var published = new List<string>(4);
        var failed = new List<string>(4);
        var slots = new List<PlaylistSlot>(4);
        string? lastError = null;
        foreach (var slot in DefaultSlots)
        {
            if (ct.IsCancellationRequested) break;
            if (assignments.TryGetValue(slot.Key, out var src) && !string.IsNullOrWhiteSpace(src))
            {
                StockSuiteExportResult r;
                try { r = await _exporter.ExportAsync(src, dir, slot.File, hotReload: false, ct, sharpen: sharpen, smooth60: smooth60).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Program.Log("StockSuite.Playlist." + slot.Key, ex); r = new(false, null, false, ex.Message); }
                if (r.Ok && r.TargetPath is { } t)
                {
                    published.Add(t);
                    slots.Add(slot with { File = Path.GetFileName(t) });   // WebM 포맷이면 확장자 반영
                    continue;
                }
                failed.Add(slot.Key);
                lastError = slot.Label + ": " + r.Message;
                if (r.NeedsPathSetup) return new(false, null, published, failed, true, r.Message);
            }
            // 미지정/실패 슬롯 — 기존 파일 유지(있을 때만 목록 포함)
            var kept = ExistingSlotFile(dir, slot.File);
            if (kept is not null) slots.Add(slot with { File = kept });
        }
        if (ct.IsCancellationRequested) return new(false, null, published, failed, false, "취소됨 — " + published.Count.ToString(CultureInfo.InvariantCulture) + "개 보존");
        if (slots.Count == 0) return new(false, null, published, failed, false, "⚠ 발행할 슬롯이 없습니다" + (lastError is null ? " — 시간대별 영상을 지정하세요." : " — " + lastError));

        try
        {
            var path = WritePlaylist(dir, new StockSuitePlaylist(enabled, slots));
            StockSuiteExporter.WriteHotReloadTrigger(Path.GetDirectoryName(path)!, PlaylistFileName, _exporter.UtcNow());
            var msg = "📅 StockSuite 장중 스케줄 발행 완료 — " + slots.Count.ToString(CultureInfo.InvariantCulture) + "개 슬롯"
                + (failed.Count > 0 ? " (실패: " + string.Join(", ", failed) + ")" : "") + " → " + path;
            return new(failed.Count == 0, path, published, failed, false, msg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.Playlist.Write", ex);
            return new(false, null, published, failed, ex is UnauthorizedAccessException, "⚠ playlist.json 기록 실패: " + ex.Message);
        }
    }

    private string? ResolveDir(string? targetDir)
        => string.IsNullOrWhiteSpace(targetDir) ? _exporter.ConfiguredDirectory : targetDir.Trim().Trim('"');

    private static string? ExistingSlotFile(string dir, string file)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            if (File.Exists(Path.Combine(full, file))) return file;
            var webm = Path.ChangeExtension(file, ".webm");
            return File.Exists(Path.Combine(full, webm)) ? webm : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return null; }
    }

    private static string KeyForTime(string time)
    {
        foreach (var d in DefaultSlots)
            if (string.Equals(d.Time, time, StringComparison.Ordinal)) return d.Key;
        return time;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
