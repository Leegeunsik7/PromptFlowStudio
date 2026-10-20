using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PromptFlowStudio.Services.Nexus;

public enum NexusStageStatus : byte { Pending, Running, Done, Skipped, Failed }

/// <summary>시나리오 한 줄에서 추출한 제작 브리프.</summary>
public sealed record CreativeBrief(string Scenario, string Genre, string Mood, int DurationSec, bool Xr, bool Bci, bool Live, Localization.DubLanguage[] Languages, string[] Keywords);

/// <summary>3막 구조 샷(렌더 비용은 상대 단위).</summary>
public sealed record NexusShot(int Index, int Act, string Beat, float StartSec, float DurationSec, string Camera, string Lighting, float RenderCost, int Node);

/// <summary>오토 체이닝 단계(모듈 타입은 DI 결선 확인용).</summary>
public sealed class NexusStage
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Icon { get; init; }
    public required Type Module { get; init; }
    public required string[] DependsOn { get; init; }
    public float CostMs { get; set; }
    public float StartMs { get; set; }
    public bool Critical { get; set; }
    public bool Linked { get; set; }
    public bool Invoked { get; set; }
    public NexusStageStatus Status { get; set; }
    public string? Artifact { get; set; }
}

/// <summary>싱귤래리티 1회 실행 결과.</summary>
public sealed record SingularityRun(CreativeBrief Brief, NexusShot[] Shots, NexusStage[] Stages, string[] RenderNodes, float Makespan, float IdealMakespan,
    float CriticalPathMs, float Quality, int Linked, int Invoked, int Failed, AnnealResult Anneal, double TotalMs);

/// <summary>
/// [PromptFlow Studio v16.70.0] 자율 창작 싱귤래리티 마스터 오케스트레이터.
/// 시나리오 한 줄 → 규칙 기반 브리프(장르 · 무드 · 길이 · XR/BCI/라이브 · 대상 언어) → 3막 비트 샷 리스트(카메라 · 조명 · 렌더 비용) →
/// v16.x 모듈 카탈로그(각본 · 스토리보드 · 3DGS · ICVFX · 4D 월드 · VFX · Atmos · BCI · 스웜 리뷰 · 더빙 · QA · OTA · XR 송출) 에서 필요한 단계만 골라
/// 의존 DAG 를 구성하고 ASAP 스케줄 · 임계 경로를 계산한다. 샷 렌더 잡은 양자 영감 어닐러(QUBO)로 노드에 배치.
/// 실행은 위상 순서 · 단계별 예외 격리(실패 단계의 후속만 건너뛰는 [부분 구제]). 각 단계는 DI 에서 모듈 존재를 확인(Linked)하고,
/// 결선 실행기가 등록된 단계(더빙 등)만 실제 모듈을 호출(Invoked) — 그 외는 계획 산출물만 생성한다.
/// </summary>
public sealed class AutonomousCreativeSingularityOrchestrator
{
    public static readonly string[] RenderNodeNames = { "RTX-5090 로컬", "에지 GPU-서울", "클라우드 H200", "NPU 저전력" };
    public static readonly float[] RenderNodeCapacity = { 4f, 2.5f, 6f, 1f };
    public static readonly float[] RenderNodeLatency = { 0.2f, 0.6f, 1.6f, 0.3f };

    private static readonly (string Key, string Genre)[] GenreLexicon =
    {
        ("우주", "SF"), ("sf", "SF"), ("로봇", "SF"), ("미래", "SF"), ("사이버", "SF"), ("space", "SF"),
        ("추격", "액션"), ("액션", "액션"), ("폭발", "액션"), ("전투", "액션"), ("action", "액션"),
        ("사랑", "로맨스"), ("연인", "로맨스"), ("고백", "로맨스"), ("love", "로맨스"),
        ("귀신", "호러"), ("공포", "호러"), ("유령", "호러"), ("horror", "호러"),
        ("드래곤", "판타지"), ("마법", "판타지"), ("왕국", "판타지"), ("dragon", "판타지"),
        ("다큐", "다큐"), ("자연", "다큐"), ("역사", "다큐"),
    };
    private static readonly (string Key, string Mood)[] MoodLexicon =
    {
        ("슬픈", "멜랑콜리"), ("눈물", "멜랑콜리"), ("이별", "멜랑콜리"), ("어두운", "누아르"), ("밤", "누아르"), ("빗속", "누아르"),
        ("희망", "업리프팅"), ("축제", "업리프팅"), ("밝은", "업리프팅"), ("긴장", "서스펜스"), ("비밀", "서스펜스"), ("웅장", "에픽"), ("전설", "에픽"),
    };
    private static readonly (string Key, Localization.DubLanguage Lang)[] LangLexicon =
    {
        ("영어", Localization.DubLanguage.En), ("미국", Localization.DubLanguage.En), ("english", Localization.DubLanguage.En),
        ("일본", Localization.DubLanguage.Ja), ("중국", Localization.DubLanguage.Zh), ("스페인", Localization.DubLanguage.Es),
        ("프랑스", Localization.DubLanguage.Fr), ("독일", Localization.DubLanguage.De), ("아랍", Localization.DubLanguage.Ar),
    };
    private static readonly string[][] Beats =
    {
        new[] { "오프닝 이미지", "세계관 제시", "촉발 사건", "망설임" },
        new[] { "2막 진입", "시련과 동맹", "미드포인트 반전", "위기 고조", "모든 것을 잃다" },
        new[] { "각성", "클라이맥스", "결말 이미지" },
    };
    private static readonly string[] Cameras = { "와이드 크레인", "미디엄 돌리", "클로즈업 핸드헬드", "오버숄더", "드론 FPV", "익스트림 클로즈업", "스테디캠 트래킹" };

    private readonly IServiceProvider? _sp;
    private readonly QuantumHybridNeuralMeshEngine _mesh;
    private readonly Dictionary<string, Func<SingularityContext, string?>> _executors = new(StringComparer.Ordinal);
    private SingularityRun? _last;
    private long _runs;

    /// <summary>실행기 입력(브리프 · 샷 · 단계).</summary>
    public sealed record SingularityContext(CreativeBrief Brief, NexusShot[] Shots, NexusStage Stage, IServiceProvider? Services, CancellationToken Token);

    public AutonomousCreativeSingularityOrchestrator(IServiceProvider? sp = null, QuantumHybridNeuralMeshEngine? mesh = null)
    {
        _sp = sp;
        _mesh = mesh ?? (sp?.GetService(typeof(QuantumHybridNeuralMeshEngine)) as QuantumHybridNeuralMeshEngine) ?? new QuantumHybridNeuralMeshEngine();
        _executors["dub"] = DubExecutor;
    }

    public event Action? OnChange;

    public SingularityRun? Last => Volatile.Read(ref _last);
    public long Runs => Interlocked.Read(ref _runs);
    public QuantumHybridNeuralMeshEngine Mesh => _mesh;
    public string? LastAction { get; private set; }
    public NexusStage? Current { get; private set; }

    /// <summary>단계 실행기 결선(실 모듈 호출). 반환 문자열은 산출물 요약.</summary>
    public void RegisterExecutor(string stageId, Func<SingularityContext, string?> executor) { lock (_executors) _executors[stageId] = executor; }

    public static CreativeBrief Parse(string scenario)
    {
        var s = (scenario ?? "").Trim();
        var low = s.ToLowerInvariant();
        string genre = "드라마", mood = "시네마틱";
        foreach (var (k, g) in GenreLexicon) if (low.Contains(k, StringComparison.Ordinal)) { genre = g; break; }
        foreach (var (k, m) in MoodLexicon) if (low.Contains(k, StringComparison.Ordinal)) { mood = m; break; }
        var dur = 60;
        var m1 = Regex.Match(low, @"(\d{1,4})\s*(초|분|sec|s\b|min|m\b)");
        if (m1.Success && int.TryParse(m1.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            dur = m1.Groups[2].Value is "분" or "min" or "m" ? v * 60 : v;
        dur = Math.Clamp(dur, 15, 600);
        var langs = new List<Localization.DubLanguage>();
        foreach (var (k, l) in LangLexicon) if (low.Contains(k, StringComparison.Ordinal) && !langs.Contains(l)) langs.Add(l);
        if (low.Contains("글로벌", StringComparison.Ordinal) || low.Contains("global", StringComparison.Ordinal))
            foreach (var l in new[] { Localization.DubLanguage.En, Localization.DubLanguage.Ja, Localization.DubLanguage.Es }) if (!langs.Contains(l)) langs.Add(l);
        var xr = low.Contains("vr", StringComparison.Ordinal) || low.Contains("xr", StringComparison.Ordinal) || low.Contains("4d", StringComparison.Ordinal) || low.Contains("메타버스", StringComparison.Ordinal);
        var bci = low.Contains("뇌파", StringComparison.Ordinal) || low.Contains("bci", StringComparison.Ordinal);
        var live = low.Contains("라이브", StringComparison.Ordinal) || low.Contains("생중계", StringComparison.Ordinal) || low.Contains("live", StringComparison.Ordinal);
        var kw = new List<string>();
        foreach (var w in s.Split(new[] { ' ', ',', '.', '!', '?', '·' }, StringSplitOptions.RemoveEmptyEntries))
            if (w.Length >= 2 && kw.Count < 6 && !kw.Contains(w)) kw.Add(w);
        return new CreativeBrief(s, genre, mood, dur, xr, bci, live, langs.ToArray(), kw.ToArray());
    }

    /// <summary>3막 비트 샷 리스트(6~12샷 · 길이 25/50/25 배분 · 장르별 VFX 비용).</summary>
    public static NexusShot[] PlanShots(CreativeBrief b)
    {
        var count = Math.Clamp(b.DurationSec / 5, 6, 12);
        var vfx = b.Genre switch { "SF" => 1.8f, "액션" => 1.6f, "판타지" => 1.7f, "호러" => 1.2f, "다큐" => 0.6f, _ => 0.9f };
        int[] perAct = { Math.Max(1, count / 4), 0, Math.Max(1, count / 4) };
        perAct[1] = count - perAct[0] - perAct[2];
        float[] actShare = { 0.25f, 0.5f, 0.25f };
        var shots = new NexusShot[count];
        var t = 0f; var idx = 0;
        var seed = 0;
        foreach (var c in b.Scenario) seed = seed * 31 + c;
        for (var a = 0; a < 3; a++)
            for (var i = 0; i < perAct[a]; i++)
            {
                var beats = Beats[a];
                var beat = beats[Math.Min(beats.Length - 1, i * beats.Length / perAct[a])];
                var dur = b.DurationSec * actShare[a] / perAct[a];
                var climax = beat is "클라이맥스" or "미드포인트 반전" or "촉발 사건";
                var cam = Cameras[(int)((uint)(seed + idx * 7) % (uint)Cameras.Length)];
                if (climax) cam = b.Genre is "액션" or "SF" ? "드론 FPV" : "익스트림 클로즈업";
                var light = b.Mood switch { "누아르" => "로우키 · 사이드 하드라이트", "멜랑콜리" => "블루아워 · 소프트", "업리프팅" => "골든아워 · 하이키", "서스펜스" => "실루엣 · 역광", "에픽" => "볼류메트릭 갓레이", _ => "3점 조명" };
                var cost = 0.6f + vfx * (climax ? 1.6f : 1f) * (0.7f + 0.3f * ((seed >> (idx % 16)) & 3) / 3f) + dur * 0.04f;
                shots[idx] = new NexusShot(idx, a + 1, beat, t, dur, cam, light, MathF.Round(cost, 2), -1);
                t += dur; idx++;
            }
        return shots;
    }

    /// <summary>브리프 기반 단계 DAG(조건부 단계 포함).</summary>
    public static NexusStage[] PlanStages(CreativeBrief b, int shots)
    {
        var list = new List<NexusStage>
        {
            S("script", "각본 · 비트 시트", "📝", typeof(Screenplay.ScreenplayToStoryboardConverter), 120),
            S("board", "스토리보드 샷 리스트", "🎞", typeof(Screenplay.ScreenplayToStoryboardConverter), 40 * shots, "script"),
            S("gs", "3DGS 세트 재구성", "✨", typeof(Neural3d.GaussianSplattingStudioRunner), 90 * shots, "board"),
            S("icvfx", "ICVFX LED 볼륨 촬영", "🎥", typeof(Icvfx.VirtualProductionStudioRunner), 70 * shots, "gs"),
            S("vfx", "AI VFX 리라이팅", "🌈", typeof(Vfx.AiNeuralVfxRelightingEngine), 110 * shots, "icvfx"),
            S("atmos", "Dolby Atmos 믹스", "🔊", typeof(Sound.DolbyAtmosSpatialAudioMapper), 25 * shots, "board"),
            S("swarm", "에이전트 스웜 리뷰", "🤖", typeof(AgentFederation.MultiAgentSwarmFederationOrchestrator), 180, "vfx", "atmos"),
            S("qa", "자가치유 QA", "🧪", typeof(Production.AiSelfHealingQaTelemetryAuditor), 150, "swarm"),
            S("ota", "글로벌 OTA 배포", "🚀", typeof(Production.GlobalCloudEdgeOtaSyncEngine), 200, "qa"),
        };
        if (b.Xr || b.Genre is "SF" or "판타지")
        {
            list.Add(S("world4d", "4D 월드 모델", "🌍", typeof(World.WorldModelStudioRunner), 60 * shots, "gs"));
            Add(list, "vfx", "world4d");
        }
        if (b.Bci)
        {
            list.Add(S("bci", "BCI 감정 반응 편집", "🧠", typeof(Neuro.BciNeuroStudioRunner), 30 * shots, "vfx"));
            Add(list, "swarm", "bci");
        }
        if (b.Languages.Length > 0)
        {
            list.Add(S("dub", "글로벌 더빙 · 립싱크", "🗣", typeof(Localization.GlobalLocalizationStudioRunner), 260 * b.Languages.Length, "atmos", "vfx"));
            Add(list, "qa", "dub");
        }
        if (b.Xr || b.Live)
        {
            list.Add(S("xr", "XR 메타버스 송출", "🥽", typeof(Metaverse.SpatialXrMetaverseBroadcastingEngine), 140, "qa"));
        }
        return list.ToArray();

        static NexusStage S(string id, string label, string icon, Type t, float cost, params string[] deps) =>
            new() { Id = id, Label = label, Icon = icon, Module = t, CostMs = cost, DependsOn = deps };
        static void Add(List<NexusStage> l, string id, string dep)
        {
            var s = l.Find(x => x.Id == id)!;
            var arr = new string[s.DependsOn.Length + 1];
            s.DependsOn.CopyTo(arr, 0); arr[^1] = dep;
            l[l.IndexOf(s)] = new NexusStage { Id = s.Id, Label = s.Label, Icon = s.Icon, Module = s.Module, CostMs = s.CostMs, DependsOn = arr };
        }
    }

    /// <summary>위상 정렬(Kahn). 순환/미해결 의존 시 예외.</summary>
    public static NexusStage[] TopoSort(NexusStage[] stages)
    {
        var byId = new Dictionary<string, NexusStage>(StringComparer.Ordinal);
        foreach (var s in stages) byId[s.Id] = s;
        var indeg = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in stages)
        {
            indeg[s.Id] = 0;
            foreach (var d in s.DependsOn) if (!byId.ContainsKey(d)) throw new InvalidOperationException("미해결 의존: " + s.Id + " → " + d);
        }
        foreach (var s in stages) indeg[s.Id] = s.DependsOn.Length;
        var q = new Queue<NexusStage>();
        foreach (var s in stages) if (indeg[s.Id] == 0) q.Enqueue(s);
        var order = new List<NexusStage>();
        while (q.Count > 0)
        {
            var s = q.Dequeue();
            order.Add(s);
            foreach (var t in stages)
                if (Array.IndexOf(t.DependsOn, s.Id) >= 0 && --indeg[t.Id] == 0) q.Enqueue(t);
        }
        if (order.Count != stages.Length) throw new InvalidOperationException("단계 DAG 순환");
        return order.ToArray();
    }

    /// <summary>ASAP 시작 시각 · 임계 경로 표시. 반환: 임계 경로 길이(ms).</summary>
    public static float Schedule(NexusStage[] ordered)
    {
        var finish = new Dictionary<string, float>(StringComparer.Ordinal);
        var pred = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var s in ordered)
        {
            var start = 0f; string? p = null;
            foreach (var d in s.DependsOn) if (finish[d] > start) { start = finish[d]; p = d; }
            s.StartMs = start;
            finish[s.Id] = start + s.CostMs;
            pred[s.Id] = p;
        }
        var end = 0f; string? cur = null;
        foreach (var (id, f) in finish) if (f > end) { end = f; cur = id; }
        foreach (var s in ordered) s.Critical = false;
        while (cur is not null)
        {
            Array.Find(ordered, x => x.Id == cur)!.Critical = true;
            cur = pred[cur];
        }
        return end;
    }

    /// <summary>시나리오 한 줄 → 브리프 · 샷 · DAG · 양자 영감 렌더 배치 → 위상 순서 오토 체이닝 실행.</summary>
    public SingularityRun Run(string scenario, CancellationToken ct = default)
    {
        var t0 = Stopwatch.GetTimestamp();
        var brief = Parse(scenario);
        var shots = PlanShots(brief);
        var stages = TopoSort(PlanStages(brief, shots.Length));
        var critical = Schedule(stages);

        // 렌더 잡 배치(SQA QUBO) — 샷 × 노드 ≤ 64 큐비트.
        var cost = new float[shots.Length];
        for (var i = 0; i < shots.Length; i++) cost[i] = shots[i].RenderCost;
        var assign = new int[shots.Length];
        _mesh.AssignJobs(cost, RenderNodeCapacity, RenderNodeLatency, 0.02f, assign, seed: 0x5EED1670UL);
        var anneal = _mesh.LastAnneal;
        var load = new float[RenderNodeCapacity.Length];
        for (var i = 0; i < shots.Length; i++) { shots[i] = shots[i] with { Node = assign[i] }; load[assign[i]] += cost[i]; }
        var makespan = 0f; var capSum = 0f; var costSum = 0f;
        for (var m = 0; m < load.Length; m++) { makespan = MathF.Max(makespan, load[m] / RenderNodeCapacity[m]); capSum += RenderNodeCapacity[m]; }
        foreach (var c in cost) costSum += c;
        var ideal = costSum / capSum;

        int linked = 0, invoked = 0, failed = 0;
        foreach (var s in stages)
        {
            ct.ThrowIfCancellationRequested();
            if (Array.Exists(s.DependsOn, d => Array.Find(stages, x => x.Id == d)!.Status is NexusStageStatus.Failed or NexusStageStatus.Skipped))
            {
                s.Status = NexusStageStatus.Skipped;
                s.Artifact = "[부분 구제] 선행 단계 실패로 건너뜀";
                continue;
            }
            s.Linked = _sp?.GetService(s.Module) is not null;
            if (s.Linked) linked++;
            s.Status = NexusStageStatus.Running;
            Current = s;
            Announce(s.Icon + " " + s.Label + " 실행 중");
            Func<SingularityContext, string?>? exec;
            lock (_executors) _executors.TryGetValue(s.Id, out exec);
            try
            {
                var ctx = new SingularityContext(brief, shots, s, _sp, ct);
                string? art = exec?.Invoke(ctx);
                if (art is not null) { s.Invoked = true; invoked++; }
                s.Artifact = art ?? PlanArtifact(s, brief, shots);
                s.Status = NexusStageStatus.Done;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                s.Status = NexusStageStatus.Failed;
                s.Artifact = "실패: " + ex.Message;
                failed++;
                PromptFlowStudio.Program.Log("Nexus.Singularity." + s.Id, ex);
            }
        }
        Current = null;

        // 품질: 단계 완료율 · 렌더 균형(ideal/makespan) · 페이싱(3막 비율 준수).
        var done = 0;
        foreach (var s in stages) if (s.Status == NexusStageStatus.Done) done++;
        var balance = makespan > 0 ? ideal / makespan : 1;
        float a1 = 0, a2 = 0;
        foreach (var sh in shots) { if (sh.Act == 1) a1 += sh.DurationSec; else if (sh.Act == 2) a2 += sh.DurationSec; }
        var pacing = 1 - MathF.Min(1, MathF.Abs(a1 / brief.DurationSec - 0.25f) + MathF.Abs(a2 / brief.DurationSec - 0.5f));
        var quality = 0.5f * done / stages.Length + 0.3f * balance + 0.2f * pacing;

        var run = new SingularityRun(brief, shots, stages, RenderNodeNames, makespan, ideal, critical, quality, linked, invoked, failed, anneal,
            (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        Volatile.Write(ref _last, run);
        Interlocked.Increment(ref _runs);
        Announce("🌌 싱귤래리티 완료 · " + brief.Genre + "/" + brief.Mood + " · 샷 " + shots.Length.ToString(CultureInfo.InvariantCulture)
            + " · 단계 " + done.ToString(CultureInfo.InvariantCulture) + "/" + stages.Length.ToString(CultureInfo.InvariantCulture)
            + " · 품질 " + (quality * 100).ToString("0", CultureInfo.InvariantCulture) + "%");
        return run;
    }

    private static string PlanArtifact(NexusStage s, CreativeBrief b, NexusShot[] shots) => s.Id switch
    {
        "script" => "로그라인 · " + b.Genre + " / " + b.Mood + " · " + b.DurationSec.ToString(CultureInfo.InvariantCulture) + "초 · 비트 " + shots.Length.ToString(CultureInfo.InvariantCulture),
        "board" => "샷 " + shots.Length.ToString(CultureInfo.InvariantCulture) + "컷 · 카메라 " + shots[0].Camera + " 외",
        "gs" => "세트 가우시안 " + (shots.Length * 180_000).ToString("N0", CultureInfo.InvariantCulture) + "개 계획",
        "icvfx" => "LED 볼륨 프러스텀 " + shots.Length.ToString(CultureInfo.InvariantCulture) + "셋업",
        "world4d" => "4D 동적 월드 " + (b.DurationSec * 24).ToString(CultureInfo.InvariantCulture) + "프레임",
        "vfx" => "리라이팅 패스 · " + shots[0].Lighting,
        "atmos" => "Atmos 오브젝트 " + (shots.Length * 3).ToString(CultureInfo.InvariantCulture) + " · 7.1.4 베드",
        "bci" => "감정 곡선 기반 컷 길이 보정 계획",
        "dub" => "더빙 대상 " + b.Languages.Length.ToString(CultureInfo.InvariantCulture) + "개 언어 계획",
        "swarm" => "감독 · 편집 · 사운드 에이전트 합의 리뷰 계획",
        "qa" => "QA 체크리스트 " + (12 + shots.Length).ToString(CultureInfo.InvariantCulture) + "항목",
        "ota" => "에지 배포 매니페스트 계획",
        "xr" => "XR 송출 세션 계획",
        _ => "계획 산출물",
    };

    /// <summary>더빙 단계 실행기 — 로컬라이제이션 러너가 DI 에 있으면 첫 대상 언어로 실제 파이프라인을 1회 실행.</summary>
    private static string? DubExecutor(SingularityContext c)
    {
        if (c.Services?.GetService(typeof(Localization.GlobalLocalizationStudioRunner)) is not Localization.GlobalLocalizationStudioRunner loc || c.Brief.Languages.Length == 0)
            return null;
        var r = loc.Localize(0, c.Brief.Languages[0]);
        return "실행 · " + Localization.RealtimeNeuralAudioDubbingEngine.LanguageLabel(r.Target) + " 립싱크 상관 " + r.LipSync.AfterCorrelation.ToString("0.00", CultureInfo.InvariantCulture)
            + " · 음색 " + r.TimbreSimilarity.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void Announce(string action)
    {
        LastAction = action;
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Nexus.Singularity.OnChange", ex); }
    }
}
