using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.World;

public enum WorldShape : byte { Sphere = 0, Box = 1 }

/// <summary>월드 개입 연산(분기·스케줄·UI 슬라이더 공용). Impulse 는 Δv(m/s), Obj &lt; 0 = 전체(Restitution/Friction).</summary>
public enum WorldOpKind : byte { None = 0, Impulse, SetVelocity, Gravity, Hold, Release, Hide, Show, TimeScale, Restitution, Friction, Reflectivity, Teleport }

public readonly record struct WorldOp(WorldOpKind Kind, int Obj, float X, float Y, float Z);

public enum CameraMode : byte { Static = 0, Orbit, Dolly, Track, Overhead, Crane, LowAngle }

public enum LightPreset : byte { Studio = 0, Noon, Sunset, Night, Overcast }

/// <summary>카메라 궤적 사양(시간 매개 · 이징). Target = 추적 객체(-1 없음).</summary>
public struct CameraSpec
{
    public CameraMode Mode;
    public int Target;
    public float Cx, Cy, Cz, Radius, Height, Angle0, Sweep, Fov, Duration, Radius1, Shake;
}

/// <summary>시공간 일관성 리포트(물리 · 영속성 · 카메라 · 시간 스파이크).</summary>
public sealed record WorldConsistencyReport(int Score, int Frames, float Seconds, long EnergyViolations, float MaxEnergyDrift, float MaxPenetration,
    long PermanenceViolations, long CameraJumps, float MaxCameraDegPerSec, float MeanDelta, float MaxSpike, IReadOnlyList<string> Issues);

/// <summary>
/// [PromptFlow Studio v16.54.0] ▶ AI 4D 시공간 월드 모델 &amp; 물리 시뮬레이션 엔진.
///   ▸ 4D 월드 상태: 강체(구/AABB) SoA · 240Hz 고정 스텝 반암시적 오일러 · 충격량 기반 접촉(반발/쿨롱 마찰 · 구체=구름 저항 근사) · 중력/감쇠/시간 배속 · 스케줄 연산.
///   ▸ 카메라 궤적: Static/Orbit/Dolly/Track(임계 감쇠 추종)/Overhead/Crane/LowAngle + 핸드헬드 · 스무스스텝 이징 · 전환 블렌드.
///   ▸ 일관성 보장 모니터: 에너지 비증가 · 최대 침투 · 객체 영속성(NaN/터널링 복구) · 카메라 점프 · 프레임 시간 스파이크 → 0~100 점수.
///   ▸ 렌더: 무할당 소프트웨어 레이트레이서(그림자 · 바닥 반사 · 안개) → RGB + 깊이 + 객체 ID(비디오 모델 조건부 맵).
///   ▸ 4D 상태 매트릭스 Capture/Restore(무할당) — 분기 이관 · 되감기 · 섀도 프리뷰 · 결정론 재현.
///   ▸ 결선점: <see cref="BuildConditioningJson"/>(카메라 포즈 + 객체 궤적 4D 컨디셔닝) → 외부 생성형 비디오 백엔드. 미결선 시 내장 레이트레이서 프리뷰.
///   스레드 모델: 단일 소유자(러너/플레이어 잠금) — 카운터 읽기만 교차 스레드 허용.
/// </summary>
public sealed class AiGenerative4dWorldModelEngine
{
    public const int MaxObjects = 64;
    public const int PhysicsHz = 240;
    private const int PerObj = 20, Header = 48, MaxSchedule = 64, SchedStride = 6;
    private const int SchedBase = Header + MaxObjects * PerObj;
    public const int StateFloats = SchedBase + 2 + MaxSchedule * SchedStride;
    private const byte FAlive = 1, FStatic = 2, FHeld = 4, FHidden = 8;
    private const float RestThreshold = 0.25f, RollingFactor = 0.08f, FloorExtent2 = 1600f;

    private readonly float[] _px = new float[MaxObjects], _py = new float[MaxObjects], _pz = new float[MaxObjects];
    private readonly float[] _vx = new float[MaxObjects], _vy = new float[MaxObjects], _vz = new float[MaxObjects];
    private readonly float[] _hx = new float[MaxObjects], _hy = new float[MaxObjects], _hz = new float[MaxObjects];
    private readonly float[] _inv = new float[MaxObjects], _mass = new float[MaxObjects], _rest = new float[MaxObjects], _fric = new float[MaxObjects];
    private readonly float[] _cr = new float[MaxObjects], _cg = new float[MaxObjects], _cb = new float[MaxObjects];
    private readonly byte[] _shape = new byte[MaxObjects], _flags = new byte[MaxObjects];
    private readonly string[] _names = new string[MaxObjects], _labels = new string[MaxObjects];
    private int _count, _fps = 30, _frame;

    private float _time, _gx, _gy = -9.81f, _gz, _damping = 0.02f, _reflect = 0.25f, _timeScale = 1f;
    private LightPreset _light;
    private float _lx, _ly, _lz, _lr, _lg, _lb, _amb, _skr, _skg, _skb, _sbr, _sbg, _sbb;

    private CameraSpec _cam;
    private float _camStart;
    private float _bex, _bey, _bez, _btx, _bty, _btz, _bfov;
    private int _blendTotal, _blendLeft;
    private float _trx, _try, _trz;
    private bool _trackInit;
    private float _ex, _ey, _ez, _tx, _ty, _tz, _fov = 45f;
    private bool _poseValid, _cutPending;

    private readonly float[] _sT = new float[MaxSchedule];
    private readonly WorldOp[] _sOp = new WorldOp[MaxSchedule];
    private int _sN, _sCur;

    private long _framesSim, _framesRendered, _energyViol, _permViol, _camJumps;
    private float _maxDrift, _maxPen, _maxCamDeg, _lastPen, _eRef;
    private bool _eValid;
    private int _expectedVisible;

    public AiGenerative4dWorldModelEngine()
    {
        for (var i = 0; i < MaxObjects; i++) { _names[i] = ""; _labels[i] = ""; }
        Reset();
    }

    public event Action? OnChange;

    public int ObjectCount => _count;
    public int Fps { get => _fps; set => _fps = Math.Clamp(value, 12, 60) switch { <= 12 => 12, <= 15 => 15, <= 16 => 16, <= 20 => 20, <= 24 => 24, <= 30 => 30, <= 40 => 40, <= 48 => 48, _ => 60 }; }
    public float Time => _time;
    public int Frame => _frame;
    public float GravityY => _gy;
    public float Reflectivity => _reflect;
    public float TimeScale => _timeScale;
    public LightPreset Light => _light;
    public CameraSpec Camera => _cam;
    public long FramesSimulated => Interlocked.Read(ref _framesSim);
    public long FramesRendered => Interlocked.Read(ref _framesRendered);
    public long EnergyViolations => Interlocked.Read(ref _energyViol);
    public long PermanenceViolations => Interlocked.Read(ref _permViol);
    public long CameraJumps => Interlocked.Read(ref _camJumps);
    public float MaxEnergyDrift => _maxDrift;
    public float MaxPenetration => _maxPen;
    public float MaxCameraDegPerSec => _maxCamDeg;
    public WorldConsistencyReport? LastReport { get; private set; }
    public string? LastAction { get; private set; }

    public string Name(int i) => _names[i];
    public string Label(int i) => _labels[i];
    public WorldShape Shape(int i) => (WorldShape)_shape[i];
    public (float X, float Y, float Z) Position(int i) => (_px[i], _py[i], _pz[i]);
    public (float X, float Y, float Z) Velocity(int i) => (_vx[i], _vy[i], _vz[i]);
    public (float X, float Y, float Z) Half(int i) => (_hx[i], _hy[i], _hz[i]);
    public (float R, float G, float B) Color(int i) => (_cr[i], _cg[i], _cb[i]);
    public float Mass(int i) => _mass[i];
    public float Restitution(int i) => _rest[i];
    public bool IsStatic(int i) => (_flags[i] & FStatic) != 0;
    public bool IsHidden(int i) => (_flags[i] & FHidden) != 0;
    public bool IsHeld(int i) => (_flags[i] & FHeld) != 0;
    public int ScheduledCount => _sN;

    public int IndexOf(string name)
    {
        for (var i = 0; i < _count; i++) if (string.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ══════════════════════ 장면 구성(비 핫패스) ══════════════════════

    public void Reset()
    {
        Array.Clear(_flags);
        _count = 0; _frame = 0; _time = 0; _gx = 0; _gy = -9.81f; _gz = 0; _damping = 0.02f; _reflect = 0.25f; _timeScale = 1f;
        _sN = 0; _sCur = 0; _blendLeft = 0; _blendTotal = 0; _trackInit = false; _poseValid = false; _cutPending = false;
        _expectedVisible = 0; _eValid = false;
        SetLighting(LightPreset.Studio);
        _cam = new CameraSpec { Mode = CameraMode.Static, Target = -1, Cy = 0.5f, Radius = 5f, Height = 1.4f, Fov = 45f, Duration = 5f, Radius1 = 3f };
        _camStart = 0;
    }

    /// <summary>강체 생성. mass ≤ 0 = 정적(지지체). 구체는 hx = 반지름.</summary>
    public int Spawn(string name, string label, WorldShape shape, float x, float y, float z, float hx, float hy, float hz, float mass, float rest, float fric, float r, float g, float b)
    {
        if (_count >= MaxObjects) return -1;
        var i = _count++;
        _names[i] = name; _labels[i] = label; _shape[i] = (byte)shape;
        _px[i] = x; _py[i] = y; _pz[i] = z; _vx[i] = 0; _vy[i] = 0; _vz[i] = 0;
        hx = Math.Max(0.02f, hx);
        _hx[i] = hx; _hy[i] = shape == WorldShape.Sphere ? hx : Math.Max(0.02f, hy); _hz[i] = shape == WorldShape.Sphere ? hx : Math.Max(0.02f, hz);
        _mass[i] = mass > 0 ? mass : 0; _inv[i] = mass > 0 ? 1f / mass : 0;
        _rest[i] = Math.Clamp(rest, 0f, 0.95f); _fric[i] = Math.Clamp(fric, 0f, 1.5f);
        _cr[i] = r; _cg[i] = g; _cb[i] = b;
        _flags[i] = (byte)(FAlive | (mass > 0 ? 0 : FStatic));
        _expectedVisible++;
        _eValid = false;
        return i;
    }

    public void SetLighting(LightPreset p)
    {
        _light = p;
        switch (p)
        {
            case LightPreset.Noon: Sun(0.15f, 0.97f, 0.2f, 1.1f, 1.1f, 1.05f, 0.32f, 0.25f, 0.5f, 0.9f, 0.7f, 0.82f, 0.95f); break;
            case LightPreset.Sunset: Sun(-0.85f, 0.22f, 0.3f, 1.05f, 0.65f, 0.37f, 0.2f, 0.25f, 0.22f, 0.45f, 0.98f, 0.55f, 0.3f); break;
            case LightPreset.Night: Sun(0.3f, 0.6f, -0.5f, 0.25f, 0.3f, 0.47f, 0.1f, 0.02f, 0.03f, 0.08f, 0.06f, 0.08f, 0.16f); break;
            case LightPreset.Overcast: Sun(0.1f, 1f, 0.1f, 0.51f, 0.52f, 0.54f, 0.45f, 0.55f, 0.57f, 0.6f, 0.72f, 0.74f, 0.77f); break;
            default: Sun(0.35f, 0.85f, 0.4f, 1f, 0.98f, 0.95f, 0.28f, 0.2f, 0.22f, 0.27f, 0.42f, 0.44f, 0.48f); break;
        }
    }

    private void Sun(float x, float y, float z, float r, float g, float b, float amb, float tr, float tg, float tb, float br, float bg, float bb)
    {
        var l = MathF.Sqrt(x * x + y * y + z * z);
        _lx = x / l; _ly = y / l; _lz = z / l; _lr = r; _lg = g; _lb = b; _amb = amb;
        _skr = tr; _skg = tg; _skb = tb; _sbr = br; _sbg = bg; _sbb = bb;
    }

    /// <summary>예약 연산(시뮬 시각 도달 시 적용 · 정렬 삽입).</summary>
    public bool Schedule(float atSeconds, in WorldOp op)
    {
        if (_sN >= MaxSchedule) return false;
        var k = _sN;
        while (k > _sCur && _sT[k - 1] > atSeconds) { _sT[k] = _sT[k - 1]; _sOp[k] = _sOp[k - 1]; k--; }
        _sT[k] = atSeconds; _sOp[k] = op; _sN++;
        return true;
    }

    /// <summary>장면 중심/범위(가시 객체 AABB).</summary>
    public void SceneCenter(out float cx, out float cy, out float cz, out float extent)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
        for (var i = 0; i < _count; i++)
        {
            if ((_flags[i] & (FAlive | FHidden)) != FAlive) continue;
            x0 = Math.Min(x0, _px[i] - _hx[i]); x1 = Math.Max(x1, _px[i] + _hx[i]);
            y0 = Math.Min(y0, _py[i] - _hy[i]); y1 = Math.Max(y1, _py[i] + _hy[i]);
            z0 = Math.Min(z0, _pz[i] - _hz[i]); z1 = Math.Max(z1, _pz[i] + _hz[i]);
        }
        if (x0 > x1) { cx = 0; cy = 0.5f; cz = 0; extent = 1f; return; }
        cx = (x0 + x1) * 0.5f; cz = (z0 + z1) * 0.5f;
        cy = Math.Clamp((Math.Max(0, y0) + Math.Min(y1, 3f)) * 0.5f, 0.3f, 2f);
        extent = Math.Max(1f, Math.Max(x1 - x0, Math.Max(Math.Min(y1, 4f) - Math.Max(0, y0), z1 - z0)));
    }

    /// <summary>모드별 카메라 사양 생성(장면 범위 자동 프레이밍 · 무할당).</summary>
    public CameraSpec MakeCamera(CameraMode mode, int target, bool slow, float fov, float duration)
    {
        SceneCenter(out var cx, out var cy, out var cz, out var ext);
        var r = Math.Clamp(ext * 1.6f + 3f, 3.5f, 14f);
        return new CameraSpec
        {
            Mode = mode, Target = mode == CameraMode.Track && (uint)target < (uint)_count ? target : -1,
            Cx = cx, Cy = cy, Cz = cz, Radius = r, Height = 1f + ext * 0.25f, Angle0 = 0f,
            Sweep = slow ? 1.05f : 2.1f, Fov = fov > 5f ? Math.Min(fov, 100f) : 45f, Duration = duration > 0.5f ? duration : 5f, Radius1 = r * 0.55f, Shake = 0f,
        };
    }

    /// <summary>카메라 궤적 교체 — blendFrames &gt; 0 이면 현재 포즈에서 스무스 블렌드(끊김 없음), 0 이면 컷.</summary>
    public void SetCamera(in CameraSpec spec, int blendFrames)
    {
        EvaluatePose(out _bex, out _bey, out _bez, out _btx, out _bty, out _btz, out _bfov);
        _cam = spec;
        _camStart = _time;
        _trackInit = false;
        _blendTotal = _blendLeft = Math.Max(0, blendFrames);
        if (blendFrames <= 0) _cutPending = true;
    }

    /// <summary>의도된 하드 컷(다음 프레임 카메라 점프 검사 면제).</summary>
    public void CutCamera() => _cutPending = true;

    // ══════════════════════ 개입 연산(무할당) ══════════════════════

    public void Apply(in WorldOp op)
    {
        var i = op.Obj;
        var valid = (uint)i < (uint)_count && (_flags[i] & FAlive) != 0;
        switch (op.Kind)
        {
            case WorldOpKind.Impulse:
                if (valid && _inv[i] > 0) { _flags[i] = (byte)(_flags[i] & ~FHeld); _vx[i] += op.X; _vy[i] += op.Y; _vz[i] += op.Z; }
                break;
            case WorldOpKind.SetVelocity:
                if (valid && _inv[i] > 0) { _flags[i] = (byte)(_flags[i] & ~FHeld); _vx[i] = op.X; _vy[i] = op.Y; _vz[i] = op.Z; }
                break;
            case WorldOpKind.Teleport:
                if (valid) { _px[i] = op.X; _py[i] = op.Y; _pz[i] = op.Z; _vx[i] = 0; _vy[i] = 0; _vz[i] = 0; }
                break;
            case WorldOpKind.Gravity: _gx = op.X; _gy = op.Y; _gz = op.Z; break;
            case WorldOpKind.Hold:
                if (valid && _inv[i] > 0) { _flags[i] = (byte)(_flags[i] | FHeld); _vx[i] = 0; _vy[i] = 0; _vz[i] = 0; }
                break;
            case WorldOpKind.Release:
                if (valid) _flags[i] = (byte)(_flags[i] & ~FHeld);
                break;
            case WorldOpKind.Hide:
                if (valid && (_flags[i] & FHidden) == 0) { _flags[i] = (byte)(_flags[i] | FHidden); _expectedVisible--; }
                break;
            case WorldOpKind.Show:
                if (valid && (_flags[i] & FHidden) != 0) { _flags[i] = (byte)(_flags[i] & ~FHidden); _expectedVisible++; }
                break;
            case WorldOpKind.TimeScale: _timeScale = Math.Clamp(op.X, 0.05f, 4f); break;
            case WorldOpKind.Restitution:
                for (var k = 0; k < _count; k++) if ((i < 0 || k == i) && _inv[k] > 0) _rest[k] = Math.Clamp(op.X, 0f, 0.95f);
                break;
            case WorldOpKind.Friction:
                for (var k = 0; k < _count; k++) if (i < 0 || k == i) _fric[k] = Math.Clamp(op.X, 0f, 1.5f);
                break;
            case WorldOpKind.Reflectivity: _reflect = Math.Clamp(op.X, 0f, 0.9f); break;
        }
        _eValid = false;   // 외부 에너지 주입 → 기준선 재설정
    }

    // ══════════════════════ 시뮬레이션(핫패스 · 무할당) ══════════════════════

    /// <summary>비디오 1프레임 진행(240Hz 서브스텝 · 스케줄 · 카메라 · 일관성 모니터).</summary>
    public void StepFrame()
    {
        var sub = PhysicsHz / _fps;
        var dt = _timeScale / PhysicsHz;
        for (var s = 0; s < sub; s++)
        {
            while (_sCur < _sN && _sT[_sCur] <= _time + 1e-6f) { Apply(in _sOp[_sCur]); _sCur++; }
            Integrate(dt);
            Solve();
            _time += dt;
        }
        _frame++;
        Interlocked.Increment(ref _framesSim);
        UpdateTrack(_timeScale / _fps);
        MonitorFrame();
        if (_blendLeft > 0) _blendLeft--;
    }

    private bool Dyn(int i) => (_flags[i] & (FAlive | FStatic | FHeld | FHidden)) == FAlive && _inv[i] > 0;
    private bool Solid(int i) => (_flags[i] & (FAlive | FHidden)) == FAlive;

    private void Integrate(float dt)
    {
        var damp = 1f - _damping * dt;
        for (var i = 0; i < _count; i++)
        {
            if (!Dyn(i)) continue;
            _vx[i] = (_vx[i] + _gx * dt) * damp; _vy[i] = (_vy[i] + _gy * dt) * damp; _vz[i] = (_vz[i] + _gz * dt) * damp;
            _px[i] += _vx[i] * dt; _py[i] += _vy[i] * dt; _pz[i] += _vz[i] * dt;
        }
    }

    private void Solve()
    {
        var maxPen = 0f;
        for (var it = 0; it < 3; it++)
        {
            maxPen = 0f;
            for (var i = 0; i < _count; i++) if (Dyn(i)) Floor(i, ref maxPen);
            for (var i = 0; i < _count; i++)
            {
                if (!Solid(i)) continue;
                for (var j = i + 1; j < _count; j++)
                {
                    if (!Solid(j) || (!Dyn(i) && !Dyn(j))) continue;
                    if (Contact(i, j, out var nx, out var ny, out var nz, out var pen)) Resolve(i, j, nx, ny, nz, pen, ref maxPen);
                }
            }
        }
        _lastPen = maxPen;
    }

    private void Floor(int i, ref float maxPen)
    {
        var bottom = _py[i] - _hy[i];
        if (bottom >= 0) return;
        var pen = -bottom;
        if (pen > maxPen) maxPen = pen;
        _py[i] += pen;
        if (_vy[i] >= 0) return;
        var vn = -_vy[i];
        var e = vn < RestThreshold ? 0f : _rest[i];
        _vy[i] = vn * e;
        var mu = _shape[i] == (byte)WorldShape.Sphere ? _fric[i] * RollingFactor : _fric[i];
        var dv = mu * vn * (1f + e);
        var sp = MathF.Sqrt(_vx[i] * _vx[i] + _vz[i] * _vz[i]);
        if (sp <= dv) { _vx[i] = 0; _vz[i] = 0; }
        else { var k = (sp - dv) / sp; _vx[i] *= k; _vz[i] *= k; }
    }

    /// <summary>접촉 판정(법선 i→j · 침투 깊이).</summary>
    private bool Contact(int i, int j, out float nx, out float ny, out float nz, out float pen)
    {
        var si = _shape[i] == (byte)WorldShape.Sphere; var sj = _shape[j] == (byte)WorldShape.Sphere;
        if (si && sj)
        {
            float dx = _px[j] - _px[i], dy = _py[j] - _py[i], dz = _pz[j] - _pz[i];
            var d2 = dx * dx + dy * dy + dz * dz; var rr = _hx[i] + _hx[j];
            if (d2 >= rr * rr) { nx = ny = nz = pen = 0; return false; }
            var d = MathF.Sqrt(d2);
            if (d < 1e-6f) { nx = 0; ny = 1; nz = 0; } else { nx = dx / d; ny = dy / d; nz = dz / d; }
            pen = rr - d;
            return true;
        }
        if (si) return SphereBox(i, j, out nx, out ny, out nz, out pen);
        if (sj)
        {
            var hit = SphereBox(j, i, out nx, out ny, out nz, out pen);
            nx = -nx; ny = -ny; nz = -nz;
            return hit;
        }
        float ox = _hx[i] + _hx[j] - MathF.Abs(_px[j] - _px[i]), oy = _hy[i] + _hy[j] - MathF.Abs(_py[j] - _py[i]), oz = _hz[i] + _hz[j] - MathF.Abs(_pz[j] - _pz[i]);
        if (ox <= 0 || oy <= 0 || oz <= 0) { nx = ny = nz = pen = 0; return false; }
        nx = ny = nz = 0;
        if (oy <= ox && oy <= oz) { ny = _py[j] >= _py[i] ? 1 : -1; pen = oy; }
        else if (ox <= oz) { nx = _px[j] >= _px[i] ? 1 : -1; pen = ox; }
        else { nz = _pz[j] >= _pz[i] ? 1 : -1; pen = oz; }
        return true;
    }

    /// <summary>구(s)-상자(b) 접촉 · 법선 s→b.</summary>
    private bool SphereBox(int s, int b, out float nx, out float ny, out float nz, out float pen)
    {
        float cx = _px[s], cy = _py[s], cz = _pz[s], r = _hx[s];
        float qx = Math.Clamp(cx, _px[b] - _hx[b], _px[b] + _hx[b]), qy = Math.Clamp(cy, _py[b] - _hy[b], _py[b] + _hy[b]), qz = Math.Clamp(cz, _pz[b] - _hz[b], _pz[b] + _hz[b]);
        float dx = qx - cx, dy = qy - cy, dz = qz - cz;
        var d2 = dx * dx + dy * dy + dz * dz;
        if (d2 > 1e-12f)
        {
            if (d2 >= r * r) { nx = ny = nz = pen = 0; return false; }
            var d = MathF.Sqrt(d2);
            nx = dx / d; ny = dy / d; nz = dz / d; pen = r - d;
            return true;
        }
        // 중심이 상자 내부 → 최소 탈출 축
        float ex = _hx[b] - MathF.Abs(cx - _px[b]), ey = _hy[b] - MathF.Abs(cy - _py[b]), ez = _hz[b] - MathF.Abs(cz - _pz[b]);
        nx = ny = nz = 0;
        if (ey <= ex && ey <= ez) { ny = cy >= _py[b] ? -1 : 1; pen = r + ey; }
        else if (ex <= ez) { nx = cx >= _px[b] ? -1 : 1; pen = r + ex; }
        else { nz = cz >= _pz[b] ? -1 : 1; pen = r + ez; }
        return true;
    }

    private void Resolve(int i, int j, float nx, float ny, float nz, float pen, ref float maxPen)
    {
        var wi = Dyn(i) ? _inv[i] : 0f; var wj = Dyn(j) ? _inv[j] : 0f; var w = wi + wj;
        if (w <= 0) return;
        if (pen > maxPen) maxPen = pen;
        var corr = Math.Max(pen - 0.0005f, 0f) / w * 0.8f;
        _px[i] -= nx * corr * wi; _py[i] -= ny * corr * wi; _pz[i] -= nz * corr * wi;
        _px[j] += nx * corr * wj; _py[j] += ny * corr * wj; _pz[j] += nz * corr * wj;
        float rx = _vx[j] - _vx[i], ry = _vy[j] - _vy[i], rz = _vz[j] - _vz[i];
        var vn = rx * nx + ry * ny + rz * nz;
        if (vn >= 0) return;
        var e = -vn < RestThreshold ? 0f : Math.Min(_rest[i], _rest[j]);
        var jn = -(1f + e) * vn / w;
        _vx[i] -= nx * jn * wi; _vy[i] -= ny * jn * wi; _vz[i] -= nz * jn * wi;
        _vx[j] += nx * jn * wj; _vy[j] += ny * jn * wj; _vz[j] += nz * jn * wj;
        rx = _vx[j] - _vx[i]; ry = _vy[j] - _vy[i]; rz = _vz[j] - _vz[i];
        vn = rx * nx + ry * ny + rz * nz;
        float tx = rx - nx * vn, ty = ry - ny * vn, tz = rz - nz * vn;
        var tl = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
        if (tl < 1e-6f) return;
        var mu = MathF.Sqrt(_fric[i] * _fric[j]);
        if (_shape[i] == (byte)WorldShape.Sphere || _shape[j] == (byte)WorldShape.Sphere) mu *= RollingFactor;
        var jt = Math.Min(tl / w, mu * jn);
        tx /= tl; ty /= tl; tz /= tl;
        _vx[i] += tx * jt * wi; _vy[i] += ty * jt * wi; _vz[i] += tz * jt * wi;
        _vx[j] -= tx * jt * wj; _vy[j] -= ty * jt * wj; _vz[j] -= tz * jt * wj;
    }

    private void UpdateTrack(float dt)
    {
        var t = _cam.Target;
        if (_cam.Mode != CameraMode.Track || (uint)t >= (uint)_count) return;
        if (!_trackInit) { _trx = _px[t]; _try = _py[t]; _trz = _pz[t]; _trackInit = true; return; }
        var k = 1f - MathF.Exp(-4f * dt);
        _trx += (_px[t] - _trx) * k; _try += (Math.Max(0.3f, _py[t]) - _try) * k; _trz += (_pz[t] - _trz) * k;
    }

    /// <summary>총 기계적 에너지(동적 · 가시).</summary>
    public float Energy()
    {
        var e = 0f;
        for (var i = 0; i < _count; i++)
        {
            if (!Dyn(i)) continue;
            var v2 = _vx[i] * _vx[i] + _vy[i] * _vy[i] + _vz[i] * _vz[i];
            e += _mass[i] * (0.5f * v2 - (_gx * _px[i] + _gy * _py[i] + _gz * _pz[i]));
        }
        return e;
    }

    private void MonitorFrame()
    {
        // ① 에너지 비증가(외부 연산 이후 기준선 재설정)
        var e = Energy();
        var massSum = 0f;
        for (var i = 0; i < _count; i++) if (Dyn(i)) massSum += _mass[i];
        if (!_eValid) { _eRef = e; _eValid = true; }
        else
        {
            var tol = 0.03f * MathF.Abs(_eRef) + 0.02f * massSum + 1e-3f;
            if (e > _eRef + tol)
            {
                Interlocked.Increment(ref _energyViol);
                var drift = (e - _eRef) / Math.Max(MathF.Abs(_eRef), 1e-3f);
                if (drift > _maxDrift) _maxDrift = drift;
                _eRef = e;
            }
            else if (e < _eRef) _eRef = e;
        }
        // ② 침투
        if (_lastPen > _maxPen) _maxPen = _lastPen;
        // ③ 객체 영속성(가시 개수 · NaN · 터널링 → 복구)
        var visible = 0;
        for (var i = 0; i < _count; i++)
        {
            if ((_flags[i] & (FAlive | FHidden)) != FAlive) continue;
            visible++;
            if (_inv[i] <= 0) continue;
            if (float.IsNaN(_px[i] + _py[i] + _pz[i]) || MathF.Abs(_px[i]) > 1e4f || MathF.Abs(_pz[i]) > 1e4f || _py[i] < -1f || _py[i] > 1e4f)
            {
                Interlocked.Increment(ref _permViol);
                _px[i] = float.IsNaN(_px[i]) ? 0 : Math.Clamp(_px[i], -50f, 50f); _pz[i] = float.IsNaN(_pz[i]) ? 0 : Math.Clamp(_pz[i], -50f, 50f);
                _py[i] = _hy[i]; _vx[i] = 0; _vy[i] = 0; _vz[i] = 0;
                _eValid = false;
            }
        }
        if (visible != _expectedVisible) { Interlocked.Increment(ref _permViol); _expectedVisible = visible; }
        // ④ 카메라 연속성
        EvaluatePose(out var ex, out var ey, out var ez, out var tx, out var ty, out var tz, out var fov);
        if (_poseValid && !_cutPending)
        {
            float ax = _tx - _ex, ay = _ty - _ey, az = _tz - _ez, bx = tx - ex, by = ty - ey, bz = tz - ez;
            var la = MathF.Sqrt(ax * ax + ay * ay + az * az); var lb = MathF.Sqrt(bx * bx + by * by + bz * bz);
            var cos = la > 1e-6f && lb > 1e-6f ? Math.Clamp((ax * bx + ay * by + az * bz) / (la * lb), -1f, 1f) : 1f;
            var deg = MathF.Acos(cos) * (180f / MathF.PI);
            var mv = MathF.Sqrt((ex - _ex) * (ex - _ex) + (ey - _ey) * (ey - _ey) + (ez - _ez) * (ez - _ez));
            var dps = deg * _fps;
            if (dps > _maxCamDeg) _maxCamDeg = dps;
            if (deg > 25f || mv > 1.5f || MathF.Abs(fov - _fov) > 15f) Interlocked.Increment(ref _camJumps);
        }
        _ex = ex; _ey = ey; _ez = ez; _tx = tx; _ty = ty; _tz = tz; _fov = fov;
        _poseValid = true; _cutPending = false;
    }

    /// <summary>현재 시각 카메라 포즈(궤적 + 블렌드).</summary>
    public void EvaluatePose(out float ex, out float ey, out float ez, out float tx, out float ty, out float tz, out float fov)
    {
        var c = _cam;
        var u = Math.Clamp((_time - _camStart) / Math.Max(0.5f, c.Duration), 0f, 1f);
        var s = u * u * (3f - 2f * u);
        float cx = c.Cx, cy = c.Cy, cz = c.Cz, r = c.Radius, h = c.Height, a = c.Angle0;
        switch (c.Mode)
        {
            case CameraMode.Orbit: a += c.Sweep * s; break;
            case CameraMode.Dolly: r += (c.Radius1 - r) * s; break;
            case CameraMode.Crane: h += r * 0.8f * s; break;
            case CameraMode.Track: if (_trackInit) { cx = _trx; cy = _try; cz = _trz; } break;
            case CameraMode.LowAngle: h = 0.2f; break;
        }
        if (c.Mode == CameraMode.Overhead) { ex = cx + 0.001f; ey = cy + r * 1.5f; ez = cz + r * 0.3f; }
        else { ex = cx + r * MathF.Sin(a); ey = cy + h; ez = cz + r * MathF.Cos(a); }
        tx = cx; ty = c.Mode == CameraMode.LowAngle ? cy + 0.4f : cy; tz = cz;
        if (c.Shake > 0)
        {
            var n = _time;
            ex += c.Shake * 0.03f * (MathF.Sin(n * 7.1f) + 0.5f * MathF.Sin(n * 13.7f));
            ey += c.Shake * 0.02f * MathF.Sin(n * 9.3f + 1f);
        }
        fov = c.Fov;
        if (_blendLeft > 0 && _blendTotal > 0)
        {
            var w = 1f - (float)_blendLeft / _blendTotal;
            w = w * w * (3f - 2f * w);
            ex = _bex + (ex - _bex) * w; ey = _bey + (ey - _bey) * w; ez = _bez + (ez - _bez) * w;
            tx = _btx + (tx - _btx) * w; ty = _bty + (ty - _bty) * w; tz = _btz + (tz - _btz) * w;
            fov = _bfov + (fov - _bfov) * w;
        }
    }

    // ══════════════════════ 렌더(무할당 레이트레이서) ══════════════════════

    /// <summary>RGB24(+선택 깊이/객체 ID) 렌더. ID: 0=하늘 · 1=바닥 · 2+i=객체.</summary>
    public void Render(Span<byte> rgb, int w, int h, Span<float> depth = default, Span<byte> ids = default)
    {
        if (w <= 0 || h <= 0 || rgb.Length < w * h * 3) throw new ArgumentException("버퍼 크기 부족", nameof(rgb));
        float ex, ey, ez, tx, ty, tz, fov;
        if (_poseValid) { ex = _ex; ey = _ey; ez = _ez; tx = _tx; ty = _ty; tz = _tz; fov = _fov; }
        else EvaluatePose(out ex, out ey, out ez, out tx, out ty, out tz, out fov);
        float fx = tx - ex, fy = ty - ey, fz = tz - ez;
        var fl = MathF.Sqrt(fx * fx + fy * fy + fz * fz); fx /= fl; fy /= fl; fz /= fl;
        float rx = -fz, rz = fx;                                   // right = f × up(0,1,0)
        var rl = MathF.Sqrt(rx * rx + rz * rz);
        if (rl < 1e-6f) { rx = 1; rz = 0; rl = 1; }
        rx /= rl; rz /= rl;
        float ux = -rz * fy, uy = rz * fx - rx * fz, uz = rx * fy;  // up = r × f
        var th = MathF.Tan(fov * (MathF.PI / 360f)); var asp = (float)w / h;
        var wantDepth = depth.Length >= w * h; var wantIds = ids.Length >= w * h;
        for (var y = 0; y < h; y++)
        {
            var py = (1f - 2f * (y + 0.5f) / h) * th;
            for (var x = 0; x < w; x++)
            {
                var px = (2f * (x + 0.5f) / w - 1f) * th * asp;
                float dx = fx + rx * px + ux * py, dy = fy + uy * py, dz = fz + rz * px + uz * py;
                var dl = MathF.Sqrt(dx * dx + dy * dy + dz * dz); dx /= dl; dy /= dl; dz /= dl;
                Trace(ex, ey, ez, dx, dy, dz, true, out var cr, out var cg, out var cb, out var t, out var id);
                var o = (y * w + x) * 3;
                rgb[o] = ToByte(cr); rgb[o + 1] = ToByte(cg); rgb[o + 2] = ToByte(cb);
                if (wantDepth) depth[y * w + x] = id == -2 ? 1e4f : t;
                if (wantIds) ids[y * w + x] = (byte)Math.Min(255, id + 2);
            }
        }
        Interlocked.Increment(ref _framesRendered);
    }

    private static byte ToByte(float c) => (byte)(MathF.Sqrt(Math.Clamp(c, 0f, 1f)) * 255f + 0.5f);

    private void Trace(float ox, float oy, float oz, float dx, float dy, float dz, bool reflect, out float r, out float g, out float b, out float t, out int id)
    {
        if (!Intersect(ox, oy, oz, dx, dy, dz, 1e9f, false, out t, out id))
        {
            var k = Math.Clamp(dy * 1.6f + 0.25f, 0f, 1f);
            r = _sbr + (_skr - _sbr) * k; g = _sbg + (_skg - _sbg) * k; b = _sbb + (_skb - _sbb) * k;
            return;
        }
        float hx = ox + dx * t, hy = oy + dy * t, hz = oz + dz * t, nx, ny, nz, br, bg, bb;
        if (id < 0)
        {
            nx = 0; ny = 1; nz = 0;
            var chk = (((int)MathF.Floor(hx) + (int)MathF.Floor(hz)) & 1) == 0 ? 0.55f : 0.42f;
            br = chk; bg = chk; bb = chk * 1.04f;
        }
        else
        {
            if (_shape[id] == (byte)WorldShape.Sphere) { var inv = 1f / _hx[id]; nx = (hx - _px[id]) * inv; ny = (hy - _py[id]) * inv; nz = (hz - _pz[id]) * inv; }
            else
            {
                float qx = (hx - _px[id]) / _hx[id], qy = (hy - _py[id]) / _hy[id], qz = (hz - _pz[id]) / _hz[id];
                float ax = MathF.Abs(qx), ay = MathF.Abs(qy), az = MathF.Abs(qz);
                nx = ny = nz = 0;
                if (ax >= ay && ax >= az) nx = qx >= 0 ? 1 : -1; else if (ay >= az) ny = qy >= 0 ? 1 : -1; else nz = qz >= 0 ? 1 : -1;
            }
            br = _cr[id]; bg = _cg[id]; bb = _cb[id];
        }
        var ndl = Math.Max(0f, nx * _lx + ny * _ly + nz * _lz);
        var sh = 1f;
        if (ndl > 0 && Intersect(hx + nx * 1e-3f, hy + ny * 1e-3f, hz + nz * 1e-3f, _lx, _ly, _lz, 1e9f, true, out _, out _)) sh = 0.25f;
        r = br * (_amb + ndl * sh * _lr); g = bg * (_amb + ndl * sh * _lg); b = bb * (_amb + ndl * sh * _lb);
        if (id >= 0 && sh > 0.5f)
        {
            float hvx = _lx - dx, hvy = _ly - dy, hvz = _lz - dz;
            var hl = MathF.Sqrt(hvx * hvx + hvy * hvy + hvz * hvz);
            var sp = MathF.Pow(Math.Max(0f, (nx * hvx + ny * hvy + nz * hvz) / hl), 40f) * 0.4f;
            r += sp * _lr; g += sp * _lg; b += sp * _lb;
        }
        if (id < 0 && reflect && _reflect > 0)
        {
            Trace(hx, hy + 1e-3f, hz, dx, -dy, dz, false, out var rr, out var rg, out var rb, out _, out _);
            r += (rr - r) * _reflect; g += (rg - g) * _reflect; b += (rb - b) * _reflect;
        }
        var fog = 1f - MathF.Exp(-t * 0.015f);
        r += (_sbr - r) * fog; g += (_sbg - g) * fog; b += (_sbb - b) * fog;
    }

    private bool Intersect(float ox, float oy, float oz, float dx, float dy, float dz, float tMax, bool any, out float t, out int id)
    {
        var best = tMax; id = -2;
        for (var i = 0; i < _count; i++)
        {
            if ((_flags[i] & (FAlive | FHidden)) != FAlive) continue;
            float lx = ox - _px[i], ly = oy - _py[i], lz = oz - _pz[i];
            if (_shape[i] == (byte)WorldShape.Sphere)
            {
                var bq = lx * dx + ly * dy + lz * dz; var c = lx * lx + ly * ly + lz * lz - _hx[i] * _hx[i];
                var disc = bq * bq - c;
                if (disc < 0) continue;
                var sq = MathF.Sqrt(disc); var t0 = -bq - sq;
                if (t0 < 1e-4f) t0 = -bq + sq;
                if (t0 > 1e-4f && t0 < best) { best = t0; id = i; if (any) { t = best; return true; } }
            }
            else
            {
                float t0 = -1e30f, t1 = 1e30f;
                if (!Slab(lx, dx, _hx[i], ref t0, ref t1) || !Slab(ly, dy, _hy[i], ref t0, ref t1) || !Slab(lz, dz, _hz[i], ref t0, ref t1)) continue;
                var te = t0 > 1e-4f ? t0 : t1;
                if (te > 1e-4f && te < best) { best = te; id = i; if (any) { t = best; return true; } }
            }
        }
        if (dy < -1e-6f)
        {
            var tf = -oy / dy;
            if (tf > 1e-4f && tf < best)
            {
                float fx = ox + dx * tf, fz = oz + dz * tf;
                if (fx * fx + fz * fz < FloorExtent2) { best = tf; id = -1; }
            }
        }
        t = best;
        return id != -2;
    }

    private static bool Slab(float o, float d, float h, ref float t0, ref float t1)
    {
        if (MathF.Abs(d) < 1e-8f) return o >= -h && o <= h;
        var inv = 1f / d;
        var a = (-h - o) * inv; var b = (h - o) * inv;
        if (a > b) (a, b) = (b, a);
        if (a > t0) t0 = a;
        if (b < t1) t1 = b;
        return t0 <= t1;
    }

    /// <summary>객체 중심 화면 투영(현재 포즈).</summary>
    public bool Project(int i, int w, int h, out float sx, out float sy)
    {
        sx = sy = -1;
        if ((uint)i >= (uint)_count) return false;
        EvaluatePose(out var ex, out var ey, out var ez, out var tx, out var ty, out var tz, out var fov);
        float fx = tx - ex, fy = ty - ey, fz = tz - ez;
        var fl = MathF.Sqrt(fx * fx + fy * fy + fz * fz); fx /= fl; fy /= fl; fz /= fl;
        float rx = -fz, rz = fx; var rl = MathF.Sqrt(rx * rx + rz * rz); rx /= rl; rz /= rl;
        float ux = -rz * fy, uy = rz * fx - rx * fz, uz = rx * fy;
        float vx = _px[i] - ex, vy = _py[i] - ey, vz = _pz[i] - ez;
        var zf = vx * fx + vy * fy + vz * fz;
        if (zf <= 0.01f) return false;
        var th = MathF.Tan(fov * (MathF.PI / 360f)); var asp = (float)w / h;
        var xr = (vx * rx + vz * rz) / zf / (th * asp); var yu = (vx * ux + vy * uy + vz * uz) / zf / th;
        sx = (xr + 1f) * 0.5f * w; sy = (1f - yu) * 0.5f * h;
        return sx >= 0 && sx < w && sy >= 0 && sy < h;
    }

    /// <summary>두 RGB 프레임 평균 절대 휘도 차(0~255).</summary>
    public static float MeanAbsLumaDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var n = Math.Min(a.Length, b.Length) / 3;
        if (n == 0) return 0;
        long sum = 0;
        for (var i = 0; i < n; i++)
        {
            var o = i * 3;
            var la = a[o] * 77 + a[o + 1] * 150 + a[o + 2] * 29;
            var lb = b[o] * 77 + b[o + 1] * 150 + b[o + 2] * 29;
            sum += Math.Abs(la - lb);
        }
        return sum / 256f / n;
    }

    // ══════════════════════ 4D 상태 매트릭스(무할당) ══════════════════════

    public void Capture(Span<float> s)
    {
        if (s.Length < StateFloats) throw new ArgumentException("상태 버퍼 크기 부족", nameof(s));
        s[..StateFloats].Clear();
        s[0] = _time; s[1] = _gx; s[2] = _gy; s[3] = _gz; s[4] = _count; s[5] = _damping; s[6] = _reflect; s[7] = _timeScale; s[8] = _frame; s[9] = (float)_light; s[10] = _fps;
        s[12] = (float)_cam.Mode; s[13] = _cam.Target; s[14] = _cam.Cx; s[15] = _cam.Cy; s[16] = _cam.Cz; s[17] = _cam.Radius; s[18] = _cam.Height; s[19] = _cam.Angle0;
        s[20] = _cam.Sweep; s[21] = _cam.Fov; s[22] = _cam.Duration; s[23] = _cam.Radius1; s[24] = _cam.Shake; s[25] = _camStart;
        s[26] = _bex; s[27] = _bey; s[28] = _bez; s[29] = _btx; s[30] = _bty; s[31] = _btz; s[32] = _bfov; s[33] = _blendTotal; s[34] = _blendLeft;
        s[35] = _trx; s[36] = _try; s[37] = _trz; s[38] = _trackInit ? 1 : 0;
        s[39] = _ex; s[40] = _ey; s[41] = _ez; s[42] = _tx; s[43] = _ty; s[44] = _tz; s[45] = _fov; s[46] = _poseValid ? 1 : 0;
        for (var i = 0; i < _count; i++)
        {
            var o = Header + i * PerObj;
            s[o] = _px[i]; s[o + 1] = _py[i]; s[o + 2] = _pz[i]; s[o + 3] = _vx[i]; s[o + 4] = _vy[i]; s[o + 5] = _vz[i];
            s[o + 6] = _hx[i]; s[o + 7] = _hy[i]; s[o + 8] = _hz[i]; s[o + 9] = _inv[i]; s[o + 10] = _mass[i]; s[o + 11] = _rest[i]; s[o + 12] = _fric[i];
            s[o + 13] = _cr[i]; s[o + 14] = _cg[i]; s[o + 15] = _cb[i]; s[o + 16] = _shape[i]; s[o + 17] = _flags[i];
        }
        s[SchedBase] = _sN; s[SchedBase + 1] = _sCur;
        for (var k = 0; k < _sN; k++)
        {
            var o = SchedBase + 2 + k * SchedStride;
            ref readonly var op = ref _sOp[k];
            s[o] = _sT[k]; s[o + 1] = (float)op.Kind; s[o + 2] = op.Obj; s[o + 3] = op.X; s[o + 4] = op.Y; s[o + 5] = op.Z;
        }
    }

    public void Restore(ReadOnlySpan<float> s)
    {
        if (s.Length < StateFloats) throw new ArgumentException("상태 버퍼 크기 부족", nameof(s));
        _time = s[0]; _gx = s[1]; _gy = s[2]; _gz = s[3]; _count = Math.Clamp((int)s[4], 0, MaxObjects); _damping = s[5]; _reflect = s[6]; _timeScale = s[7]; _frame = (int)s[8];
        SetLighting((LightPreset)(int)s[9]); _fps = (int)s[10] > 0 ? (int)s[10] : 30;
        _cam = new CameraSpec
        {
            Mode = (CameraMode)(int)s[12], Target = (int)s[13], Cx = s[14], Cy = s[15], Cz = s[16], Radius = s[17], Height = s[18], Angle0 = s[19],
            Sweep = s[20], Fov = s[21], Duration = s[22], Radius1 = s[23], Shake = s[24],
        };
        _camStart = s[25];
        _bex = s[26]; _bey = s[27]; _bez = s[28]; _btx = s[29]; _bty = s[30]; _btz = s[31]; _bfov = s[32]; _blendTotal = (int)s[33]; _blendLeft = (int)s[34];
        _trx = s[35]; _try = s[36]; _trz = s[37]; _trackInit = s[38] != 0;
        _ex = s[39]; _ey = s[40]; _ez = s[41]; _tx = s[42]; _ty = s[43]; _tz = s[44]; _fov = s[45]; _poseValid = s[46] != 0;
        var vis = 0;
        for (var i = 0; i < MaxObjects; i++)
        {
            if (i >= _count) { _flags[i] = 0; continue; }
            var o = Header + i * PerObj;
            _px[i] = s[o]; _py[i] = s[o + 1]; _pz[i] = s[o + 2]; _vx[i] = s[o + 3]; _vy[i] = s[o + 4]; _vz[i] = s[o + 5];
            _hx[i] = s[o + 6]; _hy[i] = s[o + 7]; _hz[i] = s[o + 8]; _inv[i] = s[o + 9]; _mass[i] = s[o + 10]; _rest[i] = s[o + 11]; _fric[i] = s[o + 12];
            _cr[i] = s[o + 13]; _cg[i] = s[o + 14]; _cb[i] = s[o + 15]; _shape[i] = (byte)s[o + 16]; _flags[i] = (byte)s[o + 17];
            if ((_flags[i] & (FAlive | FHidden)) == FAlive) vis++;
        }
        _expectedVisible = vis;
        _sN = Math.Clamp((int)s[SchedBase], 0, MaxSchedule); _sCur = Math.Clamp((int)s[SchedBase + 1], 0, _sN);
        for (var k = 0; k < _sN; k++)
        {
            var o = SchedBase + 2 + k * SchedStride;
            _sT[k] = s[o]; _sOp[k] = new WorldOp((WorldOpKind)(int)s[o + 1], (int)s[o + 2], s[o + 3], s[o + 4], s[o + 5]);
        }
        _eValid = false; _cutPending = true;
    }

    /// <summary>다른 월드의 정의(이름) + 상태 복제(섀도 프리뷰용 · 무할당).</summary>
    public void CopyFrom(AiGenerative4dWorldModelEngine src)
    {
        Array.Copy(src._names, _names, MaxObjects);
        Array.Copy(src._labels, _labels, MaxObjects);
        Span<float> tmp = stackalloc float[StateFloats];
        src.Capture(tmp);
        Restore(tmp);
    }

    /// <summary>상태 해시(FNV-1a 64 · 결정론 검증).</summary>
    public ulong StateHash()
    {
        Span<float> tmp = stackalloc float[StateFloats];
        Capture(tmp);
        var h = 14695981039346656037UL;
        foreach (var x in MemoryMarshal.AsBytes(tmp)) { h ^= x; h *= 1099511628211UL; }
        return h;
    }

    public void ResetMonitors()
    {
        Interlocked.Exchange(ref _energyViol, 0); Interlocked.Exchange(ref _permViol, 0); Interlocked.Exchange(ref _camJumps, 0);
        _maxDrift = 0; _maxPen = 0; _maxCamDeg = 0; _eValid = false;
    }

    // ══════════════════════ 일관성 분석 · 컨디셔닝(비 핫패스) ══════════════════════

    /// <summary>현재 상태에서 seconds 만큼 시뮬 + 저해상 렌더로 시공간 일관성 평가 후 상태 원복.</summary>
    public WorldConsistencyReport Analyze(float seconds, int w = 96, int h = 54)
    {
        var snap = new float[StateFloats];
        Capture(snap);
        ResetMonitors();
        var frames = Math.Max(2, (int)(seconds * _fps));
        var a = new byte[w * h * 3]; var b = new byte[w * h * 3];
        var deltas = new float[frames];
        Render(a, w, h);
        for (var f = 0; f < frames; f++)
        {
            StepFrame();
            Render(b, w, h);
            deltas[f] = MeanAbsLumaDiff(a, b);
            (a, b) = (b, a);
        }
        float mean = 0, maxSpike = 0;
        for (var f = 0; f < frames; f++) mean += deltas[f];
        mean /= frames;
        for (var f = 1; f < frames - 1; f++)
        {
            var nb = (deltas[f - 1] + deltas[f + 1]) * 0.5f;
            if (deltas[f] < 4f) continue;
            var spike = deltas[f] / Math.Max(nb, 0.5f);
            if (spike > maxSpike) maxSpike = spike;
        }
        var issues = new List<string>();
        var score = 100;
        var ev = EnergyViolations; var pv = PermanenceViolations; var cj = CameraJumps;
        if (ev > 0) { score -= (int)Math.Min(30, ev * 10); issues.Add("에너지 증가 위반 " + ev.ToString(CultureInfo.InvariantCulture) + "회(최대 " + (_maxDrift * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%)"); }
        if (_maxPen > 0.02f) { score -= (int)Math.Min(25, (_maxPen - 0.02f) / 0.05f * 20); issues.Add("객체 침투 " + (_maxPen * 100).ToString("0.0", CultureInfo.InvariantCulture) + "cm"); }
        if (pv > 0) { score -= (int)Math.Min(30, pv * 15); issues.Add("객체 영속성 위반 " + pv.ToString(CultureInfo.InvariantCulture) + "회(소실/터널링 복구)"); }
        if (cj > 0) { score -= (int)Math.Min(20, cj * 10); issues.Add("카메라 점프 " + cj.ToString(CultureInfo.InvariantCulture) + "회"); }
        if (maxSpike > 3f) { score -= (int)Math.Min(15, (maxSpike - 3f) * 3); issues.Add("시간 스파이크(플리커) ×" + maxSpike.ToString("0.0", CultureInfo.InvariantCulture)); }
        var rep = new WorldConsistencyReport(Math.Max(0, score), frames, seconds, ev, _maxDrift, _maxPen, pv, cj, _maxCamDeg, mean, maxSpike, issues);
        Restore(snap);
        LastReport = rep;
        LastAction = "🌐 4D 일관성 " + rep.Score.ToString(CultureInfo.InvariantCulture) + "점 · " + frames.ToString(CultureInfo.InvariantCulture) + "프레임";
        Raise();
        return rep;
    }

    /// <summary>
    /// 생성형 비디오 모델 결선용 4D 컨디셔닝(JSON): 프롬프트 · 네거티브 · 프레임별 카메라 포즈 · 동적 객체 궤적. 시뮬 후 상태 원복.
    /// </summary>
    public string BuildConditioningJson(string prompt, string negative, float seconds)
    {
        var snap = new float[StateFloats];
        Capture(snap);
        var frames = Math.Max(1, (int)(seconds * _fps));
        using var ms = new MemoryStream();
        using (var j = new Utf8JsonWriter(ms))
        {
            j.WriteStartObject();
            j.WriteString("schema", "pfs.world4d.v1");
            j.WriteString("coordinates", "Y-up, meters, right-handed");
            j.WriteString("prompt", prompt);
            j.WriteString("negativePrompt", negative);
            j.WriteNumber("fps", _fps);
            j.WriteNumber("frames", frames);
            j.WriteNumber("gravity", Math.Round(_gy, 3));
            j.WriteStartArray("objects");
            for (var i = 0; i < _count; i++)
            {
                j.WriteStartObject();
                j.WriteString("id", _names[i]); j.WriteString("label", _labels[i]); j.WriteString("shape", _shape[i] == (byte)WorldShape.Sphere ? "sphere" : "box");
                j.WriteBoolean("static", (_flags[i] & FStatic) != 0);
                j.WriteEndObject();
            }
            j.WriteEndArray();
            j.WriteStartArray("timeline");
            for (var f = 0; f < frames; f++)
            {
                StepFrame();
                EvaluatePose(out var ex, out var ey, out var ez, out var tx, out var ty, out var tz, out var fov);
                j.WriteStartObject();
                j.WriteNumber("t", Math.Round(_time, 4));
                j.WriteStartArray("cam");
                j.WriteNumberValue(Math.Round(ex, 3)); j.WriteNumberValue(Math.Round(ey, 3)); j.WriteNumberValue(Math.Round(ez, 3));
                j.WriteNumberValue(Math.Round(tx, 3)); j.WriteNumberValue(Math.Round(ty, 3)); j.WriteNumberValue(Math.Round(tz, 3)); j.WriteNumberValue(Math.Round(fov, 2));
                j.WriteEndArray();
                j.WriteStartArray("pos");
                for (var i = 0; i < _count; i++)
                {
                    if (_inv[i] <= 0) continue;
                    j.WriteNumberValue(Math.Round(_px[i], 3)); j.WriteNumberValue(Math.Round(_py[i], 3)); j.WriteNumberValue(Math.Round(_pz[i], 3));
                }
                j.WriteEndArray();
                j.WriteEndObject();
            }
            j.WriteEndArray();
            j.WriteEndObject();
        }
        Restore(snap);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public void Touch(string action) { LastAction = action; Raise(); }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("World4d.OnChange", ex); }
    }
}
