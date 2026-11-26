using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PromptFlowStudio.Services.Batch;

namespace PromptFlowStudio.Services.Render;

/// <summary>비디오 듀레이션 모드 — 5초 단일 · 10초 2컷 · 15초 3컷 · 15초 앰비언트 슬로우(단일 5초 + 3배 슬로우 루프).</summary>
/// <summary>영상 길이 모드. <see cref="Sec15_OneTake"/>·<see cref="Sec30_Auto"/> 는 메인 프롬프트 1개만 사용(파트 입력 불필요) —
/// 네이티브 길이(Wan 2.6 = 15초 · 그 외 5초)에 맞춰 <see cref="MultiDurationPlanner.SegmentPlan"/> 로 자동 분할 · 합본.</summary>
public enum VideoDurationMode { Sec5_Single, Sec10_Dual, Sec15_Triple, Sec15_AmbientSlow, Sec15_OneTake, Sec30_Auto }

/// <summary>
/// 멀티 컷(10s 2컷 · 15s 3컷) 컷 전환 방식.
///   ▸ <see cref="IndependentCrossfade"/>(기본): 컷마다 자기 프롬프트로 독립 생성(1번 컷만 참조/첫 화면 이미지) → 0.25초 xfade 디졸브 합본.
///     카메라 앵글 전환(와이드 → 미디엄 → 클로즈업)이 자연스럽고, 프레임 밖으로 나간 피사체를 억지로 이어 붙이는 구도·인체 왜곡이 없다.
///   ▸ <see cref="LastFrameChain"/>: 이전 컷 마지막 프레임 → 다음 컷 I2V 앵커(연속 롱테이크) → 하드 컷 concat.
/// </summary>
public enum CutTransitionMode : byte { IndependentCrossfade, LastFrameChain }

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
    /// <summary>작업 메타 — 컷 전환 방식(<see cref="CutTransitionMode"/> 이름). 부재 = 독립 컷(기본).</summary>
    public const string MetaTransition = "duration.transition";
    /// <summary>독립 컷 합본 xfade 디졸브 길이(초).</summary>
    public const double CrossfadeSeconds = 0.25;
    /// <summary>독립 컷 일관성 힌트 — 피사체·의상(유니폼)·조명 동일, 전신/머리 잘림 금지(멱등 부착).</summary>
    public const string ContinuityHint = "same subject identity and same outfit/uniform colors as the other cuts, consistent lighting and color palette, head and full body fully in frame, anatomically correct";

    public const CutTransitionMode DefaultTransition = CutTransitionMode.IndependentCrossfade;

    public static string TransitionLabel(CutTransitionMode mode) => mode == CutTransitionMode.LastFrameChain
        ? "연속 프레임 이어붙이기 (Last-Frame I2V Chain)"
        : "독립 시네마틱 컷 (Independent T2V + Crossfade)";

    /// <summary>작업 메타 → 전환 방식(부재·미상 = 기본 독립 컷).</summary>
    public static CutTransitionMode TransitionOf(BatchRenderJob job)
        => string.Equals(MetaOf(job, MetaTransition), nameof(CutTransitionMode.LastFrameChain), StringComparison.Ordinal)
            ? CutTransitionMode.LastFrameChain : CutTransitionMode.IndependentCrossfade;

    /// <summary>다음 컷에 마지막 프레임 I2V 앵커를 주입할지 — 체인 모드 멀티 샷만.</summary>
    public static bool ShouldChainLastFrame(BatchRenderJob job) => (IsMultiShot(job.DurationMode) || IsGroupedShot(job)) && TransitionOf(job) == CutTransitionMode.LastFrameChain;

    /// <summary>메타에 전환 방식 기록.</summary>
    public static Dictionary<string, string> WithTransition(IReadOnlyDictionary<string, string>? meta, CutTransitionMode mode)
        => WithMeta(meta, MetaTransition, mode.ToString());

    /// <summary>일관성 힌트 멱등 부착(빈 프롬프트는 그대로 — 파트 상속/카메라 모션이 채움).</summary>
    public static string WithContinuityHint(string? prompt)
    {
        var p = (prompt ?? "").Trim();
        if (p.Length == 0 || p.Contains(ContinuityHint, StringComparison.Ordinal)) return p;
        return p.TrimEnd(',', ' ') + ", " + ContinuityHint;
    }

    /// <summary>
    /// [✨ 생성 시작] 주 프롬프트 — 메인이 비어 있고 멀티 컷이며 파트 1 이 채워져 있으면 파트 1 로 폴백.
    /// </summary>
    public static string ResolvePrimaryPrompt(string? main, IReadOnlyList<string?>? subPrompts, VideoDurationMode mode)
    {
        if (!string.IsNullOrWhiteSpace(main)) return main.Trim();
        if (IsMultiShot(mode) && subPrompts is { Count: > 0 } && !string.IsNullOrWhiteSpace(subPrompts[0])) return subPrompts[0]!.Trim();
        return "";
    }

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
        VideoDurationMode.Sec15_Triple or VideoDurationMode.Sec15_AmbientSlow or VideoDurationMode.Sec15_OneTake => 15,
        VideoDurationMode.Sec30_Auto => 30,
        _ => 5,
    };

    /// <summary>추론 과금 가중치(5초 단위) — 5s 1x · 10s 2x · 15s 3x · 15s 원테이크 3x · 30s 자동 6x · 15s-Ambient 1x(로컬 후처리 무과금).</summary>
    public static int CostMultiplier(VideoDurationMode mode) => mode switch
    {
        VideoDurationMode.Sec15_OneTake => 3,
        VideoDurationMode.Sec30_Auto => 6,
        _ => ShotCount(mode),
    };

    /// <summary>메인 프롬프트 1개로 자동 분할되는 장편 모드(파트 입력 UI 없음).</summary>
    public static bool IsSinglePromptLong(VideoDurationMode mode) => mode is VideoDurationMode.Sec15_OneTake or VideoDurationMode.Sec30_Auto;

    /// <summary>작업 메타 — 엔진 네이티브 최대 클립 길이(초 · Wan 2.6 Cloud = 15, 그 외 5).</summary>
    public const string MetaNativeSeconds = "duration.native";
    /// <summary>작업 메타 — Wan 모델 계열(<see cref="PromptFlowStudio.Services.Wan.WanModelFamily"/> 이름).</summary>
    public const string MetaWanFamily = "wan.family";

    /// <summary>
    /// 단일 프롬프트 장편 분할 — (세그먼트 수, 세그먼트 길이 초).
    ///   ▸ 15초 원테이크: 네이티브 ≥15 → 1×15(진짜 원테이크) · 아니면 3×5 비트.
    ///   ▸ 30초 자동: ≥15 → 2×15 · ≥10 → 3×10 · 아니면 6×5.
    ///   ▸ 그 외 모드: (<see cref="ShotCount"/>, 5).
    /// </summary>
    public static (int Count, int Seconds) SegmentPlan(VideoDurationMode mode, int nativeMaxSeconds) => mode switch
    {
        VideoDurationMode.Sec15_OneTake => nativeMaxSeconds >= 15 ? (1, 15) : (3, 5),
        VideoDurationMode.Sec30_Auto => nativeMaxSeconds >= 15 ? (2, 15) : nativeMaxSeconds >= 10 ? (3, 10) : (6, 5),
        _ => (ShotCount(mode), (int)ShotSeconds),
    };

    private static readonly string[][] NarrativeBeats =
    {
        new[] { "continuous single take" },
        new[] { "opening beat: establish the scene and the subject", "closing beat: peak action and satisfying resolution" },
        new[] { "opening beat: establish the scene and the subject", "middle beat: the action develops and builds", "closing beat: peak moment and resolution" },
        new[] { "beat 1: establishing shot", "beat 2: the action begins", "beat 3: rising action", "beat 4: resolution" },
        new[] { "beat 1: establishing shot", "beat 2: the action begins", "beat 3: rising action", "beat 4: peak moment", "beat 5: resolution" },
        new[] { "beat 1: establishing wide shot", "beat 2: introduce the subject's action", "beat 3: build-up", "beat 4: peak action", "beat 5: reaction close-up", "beat 6: calm closing shot" },
    };

    /// <summary>세그먼트 i/n 서사 비트 구문(범위 밖은 빈 문자열).</summary>
    public static string BeatOf(int index, int count)
        => count >= 1 && count <= NarrativeBeats.Length && (uint)index < (uint)count ? NarrativeBeats[count - 1][index] : "";

    /// <summary>세그먼트 프롬프트 — 메인 + 서사 비트 + (다중 세그먼트면) 일관성 힌트. 단일 원테이크는 메인 그대로.</summary>
    public static string SegmentPrompt(string? main, int index, int count)
    {
        var m = (main ?? "").Trim();
        if (count <= 1) return m;
        var beat = BeatOf(index, count);
        var p = m.Length == 0 ? beat : beat.Length == 0 ? m : m.TrimEnd(',', ' ') + ", " + beat;
        return WithContinuityHint(p);
    }

    /// <summary>메타의 네이티브 길이(부재/불량 = 5).</summary>
    public static int NativeSecondsOf(BatchRenderJob job)
        => int.TryParse(MetaOf(job, MetaNativeSeconds), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : (int)ShotSeconds;

    /// <summary>그룹 컷 수 — 메타 <see cref="MetaShots"/> 우선, 없으면 모드 기본.</summary>
    public static int ShotCountOf(BatchRenderJob job)
        => int.TryParse(MetaOf(job, MetaShots), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : ShotCount(job.DurationMode);

    /// <summary>합본 대상 그룹 컷 여부(그룹 키 + 2컷 이상).</summary>
    public static bool IsGroupedShot(BatchRenderJob job) => job.StitchGroupId is { Length: > 0 } && ShotCountOf(job) > 1;

    public static bool IsMultiShot(VideoDurationMode mode) => ShotCount(mode) > 1;

    public static string Label(VideoDurationMode mode) => mode switch
    {
        VideoDurationMode.Sec10_Dual => "10초 · 2컷",
        VideoDurationMode.Sec15_Triple => "15초 · 3컷",
        VideoDurationMode.Sec15_AmbientSlow => "15초 앰비언트",
        VideoDurationMode.Sec15_OneTake => "15초 · 원테이크",
        VideoDurationMode.Sec30_Auto => "30초 · 자동",
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
        if (IsSinglePromptLong(mode)) return ExpandSinglePrompt(template, projectName);
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

    /// <summary>
    /// 단일 프롬프트 장편(15초 원테이크 · 30초 자동) 전개 — 메타 <see cref="MetaNativeSeconds"/> 기준 <see cref="SegmentPlan"/>.
    /// 1세그먼트면 템플릿 1건(DurationSec=15 · 네이티브 원테이크), 다중이면 독립 컷 그룹(서사 비트 + 일관성 힌트 · 1번만 참조 이미지 · 디졸브 합본).
    /// </summary>
    public static BatchRenderJob[] ExpandSinglePrompt(BatchRenderJob template, string? projectName)
    {
        ArgumentNullException.ThrowIfNull(template);
        var mode = template.DurationMode;
        var project = string.IsNullOrWhiteSpace(projectName) ? DefaultProject : projectName.Trim();
        var (n, sec) = SegmentPlan(mode, NativeSecondsOf(template));
        if (n <= 1)
        {
            return new[]
            {
                new BatchRenderJob
                {
                    Prompt = (template.Prompt ?? "").Trim(),
                    Seed = template.Seed,
                    Resolution = template.Resolution,
                    DurationSec = sec,   // 네이티브 원테이크(Wan 2.6 duration=15)
                    SubjectAnchor = template.SubjectAnchor,
                    ReferenceImagePath = template.ReferenceImagePath,
                    OutputPath = template.OutputPath,
                    DurationMode = mode,
                    Metadata = WithMeta(template.Metadata, MetaMode, mode.ToString(), MetaProject, project),
                    EngineKind = template.EngineKind,
                },
            };
        }

        var group = string.IsNullOrWhiteSpace(template.StitchGroupId) ? FFmpegVideoStitchService.NewGroupId() : template.StitchGroupId;
        var shots = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var jobs = new BatchRenderJob[n];
        for (var i = 0; i < n; i++)
        {
            var meta = WithMeta(FFmpegVideoStitchService.WithGroup(template.Metadata, group), MetaMode, mode.ToString(), MetaProject, project);
            meta[MetaShots] = shots;
            meta[MetaTransition] = nameof(CutTransitionMode.IndependentCrossfade);
            jobs[i] = new BatchRenderJob
            {
                Prompt = SegmentPrompt(template.Prompt, i, n),
                Seed = template.Seed,
                Resolution = template.Resolution,
                DurationSec = sec,
                CutIndex = i + 1,
                StartSec = i * sec,
                SubjectAnchor = template.SubjectAnchor,
                ReferenceImagePath = i == 0 ? template.ReferenceImagePath : null,
                OutputPath = PartOutputPath(template.OutputPath, i),
                DurationMode = mode,
                StitchGroupId = group,
                Metadata = meta,
                EngineKind = template.EngineKind,
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
