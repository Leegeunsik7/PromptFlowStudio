using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Shared.Store;

/// <summary>project.json 스키마 호환을 실제로 검증하는 시임(데스크톱=StoryboardProject 왕복 · 기본=JSON 구조 검사).</summary>
public interface ISchemaCompatProbe
{
    SchemaCompatResult Check(string projectJson);
}

/// <summary>기본 스키마 프로브 — JSON 유효성 + 버전 마커 탐지(도메인 타입 미참조 · RCL 안전).</summary>
public sealed class DefaultSchemaCompatProbe : ISchemaCompatProbe
{
    // v1.0~v4.2 에 걸쳐 도입된 대표 필드(존재 개수로 스키마 진화 마커 집계).
    private static readonly string[] Markers =
    {
        "Id", "Scenes", "ExportResolution", "PresetKey", "CoverTitle",              // ~v2
        "CadModelPath", "TelemetryHudStyle", "TenantId", "VirtualStudioTemplate",   // v2.5~v3.0
        "BabyName", "BabyArtStyle", "WanRenderMode", "VoiceTone",                    // v3.7~v3.8
        "RemoteWorkerUrl", "MobileCaptureSource",                                    // v4.0
    };

    public SchemaCompatResult Check(string projectJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(projectJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new SchemaCompatResult(false, 0, "루트가 객체가 아님 — project.json 아님");
            var found = Markers.Count(m => doc.RootElement.TryGetProperty(m, out _));
            // 구버전 JSON 은 신규 필드가 없어도 유효(누락=기본값). 최소 코어(Id/Scenes)만 확인.
            var core = doc.RootElement.TryGetProperty("Id", out _) || doc.RootElement.TryGetProperty("Scenes", out _);
            return new SchemaCompatResult(core, found, core ? $"역호환 OK(마커 {found}/{Markers.Length} · 누락 필드는 기본값 로드)" : "코어 필드(Id/Scenes) 없음");
        }
        catch (Exception ex) { return new SchemaCompatResult(false, 0, "JSON 파싱 실패: " + ex.Message); }
    }
}

/// <summary>
/// [PromptFlow Studio v4.3.0] 양대 스토어 통합 프리플라이트 진단기(Task 3 · 순수 BCL).
///   ▸ AAB/IPA SHA-256 무결성 · project.json v1.0~v4.2 역호환 · 번들 용량(Android ≤200MB · iOS ≤4GB) 정량 검증.
///   ▸ Android/iOS 리포트를 통합해 <b>스토어 준비도(0~100)</b>를 산출한다.
/// </summary>
public static class StoreDeploymentInspector
{
    /// <summary>Android AAB 전달 용량 가이드(200MB).</summary>
    public const long AndroidLimitBytes = 200L * 1024 * 1024;
    /// <summary>iOS App 용량 한도(4GB).</summary>
    public const long IosLimitBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>바이트 → SHA-256 16진.</summary>
    public static string Sha256Hex(byte[] data)
    {
        using var sha = SHA256.Create();
        var h = sha.ComputeHash(data ?? Array.Empty<byte>());
        var sb = new StringBuilder(h.Length * 2);
        foreach (var b in h) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>파일 → SHA-256 16진(없으면 null).</summary>
    public static string? Sha256File(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(fs);
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch { return null; }
    }

    /// <summary>용량 한도 검사.</summary>
    public static BundleSizeResult CheckSize(long bytes, StorePlatform platform)
    {
        var limit = platform == StorePlatform.Android ? AndroidLimitBytes : IosLimitBytes;
        return new BundleSizeResult(bytes, limit, bytes > 0 && bytes <= limit);
    }

    /// <summary>Android/iOS 리포트 + 전역(스키마/용량) 검사를 통합해 프리플라이트 리포트를 만든다.</summary>
    public static PreflightReport Preflight(StoreReport android, StoreReport ios, SchemaCompatResult schema, BundleSizeResult androidSize, BundleSizeResult iosSize)
    {
        var global = new List<StoreCheck>
        {
            schema.Ok
                ? new("schema", "project.json 역호환", CheckSeverity.Pass, schema.Message)
                : new("schema", "project.json 역호환", CheckSeverity.Fail, schema.Message),
            androidSize.Ok
                ? new("size-android", $"Android 용량 {androidSize.Mb:0.0}MB", CheckSeverity.Pass, $"200MB 이하({androidSize.Percent}%)")
                : new("size-android", "Android 용량 초과", CheckSeverity.Fail, $"{androidSize.Mb:0.0}MB / 200MB"),
            iosSize.Ok
                ? new("size-ios", $"iOS 용량 {iosSize.Mb:0.0}MB", CheckSeverity.Pass, $"4GB 이하({iosSize.Percent}%)")
                : new("size-ios", "iOS 용량 초과", CheckSeverity.Fail, $"{iosSize.Mb:0.0}MB / 4096MB"),
        };
        var globalReport = StoreReportFactory.Build(StorePlatform.Android, global);
        var readiness = (int)Math.Round((android.Score + ios.Score + globalReport.Score) / 3.0);
        var risk = android.FailCount + ios.FailCount + globalReport.FailCount;
        var summary = risk > 0 ? $"❌ 거절 위험 총 {risk}건 — 배포 보류" : readiness >= 90 ? "✅ 양대 스토어 제출 준비 완료" : "⚠ 경고 보완 후 제출 권장";
        return new PreflightReport(android, ios, global, readiness, summary);
    }
}
