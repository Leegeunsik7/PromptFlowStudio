using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Studio;

/// <summary>납품 명세서 자산 1건 — 분류·파일명·상대경로·바이트·SHA256.</summary>
public sealed record ManifestAsset(string Category, string Name, string RelPath, long Bytes, string Sha256);

/// <summary>납품 종합 명세서 모델.</summary>
public sealed record DeliveryManifest(
    string Title, string ProjectId, string Buyer, string Preset, string Resolution, int Cuts,
    string Languages, string GeneratedAt, IReadOnlyList<ManifestAsset> Assets, long TotalBytes);

/// <summary>
/// [PromptFlowStudio] 납품용 종합 명세서(Delivery Manifest) 생성기(v2.7.0 자율 발상 ①) — 순수 BCL(외부 라이브러리 0).
///   ▸ 패키징된 모든 자산의 분류·크기·<b>SHA256 체크섬</b>을 집계해 납품/검수용 <b>HTML + Markdown</b> 명세서를 생성한다.
///   ▸ 바이어는 명세서만으로 자산 무결성(체크섬)·구성·수량을 검증 가능 → 상용 납품 신뢰도 확보.
/// </summary>
public sealed class DeliveryManifestService
{
    /// <summary>엔트리 목록으로 명세서 모델을 만든다(파일별 크기·SHA256 계산).</summary>
    public DeliveryManifest BuildModel(StoryboardProject project, IEnumerable<(string Category, string Name, string RelPath, string SourcePath)> entries)
    {
        var assets = new List<ManifestAsset>();
        long total = 0;
        foreach (var (cat, name, rel, src) in entries)
        {
            long bytes = 0; var sha = "";
            try { if (File.Exists(src)) { bytes = new FileInfo(src).Length; sha = Sha256File(src); } }
            catch { /* 접근 불가 자산은 크기 0·체크섬 공란 */ }
            total += bytes;
            assets.Add(new ManifestAsset(cat, name, rel, bytes, sha));
        }
        var langs = project.SelectedDubbingLanguages.Count > 0
            ? string.Join(", ", project.SelectedDubbingLanguages.Select(c => c.ToUpperInvariant())) : "KO";
        return new DeliveryManifest(
            "PromptFlow Studio — Delivery Manifest", project.Id,
            string.IsNullOrWhiteSpace(project.BuyerName) ? "-" : project.BuyerName,
            string.IsNullOrWhiteSpace(project.PresetKey) ? "-" : project.PresetKey,
            project.ExportResolution, project.Scenes.Count, langs,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), assets, total);
    }

    /// <summary>명세서 → Markdown.</summary>
    public static string RenderMarkdown(DeliveryManifest m)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(m.Title).Append("\n\n");
        sb.Append("| 항목 | 값 |\n|---|---|\n");
        sb.Append($"| Project ID | {m.ProjectId} |\n");
        sb.Append($"| Buyer | {m.Buyer} |\n");
        sb.Append($"| Preset / Equipment | {m.Preset} |\n");
        sb.Append($"| Resolution | {m.Resolution} |\n");
        sb.Append($"| Cuts | {m.Cuts} |\n");
        sb.Append($"| Dubbing | {m.Languages} |\n");
        sb.Append($"| Generated | {m.GeneratedAt} |\n");
        sb.Append($"| Assets | {m.Assets.Count} files · {HumanBytes(m.TotalBytes)} |\n\n");
        sb.Append("## Assets\n\n| # | Category | File | Size | SHA-256 |\n|---|---|---|---|---|\n");
        for (var i = 0; i < m.Assets.Count; i++)
        {
            var a = m.Assets[i];
            sb.Append($"| {i + 1} | {a.Category} | `{a.RelPath}` | {HumanBytes(a.Bytes)} | `{Short(a.Sha256)}` |\n");
        }
        return sb.ToString();
    }

    /// <summary>명세서 → 자체 완결형 HTML(인쇄/뷰 겸용).</summary>
    public static string RenderHtml(DeliveryManifest m)
    {
        var rows = new StringBuilder();
        for (var i = 0; i < m.Assets.Count; i++)
        {
            var a = m.Assets[i];
            rows.Append($"<tr><td>{i + 1}</td><td>{Esc(a.Category)}</td><td class=f>{Esc(a.RelPath)}</td><td class=n>{HumanBytes(a.Bytes)}</td><td class=h>{Esc(Short(a.Sha256))}</td></tr>");
        }
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + Esc(m.Title) + "</title><style>" +
            "body{font-family:Segoe UI,system-ui,sans-serif;background:#0b0d14;color:#e6edff;max-width:900px;margin:0 auto;padding:28px}" +
            "h1{font-size:19px}table{width:100%;border-collapse:collapse;font-size:12.5px}th,td{border-bottom:1px solid #2b2f42;padding:7px 9px;text-align:left}" +
            "th{color:#7dd3fc;font-size:10.5px;text-transform:uppercase;letter-spacing:.08em}.n{text-align:right}.f,.h{font-family:ui-monospace,monospace;font-size:11px}.h{color:#6b7392}" +
            ".meta{display:grid;grid-template-columns:1fr 1fr;gap:6px 18px;font-size:12.5px;margin:14px 0 20px}.meta b{color:#9aa6c8}" +
            "</style></head><body><h1>📦 " + Esc(m.Title) + "</h1><div class=meta>" +
            $"<div><b>Project ID</b> {Esc(m.ProjectId)}</div><div><b>Buyer</b> {Esc(m.Buyer)}</div>" +
            $"<div><b>Preset</b> {Esc(m.Preset)}</div><div><b>Resolution</b> {Esc(m.Resolution)} · {m.Cuts} cuts</div>" +
            $"<div><b>Dubbing</b> {Esc(m.Languages)}</div><div><b>Generated</b> {Esc(m.GeneratedAt)}</div>" +
            $"<div><b>Assets</b> {m.Assets.Count} files · {HumanBytes(m.TotalBytes)}</div></div>" +
            "<table><thead><tr><th>#</th><th>Category</th><th>File</th><th>Size</th><th>SHA-256</th></tr></thead><tbody>" +
            rows + "</tbody></table></body></html>";
    }

    /// <summary>파일 SHA-256(대문자 hex).</summary>
    public static string Sha256File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    private static string Short(string hex) => string.IsNullOrEmpty(hex) ? "-" : (hex.Length > 16 ? hex[..16] + "…" : hex);
    private static string HumanBytes(long b) => b >= 1_000_000 ? $"{b / 1_000_000.0:0.0} MB" : b >= 1000 ? $"{b / 1000.0:0.0} KB" : $"{b} B";
    private static string Esc(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>
/// [PromptFlowStudio] 결과 폴더 딥링크(File Explorer) 서비스(v2.7.0 자율 발상 ②) — 순수 BCL <see cref="Process"/>(외부 라이브러리 0).
///   ▸ 패키징 완료 후 산출물(ZIP)이 위치한 폴더를 <b>탐색기에서 선택 상태로 즉시 열어</b> 납품 파일 확인/전달을 원터치로 만든다.
///   ▸ Windows 전용(<c>explorer.exe</c>). 비Windows/실패는 조용히 false(Graceful).
/// </summary>
public sealed class StudioFolderLinkService
{
    /// <summary>탐색기에서 해당 파일을 선택 상태로 연다(파일 없으면 폴더만).</summary>
    public bool Reveal(string? path)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || string.IsNullOrWhiteSpace(path)) return false;
            if (File.Exists(path)) return Start("explorer.exe", $"/select,\"{path}\"");
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) && Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex) { Program.Log("FolderLink.Reveal", ex); return false; }
    }

    /// <summary>탐색기에서 폴더를 연다.</summary>
    public bool OpenFolder(string? dir)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex) { Program.Log("FolderLink.Open", ex); return false; }
    }

    private static bool Start(string exe, string args)
    {
        try { using var p = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true }); return true; }
        catch { return false; }
    }
}
