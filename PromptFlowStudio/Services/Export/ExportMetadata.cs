using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PromptFlowStudio.Services.Export;

/// <summary>
/// [PromptFlowStudio] 📦 내보내기 메타데이터 표준 — 원본 사이드카 <c>{이름}.meta.json</c> 에
/// <c>isExported:true</c> · <c>exportedAt</c>(ISO-8601 UTC) · <c>exportedTargets[]</c>(중복 제거 누적) 기록.
/// 기존 필드 보존 · 손상 메타 재작성 · 임시 파일 원자 교체 · 실패 격리(절대 throw 하지 않음).
/// </summary>
public static class ExportMetadata
{
    public const string MetaSuffix = ".meta.json";

    public static string MetaPathFor(string mediaPath) => Path.ChangeExtension(mediaPath, null) + MetaSuffix;

    public static string FormatTimestamp(DateTimeOffset at)
        => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>원본 메타에 내보내기 표식 병합 — 성공 시 true.</summary>
    public static bool MarkExported(string mediaPath, IEnumerable<string> targets, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || targets is null) return false;
        string meta = "", tmp = "";
        try
        {
            meta = MetaPathFor(Path.GetFullPath(mediaPath));
            JsonObject obj;
            try { obj = File.Exists(meta) ? JsonNode.Parse(File.ReadAllText(meta)) as JsonObject ?? new() : new(); }
            catch (JsonException) { obj = new(); }   // 손상 메타 → 새로 작성

            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (obj["exportedTargets"] is JsonArray prev)
                foreach (var n in prev)
                    if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 && seen.Add(s)) list.Add(s);
            foreach (var t in targets)
                if (!string.IsNullOrWhiteSpace(t) && seen.Add(t)) list.Add(t);

            var arr = new JsonArray();
            foreach (var t in list) arr.Add(JsonValue.Create(t));   // 원시 값 노드(리플렉션/리졸버 불요)
            obj["isExported"] = true;
            obj["exportedAt"] = FormatTimestamp(at);
            obj["exportedTargets"] = arr;

            tmp = meta + ".~tmp";
            File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, meta, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            Program.Log("Export.Meta", ex);
            try { if (tmp.Length > 0 && File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>메타 <c>isExported:true</c> 여부(부재/손상 = false).</summary>
    public static bool IsMarkedExported(string mediaPath)
    {
        try
        {
            var meta = MetaPathFor(mediaPath);
            if (!File.Exists(meta)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("isExported", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
