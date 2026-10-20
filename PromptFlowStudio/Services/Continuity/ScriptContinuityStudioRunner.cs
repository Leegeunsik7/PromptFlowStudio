using System.Numerics;
using PromptFlowStudio.Services.Atmosphere;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Mocap;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.Continuity;

/// <summary>샷 결과(타임라인 · 값 형식).</summary>
public readonly record struct ShotRecord(int Shot, int Scene, CutKind Cut, int Violations, int BibleConflicts, float ScoreBefore, float ScoreAfter, int CategoryMask, float Coherence);

/// <summary>
/// [PromptFlow Studio v16.88.0] 📜 AI 자율 스크립트 연속성 &amp; 씬 정합성 스튜디오 러너(60 Hz · 샷 3 s · <see cref="TickOnce"/> 무할당 · 동기 검증 모드).
/// 대화 씬(인물 2 · 소품 6 · 우천 산장) 8샷 순환 — 샷마다 의도적 연속성 오류 주입(소품 이동 · 흉터 반전 · 색온도 · 건조 코트 · 180° 축 · 포즈 · 소품 소실 · 사막 우천/시대 소품/사망 인물) →
/// 컷 시점 검증 + 바이블 진단 → 샷 중 자동 보정(USD 트랜스폼 · 3DGS · 룩 · 의상 · 마크 · 포즈 · 강수) → 샷 끝 잔차 재검증(피드백) → 타임라인.
/// 샷 상태 = 샷 경계 상태(샷 내부 연기 변화는 모델링하지 않음).
/// </summary>
public sealed class ScriptContinuityStudioRunner : IDisposable
{
    public const int Hz = 60, ShotFrames = 180, Plan = 8, MaxRecords = 64, Props = 6;
    public const string DefaultBible =
        "# 캐릭터\n캐릭터 미나: 머리=빨강, 흉터=왼뺨, 주손=오른손, 의상=트렌치코트\n캐릭터 준: 머리=흑발, 안경=있음, 의상=가죽재킷, 사망=씬 6\n" +
        "# 세계관\n장소 산장: 비=가능, 기온=-5..20, 위도=37.5\n장소 사막: 비=불가, 눈=불가, 기온=15..48\n시대 1920: 금지=스마트폰, 드론";
    private static readonly string[] PropNames = { "찻잔", "스마트폰", "책", "의자", "램프", "가방" };

    private readonly object _gate = new();
    private readonly RealtimeScriptSupervisorContinuityEngine _engine;
    private readonly NeuralSceneCoherenceStoryBibleMatcher _bible;
    private readonly AutomaticContinuityCorrectionGuard _guard;
    private readonly ContinuitySnapshot _reference = new(), _current = new();
    private readonly SceneMeta _meta = new();
    private readonly UsdLayer _usd = new("continuity.usda");
    private readonly GaussianScene _splats = new(Props * 8 + 64);
    private readonly ShotRecord[] _records = new ShotRecord[MaxRecords];
    private readonly Vector3[] _rest = new Vector3[MocapSkeleton.Body];
    private readonly Quaternion[] _local = new Quaternion[MocapSkeleton.Body], _global = new Quaternion[MocapSkeleton.Body];
    private readonly Vector3[] _fk = new Vector3[MocapSkeleton.Body];
    private int _recHead, _recCount, _shotFrame, _planIndex, _shot, _scene = 1;
    private int _idMina, _idJun, _idRed, _idScarL, _idScarR, _idRight, _idTrench, _idLeather, _idBlack, _idCabin, _idDesert, _id1920, _idPhone;
    private float _storyHours = 18.5f, _scoreBefore;
    private int _bibleCount;
    private CutKind _cut = CutKind.Neutral;
    private CancellationTokenSource? _cts;
    private long _frame;

    public ScriptContinuityStudioRunner(IServiceProvider sp)
        : this((RealtimeScriptSupervisorContinuityEngine?)sp.GetService(typeof(RealtimeScriptSupervisorContinuityEngine)) ?? new RealtimeScriptSupervisorContinuityEngine(),
               (NeuralSceneCoherenceStoryBibleMatcher?)sp.GetService(typeof(NeuralSceneCoherenceStoryBibleMatcher)) ?? new NeuralSceneCoherenceStoryBibleMatcher(),
               (AutomaticContinuityCorrectionGuard?)sp.GetService(typeof(AutomaticContinuityCorrectionGuard)) ?? new AutomaticContinuityCorrectionGuard()) { }

    public ScriptContinuityStudioRunner(RealtimeScriptSupervisorContinuityEngine engine, NeuralSceneCoherenceStoryBibleMatcher bible, AutomaticContinuityCorrectionGuard guard)
    {
        _engine = engine; _bible = bible; _guard = guard;
        for (var i = 0; i < _local.Length; i++) _local[i] = Quaternion.Identity;
        BibleText = DefaultBible;
        LoadBible(DefaultBible);
        for (var i = 0; i < Props; i++)
        {
            var prim = _usd.AddPath("/World/Props/" + PropNames[i] switch { "찻잔" => "Cup", "스마트폰" => "Phone", "책" => "Book", "의자" => "Chair", "램프" => "Lamp", _ => "Bag" }, UsdSpec.Def, UsdPrimType.Cube);
            _guard.MapPropToPrim(i, prim);
            _guard.MapPropToSplats(i, i * 8, 8);
        }
        _guard.BindUsd(_usd); _guard.BindSplats(_splats);
        NewShot();
    }

    public event Action? OnChange;

    public RealtimeScriptSupervisorContinuityEngine Engine => _engine;
    public NeuralSceneCoherenceStoryBibleMatcher Bible => _bible;
    public AutomaticContinuityCorrectionGuard Guard => _guard;
    public ContinuitySnapshot Reference => _reference;
    public ContinuitySnapshot Current => _current;
    public SceneMeta Meta => _meta;
    public UsdLayer Usd => _usd;
    public GaussianScene Splats => _splats;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public bool AutoCorrect { get; set; } = true;
    public bool InjectErrors { get; set; } = true;
    public bool AsyncVerify { get; set; }
    public string BibleText { get; private set; }
    public int Shot => _shot;
    public int Scene => _scene;
    public int PlanIndex => _planIndex;
    public CutKind Cut => _cut;
    public float ShotProgress => _shotFrame / (float)ShotFrames;
    public int RecordCount => _recCount;
    public ShotRecord Record(int back) => _records[(_recHead - 1 - back + 2 * MaxRecords) % MaxRecords];
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public static string PropName(int i) => i >= 0 && i < PropNames.Length ? PropNames[i] : "—";
    public static string CharName(int c) => c == 0 ? "미나" : c == 1 ? "준" : "—";

    public static string PlanName(int p) => p switch
    {
        0 => "설정 샷", 1 => "찻잔 이동", 2 => "흉터 반전 · 색온도", 3 => "코트 건조 · 축 위반", 4 => "30분 경과(정상)", 5 => "팔 포즈", 6 => "책 소실", _ => "사막 우천 · 시대 소품 · 사망 인물",
    };

    public int LoadBible(string? text)
    {
        int n;
        lock (_gate)
        {
            n = _bible.Load(text);
            if (n > 0) BibleText = text!;
            _idMina = _bible.Intern("미나"); _idJun = _bible.Intern("준"); _idRed = _bible.Intern("빨강"); _idBlack = _bible.Intern("흑발");
            _idScarL = _bible.Intern("왼뺨"); _idScarR = _bible.Intern("오른뺨"); _idRight = _bible.Intern("오른손"); _idTrench = _bible.Intern("트렌치코트"); _idLeather = _bible.Intern("가죽재킷");
            _idCabin = _bible.Intern("산장"); _idDesert = _bible.Intern("사막"); _id1920 = _bible.Intern("1920"); _idPhone = _bible.Intern("스마트폰");
        }
        LastAction = _bible.LastAction;
        Raise();
        return n;
    }

    public void NewShot()
    {
        lock (_gate)
        {
            _shot = 0; _scene = 1; _planIndex = 0; _shotFrame = 0; _storyHours = 18.5f; _recHead = _recCount = 0; _frame = 0;
            _bible.ResetTimeline();
            BuildTruth(_current);
            _reference.CopyFrom(_current);
            _cut = CutKind.Neutral;
            WriteScene();
        }
        LastAction = "🎬 새 씬 — 산장 대화 8샷";
        Raise();
    }

    /// <summary>정답 상태(샷 진행 t 반영): 인물 2 마주봄 · 소품 6 · 우천 · 주광 키 라이트.</summary>
    private void BuildTruth(ContinuitySnapshot s)
    {
        s.Shot = _shot; s.StoryTimeHours = _storyHours; s.PropCount = Props;
        for (var i = 0; i < ContinuitySnapshot.MaxProps; i++) { s.PropPresent[i] = i < Props; s.PropHandled[i] = false; }
        s.SetProp(0, new Vector3(0.2f, 0.78f, 0.1f)); s.SetProp(1, new Vector3(-0.3f, 0.78f, 0.05f)); s.SetProp(2, new Vector3(0.05f, 0.78f, -0.25f));
        s.SetProp(3, new Vector3(1.2f, 0f, 0f)); s.SetProp(4, new Vector3(-1.4f, 0f, 1.1f)); s.SetProp(5, new Vector3(-1.1f, 0f, -0.4f));
        for (var i = 0; i < Props; i++) s.PropYaw[i] = 15f * i;
        if (_planIndex == 7) s.PropPresent[1] = true;
        var sun = AtmosphericScatteringLightingEngine.SolarPosition(172, s.StoryTimeHours % 24f, 37.5f);
        s.KeyLight = AtmosphericScatteringLightingEngine.FromAngles(MathF.Max(sun.Elevation, 3f), sun.Azimuth);
        s.ColorTempK = 4300f; s.Intensity = 1f; s.Precipitation = 6f;
        s.Camera = new Vector3(0.3f, 1.5f, -3.2f); s.CameraForward = Vector3.UnitZ;
        for (var c = 0; c < ContinuitySnapshot.MaxCharacters; c++) s.CharPresent[c] = c < 2;
        // 인물: 미나(0) x = −0.7 → +X 응시 · 준(1) x = +0.7 → −X 응시 · 대화 제스처
        for (var c = 0; c < 2; c++)
        {
            for (var j = 0; j < _local.Length; j++) _local[j] = Quaternion.Identity;
            _local[MocapSkeleton.LShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -1.25f);
            _local[MocapSkeleton.RShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.25f) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f - 0.1f * c);
            MocapSkeleton.ForwardKinematics(MocapSkeleton.RestOffset, _local, Vector3.Zero, _fk, _global);
            var yaw = c == 0 ? MathF.PI / 2f : -MathF.PI / 2f;
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw); var root = new Vector3(c == 0 ? -0.7f : 0.7f, 0f, 0f);
            for (var j = 0; j < MocapSkeleton.Body; j++) s.SetJoint(c, j, root + Vector3.Transform(_fk[j], rot));
            s.Wetness[c] = 0.7f; s.Wrinkle[c] = 0.4f; s.Damage[c] = c == 0 ? 0.2f : 0f; s.Makeup[c] = 0.5f; s.Outfit[c] = c;
            for (var m = 0; m < ContinuitySnapshot.MaxMarks; m++) s.Mark(c, m) = default;
        }
        s.Mark(0, 0) = new BodyMark { Present = true, Joint = MocapSkeleton.Head, Kind = 1, Offset = new Vector3(0.05f, -0.02f, 0.08f) };
        s.Mark(1, 0) = new BodyMark { Present = true, Joint = MocapSkeleton.RWrist, Kind = 2, Offset = new Vector3(0f, 0.02f, 0f) };
    }

    /// <summary>계획별 오류 주입(현재 샷 시작 상태).</summary>
    private void Inject(ContinuitySnapshot s)
    {
        switch (_planIndex)
        {
            case 1: s.SetProp(0, s.Prop(0) + new Vector3(0.4f, 0f, 0.15f)); break;
            case 2:
                { ref var m = ref s.Mark(0, 0); m.Offset.X = -m.Offset.X; }
                s.ColorTempK = 5400f;
                break;
            case 3: s.Wetness[0] = 0.1f; s.Camera = new Vector3(0.3f, 1.5f, 3.2f); s.CameraForward = -Vector3.UnitZ; break;
            case 5: for (var j = MocapSkeleton.RShoulder; j < MocapSkeleton.Body; j += 2) s.SetJoint(0, j, s.Joint(0, j) + new Vector3(0f, 0.25f, 0.1f)); break;
            case 6: s.PropPresent[2] = false; break;
        }
    }

    private CutKind CutFor(int plan) => plan == 0 ? CutKind.Neutral : plan == 4 || plan == 7 ? CutKind.TimeJump : CutKind.Continuous;

    private void FillMeta()
    {
        var m = _meta;
        m.Scene = _scene; m.Day = 0; m.Hour = _storyHours % 24f; m.Latitude = 37.5f;
        m.ContinuousWithPrevious = _cut == CutKind.Continuous; m.Flashback = false; m.CostumeChange = false;
        var desert = _planIndex == 7 && InjectErrors;
        m.Location = desert ? _idDesert : _idCabin; m.Era = _id1920;
        m.Weather = new WeatherScenario { Precipitation = _current.Precipitation, CloudCoverage = 0.9f, Temperature = desert ? 30f : 9f, Humidity = 0.9f, WindSpeed = 3f, WindYawDeg = 60f };
        m.SnowFraction = 0f; m.GroundWetness = 0.8f;
        m.ObservedSunElevation = MathF.Asin(Math.Clamp(_current.KeyLight.Y, -1f, 1f)) * 180f / MathF.PI;
        m.CastCount = 2;
        m.Cast[0] = _idMina; m.Hair[0] = _idRed; m.Hand[0] = _idRight; m.Wardrobe[0] = _idTrench; m.Glasses[0] = false;
        m.Scar[0] = _current.Mark(0, 0).Present ? (_current.Mark(0, 0).Offset.X >= 0f ? _idScarL : _idScarR) : -1;
        m.Cast[1] = _idJun; m.Hair[1] = _idBlack; m.Hand[1] = -1; m.Wardrobe[1] = _idLeather; m.Glasses[1] = true; m.Scar[1] = -1;
        m.PropCount = 0;
        for (var i = 0; i < Props; i++) if (_current.PropPresent[i] && i == 1 && desert) m.Props[m.PropCount++] = _idPhone;
    }

    /// <summary>컷: 이전 샷 끝(reference) 확정 → 다음 샷 상태 생성 · 오류 주입 · 검증 · 바이블.</summary>
    private void CutToNext()
    {
        // 직전 샷 잔차 재검증(보정 피드백) → 기록
        if (_shot > 0) FinishRecord();
        _reference.CopyFrom(_current);
        _shot++; _planIndex = _shot % Plan; _shotFrame = 0;
        if (_planIndex == 0) { _scene = 1; _storyHours = 18.5f; _bible.ResetTimeline(); }
        _cut = CutFor(_planIndex);
        if (_cut == CutKind.TimeJump) _storyHours += 0.5f;
        if (_planIndex == 7) _scene = 7;
        else if (_planIndex == 4) _scene++;
        BuildTruth(_current);
        // 연속 컷은 이전 샷 끝 상태에서 이어짐(정답 = 기준) — 젖음/주름/손상은 경계 기준 유지
        if (_cut == CutKind.Continuous)
            for (var c = 0; c < 2; c++) { _current.Wetness[c] = _reference.Wetness[c]; _current.Wrinkle[c] = _reference.Wrinkle[c]; }
        _current.Precipitation = _reference.Precipitation;
        if (InjectErrors) Inject(_current);
        if (AsyncVerify) _engine.Submit(_reference, _current, _cut); else _engine.Verify(_reference, _current, _cut);
        FillMeta();
        _bibleCount = _bible.Diagnose(_meta);
        if (!AsyncVerify) _bible.MergeInto(_engine, _shot);
        _scoreBefore = _engine.ViolationScore;
        WriteScene();
    }

    private void FinishRecord()
    {
        var before = _scoreBefore; var count = _engine.LastViolationCount; var mask = 0;
        for (var i = 0; i < count; i++) mask |= 1 << RealtimeScriptSupervisorContinuityEngine.Category(_engine.LastViolation(i).Kind);
        // 잔차 재검증(보정 피드백): 보정 후 샷 상태 ↔ 기준
        _engine.Verify(_reference, _current, _cut);
        var after = _engine.ViolationScore;
        _records[_recHead] = new ShotRecord(_shot, _scene, _cut, count, _bibleCount, before, after, mask, _bible.CoherenceScore);
        _recHead = (_recHead + 1) % MaxRecords; if (_recCount < MaxRecords) _recCount++;
    }

    private void WriteScene()
    {
        _splats.Clear();
        for (var i = 0; i < Props; i++)
        {
            var p = _current.Prop(i);
            for (var k = 0; k < 8; k++)
            {
                var o = new Vector3((k & 1) - 0.5f, ((k >> 1) & 1) - 0.5f, ((k >> 2) & 1) - 0.5f) * 0.08f;
                _splats.Add(p + o, Quaternion.Identity, new Vector3(0.03f), _current.PropPresent[i] ? 0.95f : 0f, new Vector3(0.6f, 0.45f, 0.3f));
            }
            var (x, y, z) = (p.X, p.Y, p.Z);
            _usd.SetTranslate(_guard.PrimOf(i), x, y, z);
        }
    }

    /// <summary>1틱(무할당 · 동기 검증 모드).</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Continuity.Tick");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            if (_shotFrame >= ShotFrames) CutToNext();
            if (AutoCorrect && _shot > 0) _guard.Step(_reference, _current, _cut, dt);
            _shotFrame++;
            _frame++;
            LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    public void StepShot() { lock (_gate) { while (_shotFrame < ShotFrames) TickOnce(); TickOnce(); } Raise(); }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 연속성 감독 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Continuity.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Continuity.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
