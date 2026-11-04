using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PromptFlowStudio.Services.Batch;

namespace PromptFlowStudio.Services.Render;

/// <summary>비디오 듀레이션 모드 — 5초 단일 · 10초 2컷 · 15초 3컷 · 15초 앰비언트 슬로우(단일 5초 + 3배 슬로우 루프).</summary>
public enum VideoDurationMode { Sec5_Single, Sec10_Dual, Sec15_Triple, Sec15_AmbientSlow }

/// <summary>
/// [PromptFlowStudio] 멀티 듀레이션 파이프라인 계획기(순수 함수).
///   ▸ 10s/15s 멀티 샷 → 동일 <see cref="BatchRenderJob.StitchGroupId"/>(= <see cref="FFmpegVideoStitchService.MetaGroup"/>) 5초 하위 컷 2~3개로 전개.
///   ▸ 빈 서브 프롬프트는 메인 프롬프트 상속 + 파트별 카메라 모션 차등.
///   ▸ 이전 컷 마지막 프레임 추출 인자(<c>-sseof -0.1 … -vframes 1</c>) → 다음 컷 I2V 앵커, 전 컷 완료 시 <c>{project}_10s.mp4</c>/<c>_15s.mp4</c> 합본.
/// </summary>
public static class MultiDurationPlanner
{
    public const float ShotSeconds = 5f;
    /// <summary>작업 메타 — 듀레이션 모드 이름.</summary>
    public const string MetaMode = "duration.mode";
    /// <summary>작업 메타 — 합본 출력 프로젝트 이름.</summary>
    public const string MetaProject = "duration.project";
    /// <summary>작업 메타 — 그룹 총 컷 수.</summary>
    public const string MetaShots = "duration.shots";
    /// <summary>작업 메타 — 주입된 I2V 앵커 프레임 경로.</summary>
    public const string MetaAnchorFrame = "duration.anchor";
    /// <summary>작업 메타 — 그룹 합본 완료 경로(마지막 완료 컷에 기록).</summary>
    public const string MetaStitched = "duration.stitched";
    /// <summary>작업 메타 — ffmpeg 부재 가상 병합 매니페스트 경로.</summary>
    public const string MetaStitchedVirtual = "duration.stitched.virtual";
    public const string LastFrameSuffix = ".lastframe.png";
    public const string DefaultProject = "workspace";

    /// <summary>파트별 카메라 모션 차등(상속 프롬프트 전용) — 도입 · 전개 · 마무리.</summary>
    public static readonly string[] PartCameraMotions =
    {
        "slow dolly in, establishing shot",
        "smooth lateral tracking shot, continuous motion",
        "gentle crane up and pull back, closing shot",
    };

    private static readonly string[] PartLabels = { "파트 1 (0~5s)", "파트 2 (5~10s)", "파트 3 (10~15s)" };

    public static int ShotCount(VideoDurationMode mode) => mode switch
    {
        VideoDurationMode.Sec10_Dual => 2,
        VideoDurationMode.Sec15_Triple => 3,
        _ => 1,
    };

    public static int TargetSeconds(VideoDurationMode mode) => mode switch
    {
        VideoDurationMode.Sec10_Dual => 10,
        VideoDurationMode.Sec15_Triple or VideoDurationMode.Sec15_AmbientSlow => 15,
        _ => 5,
    };

    /// <summary>추론 과금 가중치 — 5s 1x · 10s 2x · 15s 3x · 15s-Ambient 1x(로컬 후처리 무과금).</summary>
    public static int CostMultiplier(VideoDurationMode mode) => ShotCount(mode);

    public static bool IsMultiShot(VideoDurationMode mode) => ShotCount(mode) > 1;

    public static string Label(VideoDurationMode mode) => mode switch
    {
        VideoDurationMode.Sec10_Dual => "10초 · 2컷",
        VideoDurationMode.Sec15_Triple => "15초 · 3컷",
        VideoDurationMode.Sec15_AmbientSlow => "15초 앰비언트",
        _ => "5초",
    };

    /// <summary>"파트 N (a~bs)" — 범위 밖은 빈 문자열.</summary>
    public static string PartLabel(int partIndex) => (uint)partIndex < (uint)PartLabels.Length ? PartLabels[partIndex] : "";

    /// <summary>서브 프롬프트 우선, 비면 메인 상속 + 파트 카메라 모션(멀티 샷만). 메인도 비면 카메라 모션만.</summary>
    public static string ResolvePartPrompt(string? main, string? sub, int partIndex, int shotCount)
    {
        if (!string.IsNullOrWhiteSpace(sub)) return sub.Trim();
        var m = string.IsNullOrWhiteSpace(main) ? "" : main.Trim();
        if (shotCount <= 1 || (uint)partIndex >= (uint)PartCameraMotions.Length) return m;
        var motion = PartCameraMotions[partIndex];
        return m.Length == 0 ? motion : m.TrimEnd(',', ' ') + ", " + motion;
    }

    /// <summary>
    /// 템플릿 작업 → 큐 적재 단위. 단일/앰비언트는 템플릿 1건(메타에 모드·프로젝트 기록), 멀티 샷은 새 그룹 키의 5초 하위 컷 N건
    /// (CutIndex 1..N · StartSec 0/5/10 · 1번 컷만 템플릿 참조 이미지 · 출력 경로 <c>_p{N}</c> 접미).
    /// </summary>
    public static BatchRenderJob[] Expand(BatchRenderJob template, IReadOnlyList<string?>? subPrompts = null, string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        var mode = template.DurationMode;
        var n = ShotCount(mode);
        var project = string.IsNullOrWhiteSpace(projectName) ? DefaultProject : projectName.Trim();
        if (n == 1)
        {
            template.Metadata = WithMeta(template.Metadata, MetaMode, mode.ToString(), MetaProject, project);
            return new[] { template };
        }

        var group = string.IsNullOrWhiteSpace(template.StitchGroupId) ? FFmpegVideoStitchService.NewGroupId() : template.StitchGroupId;
        var shots = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var jobs = new BatchRenderJob[n];
        for (var i = 0; i < n; i++)
        {
            var sub = subPrompts is not null && i < subPrompts.Count ? subPrompts[i] : null;
            var meta = WithMeta(FFmpegVideoStitchService.WithGroup(template.Metadata, group), MetaMode, mode.ToString(), MetaProject, project);
            meta[MetaShots] = shots;
            jobs[i] = new BatchRenderJob
            {
                Prompt = ResolvePartPrompt(template.Prompt, sub, i, n),
                Seed = template.Seed,
                Resolution = template.Resolution,
                DurationSec = ShotSeconds,
                CutIndex = i + 1,
                StartSec = i * ShotSeconds,
                SubjectAnchor = template.SubjectAnchor,
                ReferenceImagePath = i == 0 ? template.ReferenceImagePath : null,
                OutputPath = PartOutputPath(template.OutputPath, i),
                DurationMode = mode,
                StitchGroupId = group,
                Metadata = meta,
                EngineKind = template.EngineKind,   // 적재 시점 렌더 모드 표기(디스패치 시 실엔진으로 재확정)
            };
        }
        return jobs;
    }

    /// <summary><c>{dir}\{name}_p{N}{ext}</c> — null 은 null(엔진 산출 경로 사용).</summary>
    public static string? PartOutputPath(string? outputPath, int partIndex)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return null;
        var ext = Path.GetExtension(outputPath);
        var stem = outputPath[..^ext.Length];
        return stem + "_p" + (partIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + (ext.Length == 0 ? ".mp4" : ext);
    }

    /// <summary>같은 그룹의 다음 컷(CutIndex+1) — 없으면 null.</summary>
    public static BatchRenderJob? FindNextCut(IReadOnlyList<BatchRenderJob> jobs, BatchRenderJob done)
    {
        if (done.StitchGroupId is not { Length: > 0 } g || done.CutIndex is not { } c) return null;
        for (var i = 0; i < jobs.Count; i++)
        {
            var j = jobs[i];
            if (j.CutIndex == c + 1 && string.Equals(j.StitchGroupId, g, StringComparison.Ordinal)) return j;
        }
        return null;
    }

    /// <summary>마지막 프레임 추출 — <c>ffmpeg -sseof -0.1 -i {video} -vframes 1 {png}</c>.</summary>
    public static string[] BuildLastFrameArgs(string videoPath, string pngPath) =>
    [
        "-y", "-hide_banner", "-loglevel", "error",
        "-sseof", "-0.1", "-i", videoPath,
        "-vframes", "1", pngPath,
    ];

    /// <summary><c>{video 확장자 제거}.lastframe.png</c>.</summary>
    public static string LastFramePathFor(string videoPath)
        => Path.Combine(Path.GetDirectoryName(videoPath) ?? "", Path.GetFileNameWithoutExtension(videoPath) + LastFrameSuffix);

    /// <summary><c>{dir}\{project}_{10|15}s.mp4</c>(앰비언트는 <c>_ambient15s.mp4</c>) — 금지 문자 '_' 치환.</summary>
    public static string BuildOutputPath(string dir, string? projectName, VideoDurationMode mode)
    {
        var suffix = mode == VideoDurationMode.Sec15_AmbientSlow ? "_ambient15s.mp4"
                   : "_" + TargetSeconds(mode).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s.mp4";
        return Path.Combine(dir, SanitizeName(projectName) + suffix);
    }

    public static string SanitizeName(string? name)
    {
        var s = string.IsNullOrWhiteSpace(name) ? DefaultProject : name.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    /// <summary>메타 값 조회(없으면 null).</summary>
    public static string? MetaOf(BatchRenderJob job, string key)
        => job.Metadata is { } m && m.TryGetValue(key, out var v) ? v : null;

    /// <summary>기존 메타 복사 + 키 1개 갱신.</summary>
    public static Dictionary<string, string> WithMeta(IReadOnlyDictionary<string, string>? meta, string key, string value)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (meta is not null) foreach (var kv in meta) d[kv.Key] = kv.Value;
        d[key] = value;
        return d;
    }

    private static Dictionary<string, string> WithMeta(IReadOnlyDictionary<string, string>? meta, string k1, string v1, string k2, string v2)
    {
        var d = WithMeta(meta, k1, v1);
        d[k2] = v2;
        return d;
    }
}
