using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using PromptFlowStudio.Services.MediaPipeline;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.Icvfx;

public enum BeatKind : byte { Establishing, Dialogue, Action, Reveal, Tension, Climax, Calm }
public enum ShotSize : byte { Wide, Medium, Close }
public enum DirectorAgent : byte { Camera, Lighting, Vfx, Coordinator, Advisor }
public enum DecisionCode : byte
{
    BeatStart, ShotChange, CoverageAdjust, SpeedLimit, VolumeClamp, AxisRule, KeyLightMatch, FillFromWall, RampLimit, FlashExecuted, FlashVetoed,
    SunMove, RainChange, FogBurst, Lightning, Explosion, VfxPriorityWide, AdvisorApplied, AdvisorFallback,
}

/// <summary>대본 비트 — 종류 · 시작/길이(s) · 장면(실내/외) · 시간대 · 에너지/정서가 · 날씨 · 원문.</summary>
public sealed record ScriptBeat(int Index, BeatKind Kind, double Start, double Duration, bool Exterior, TimeOfDay Time, float Energy, float Valence, bool Rain, bool Lightning, string Text);

/// <summary>결정 로그 항목(값 형식 · 문자열은 읽기 시 포맷).</summary>
public readonly record struct DirectorDecision(double Time, DirectorAgent Agent, DecisionCode Code, float Value);

/// <summary>DMX 조명기 패치 — 시작 주소(1-based) · 채널 레이아웃: 디머 · R · G · B · W · (무빙) Pan16 · Tilt16 · (효과) 스트로브.</summary>
public sealed record DmxFixture(string Name, int Address, bool MovingHead, bool Strobe, Vector3 Position)
{
    public int ChannelCount => 5 + (MovingHead ? 4 : 0) + (Strobe ? 1 : 0);
}

/// <summary>
/// [PromptFlow Studio v16.59.0] 🤖 에이전틱 다중 AI 자율 가상 제작 감독 스위트(Pure BCL · 틱 무할당).
///   ▸ 대본 → 비트(장면 헤딩 INT/EXT · 대사/지문 · v16.57 컨텍스트 튜너 에너지/정서가/시간대 · 날씨/번개/폭발/공개 키워드).
///   ▸ AI 카메라 감독: 비트별 샷(와이드/미디엄/클로즈 · 렌즈 · 거리 · 높이) · 배우 추적 · 180° 액션 라인 · 볼륨 경계 · 속도/가속/팬 속도 제한 ·
///     LED 월 커버리지 질의(프러스텀 렌더러) → 렌즈 장초점화/전진 보정.
///   ▸ AI 조명 감독: DMX512 패치(RGBW + 16-bit 팬/틸트 + 스트로브) · 가상 태양 방향 → 키 라이트 조준/색온도 매칭 · LED 월 아우터 평균색 → 필 라이트(IBL) ·
///     디머 램프 제한 · 광과민 안전(플래시 ≤ 3회/초) 거부권 · Art-Net ArtDmx(UDP 6454) 송출.
///   ▸ AI VFX 감독: 시간대 → 태양 방위/고도 · 비/안개 · 번개 큐(결정적 난수) · 클라이맥스 폭발(플래시 + 안개 버스트) · 공개 비트 태양 스윕.
///   ▸ 코디네이터: 우선순위 조정(클라이맥스 VFX → 카메라 와이드 강제 · 조명 안전 → VFX 플래시 거부) · 결정 로그 링(128).
///   ▸ 어드바이저(선택): 외부 LLM 비트 연출 제안 결선점 — 1초 타임아웃 · 스키마 검증 · 실패 시 규칙 기반 폴백.
/// </summary>
public sealed class AgenticAutonomousVirtualProductionDirector : IDisposable
{
    public const int LogSize = 128, MaxActors = 4, ArtNetPort = 6454, MaxFlashesPerSecond = 3;
    private static readonly Vector3 VolumeMin = new(-3.2f, 0.4f, -1.5f), VolumeMax = new(3.2f, 3.2f, 3.8f);

    private readonly object _gate = new();
    private readonly IcvfxLedWallFrustumRendererEngine? _frustum;
    private readonly AutoPresetContextTuner _tuner;
    private readonly List<ScriptBeat> _beats = new();
    private readonly DirectorDecision[] _log = new DirectorDecision[LogSize];
    private readonly Vector3[] _actors = new Vector3[MaxActors];
    private readonly bool[] _actorActive = new bool[MaxActors];
    private readonly byte[] _dmx = new byte[512];
    private readonly float[] _dimmerTarget = new float[8];
    private readonly float[] _dimmer = new float[8];
    private readonly double[] _flashTimes = new double[16];
    private readonly byte[] _artnet = new byte[18 + 512];
    private readonly List<DmxFixture> _fixtures = new();
    private int _logHead, _logCount, _beat = -1, _flashHead, _axisLoggedBeat = -2, _clampLoggedBeat = -2, _vfxPriorityBeat = -2;
    private double _time;
    private uint _rng = 0xC0FFEEu;
    private double _nextLightning = double.MaxValue;
    private byte _artSeq;
    private Socket? _udp;
    private IPEndPoint? _artTarget;
    private long _ticks, _artPackets, _conflicts;
    // 카메라 상태
    private Vector3 _camPos = new(0f, 1.6f, -0.5f), _camVel;
    private float _camYaw, _camPitch, _focal = 35f;
    private ShotSize _shot = ShotSize.Wide;
    private float _coverage = 1f, _coverageFocal;
    // VFX 상태
    private float _sunAz = 25f, _sunEl = 28f, _sunAzTarget = 25f, _sunElTarget = 28f, _rain, _rainTarget, _fog, _fogTarget = 0.012f, _flash, _sky = 1f;
    private float _kelvin = 5600f;
    private Vector3 _wallFill = new(0.3f);
    // 어드바이저
    private volatile int _advShot = -1;
    private int _advBeat = -1;

    public AgenticAutonomousVirtualProductionDirector(IServiceProvider sp)
        : this(sp.GetService(typeof(IcvfxLedWallFrustumRendererEngine)) as IcvfxLedWallFrustumRendererEngine,
               (AutoPresetContextTuner?)sp.GetService(typeof(AutoPresetContextTuner)) ?? new AutoPresetContextTuner()) { }

    public AgenticAutonomousVirtualProductionDirector(IcvfxLedWallFrustumRendererEngine? frustum, AutoPresetContextTuner? tuner = null)
    {
        _frustum = frustum;
        _tuner = tuner ?? new AutoPresetContextTuner();
        _fixtures.Add(new DmxFixture("KEY (무빙 RGBW)", 1, true, false, new Vector3(-2.5f, 3.5f, -1f)));
        _fixtures.Add(new DmxFixture("FILL (RGBW)", 11, false, false, new Vector3(2.5f, 2.5f, -1.2f)));
        _fixtures.Add(new DmxFixture("BACK/RIM (무빙 RGBW)", 21, true, false, new Vector3(0f, 4f, 3.5f)));
        _fixtures.Add(new DmxFixture("FX STROBE (RGBW)", 31, false, true, new Vector3(0f, 4.2f, 1.5f)));
        _actors[0] = new Vector3(0f, 0f, 1.5f); _actorActive[0] = true;
    }

    public event Action? OnChange;

    public IReadOnlyList<ScriptBeat> Beats => _beats;
    public IReadOnlyList<DmxFixture> Fixtures => _fixtures;
    public ReadOnlySpan<byte> Universe => _dmx;
    public int CurrentBeat => _beat;
    public ScriptBeat? Beat => (uint)_beat < (uint)_beats.Count ? _beats[_beat] : null;
    public double TimeSec => _time;
    public bool DriveCamera { get; set; } = true;
    public long Ticks => Interlocked.Read(ref _ticks);
    public long ArtNetPackets => Interlocked.Read(ref _artPackets);
    public long Conflicts => Interlocked.Read(ref _conflicts);
    public int LogCount => _logCount;
    public Vector3 CameraPosition => _camPos;
    public float CameraYawDeg => _camYaw * 180f / MathF.PI;
    public float CameraPitchDeg => _camPitch * 180f / MathF.PI;
    public float FocalMm => _focal;
    public ShotSize Shot => _shot;
    public float Coverage => _coverage;
    public float SunAzimuth => _sunAz;
    public float SunElevation => _sunEl;
    public float Rain => _rain;
    public float Fog => _fog;
    public float FlashLevel => _flash;
    public float Kelvin => _kelvin;
    public bool ArtNetEnabled => _udp is not null;
    public string? LastAction { get; private set; }
    public bool Ended => _beats.Count > 0 && _beat >= _beats.Count;

    /// <summary>외부 LLM 어드바이저(비트 요약 → JSON {"shot":"wide|medium|close"}) — 1초 타임아웃 · 실패 시 규칙 폴백.</summary>
    public Func<string, CancellationToken, Task<string?>>? Advisor { get; set; }

    public ShotSize? AdvisorShot => _advShot >= 0 ? (ShotSize)_advShot : null;

    private float Rand() { var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x; return (x & 0xFFFFFF) / 16777216f; }

    private void Log(DirectorAgent a, DecisionCode c, float v)
    {
        _log[_logHead] = new DirectorDecision(_time, a, c, v);
        _logHead = (_logHead + 1) % LogSize;
        if (_logCount < LogSize) _logCount++;
    }

    /// <summary>최근 결정 복사(최신 우선).</summary>
    public int CopyLog(Span<DirectorDecision> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(_logCount, dst.Length);
            for (var i = 0; i < n; i++) dst[i] = _log[(_logHead - 1 - i + LogSize) % LogSize];
            return n;
        }
    }

    public static string Describe(in DirectorDecision d) => d.Code switch
    {
        DecisionCode.BeatStart => "🎬 비트 " + ((int)d.Value + 1).ToString(CultureInfo.InvariantCulture) + " 시작",
        DecisionCode.ShotChange => "🎥 샷 → " + ((ShotSize)(int)d.Value) switch { ShotSize.Wide => "와이드", ShotSize.Medium => "미디엄", _ => "클로즈" },
        DecisionCode.CoverageAdjust => "🧱 월 커버리지 보정 → " + d.Value.ToString("0", CultureInfo.InvariantCulture) + "mm",
        DecisionCode.SpeedLimit => "⏱ 카메라 속도 제한 " + d.Value.ToString("0.00", CultureInfo.InvariantCulture) + "m/s",
        DecisionCode.VolumeClamp => "📦 볼륨 경계 클램프",
        DecisionCode.AxisRule => "↔ 180° 액션 라인 유지",
        DecisionCode.KeyLightMatch => "☀ 키 라이트 ↔ 가상 태양 매칭 " + d.Value.ToString("0", CultureInfo.InvariantCulture) + "K",
        DecisionCode.FillFromWall => "🟦 필 = LED 월 평균색(IBL)",
        DecisionCode.RampLimit => "🎚 디머 램프 제한",
        DecisionCode.FlashExecuted => "⚡ 플래시 실행",
        DecisionCode.FlashVetoed => "⛔ 플래시 거부(광과민 안전 ≤3회/초)",
        DecisionCode.SunMove => "🌅 태양 고도 → " + d.Value.ToString("0", CultureInfo.InvariantCulture) + "°",
        DecisionCode.RainChange => "🌧 비 → " + (d.Value * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
        DecisionCode.FogBurst => "🌫 안개 버스트",
        DecisionCode.Lightning => "🌩 번개 큐",
        DecisionCode.Explosion => "💥 폭발 큐",
        DecisionCode.VfxPriorityWide => "⚖ 코디네이터: VFX 우선 → 와이드 강제",
        DecisionCode.AdvisorApplied => "🧠 어드바이저 제안 반영",
        _ => "🧠 어드바이저 실패 → 규칙 폴백",
    } + " · " + d.Agent switch { DirectorAgent.Camera => "카메라", DirectorAgent.Lighting => "조명", DirectorAgent.Vfx => "VFX", DirectorAgent.Coordinator => "코디네이터", _ => "어드바이저" };

    // ══════════════════════ 대본 → 비트 ══════════════════════

    private static bool Has(string s, params string[] keys) { foreach (var k in keys) if (s.Contains(k, StringComparison.OrdinalIgnoreCase)) return true; return false; }

    /// <summary>대본 파싱 → 비트 목록(장면 헤딩은 다음 비트의 실내/외 · 시간대 맥락). 반환 = 비트 수.</summary>
    public int LoadScript(string script)
    {
        var beats = new List<ScriptBeat>();
        var exterior = true;
        var ctxTime = TimeOfDay.Unspecified;
        var t = 0.0;
        foreach (var raw in script.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var upper = line.ToUpperInvariant();
            if (upper.StartsWith("INT", StringComparison.Ordinal) || upper.StartsWith("EXT", StringComparison.Ordinal) || line.StartsWith("S#", StringComparison.Ordinal)
                || line.StartsWith("내부", StringComparison.Ordinal) || line.StartsWith("외부", StringComparison.Ordinal) || line.StartsWith("씬", StringComparison.Ordinal))
            {
                exterior = upper.StartsWith("EXT", StringComparison.Ordinal) || line.StartsWith("외부", StringComparison.Ordinal) || Has(line, "외부", "EXT");
                var p = _tuner.Tune(line);
                ctxTime = p.Time != TimeOfDay.Unspecified ? p.Time : Has(line, "NIGHT", "밤") ? TimeOfDay.Night : Has(line, "DAY", "낮") ? TimeOfDay.Day : ctxTime;
                if (beats.Count == 0 || beats[^1].Kind != BeatKind.Establishing)
                {
                    beats.Add(new ScriptBeat(beats.Count, BeatKind.Establishing, t, 5, exterior, ctxTime, 0.3f, 0.2f, false, false, line));
                    t += 5;
                }
                continue;
            }
            var preset = _tuner.Tune(line);
            var dialogue = line.Contains(':') && line.IndexOf(':') < 16 || line.StartsWith('"') || line.StartsWith('“');
            var kind = Has(line, "폭발", "explod", "climax", "클라이맥스", "붕괴", "collapse") ? BeatKind.Climax
                : Has(line, "드러나", "공개", "reveal", "나타난다", "모습을 드러") ? BeatKind.Reveal
                : Has(line, "긴장", "숨죽", "다가온다", "tension", "creep", "정적 속") ? BeatKind.Tension
                : Has(line, "평온", "고요", "잔잔", "calm", "peaceful", "휴식") ? BeatKind.Calm
                : dialogue ? BeatKind.Dialogue
                : preset.Energy > 0.6f ? BeatKind.Action : BeatKind.Calm;
            var rain = Has(line, "비가", "빗", "폭우", "rain", "storm", "폭풍");
            var lightning = Has(line, "번개", "천둥", "lightning", "thunder", "폭풍", "storm");
            var time = preset.Time != TimeOfDay.Unspecified ? preset.Time : ctxTime;
            var dur = Math.Clamp(3.5 + line.Length / 18.0 - preset.Energy * 1.5, 3.0, 12.0);
            beats.Add(new ScriptBeat(beats.Count, kind, t, dur, exterior, time, preset.Energy, preset.Valence, rain, lightning, line));
            t += dur;
        }
        lock (_gate)
        {
            _beats.Clear(); _beats.AddRange(beats);
            _beat = -1; _time = 0; _advShot = -1; _advBeat = -1;
        }
        LastAction = "📜 대본 " + beats.Count.ToString(CultureInfo.InvariantCulture) + "비트 로드";
        Raise();
        return beats.Count;
    }

    public void SetActor(int id, Vector3 stagePos)
    {
        if ((uint)id >= MaxActors) return;
        lock (_gate) { _actors[id] = stagePos; _actorActive[id] = true; }
    }

    public void RemoveActor(int id) { if ((uint)id < MaxActors) lock (_gate) _actorActive[id] = false; }

    public void Seek(double t) { lock (_gate) { _time = Math.Max(0, t); _beat = -1; } }

    // ══════════════════════ 틱 ══════════════════════

    /// <summary>1틱(무할당) — 비트 진행 → VFX → 카메라 → 조명 → 코디네이터 → 렌더러 반영 · DMX 프레임 · Art-Net.</summary>
    public void Tick(double dt)
    {
        lock (_gate)
        {
            _time += dt;
            var bi = -1;
            for (var i = 0; i < _beats.Count; i++) if (_time >= _beats[i].Start && _time < _beats[i].Start + _beats[i].Duration) { bi = i; break; }
            if (bi < 0 && _beats.Count > 0 && _time >= _beats[^1].Start + _beats[^1].Duration) bi = _beats.Count;
            if (bi != _beat)
            {
                _beat = bi;
                if ((uint)bi < (uint)_beats.Count) OnBeatStart(_beats[bi]);
            }
            var beat = (uint)_beat < (uint)_beats.Count ? _beats[_beat] : null;
            VfxAgent(beat, (float)dt);
            var wantShot = CameraShotFor(beat);
            if (beat?.Kind == BeatKind.Climax && wantShot != ShotSize.Wide)
            {
                wantShot = ShotSize.Wide;   // 코디네이터: 폭발/붕괴 VFX 가시성 우선
                if (_vfxPriorityBeat != _beat) { _vfxPriorityBeat = _beat; Log(DirectorAgent.Coordinator, DecisionCode.VfxPriorityWide, 0); Interlocked.Increment(ref _conflicts); }
            }
            if (wantShot != _shot) { _shot = wantShot; Log(DirectorAgent.Camera, DecisionCode.ShotChange, (int)wantShot); }
            CameraAgent(beat, (float)dt);
            LightingAgent(beat, (float)dt);
            ApplyToRenderer();
            SendArtNet();
            Interlocked.Increment(ref _ticks);
        }
        if ((Interlocked.Read(ref _ticks) & 15) == 0) Raise();
    }

    private void OnBeatStart(ScriptBeat b)
    {
        Log(DirectorAgent.Coordinator, DecisionCode.BeatStart, b.Index);
        // VFX 계획
        (_sunAzTarget, _sunElTarget, _kelvin, _sky) = b.Time switch
        {
            TimeOfDay.Dawn => (95f, 6f, 3800f, 0.6f),
            TimeOfDay.GoldenHour => (250f, 8f, 3200f, 0.8f),
            TimeOfDay.Night => (200f, -8f, 4200f, 0.12f),
            TimeOfDay.Indoor => (30f, 45f, 3600f, 0.5f),
            _ => (25f, 45f, 5600f, 1f),
        };
        if (b.Kind == BeatKind.Reveal) _sunElTarget += 12f;
        _rainTarget = b.Rain ? 0.8f : 0f;
        if (Math.Abs(_rainTarget - _rain) > 0.1f) Log(DirectorAgent.Vfx, DecisionCode.RainChange, _rainTarget);
        _fogTarget = b.Kind is BeatKind.Tension ? 0.05f : b.Rain ? 0.035f : 0.012f;
        _nextLightning = b.Lightning ? _time + 0.6 + Rand() * 1.5 : double.MaxValue;
        if (b.Kind == BeatKind.Climax)
        {
            Log(DirectorAgent.Vfx, DecisionCode.Explosion, 1);
            RequestFlash(1f);
            _fog = 0.09f; _fogTarget = 0.03f;
            Log(DirectorAgent.Vfx, DecisionCode.FogBurst, 0.09f);
        }
        Log(DirectorAgent.Vfx, DecisionCode.SunMove, _sunElTarget);
        // 어드바이저(비동기 · 핫패스 밖)
        if (Advisor is { } adv && _advBeat != b.Index)
        {
            _advBeat = b.Index;
            _advShot = -1;
            var summary = b.Kind + " | " + b.Text;
            _ = RunAdvisorAsync(adv, summary, b.Index);
        }
    }

    private async Task RunAdvisorAsync(Func<string, CancellationToken, Task<string?>> adv, string summary, int beatIndex)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            var json = await adv(summary, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            int shot = -1;
            if (json is not null)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("shot", out var s) && s.ValueKind == JsonValueKind.String)
                    shot = s.GetString() switch { "wide" => 0, "medium" => 1, "close" => 2, _ => -1 };
            }
            lock (_gate)
            {
                if (_advBeat != beatIndex) return;
                _advShot = shot;
                Log(DirectorAgent.Advisor, shot >= 0 ? DecisionCode.AdvisorApplied : DecisionCode.AdvisorFallback, shot);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or JsonException or HttpRequestException or InvalidOperationException)
        {
            lock (_gate) if (_advBeat == beatIndex) { _advShot = -1; Log(DirectorAgent.Advisor, DecisionCode.AdvisorFallback, -1); }
        }
    }

    private ShotSize CameraShotFor(ScriptBeat? b)
    {
        if (_advShot >= 0) return (ShotSize)_advShot;
        return b?.Kind switch
        {
            null or BeatKind.Establishing => ShotSize.Wide,
            BeatKind.Dialogue or BeatKind.Tension or BeatKind.Climax => ShotSize.Close,   // 카메라 감독 선호: 클라이맥스 배우 리액션 클로즈
            BeatKind.Reveal or BeatKind.Calm => ShotSize.Medium,
            _ => ShotSize.Medium,
        };
    }

    private Vector3 Subject()
    {
        Vector3 s = Vector3.Zero; var n = 0;
        for (var i = 0; i < MaxActors; i++) if (_actorActive[i]) { s += _actors[i]; n++; }
        return (n > 0 ? s / n : new Vector3(0, 0, 1.5f)) + new Vector3(0, 1.45f, 0);
    }

    // ── VFX 에이전트 ──
    private void VfxAgent(ScriptBeat? b, float dt)
    {
        var k = 1f - MathF.Exp(-dt / 1.5f);
        _sunAz += Wrap(_sunAzTarget - _sunAz) * k;
        _sunEl += (_sunElTarget - _sunEl) * k;
        _rain += (_rainTarget - _rain) * (1f - MathF.Exp(-dt / 0.8f));
        _fog += (_fogTarget - _fog) * (1f - MathF.Exp(-dt / 2f));
        _flash *= MathF.Exp(-dt / 0.12f);
        if (_time >= _nextLightning)
        {
            Log(DirectorAgent.Vfx, DecisionCode.Lightning, 1);
            RequestFlash(0.9f);
            _nextLightning = _time + 1.2 + Rand() * 2.5;
        }
        _ = b;
    }

    private static float Wrap(float deg) { deg %= 360f; if (deg > 180f) deg -= 360f; if (deg < -180f) deg += 360f; return deg; }

    /// <summary>플래시 요청 — 조명 안전 거부권(최근 1초 3회 초과 시 거부).</summary>
    public bool RequestFlash(float level)
    {
        lock (_gate) return RequestFlashCore(level); // v16.62.0 외부 스레드(시너지 버스) 호출 직렬화 — Tick 내부 재진입 허용
    }

    private bool RequestFlashCore(float level)
    {
        var recent = 0;
        for (var i = 0; i < _flashTimes.Length; i++) if (_flashTimes[i] > 0 && _time - _flashTimes[i] < 1.0) recent++;
        if (recent >= MaxFlashesPerSecond)
        {
            Log(DirectorAgent.Lighting, DecisionCode.FlashVetoed, level);
            Interlocked.Increment(ref _conflicts);
            return false;
        }
        _flashTimes[_flashHead] = Math.Max(_time, 1e-6);
        _flashHead = (_flashHead + 1) % _flashTimes.Length;
        _flash = MathF.Max(_flash, level);
        Log(DirectorAgent.Lighting, DecisionCode.FlashExecuted, level);
        return true;
    }

    // ── 카메라 에이전트 ──
    private void CameraAgent(ScriptBeat? b, float dt)
    {
        if (dt <= 0) return;
        var subj = Subject();
        var (dist, height, focal) = _shot switch { ShotSize.Wide => (3.6f, 2.0f, 24f), ShotSize.Medium => (2.4f, 1.65f, 40f), _ => (1.6f, 1.55f, 65f) };
        if (b?.Kind == BeatKind.Climax) height = 1.0f;                      // 로우 앵글
        var orbit = b?.Kind switch { BeatKind.Action => MathF.Sin((float)_time * 0.6f) * 0.5f, BeatKind.Reveal => -0.3f + (float)((_time - b.Start) / b.Duration) * 0.6f, _ => 0f };
        // 180° 규칙: 배우 0-1 액션 라인 기준 한쪽(카메라 −Z 측) 유지
        if (_actorActive[0] && _actorActive[1])
        {
            var line = _actors[1] - _actors[0];
            var side = Vector3.Cross(Vector3.UnitY, line);
            if (side.Z > 0) side = -side;
            if (side.LengthSquared() > 1e-6f) { var dir = Vector3.Normalize(side); orbit = MathF.Atan2(dir.X, -dir.Z); if (_axisLoggedBeat != _beat) { _axisLoggedBeat = _beat; Log(DirectorAgent.Camera, DecisionCode.AxisRule, 0); } }
        }
        var target = subj + new Vector3(MathF.Sin(orbit) * dist, height - subj.Y, -MathF.Cos(orbit) * dist);
        var clamped = Vector3.Clamp(target, VolumeMin, VolumeMax);
        if (Vector3.DistanceSquared(clamped, target) > 1e-6f) { target = clamped; if (_clampLoggedBeat != _beat) { _clampLoggedBeat = _beat; Log(DirectorAgent.Camera, DecisionCode.VolumeClamp, 0); } }
        // 임계 감쇠 스프링 + 속도/가속 제한
        const float maxSpeed = 1.5f, maxAccel = 2.5f;
        var desiredVel = (target - _camPos) * 2.5f;
        var dv = desiredVel - _camVel;
        var maxDv = maxAccel * dt;
        if (dv.Length() > maxDv) dv = Vector3.Normalize(dv) * maxDv;
        _camVel += dv;
        var sp = _camVel.Length();
        if (sp > maxSpeed) { _camVel *= maxSpeed / sp; if (_ticks % 30 == 0) Log(DirectorAgent.Camera, DecisionCode.SpeedLimit, maxSpeed); }
        _camPos += _camVel * dt;
        var look = Vector3.Normalize(subj - _camPos);
        var wantYaw = MathF.Atan2(look.X, look.Z); var wantPitch = MathF.Asin(Math.Clamp(look.Y, -1f, 1f));
        var maxPan = 60f * MathF.PI / 180f * dt;
        _camYaw += Math.Clamp(WrapRad(wantYaw - _camYaw), -maxPan, maxPan);
        _camPitch += Math.Clamp(wantPitch - _camPitch, -maxPan, maxPan);
        // LED 월 커버리지 보정(카메라 시야가 월 밖을 보면 목표 렌즈 장초점화)
        if (_frustum is not null)
        {
            var sensor = _frustum.Lens.SensorWidthMm;
            var need = focal;
            var guard = 0;
            _coverage = _frustum.Coverage(SplatCamera.FromYawPitch(_camPos, _camYaw, _camPitch, 0, Hfov(sensor, need), 64, 36));
            while (_coverage < 0.97f && need < 120f && guard++ < 12)
            {
                need *= 1.12f;
                _coverage = _frustum.Coverage(SplatCamera.FromYawPitch(_camPos, _camYaw, _camPitch, 0, Hfov(sensor, need), 64, 36));
            }
            if (MathF.Abs(need - _coverageFocal) > 2f) { _coverageFocal = need; if (need > focal + 0.5f) Log(DirectorAgent.Camera, DecisionCode.CoverageAdjust, need); }
            focal = need;
        }
        _focal += (focal - _focal) * (1f - MathF.Exp(-dt / 0.6f));
    }

    private static float Hfov(float sensorMm, float focalMm) => 2f * MathF.Atan(sensorMm * 0.5f / focalMm) * 180f / MathF.PI;

    private static float WrapRad(float a) { a %= MathF.Tau; if (a > MathF.PI) a -= MathF.Tau; if (a < -MathF.PI) a += MathF.Tau; return a; }

    // ── 조명 에이전트 ──
    private void LightingAgent(ScriptBeat? b, float dt)
    {
        var subj = Subject();
        var mood = b?.Kind switch { BeatKind.Tension => 0.45f, BeatKind.Calm => 0.7f, BeatKind.Climax => 1f, BeatKind.Dialogue => 0.8f, _ => 0.85f };
        var night = _sunEl < 0f;
        _dimmerTarget[0] = night ? 0.35f * mood : mood;                    // 키
        _dimmerTarget[1] = 0.25f + 0.35f * (1f - mood * 0.5f);             // 필
        _dimmerTarget[2] = b?.Kind == BeatKind.Tension ? 0.9f : 0.55f;      // 백/림
        _dimmerTarget[3] = 0f;                                              // 효과(플래시만)
        var limited = false;
        for (var f = 0; f < 4; f++)
        {
            var step = 1.0f * dt;
            var d = _dimmerTarget[f] - _dimmer[f];
            if (MathF.Abs(d) > step) { d = MathF.Sign(d) * step; limited = true; }
            _dimmer[f] = Math.Clamp(_dimmer[f] + d, 0f, 1f);
        }
        if (limited && _ticks % 60 == 0) Log(DirectorAgent.Lighting, DecisionCode.RampLimit, 1);
        // 키 = 가상 태양 방향에서 배우를 향해(태양 반대 방향 위치 가정 → 피사체에서 태양 쪽으로 오프셋된 가상 소스 조준)
        float az = _sunAz * MathF.PI / 180f, el = MathF.Max(5f, _sunEl) * MathF.PI / 180f;
        var sunDir = new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), MathF.Cos(az) * MathF.Cos(el));
        var keyColor = NeuralCameraPoseRelightingEngine.KelvinToRgb(_kelvin);
        var fillColor = Vector3.Clamp(_wallFill / MathF.Max(1e-3f, MathF.Max(_wallFill.X, MathF.Max(_wallFill.Y, _wallFill.Z))), Vector3.Zero, Vector3.One);
        WriteFixture(0, _dimmer[0], keyColor, AimAt(_fixtures[0].Position, subj + sunDir * 0.2f));
        WriteFixture(1, _dimmer[1], fillColor, default);
        WriteFixture(2, _dimmer[2], Vector3.Lerp(keyColor, new Vector3(0.6f, 0.75f, 1f), 0.5f), AimAt(_fixtures[2].Position, subj));
        WriteFixture(3, Math.Clamp(_flash, 0f, 1f), new Vector3(0.85f, 0.9f, 1f), default, _flash > 0.05f ? (byte)255 : (byte)0);
        if (_ticks % 90 == 0) { Log(DirectorAgent.Lighting, DecisionCode.KeyLightMatch, _kelvin); Log(DirectorAgent.Lighting, DecisionCode.FillFromWall, 0); }
    }

    /// <summary>무빙 헤드 조준 → (팬 0..1 ↔ −270..+270° · 틸트 0..1 ↔ −135..+135°).</summary>
    public static Vector2 AimAt(Vector3 from, Vector3 to)
    {
        var d = to - from;
        var pan = MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;
        var tilt = MathF.Atan2(-d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z)) * 180f / MathF.PI;   // 아래 = +
        return new Vector2(Math.Clamp((pan + 270f) / 540f, 0f, 1f), Math.Clamp((tilt + 135f) / 270f, 0f, 1f));
    }

    private void WriteFixture(int i, float dimmer, Vector3 rgb, Vector2 panTilt, byte strobe = 0)
    {
        var f = _fixtures[i];
        var a = f.Address - 1;
        var w = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
        _dmx[a] = (byte)Math.Clamp((int)(dimmer * 255f + 0.5f), 0, 255);
        _dmx[a + 1] = (byte)Math.Clamp((int)((rgb.X - w) * 255f + 0.5f), 0, 255);
        _dmx[a + 2] = (byte)Math.Clamp((int)((rgb.Y - w) * 255f + 0.5f), 0, 255);
        _dmx[a + 3] = (byte)Math.Clamp((int)((rgb.Z - w) * 255f + 0.5f), 0, 255);
        _dmx[a + 4] = (byte)Math.Clamp((int)(w * 255f + 0.5f), 0, 255);
        var c = a + 5;
        if (f.MovingHead)
        {
            var pan = (ushort)Math.Clamp((int)(panTilt.X * 65535f + 0.5f), 0, 65535);
            var tilt = (ushort)Math.Clamp((int)(panTilt.Y * 65535f + 0.5f), 0, 65535);
            _dmx[c] = (byte)(pan >> 8); _dmx[c + 1] = (byte)pan; _dmx[c + 2] = (byte)(tilt >> 8); _dmx[c + 3] = (byte)tilt;
            c += 4;
        }
        if (f.Strobe) _dmx[c] = strobe;
    }

    /// <summary>프러스텀 렌더러 아우터 평균색 → 필 라이트(IBL) 입력.</summary>
    public void SetWallFill(Vector3 averageLinear) { lock (_gate) _wallFill = averageLinear; }

    private void ApplyToRenderer()
    {
        if (_frustum is null) return;
        if (_frustum.Procedural is { } set)
        {
            set.SunAzimuthDeg = _sunAz; set.SunElevationDeg = _sunEl; set.SkyIntensity = _sky; set.FogDensity = _fog; set.Flash = _flash;
            set.SunColor = NeuralCameraPoseRelightingEngine.KelvinToRgb(_kelvin);
        }
        _frustum.Rain = _rain;
        _frustum.TimeSec = _time;
        if (DriveCamera)
        {
            _frustum.SetLens(_frustum.Lens with { FocalMm = _focal });
            _frustum.UpdateTracking(_camPos, _camYaw, _camPitch, 0f, (long)(_time * 1e6));
        }
    }

    // ══════════════════════ Art-Net ══════════════════════

    /// <summary>ArtDmx 패킷 작성(무할당) — "Art-Net\0" · OpDmx 0x5000(LE) · ProtVer 14(BE) · Seq · Phys · SubUni · Net · Length(BE · 짝수). 반환 = 길이.</summary>
    public static int WriteArtDmx(Span<byte> dst, ReadOnlySpan<byte> dmx, byte sequence, int universe)
    {
        var len = Math.Min(512, (dmx.Length + 1) & ~1);
        if (dst.Length < 18 + len) return 0;
        "Art-Net\0"u8.CopyTo(dst);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], 0x5000);
        BinaryPrimitives.WriteUInt16BigEndian(dst[10..], 14);
        dst[12] = sequence; dst[13] = 0;
        dst[14] = (byte)(universe & 0xFF); dst[15] = (byte)((universe >> 8) & 0x7F);
        BinaryPrimitives.WriteUInt16BigEndian(dst[16..], (ushort)len);
        dmx[..Math.Min(dmx.Length, len)].CopyTo(dst[18..]);
        if (dmx.Length < len) dst[(18 + dmx.Length)..(18 + len)].Clear();
        return 18 + len;
    }

    /// <summary>Art-Net 송출 시작(기본 비활성 · 브로드캐스트/노드 IP).</summary>
    public void EnableArtNet(string host, int universe = 0)
    {
        DisableArtNet();
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        lock (_gate) { _udp = s; _artTarget = new IPEndPoint(IPAddress.Parse(host), ArtNetPort); ArtUniverse = universe; }
        LastAction = "📡 Art-Net → " + host;
        Raise();
    }

    public int ArtUniverse { get; private set; }

    public void DisableArtNet()
    {
        Socket? s;
        lock (_gate) { s = _udp; _udp = null; _artTarget = null; }
        s?.Dispose();
    }

    private void SendArtNet()
    {
        if (_udp is not { } s || _artTarget is null) return;
        _artSeq = (byte)(_artSeq == 255 ? 1 : _artSeq + 1);
        var n = WriteArtDmx(_artnet, _dmx, _artSeq, ArtUniverse);
        try { s.SendTo(_artnet.AsSpan(0, n), SocketFlags.None, _artTarget); Interlocked.Increment(ref _artPackets); }
        catch (SocketException ex) { PromptFlowStudio.Program.Log("Icvfx.ArtNet", ex); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>DMX 채널(1-based) 값.</summary>
    public byte Channel(int ch) => (uint)(ch - 1) < 512 ? _dmx[ch - 1] : (byte)0;

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Icvfx.Director.OnChange", ex); }
    }

    public void Dispose() => DisableArtNet();
}
