using System.Diagnostics;
using System.Numerics;

namespace PromptFlowStudio.Services.HoloTelepresence;

/// <summary>사이버 멀미 단계.</summary>
public enum SicknessLevel : byte { Comfortable, Mild, Moderate, Severe }

/// <summary>
/// [PromptFlow Studio v17.00.0] ✋ 다차원 공간 햅틱 포스 피드백 &amp; 인지 앵커 상호작용 가드(무할당 · 결정적).
///   ▸ 접촉: 홀로그래픽 구 객체(최대 32 · 강성/감쇠/거칠기/결 간격) × 손 2 — 갓 오브젝트(프록시)를 표면에 구속, 침투 깊이 · 법선 속도 → 페널티 역각(≤ 3.3 N 포화).
///   ▸ 관통 이탈 차단: 직전 틱 밖 → 이번 틱 밖이지만 선분이 구를 통과(고속 스와이프 터널링) = 진입점에 프록시 고정 · 접촉 유지 · 반대편 이탈 금지(법선 반구 구속).
///   ▸ 진동 파형(960 Hz · 틱당 16 샘플): 접촉 과도(180 Hz 감쇠 정현 · 진폭 ∝ 접근 속도) + 결 진동(주파수 = 접선 속도 / 결 간격 · 진폭 ∝ 거칠기) → SIMD 포화.
///   ▸ 인지 안정 가드: 시각(가상 카메라) vs 전정(머리) 각속도/선가속 불일치 + 모션-투-포톤 지연 + 프레임 지연 → 멀미 누적(누설 적분 τ 8 s) →
///     인공 회전 속도 제한(90 → 30 °/s) · 비네트 · 인지 앵커(정지 기준틀) 불투명도 · 컴포트 모드(히스테리시스 0.6/0.35) · 재투영 플래그.
/// </summary>
public sealed class SpatialHapticCognitiveInteractionGuard
{
    public const int MaxObjects = 32, Hands = 2, BlockSamples = 16, HapticRate = 960;
    public const float MaxForceN = 3.3f, TransientHz = 180f, TransientTau = 0.025f, RecoveryTau = 8f, SicknessGain = 0.25f;
    private static readonly double TickUs = 1e6 / Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly Vector3[] _oc = new Vector3[MaxObjects], _ov = new Vector3[MaxObjects];
    private readonly float[] _or = new float[MaxObjects], _ok = new float[MaxObjects], _ob = new float[MaxObjects], _orough = new float[MaxObjects], _ograin = new float[MaxObjects];
    private readonly Vector3[] _dev = new Vector3[Hands], _prevDev = new Vector3[Hands], _proxy = new Vector3[Hands], _normal = new Vector3[Hands], _force = new Vector3[Hands];
    private readonly int[] _contact = { -1, -1 };
    private readonly bool[] _init = new bool[Hands];
    private readonly float[] _depth = new float[Hands], _transAmp = new float[Hands], _transT = new float[Hands], _texPhase = new float[Hands], _texAmp = new float[Hands], _texHz = new float[Hands];
    private readonly float[][] _wave = { new float[BlockSamples], new float[BlockSamples] };
    private int _objects;
    private bool _comfort;

    // ── 햅틱 계측 ──
    public int ObjectCount => _objects;
    public long Touches { get; private set; }
    public long Releases { get; private set; }
    public long PopThroughBlocked { get; private set; }
    public long ForceClamped { get; private set; }
    public double LastHapticUs { get; private set; }
    public int ContactObject(int h) => _contact[h];
    public Vector3 Force(int h) => _force[h];
    public float ForceMagnitude(int h) => _force[h].Length();
    public float Penetration(int h) => _depth[h];
    public Vector3 Proxy(int h) => _proxy[h];
    public Vector3 Device(int h) => _dev[h];
    public ReadOnlySpan<float> Waveform(int h) => _wave[h];
    public float TextureHz(int h) => _texHz[h];
    public Vector3 ObjectCenter(int i) => _oc[i];
    public float ObjectRadius(int i) => _or[i];

    // ── 인지 가드 계측 ──
    public float Mismatch { get; private set; }
    public float Sickness { get; private set; }
    public float PeakSickness { get; private set; }
    public float Vignette { get; private set; }
    public float AnchorOpacity { get; private set; }
    public float RotationLimitDeg { get; private set; } = 90f;
    public bool ComfortMode => _comfort;
    public bool Reprojection { get; private set; }
    public long Interventions { get; private set; }
    public long ComfortEntries { get; private set; }
    public Vector3 CorrectedVisualAngVel { get; private set; }
    public SicknessLevel Level => Sickness < 0.2f ? SicknessLevel.Comfortable : Sickness < 0.4f ? SicknessLevel.Mild : Sickness < 0.6f ? SicknessLevel.Moderate : SicknessLevel.Severe;

    public static string LevelName(SicknessLevel l) => l switch
    {
        SicknessLevel.Comfortable => "쾌적", SicknessLevel.Mild => "경미", SicknessLevel.Moderate => "주의", _ => "위험",
    };

    /// <summary>홀로그래픽 객체(구) 추가: 강성 N/m · 감쇠 N·s/m · 거칠기 0..1 · 결 간격 m.</summary>
    public int AddObject(Vector3 center, float radius, float stiffness = 600f, float damping = 4f, float roughness = 0.5f, float grain = 0.002f)
    {
        lock (_gate)
        {
            if (_objects >= MaxObjects) return -1;
            var i = _objects++;
            _oc[i] = center; _ov[i] = Vector3.Zero; _or[i] = MathF.Max(0.01f, radius); _ok[i] = stiffness; _ob[i] = damping;
            _orough[i] = Math.Clamp(roughness, 0f, 1f); _ograin[i] = MathF.Max(0.0002f, grain);
            return i;
        }
    }

    public void MoveObject(int i, Vector3 center, Vector3 velocity) { _oc[i] = center; _ov[i] = velocity; }

    public void ClearObjects()
    {
        lock (_gate) { _objects = 0; _contact[0] = _contact[1] = -1; }
    }

    /// <summary>손(햅틱 장치) 1틱: 접촉/관통 판정 · 프록시 구속 · 역각 · 파형 합성(무할당).</summary>
    public void StepHand(int h, Vector3 device, float dt)
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            if (!_init[h]) { _prevDev[h] = device; _init[h] = true; }
            var prev = _prevDev[h];
            _dev[h] = device;
            var vel = (device - prev) / MathF.Max(dt, 1e-4f);
            var c = _contact[h];
            if (c < 0)
            {
                for (var i = 0; i < _objects; i++)
                {
                    var rel = device - _oc[i];
                    var inside = rel.LengthSquared() < _or[i] * _or[i];
                    if (inside || SegmentCrosses(prev, device, _oc[i], _or[i], out _))
                    {
                        // 진입 법선 = 직전 위치 방향(관통 시에도 진입면 유지)
                        var n = prev - _oc[i];
                        n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                        if (!inside) PopThroughBlocked++;
                        _contact[h] = c = i; _normal[h] = n; Touches++;
                        var vn = -Vector3.Dot(vel - _ov[i], n);
                        _transAmp[h] = Math.Clamp(vn * 0.8f, 0f, 1f); _transT[h] = 0f;
                        break;
                    }
                }
            }
            if (c >= 0)
            {
                var rel = device - _oc[c];
                var dir = rel.LengthSquared() > 1e-12f ? Vector3.Normalize(rel) : _normal[h];
                // 갓 오브젝트: 장치 방향이 진입 반구 안일 때만 표면 따라 이동(최대 30%/틱) — 반대편 이탈 금지
                if (Vector3.Dot(dir, _normal[h]) > 0f) _normal[h] = Vector3.Normalize(Vector3.Lerp(_normal[h], dir, 0.3f));
                var n = _normal[h];
                var depth = _or[c] - Vector3.Dot(rel, n);
                if (depth <= 0f || depth > _or[c] + 0.35f) // 표면 이탈 또는 장치가 객체 너머 35 cm 이상 벗어남(안전 해제)
                {
                    _contact[h] = -1; _depth[h] = 0f; _force[h] = Vector3.Zero; _texAmp[h] = 0f; Releases++;
                    _proxy[h] = device;
                }
                else
                {
                    var rv = vel - _ov[c];
                    var vn = Vector3.Dot(rv, n);
                    var f = _ok[c] * depth - _ob[c] * vn;
                    if (f < 0f) f = 0f;
                    if (f > MaxForceN) { f = MaxForceN; ForceClamped++; }
                    _depth[h] = depth; _force[h] = n * f;
                    _proxy[h] = _oc[c] + n * _or[c];
                    var vt = (rv - n * vn).Length();
                    _texHz[h] = Math.Clamp(vt / _ograin[c], 40f, 400f);
                    _texAmp[h] = Math.Clamp(_orough[c] * vt * 2f, 0f, 0.6f) * MathF.Min(1f, f / 1f);
                }
            }
            else { _proxy[h] = device; _force[h] = Vector3.Zero; _depth[h] = 0f; _texAmp[h] = 0f; }
            Synthesize(h);
            _prevDev[h] = device;
            LastHapticUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
        }
    }

    /// <summary>선분 p→q가 구를 관통(양 끝 모두 밖)하는지.</summary>
    public static bool SegmentCrosses(Vector3 p, Vector3 q, Vector3 c, float r, out float tHit)
    {
        tHit = 0f;
        var d = q - p; var f = p - c;
        var a = Vector3.Dot(d, d);
        if (a < 1e-12f) return false;
        var b = 2f * Vector3.Dot(f, d); var cc = Vector3.Dot(f, f) - r * r;
        if (cc <= 0f) return false;                         // 시작점이 이미 안 = 일반 접촉 경로
        var disc = b * b - 4f * a * cc;
        if (disc < 0f) return false;
        var t = (-b - MathF.Sqrt(disc)) / (2f * a);
        tHit = t;
        return t >= 0f && t <= 1f;
    }

    private void Synthesize(int h)
    {
        var w = _wave[h];
        const float ds = 1f / HapticRate;
        float tAmp = _transAmp[h], tT = _transT[h], ph = _texPhase[h], xAmp = _texAmp[h], xHz = _texHz[h];
        for (var s = 0; s < BlockSamples; s++)
        {
            var tr = tAmp > 0f ? tAmp * MathF.Exp(-tT / TransientTau) * MathF.Sin(MathF.Tau * TransientHz * tT) : 0f;
            var tx = xAmp > 0f ? xAmp * MathF.Sin(ph) : 0f;
            w[s] = tr + tx;
            tT += ds; ph += MathF.Tau * xHz * ds;
        }
        if (ph > 1000f) ph -= MathF.Tau * MathF.Floor(ph / MathF.Tau);
        if (tT > 8f * TransientTau) tAmp = 0f;
        _transAmp[h] = tAmp; _transT[h] = tT; _texPhase[h] = ph;
        HoloSimd.Clamp(w, 1f);
    }

    /// <summary>
    /// 인지 안정 1틱: 머리(전정) 각속도 rad/s · 선가속 m/s² vs 시각 카메라 각속도/선가속 + 지연 → 인공 회전 제한 · 멀미 누적 · 비네트/앵커/컴포트.
    /// 반환 = 보정된 시각 각속도(렌더러가 적용).
    /// </summary>
    public Vector3 StepCognitive(Vector3 headAngVel, Vector3 headLinAcc, Vector3 visualAngVel, Vector3 visualLinAcc, float motionToPhotonMs, float frameMs, float dt)
    {
        lock (_gate)
        {
            // 인공 회전(시각 − 머리) 크기 제한
            var art = visualAngVel - headAngVel;
            var lim = RotationLimitDeg * MathF.PI / 180f;
            var mag = art.Length();
            if (mag > lim) { art *= lim / mag; Interventions++; }
            var corrected = headAngVel + art;
            CorrectedVisualAngVel = corrected;
            var mm = (corrected - headAngVel).Length() * 0.6f
                   + (visualLinAcc - headLinAcc).Length() / 9.81f * 0.5f
                   + MathF.Max(0f, motionToPhotonMs - 20f) / 50f
                   + MathF.Max(0f, frameMs - 16.7f) / 30f;
            Mismatch = mm;
            Reprojection = motionToPhotonMs > 20f;
            var s = Sickness + (SicknessGain * MathF.Max(0f, mm - 0.15f) - Sickness / RecoveryTau) * dt;
            Sickness = Math.Clamp(s, 0f, 1f);
            if (Sickness > PeakSickness) PeakSickness = Sickness;
            if (!_comfort && Sickness > 0.6f) { _comfort = true; ComfortEntries++; }
            else if (_comfort && Sickness < 0.35f) _comfort = false;
            RotationLimitDeg = _comfort ? 30f : 90f - 60f * Math.Clamp(Sickness / 0.6f, 0f, 1f);
            var vt = Math.Clamp(0.8f * Sickness + 0.5f * Math.Clamp(mm - 0.3f, 0f, 1f), 0f, 0.85f);
            if (_comfort) vt = MathF.Max(vt, 0.4f);
            Vignette += (vt - Vignette) * MathF.Min(1f, dt * 4f);
            AnchorOpacity = _comfort ? 1f : Math.Clamp((mm - 0.2f) * 2f + Sickness, 0f, 1f);
            return corrected;
        }
    }

    public void ResetCognitive()
    {
        lock (_gate) { Sickness = 0f; PeakSickness = 0f; Vignette = 0f; AnchorOpacity = 0f; _comfort = false; RotationLimitDeg = 90f; Mismatch = 0f; }
    }
}
