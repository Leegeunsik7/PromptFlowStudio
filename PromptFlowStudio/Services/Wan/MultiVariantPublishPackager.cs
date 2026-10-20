using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Wan;

/// <summary>v16.23.0 A/B 변주 1건 메타데이터 패키지 — 문자 · 스타일 · 영상 · 썸네일 · 제목/설명/후킹 해시태그.</summary>
public sealed record AbVariantPackage(char Letter, ViralHookStyle Style, string VideoPath, string? ThumbnailPath,
    string Title, string Description, IReadOnlyList<string> Hashtags, string? MetadataPath);

/// <summary>v16.23.0 A/B 실험 패키지 — 실험 Id · 폴더 · 변주 목록 · manifest.</summary>
public sealed record AbExperimentPackage(Guid ExperimentId, string Root, IReadOnlyList<AbVariantPackage> Variants, string ManifestPath, string Message);

/// <summary>v16.23.0 A/B 게시 결과(변주 × 대상).</summary>
public sealed record AbPublishOutcome(char Letter, SocialPublishResult Result);

/// <summary>
/// [PromptFlowStudio] v16.23.0 🧪 A/B 테스트 멀티 변주 패키저 — 후킹 변주(A/B/C) → 변주별 제목·썸네일·후킹 해시태그 → <c>ABTEST_*/variant_X/</c> + ab_manifest.json
///   → v16.17.0 <see cref="DirectSocialPublisherBridge"/> 1-Click 일괄 게시(변주 × 대상).
///   ▸ 메타데이터: <see cref="SocialMetadataAiGenerator"/>(Gemini → 오프라인 SEO → 템플릿 폴백) 기본 제목/해시태그 + 스타일별 접두·후킹 태그 + 실험 태그(#ab_X).
///   ▸ 썸네일 추출 실패는 생략([부분 구제]) · 게시는 기본 비공개(공개는 명시 동의) · 동시 실행 1건 · 예외 0.
/// </summary>
public sealed class MultiVariantPublishPackager(ViralHookVariantGeneratorService hooks, SocialMetadataAiGenerator metadata, DirectSocialPublisherBridge bridge)
{
    public const int MaxHashtags = 15;

    private int _busy;
    private int _packaged;
    private int _published;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public int PackagedCount => Volatile.Read(ref _packaged);
    public int PublishedCount => Volatile.Read(ref _published);
    public AbExperimentPackage? LastPackage { get; private set; }
    public string? LastAction { get; private set; }
    public bool IsEnabled(SocialPublishTarget t) => bridge.IsEnabled(t);

    public event Action? OnChange;

    /// <summary>스타일별 후킹 해시태그(순수).</summary>
    public static IReadOnlyList<string> HookTagsOf(ViralHookStyle s) => s switch
    {
        ViralHookStyle.ShockQuestion => new[] { "충격", "궁금증", "알고계셨나요" },
        ViralHookStyle.ExtremeCloseUp => new[] { "클로즈업", "디테일", "끝까지보세요" },
        _ => new[] { "레전드", "반전", "실화" },
    };

    /// <summary>스타일별 제목(순수 · 원 제목 앞 접두).</summary>
    public static string TitleOf(ViralHookStyle s, string baseTitle)
    {
        var t = string.IsNullOrWhiteSpace(baseTitle) ? "숏폼" : baseTitle.Trim();
        return s switch
        {
            ViralHookStyle.ShockQuestion => "❓ " + t + " — 진짜 알고 있었나요?",
            ViralHookStyle.ExtremeCloseUp => "🔍 " + t + " (끝까지 보세요)",
            _ => "🔥 " + t + " | 3초 만에 빠져듦",
        };
    }

    /// <summary>해시태그 병합(기본 → 후킹 → 실험 태그 · 정규화/중복 제거 · 상한).</summary>
    public static List<string> MergeTags(IReadOnlyList<string>? baseTags, ViralHookStyle s, char letter, int max = MaxHashtags)
    {
        var list = new List<string>(max);
        SocialMetadataAiGenerator.AddTag(list, "ab_" + char.ToLowerInvariant(letter), max);
        foreach (var t in HookTagsOf(s)) SocialMetadataAiGenerator.AddTag(list, t, max);
        if (baseTags is not null) foreach (var t in baseTags) SocialMetadataAiGenerator.AddTag(list, t, max);
        return list;
    }

    /// <summary>변주 → 게시 요청(변주 × 대상 · 순수). 기본 비공개.</summary>
    public static List<SocialPublishRequest> BuildRequests(AbExperimentPackage pkg, IReadOnlyList<SocialPublishTarget> targets, bool publicVisibility = false)
    {
        var list = new List<SocialPublishRequest>(pkg.Variants.Count * Math.Max(1, targets.Count));
        foreach (var v in pkg.Variants)
            foreach (var t in targets)
                list.Add(new SocialPublishRequest(t, v.VideoPath, v.Title, v.Description, v.Hashtags, PublicVisibility: publicVisibility));
        return list;
    }

    /// <summary>후킹 변주 → 변주별 메타데이터·썸네일 패키지(예외 0 · 성공 변주 0 → null).</summary>
    public async Task<AbExperimentPackage?> PackageAsync(StoryboardProject project, IReadOnlyList<ViralHookRenderResult> variants,
        bool useAi = true, string? outputRoot = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(variants);
        if (Interlocked.Exchange(ref _busy, 1) != 0) { Done("⚠ 이미 A/B 패키징/게시가 진행 중입니다."); return null; }
        try
        {
            var ok = new List<ViralHookRenderResult>(variants.Count);
            foreach (var v in variants) if (v.Ok && v.VideoPath is not null && File.Exists(v.VideoPath)) ok.Add(v);
            if (ok.Count == 0) { Done("⚠ 패키징할 후킹 변주가 없습니다 — 먼저 A/B/C 변주를 생성하세요."); return null; }

            var meta = await metadata.GenerateAsync(SocialMetadataAiGenerator.FromProject(project), new[] { SocialChannel.YouTube }, project, useAi, ct).ConfigureAwait(false);
            var basePost = meta.Posts[SocialChannel.YouTube];
            var id = Guid.NewGuid();
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var root = outputRoot ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ok[0].VideoPath!))!, "ABTEST_" + stamp);
            Directory.CreateDirectory(root);

            var packs = new List<AbVariantPackage>(ok.Count);
            foreach (var v in ok)
            {
                ct.ThrowIfCancellationRequested();
                var letter = ViralHookVariantGeneratorService.SpecOf(v.Style).Letter;
                var dir = Path.Combine(root, "variant_" + letter);
                Directory.CreateDirectory(dir);
                var thumb = await hooks.ExtractThumbnailAsync(v.VideoPath!, Path.Combine(dir, "thumbnail.jpg"), ct).ConfigureAwait(false);
                var title = TitleOf(v.Style, basePost.Title);
                var tags = MergeTags(basePost.Hashtags, v.Style, letter);
                var desc = v.Copy.Replace('\n', ' ') + "\n\n" + basePost.Body;
                var txt = Path.Combine(dir, "metadata.txt");
                var header = "🧪 A/B 실험 " + id.ToString("N")[..8] + " · 변주 " + letter + " · " + ViralHookVariantGeneratorService.SpecOf(v.Style).Label;
                await File.WriteAllTextAsync(txt, SocialMetadataAiGenerator.ToText(basePost with { Title = title, Body = desc, Hashtags = tags }, header),
                    new UTF8Encoding(true), ct).ConfigureAwait(false);
                packs.Add(new AbVariantPackage(letter, v.Style, v.VideoPath!, thumb, title, desc, tags, txt));
            }

            var manifest = Path.Combine(root, "ab_manifest.json");
            await WriteManifestAsync(manifest, root, id, project, packs, meta.Message, ct).ConfigureAwait(false);
            var pkg = new AbExperimentPackage(id, root, packs, manifest,
                packs.Count.ToString(CultureInfo.InvariantCulture) + "개 변주 패키징 · 메타데이터 " + meta.Message);
            LastPackage = pkg;
            Interlocked.Increment(ref _packaged);
            Done("✅ " + pkg.Message);
            return pkg;
        }
        catch (OperationCanceledException) { Done("⚠ 취소됨"); return null; }
        catch (Exception ex) { Program.Log("AbPackager.Package", ex); Done("⚠ A/B 패키징 실패: " + ex.Message); return null; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>1-Click A/B 실험 게시(변주 × 대상 · 개별 실패는 결과로 반환 [부분 구제]).</summary>
    public async Task<IReadOnlyList<AbPublishOutcome>> PublishAsync(AbExperimentPackage pkg, IReadOnlyList<SocialPublishTarget> targets,
        bool publicVisibility = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pkg);
        if (targets is null || targets.Count == 0) { Done("⚠ 게시 대상을 1개 이상 선택하세요."); return Array.Empty<AbPublishOutcome>(); }
        if (Interlocked.Exchange(ref _busy, 1) != 0) { Done("⚠ 이미 A/B 패키징/게시가 진행 중입니다."); return Array.Empty<AbPublishOutcome>(); }
        try
        {
            var reqs = BuildRequests(pkg, targets, publicVisibility);
            var results = await bridge.PublishAllAsync(reqs, ct).ConfigureAwait(false);
            var outcomes = new List<AbPublishOutcome>(results.Count);
            var ok = 0;
            for (var i = 0; i < results.Count; i++)
            {
                var letter = pkg.Variants[Math.Min(pkg.Variants.Count - 1, i / targets.Count)].Letter;
                outcomes.Add(new AbPublishOutcome(letter, results[i]));
                if (results[i].Ok) ok++;
            }
            Interlocked.Add(ref _published, ok);
            Done((ok > 0 ? "✅ " : "⚠ ") + "A/B 게시 " + ok.ToString(CultureInfo.InvariantCulture) + "/" + reqs.Count.ToString(CultureInfo.InvariantCulture));
            return outcomes;
        }
        catch (Exception ex) { Program.Log("AbPackager.Publish", ex); Done("⚠ A/B 게시 실패: " + ex.Message); return Array.Empty<AbPublishOutcome>(); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static async Task WriteManifestAsync(string path, string root, Guid id, StoryboardProject project, List<AbVariantPackage> packs,
        string metaSource, CancellationToken ct)
    {
        var entries = new List<object>(packs.Count);
        foreach (var p in packs)
        {
            entries.Add(new
            {
                letter = p.Letter.ToString(), style = p.Style.ToString(), title = p.Title, hashtags = p.Hashtags,
                video = p.VideoPath, thumbnail = p.ThumbnailPath is null ? null : Path.GetRelativePath(root, p.ThumbnailPath),
                metadata = p.MetadataPath is null ? null : Path.GetRelativePath(root, p.MetadataPath),
            });
        }
        var json = JsonSerializer.Serialize(new
        {
            version = "16.23.0", experimentId = id.ToString("N"), project = project.Title, metadataSource = metaSource,
            createdAt = DateTime.Now.ToString("O", CultureInfo.InvariantCulture), variants = entries,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    private void Done(string msg)
    {
        LastAction = msg;
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("AbPackager.OnChange", ex); }
    }
}
