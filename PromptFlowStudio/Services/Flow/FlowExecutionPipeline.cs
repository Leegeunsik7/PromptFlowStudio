using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject
using PromptFlowStudio.Services.Wan;          // WanPromptCompiler · WanRenderClient · MotionVector · WanPromptBuild

namespace PromptFlowStudio.Services.Flow;

/// <summary>
/// [PromptFlow Studio v5.5 · Task3] Wan 3.0 노드 실행기 — DAG 노드에 실제 실행 로직(Executor)을 부여하는 팩토리 + 기본 그래프 빌더.
///   ▸ [프롬프트]→[모션]→[Wan 비디오]→[프리뷰] 파이프라인이 생성되면 <see cref="NodeGraphEngine"/> 가 위상순으로 데이터를 조합,
///     Wan 노드 실행 시 <see cref="WanPromptCompiler"/> 로 프롬프트를 합성하고 <see cref="WanRenderClient"/> 로 백엔드에 전달한다(오프라인 큐 폴백 포함).
///   ▸ 각 노드의 실행 상태(Idle/Running/Completed/Faulted)는 엔진이 설정 → 캔버스가 헤더 테두리·스피너로 실시간 반영.
///   ⚠ 순수 BCL + 기존 v5.4.1 Wan 스택 재사용(외부 SDK 0). 데스크톱 net8.0-windows 컴파일 정합.
/// </summary>
public sealed class FlowExecutionPipeline
{
    private readonly WanRenderClient _wan;

    /// <summary>[v5.5.5] concat 노드 세그먼트 입력 포트 수(자동 체인 최대 세그먼트 = 이 값).</summary>
    public const int ConcatInputs = 8;

    /// <summary>노드 팔레트(캔버스 추가 버튼) — 종류/이모지/라벨.</summary>
    public static readonly (string Kind, string Emoji, string Label)[] Palette =
    {
        ("prompt",  "✍️", "프롬프트"),
        ("motion",  "🎯", "모션"),
        ("wan",     "🎬", "Wan 3.0"),
        ("preview", "🖥️", "프리뷰"),
    };

    public FlowExecutionPipeline(WanRenderClient wan) => _wan = wan;

    /// <summary>
    /// [v5.6.7] 완료된 Wan/Concat 결과의 대표 프레임(실측 첫 프레임 대조·갤러리용) 썸네일 URL.
    ///   ▸ 실제 렌더가 남긴 프레임(LastFrameUrl)이 있으면 그것을, 없으면 null(합성 프레임을 실측으로 위장하지 않음).
    /// </summary>
    public static string? ResultFrameThumb(WanVideoRef? v)
        => !string.IsNullOrWhiteSpace(v?.LastFrameUrl) ? v!.LastFrameUrl : null;

    /// <summary>닥 다중 세그먼트 체인 결과(병합 URL + 메시지).</summary>
    public sealed record DockChainResult(bool Ok, string Message, string? MergedUrl);

    /// <summary>[v5.7.2] 세그먼트 단계별 라이브 진행 보고 — 단계/총단계/메시지/퍼센트/완료 썸네일/완료 플래그.</summary>
    public sealed record SegmentProgress(int StepIndex, int TotalSteps, string StepMessage, int ProgressPercent, string? CompletedThumbUrl = null, bool Completed = false);

    /// <summary>
    /// [v5.7.1·v5.7.2] 플로팅 닥 N컷 연쇄 생성 — i2v 체인(이전 컷 마지막 프레임 → 다음 컷 입력) 순차 생성 후 Concat 병합.
    ///   ▸ <see cref="WanRenderClient"/> 직결(백엔드 미구성 시 스텁 폴백) · <see cref="IProgress{T}"/>(SegmentProgress)로 단계 로그 + 완료 컷 썸네일 실시간 통지.
    /// </summary>
    public async Task<DockChainResult> RunShortsChainAsync(string prompt, int segments, StoryboardProject? project, IProgress<SegmentProgress>? progress = null, CancellationToken ct = default, IReadOnlyList<string>? cutPrompts = null, IReadOnlyList<string>? existingCompletedUrls = null)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return new(false, "프롬프트가 비어 있습니다.", null);
        segments = Math.Clamp(segments, 1, ConcatInputs);

        var urls = new List<string>();
        string? lastFrame = null;

        // v5.7.7: 끊긴 체인 이어보기 — 이미 완성/구제된 컷을 복원하고 마지막 프레임을 I2V 시드 앵커로 승격(중복 렌더 0).
        if (existingCompletedUrls is not null)
        {
            for (var k = 0; k < existingCompletedUrls.Count && urls.Count < segments; k++)
            {
                var done = existingCompletedUrls[k];
                if (string.IsNullOrEmpty(done)) continue;
                urls.Add(done);
                lastFrame = done;   // 끊긴 지점의 연속성 앵커(다음 컷 imageRef)
            }
        }
        var startIdx = urls.Count;         // 이미 완성된 컷 다음 번호부터 신규 렌더
        var resuming = startIdx > 0;

        try
        {
            for (var i = startIdx; i < segments; i++)
            {
                ct.ThrowIfCancellationRequested();   // v5.7.3: 취소 시 OperationCanceledException → 세그먼트 루프 조기 탈출
                progress?.Report(new SegmentProgress(i + 1, segments, $"[{i + 1}/{segments}] 컷 {i + 1} I2V {(resuming ? "이어 " : "연쇄 ")}생성 중…", i * 80 / segments, lastFrame, false));
                // v5.7.6: 컷별 사용자 오버라이드 최우선 → 없으면 단일 씨드 자동 연장(seamless).
                var custom = cutPrompts is not null && i < cutPrompts.Count ? cutPrompts[i] : null;
                var segPrompt = !string.IsNullOrWhiteSpace(custom) ? custom!.Trim()
                    : segments == 1 ? prompt : $"{prompt} — cut {i + 1}/{segments}, seamless continuation, consistent identity";
                var build = new WanPromptBuild(segPrompt, 30, Array.Empty<string>(), null);
                var r = await _wan.SubmitAsync(build, project, ct, lastFrame).ConfigureAwait(false);   // imageRef=이전 마지막 프레임(i2v 정체성 연속)
                if (!string.IsNullOrEmpty(r.VideoUrl)) urls.Add(r.VideoUrl!);
                lastFrame = r.LastFrameUrl ?? r.VideoUrl;
                progress?.Report(new SegmentProgress(i + 1, segments, $"[{i + 1}/{segments}] 컷 {i + 1} 완성 ✓", (i + 1) * 80 / segments, lastFrame, true));   // 완료 썸네일 통지
            }

            if (urls.Count == 0) return new(false, "세그먼트 생성 실패(백엔드/스텁 확인).", null);
            if (urls.Count == 1)
            {
                progress?.Report(new SegmentProgress(1, 1, "✅ 1컷 생성 완료", 100, urls[0], true));
                return new(true, "1컷 생성 완료", urls[0]);
            }

            ct.ThrowIfCancellationRequested();   // v5.7.3: 병합 직전 취소 확인
            progress?.Report(new SegmentProgress(segments, segments, "🎞️ Concat 롱폼 자동 병합 중…", 92, lastFrame, false));
            var concat = await _wan.ConcatAsync(urls, project, ct).ConfigureAwait(false);   // 롱폼 병합
            var merged = concat.VideoUrl ?? urls[^1];
            progress?.Report(new SegmentProgress(segments, segments, concat.Ok ? (resuming ? "✅ 숏츠 체인 이어서 최종 완료" : "✅ 숏츠 체인 최종 완료") : "⚠ 병합 실패", 100, merged, concat.Ok));
            return concat.Ok
                ? new(true, $"{urls.Count}컷 {(resuming ? "이어서 병합" : "연쇄 생성 + 병합")} 완료", merged)
                : new(false, concat.Message, merged);
        }
        catch (Exception) when (urls.Count > 0)
        {
            RescuePartial(urls, project, startIdx);   // v5.7.6/v5.7.7: 취소·중간 실패 시 완성 세그먼트 자동 구제(이어보기 계승분은 skip=중복 방지)
            throw;                          // 취소·오류는 그대로 전파(caller UI 분기: 중단 vs 오류)
        }
    }

    // v5.7.6: 완성된 세그먼트 URL 을 Project.Gallery + 스토리보드 컷에 부분 자산(Partial Snapshot)으로 보존 등록.
    // v5.7.7: alreadyRegistered(이어보기로 이미 계승·저장된 컷 수)만큼 건너뛰어 중복 구제 방지.
    private static void RescuePartial(List<string> urls, StoryboardProject? project, int alreadyRegistered = 0)
    {
        if (project is null) return;
        for (var i = Math.Max(alreadyRegistered, 0); i < urls.Count; i++)
        {
            var url = urls[i];
            if (string.IsNullOrEmpty(url)) continue;
            var img = WanSampleFactory.IsImageUrl(url);   // 스텁=이미지 data URL · 백엔드=비디오 URL 구분
            project.Gallery.Insert(0, new GalleryAsset { Url = url, Kind = img ? GalleryAssetKind.Image : GalleryAssetKind.Video, Label = $"부분 구제 컷{i + 1}", CreatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            project.Scenes.Add(new StoryboardScene { Prompt = $"[부분 구제] 컷 {i + 1}", Status = SceneStatus.Completed, VideoUrl = img ? null : url, PosterUrl = img ? url : null });
        }
    }

    /// <summary>기본 파이프라인 그래프 생성 — 프롬프트→모션→Wan→프리뷰 연결까지 완비.</summary>
    public NodeGraphEngine BuildDefaultGraph(StoryboardProject? project)
    {
        var e = new NodeGraphEngine();
        var prompt = NewNode(e, "prompt", 30, 40, project);
        var motion = NewNode(e, "motion", 285, 40, project);
        var wan = NewNode(e, "wan", 540, 40, project);
        var preview = NewNode(e, "preview", 800, 40, project);

        e.Connect(prompt.Id, prompt.Outputs[0].Id, motion.Id, motion.Inputs[0].Id);   // prompt
        e.Connect(motion.Id, motion.Outputs[0].Id, wan.Id, wan.Inputs[0].Id);          // prompt passthrough
        e.Connect(motion.Id, motion.Outputs[1].Id, wan.Id, wan.Inputs[1].Id);          // motion
        e.Connect(wan.Id, wan.Outputs[0].Id, preview.Id, preview.Inputs[0].Id);        // result
        return e;
    }

    /// <summary>종류별 노드 생성(포트 + Executor 부여) 후 엔진에 추가.</summary>
    public FlowNode NewNode(NodeGraphEngine engine, string kind, double x, double y, StoryboardProject? project)
    {
        var node = CreateNode(kind, $"{kind}-{Guid.NewGuid().ToString("N")[..6]}", project);
        node.X = x; node.Y = y;
        engine.AddNode(node);
        return node;
    }

    /// <summary>지정 Id 로 종류별 노드 생성(엔진 미추가) — 그래프 역직렬화 시 Id/포트Id 보존용.</summary>
    public FlowNode CreateNode(string kind, string id, StoryboardProject? project) => kind switch
    {
        "prompt" => BuildPrompt(id),
        "motion" => BuildMotion(id),
        "wan" => BuildWan(id, project),
        "preview" => BuildPreview(id),
        "imagemix" => BuildImageMix(id),
        "concat" => BuildConcat(id, project),
        _ => BuildPrompt(id),
    };

    /// <summary>그래프 실행(엔진 위임 · 편의 API).</summary>
    public Task<GraphExecutionResult> RunAsync(NodeGraphEngine engine, CancellationToken ct = default)
        => engine is null ? Task.FromResult(new GraphExecutionResult(false, 0, 0, "그래프가 없습니다.")) : engine.ExecuteAsync(ct);

    /// <summary>
    /// [v5.5.4] 무인 N회 I2V 체인 그래프를 대상 엔진에 구성(기존 그래프 Clear).
    ///   프롬프트→모션→Wan[→imagemix→Wan]×(N-1)→프리뷰. 위상 실행 1회로 Wan(i).lastframe → imagemix(i).frame → Wan(i+1).image 가
    ///   DAG 전파를 통해 자동 체이닝된다(각 노드 정확히 1회 실행 · O(N)).
    /// </summary>
    public void BuildChainInto(NodeGraphEngine engine, int count, StoryboardProject? project)
    {
        if (engine is null) return;
        engine.Clear();
        var n = Math.Clamp(count, 1, ConcatInputs);   // concat 용량으로 클램프
        const double dx = 240; double x = 20; const double y = 60;
        var wans = new List<FlowNode>();

        var prompt = NewNode(engine, "prompt", x, y, project); x += dx;
        var motion = NewNode(engine, "motion", x, y, project); x += dx;
        engine.Connect(prompt.Id, Pid(prompt, false, "prompt"), motion.Id, Pid(motion, true, "prompt"));

        var wan = NewNode(engine, "wan", x, y, project); x += dx; wans.Add(wan);
        engine.Connect(motion.Id, Pid(motion, false, "prompt"), wan.Id, Pid(wan, true, "prompt"));
        engine.Connect(motion.Id, Pid(motion, false, "motion"), wan.Id, Pid(wan, true, "motion"));

        for (var i = 1; i < n; i++)
        {
            var mix = NewNode(engine, "imagemix", x, y + 150, project); x += dx;
            engine.Connect(wan.Id, Pid(wan, false, "lastframe"), mix.Id, Pid(mix, true, "frame"));   // 이전 Wan 마지막 프레임 → 믹서
            var next = NewNode(engine, "wan", x, y, project); x += dx; wans.Add(next);
            engine.Connect(mix.Id, Pid(mix, false, "image"), next.Id, Pid(next, true, "image"));     // i2v 소스
            engine.Connect(mix.Id, Pid(mix, false, "prompt"), next.Id, Pid(next, true, "prompt"));   // i2v 프롬프트
            wan = next;
        }

        // 종단: 모든 Wan 세그먼트 → concat(롱폼 병합) → preview(단일 재생)
        var concat = NewNode(engine, "concat", x, y, project); x += dx;
        for (var i = 0; i < wans.Count && i < ConcatInputs; i++)
            engine.Connect(wans[i].Id, Pid(wans[i], false, "video"), concat.Id, Pid(concat, true, $"seg{i}"));
        var preview = NewNode(engine, "preview", x, y, project);
        engine.Connect(concat.Id, Pid(concat, false, "video"), preview.Id, Pid(preview, true, "video"));
    }

    /// <summary>노드의 이름으로 포트 Id 조회(input=true 면 입력 포트).</summary>
    private static string Pid(FlowNode n, bool input, string name)
        => (input ? n.Inputs : n.Outputs).First(p => p.Name == name).Id;

    // ── 노드 팩토리(포트 + Executor) ──

    private static FlowNode BuildPrompt(string id)
    {
        var n = new FlowNode(id, "prompt", "프롬프트", "✍️") { Param = "cinematic establishing shot, golden hour" };
        n.AddOutput("prompt", "prompt");
        n.Executor = (_, _) => Done(("prompt", (object?)(string.IsNullOrWhiteSpace(n.Param) ? "cinematic shot" : n.Param)));
        return n;
    }

    private static FlowNode BuildMotion(string id)
    {
        var n = new FlowNode(id, "motion", "모션", "🎯") { Param = "zoom-in" };
        n.AddInput("prompt", "prompt");
        n.AddOutput("prompt", "prompt");
        n.AddOutput("motion", "motion");
        n.Executor = (inputs, _) =>
        {
            var pass = inputs.TryGetValue("prompt", out var p) ? p : null;
            IReadOnlyList<MotionVector> motion = new List<MotionVector> { ParseMotion(n.Param) };
            return Done(("prompt", pass), ("motion", motion));
        };
        return n;
    }

    private FlowNode BuildWan(string id, StoryboardProject? project)
    {
        var n = new FlowNode(id, "wan", "Wan 3.0 비디오", "🎬");
        n.AddInput("prompt", "prompt");
        n.AddInput("motion", "motion");
        n.AddInput("image", "image");    // v5.5.3: i2v 소스(imagemix 연결 시) — 있으면 Image-to-Video 모드로 루프 완결
        n.AddOutput("video", "video");   // v5.5.1: WanVideoRef(video_url + job_id) 출력 → 프리뷰 노드 인라인 재생
        n.AddOutput("lastframe", "image"); // v5.5.4: 렌더 마지막 프레임 URL → 다음 imagemix 로 자동 체이닝(무인 N회)
        n.Executor = async (inputs, ct) =>
        {
            var promptText = inputs.TryGetValue("prompt", out var p) ? p as string : null;
            var motion = inputs.TryGetValue("motion", out var m) ? m as IReadOnlyList<MotionVector> : null;
            var imageRef = inputs.TryGetValue("image", out var im) ? im as string : null;   // i2v 프레임(data URL)

            var phrases = WanPromptCompiler.CompileMotion(motion);
            var intensity = WanPromptCompiler.AggregateIntensity(motion);
            var basePrompt = string.IsNullOrWhiteSpace(promptText) ? "cinematic video" : promptText!;
            var full = string.Join(", ", new[] { basePrompt }.Concat(phrases));   // v5.6.4: 코어는 SubmitAsync 에서 설정 기반 단일 주입
            var build = new WanPromptBuild(full, intensity, phrases, null);

            var r = await _wan.SubmitAsync(build, project, ct, imageRef).ConfigureAwait(false);
            if (!r.Ok && !r.Queued) throw new InvalidOperationException(r.Message);   // 완전 실패만 Faulted(큐 적재는 성공 간주)

            var videoUrl = r.VideoUrl; var status = r.Status; var jobId = r.JobId; var lastFrame = r.LastFrameUrl;
            // 비동기(Zero-Credit 로컬/커스텀) Wan 백엔드: video_url 이 아직 없으면 완료까지 폴링(상한 60초).
            if (r.Ok && string.IsNullOrEmpty(videoUrl))
            {
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    var poll = await _wan.PollStatusAsync(jobId, project, ct).ConfigureAwait(false);
                    status = poll.Status;
                    if (!string.IsNullOrEmpty(poll.LastFrameUrl)) lastFrame = poll.LastFrameUrl;
                    if (!string.IsNullOrEmpty(poll.VideoUrl)) { videoUrl = poll.VideoUrl; break; }
                    if (status is "failed" or "error") break;
                }
            }
            if (string.IsNullOrEmpty(lastFrame)) lastFrame = videoUrl;   // 폴백: 마지막 프레임 미제공 시 비디오 URL 을 체인 소스로

            var finalStatus = !string.IsNullOrEmpty(videoUrl) ? "completed" : (r.Queued ? "queued" : (string.IsNullOrEmpty(status) ? "running" : status));
            return Dict(
                ("video", (object?)new WanVideoRef(videoUrl, jobId, finalStatus, lastFrame)),
                ("lastframe", (object?)lastFrame));
        };
        return n;
    }

    private static FlowNode BuildPreview(string id)
    {
        var n = new FlowNode(id, "preview", "프리뷰", "🖥️");
        n.AddInput("video", "video");     // v5.5.1: 상위 Wan 노드의 WanVideoRef 수신 → 인라인 HTML5 <video> 재생
        n.AddOutput("summary", "any");
        n.AddOutput("frame", "image");    // v5.5.2: 📸 추출된 키프레임(data URL) → I2V 믹싱 노드로 전파
        n.Executor = (inputs, _) =>
        {
            var vref = inputs.TryGetValue("video", out var v) ? v as WanVideoRef : null;
            var summary = vref is null ? "(대기)"
                : vref.HasVideo ? $"▶ 재생 준비 · {vref.JobId}"
                : $"⏳ {vref.Status} · {vref.JobId}";
            // 추출 프레임은 node.Param 에 보존(ResetStates 가 포트 Value 를 지워도 재실행 시 복원 → 전파 유지).
            object? frame = string.IsNullOrEmpty(n.Param) ? null : n.Param;
            return Done(("summary", (object?)summary), ("frame", frame));
        };
        return n;
    }

    // v5.5.2: I2V(Image-to-Video) 프레임 믹싱 노드 — 추출 키프레임을 받아 i2v 연속 프롬프트를 산출.
    private static FlowNode BuildImageMix(string id)
    {
        var n = new FlowNode(id, "imagemix", "I2V 프레임 믹싱", "🖼️");
        n.AddInput("frame", "image");
        n.AddOutput("image", "image");    // v5.5.3: i2v 소스 이미지(프레임 data URL) 전파 → 새 Wan 노드 image 입력(루프 완결)
        n.AddOutput("prompt", "prompt");  // i2v 텍스트 프롬프트
        n.Executor = (inputs, _) =>
        {
            var frame = inputs.TryGetValue("frame", out var f) ? f as string : null;
            var prompt = string.IsNullOrEmpty(frame)
                ? "image-to-video continuation, seamless motion"
                : "image-to-video continuation from the extracted keyframe, seamless temporal motion, consistent identity, cinematic";
            return Done(("image", (object?)frame), ("prompt", (object?)prompt));
        };
        return n;
    }

    // v5.5.5: N개 Wan 세그먼트(video) → 단일 MP4 병합 노드.
    private FlowNode BuildConcat(string id, StoryboardProject? project)
    {
        var n = new FlowNode(id, "concat", "롱폼 병합", "🎞️");
        for (var i = 0; i < ConcatInputs; i++) n.AddInput($"seg{i}", "video");   // 고정 세그먼트 입력(직렬화 결정적)
        n.AddOutput("video", "video");
        n.Executor = async (inputs, ct) =>
        {
            var urls = new List<string>();
            for (var i = 0; i < ConcatInputs; i++)
                if (inputs.TryGetValue($"seg{i}", out var v) && v is WanVideoRef vr && vr.HasVideo) urls.Add(vr.VideoUrl!);
            if (urls.Count == 0) return Dict(("video", (object?)new WanVideoRef(null, "", "no-segments")));

            var r = await _wan.ConcatAsync(urls, project, ct).ConfigureAwait(false);
            if (!r.Ok) throw new InvalidOperationException(r.Message);   // 병합 실패 → 노드 Faulted
            return Dict(("video", (object?)new WanVideoRef(r.VideoUrl, r.JobId, "completed")));
        };
        return n;
    }

    // ── 헬퍼 ──
    private static MotionVector ParseMotion(string? param) => (param ?? "").Trim().ToLowerInvariant() switch
    {
        "pan-right"     => new MotionVector(0, 0.45, MotionKind.Camera),
        "pan-left"      => new MotionVector(180, 0.45, MotionKind.Camera),
        "tilt-up"       => new MotionVector(90, 0.40, MotionKind.Camera),
        "tilt-down"     => new MotionVector(270, 0.40, MotionKind.Camera),
        "zoom-out"      => new MotionVector(270, 0.55, MotionKind.Zoom),
        "subject-right" => new MotionVector(0, 0.50, MotionKind.Subject),
        _               => new MotionVector(90, 0.55, MotionKind.Zoom),   // zoom-in 기본
    };

    private static Task<IReadOnlyDictionary<string, object?>> Done(params (string Key, object? Value)[] kv) => Task.FromResult(Dict(kv));

    private static IReadOnlyDictionary<string, object?> Dict(params (string Key, object? Value)[] kv)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }
}
