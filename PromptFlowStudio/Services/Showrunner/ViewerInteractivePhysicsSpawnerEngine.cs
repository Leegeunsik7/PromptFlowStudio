using System.Numerics;
using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.GaussianClothHair;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SplatEnvironment;

namespace PromptFlowStudio.Services.Showrunner;

public enum DropKind : byte { Tomato, WaterBalloon, Ball, Confetti }
public enum HitRegion : byte { Head, Torso, Arm, Leg }

/// <summary>v17.67.0 아바타 피격 이벤트(무할당 값 전달).</summary>
public struct PropHitEvent
{
    public DropKind Kind;
    public HitRegion Region;
    public sbyte Avatar;
    public byte Bone;
    public float Speed;
    public Vector3 Point;
    public long AtFrame;
    public int UserHash, UserLen;
    public bool Garment, Burst;
}

/// <summary>
/// [PromptFlow Studio v17.67.0] 시청자 참여형 3DGS 인터랙티브 물리 스포너(풀 500 · 60 fps · 2 서브스텝 · 무할당).
///   ▸ 커맨드: <c>!drop tomato|balloon|ball [@host|@1|@2]</c> · <c>!tomato</c> · <c>!balloon</c> · <c>!confetti</c>(16 조각) — 한글 별칭(토마토 · 물풍선 · 공 · 폭죽).
///   ▸ 스폰: 세트 상공(y 3.2 m) 무작위 점 → 대상 아바타 머리 0.75 s 탄도 조준(조준 오차 ±8 cm).
///   ▸ 충돌: v17.57 골격 23 본 캡슐(<see cref="RagdollSkeleton.BoneRadius"/>) — 몸통/골반/허벅지는 v17.59 데님 의상 셸(두께 · 마찰 · 반발) 팽창 캡슐.
///     구-캡슐 최근접점 투영 → 법선 반발(반발 계수 = 물체 · 의상 평균) · 접선 마찰 → 찌그러짐(법선 축 압축 ∝ 충돌 속도 · 체적 보존 · 임계 감쇠 복원).
///     물풍선 · 토마토는 2.5 m/s 이상 피격 시 파열(납작 데칼 0.6 s → 회수). 바닥 · 토크쇼 데스크 상판 평면 충돌.
///   ▸ 렌더: 객체당 24 스플랫 껍질(찌그러짐 축 변형) → v17.66 컴포지터 시청자 슬롯(단일 래딕스 큐 합성 · 아바타와 상호 차폐).
/// </summary>
public sealed class ViewerInteractivePhysicsSpawnerEngine
{
    public const int Capacity = 500, SplatsPer = UnifiedGaussianSceneCompositorEngine.ViewerSplats / Capacity, ConfettiPieces = 16, HitRing = 64, Bones = RagdollSkeleton.Count;
    public const float SpawnHeight = 3.2f, FlightS = 0.75f, BurstSpeed = 2.5f, LifeS = 12f, DecalS = 0.6f;
    public static readonly Vector3 Gravity = new(0f, -9.81f, 0f);

    private static readonly float[] KindRadius = { 0.045f, 0.075f, 0.06f, 0.014f };
    private static readonly float[] KindRestitution = { 0.12f, 0.25f, 0.78f, 0.05f };
    private static readonly float[] KindDrag = { 0.02f, 0.05f, 0.03f, 2.6f };
    private static readonly Vector3[] KindColor = { new(0.85f, 0.10f, 0.07f), new(0.25f, 0.55f, 0.95f), new(0.98f, 0.82f, 0.12f), new(1f, 1f, 1f) };
    private static readonly Vector3[] Shell = BuildShell();

    // SoA 풀
    private readonly Vector3[] _p = new Vector3[Capacity], _v = new Vector3[Capacity], _squashN = new Vector3[Capacity], _col = new Vector3[Capacity];
    private readonly float[] _age = new float[Capacity], _squash = new float[Capacity], _squashV = new float[Capacity], _decal = new float[Capacity];
    private readonly DropKind[] _kind = new DropKind[Capacity];
    private readonly bool[] _active = new bool[Capacity], _hit = new bool[Capacity], _dirty = new bool[Capacity];
    private readonly long[] _born = new long[Capacity];
    private readonly int[] _owner = new int[Capacity];
    private readonly int[] _free = new int[Capacity];
    // 캡슐(아바타 × 본)
    private readonly Vector3[] _ca = new Vector3[UnifiedGaussianSceneCompositorEngine.MaxAvatars * Bones], _cb = new Vector3[UnifiedGaussianSceneCompositorEngine.MaxAvatars * Bones];
    private readonly float[] _cr = new float[UnifiedGaussianSceneCompositorEngine.MaxAvatars * Bones];
    private readonly bool[] _garment = new bool[Bones];
    private readonly Vector3[] _head = new Vector3[UnifiedGaussianSceneCompositorEngine.MaxAvatars];
    private readonly PropHitEvent[] _hits = new PropHitEvent[HitRing];
    private readonly char[] _hitUser = new char[HitRing * PhysicsSpamPoolingSafetyGuard.UserCap];
    private int _freeN, _activeN, _avatars, _hitHead, _hitTail;
    private long _frame, _serial;
    private uint _rng = 0x5EED1767u;
    private readonly FabricMaterial _cloth = FabricMaterial.Of(FabricPreset.Denim);

    public ViewerInteractivePhysicsSpawnerEngine()
    {
        for (var i = 0; i < Capacity; i++) _free[i] = Capacity - 1 - i;
        _freeN = Capacity;
        foreach (var b in new[] { RagdollJoint.Spine, RagdollJoint.Chest, RagdollJoint.Pelvis, RagdollJoint.LHip, RagdollJoint.RHip, RagdollJoint.LKnee, RagdollJoint.RKnee, RagdollJoint.LShoulder, RagdollJoint.RShoulder })
            _garment[(int)b] = true;
    }

    public int Active => _activeN;
    public long Spawned { get; private set; }
    public long Hits { get; private set; }
    public long HeadHits { get; private set; }
    public long GarmentHits { get; private set; }
    public long Bursts { get; private set; }
    public long Contacts { get; private set; }
    public long Expired { get; private set; }
    public float PeakSquash { get; private set; }
    public float MaxPenetrationMm { get; private set; }
    public double LastStepUs { get; private set; }
    public double LastWriteUs { get; private set; }
    public bool IsActive(int i) => _active[i];
    public Vector3 Position(int i) => _p[i];
    public Vector3 Velocity(int i) => _v[i];
    public float Squash(int i) => _squash[i];
    public DropKind Kind(int i) => _kind[i];
    public int Avatars => _avatars;
    public long Frame => _frame;

    public static string KindName(DropKind k) => k switch { DropKind.Tomato => "🍅 토마토", DropKind.WaterBalloon => "💧 물풍선", DropKind.Ball => "⚽ 공", _ => "🎊 컨페티" };
    public static string RegionName(HitRegion r) => r switch { HitRegion.Head => "머리", HitRegion.Torso => "몸통", HitRegion.Arm => "팔", _ => "다리" };

    private static Vector3[] BuildShell()
    {
        var s = new Vector3[SplatsPer];
        var ga = MathF.PI * (3f - MathF.Sqrt(5f));
        for (var k = 0; k < s.Length; k++)
        {
            var y = 1f - 2f * (k + 0.5f) / s.Length; var r = MathF.Sqrt(1f - y * y); var a = ga * k;
            s[k] = new Vector3(r * MathF.Cos(a), y, r * MathF.Sin(a));
        }
        return s;
    }

    private float Rand() { var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x; return (x & 0xFFFFFF) / 16777215f; }

    // ══════════════════════ 커맨드 파싱(무할당) ══════════════════════
    /// <summary>채팅 → 종류 · 대상(−1 = 자동) · 비용. 커맨드가 아니면 false.</summary>
    public static bool Parse(ReadOnlySpan<char> text, out DropKind kind, out int target, out float cost)
    {
        kind = DropKind.Tomato; target = -1; cost = 1f;
        text = text.Trim();
        if (text.Length < 2 || text[0] != '!') return false;
        var body = text[1..];
        var sp = body.IndexOf(' ');
        var verb = sp < 0 ? body : body[..sp];
        var rest = sp < 0 ? ReadOnlySpan<char>.Empty : body[(sp + 1)..].Trim();
        if (verb.Equals("confetti", StringComparison.OrdinalIgnoreCase) || verb.SequenceEqual("폭죽")) { kind = DropKind.Confetti; cost = 4f; return true; }
        if (!verb.Equals("drop", StringComparison.OrdinalIgnoreCase) && !verb.SequenceEqual("드롭")) { rest = body; }
        var isp = rest.IndexOf(' ');
        var item = isp < 0 ? rest : rest[..isp];
        var tail = isp < 0 ? ReadOnlySpan<char>.Empty : rest[(isp + 1)..].Trim();
        if (item.Equals("tomato", StringComparison.OrdinalIgnoreCase) || item.SequenceEqual("토마토")) kind = DropKind.Tomato;
        else if (item.Equals("balloon", StringComparison.OrdinalIgnoreCase) || item.Equals("waterballoon", StringComparison.OrdinalIgnoreCase) || item.SequenceEqual("물풍선")) kind = DropKind.WaterBalloon;
        else if (item.Equals("ball", StringComparison.OrdinalIgnoreCase) || item.SequenceEqual("공")) kind = DropKind.Ball;
        else if (item.Equals("confetti", StringComparison.OrdinalIgnoreCase) || item.SequenceEqual("폭죽")) { kind = DropKind.Confetti; cost = 4f; }
        else return false;
        if (tail.Length >= 2 && tail[0] == '@')
        {
            var t = tail[1..];
            if (t.Equals("host", StringComparison.OrdinalIgnoreCase) || t.SequenceEqual("호스트") || t.SequenceEqual("0")) target = 0;
            else if (t.SequenceEqual("1")) target = 1;
            else if (t.SequenceEqual("2")) target = 2;
        }
        return true;
    }

    // ══════════════════════ 아바타 콜라이더(틱 · 무할당) ══════════════════════
    public void UpdateColliders(UnifiedGaussianSceneCompositorEngine comp)
    {
        _avatars = comp.Avatars;
        var thick = _cloth.Thickness * 6f + 0.012f;   // 의상 셸(원단 두께 + 여유 주름)
        for (var a = 0; a < _avatars; a++)
        {
            var j = comp.Joints(a);
            for (var b = 1; b < Bones; b++)
            {
                var k = a * Bones + b;
                _ca[k] = j[RagdollSkeleton.Parent[b]]; _cb[k] = j[b];
                _cr[k] = RagdollSkeleton.BoneRadius[b] + (_garment[b] ? thick : 0f);
            }
            _head[a] = (j[(int)RagdollJoint.Head] + j[(int)RagdollJoint.HeadTop]) * 0.5f;
        }
    }

    // ══════════════════════ 스폰(무할당) ══════════════════════
    /// <summary>객체 1 개(컨페티 = 16 조각) 활성화 — 빈 슬롯만 사용(만석 재활용은 호출자가 <see cref="Recycle"/> 선행). 반환 = 활성 수.</summary>
    public int Spawn(DropKind kind, int target, int userHash)
    {
        if (_avatars == 0) return 0;
        var t = (uint)target < (uint)_avatars ? target : (int)(_serial % (uint)_avatars);
        var n = kind == DropKind.Confetti ? ConfettiPieces : 1;
        var origin = new Vector3(_head[t].X + (Rand() - 0.5f) * 1.6f, SpawnHeight, _head[t].Z + 0.3f + (Rand() - 0.5f) * 1.0f);
        var made = 0;
        for (var q = 0; q < n; q++)
        {
            if (_freeN == 0) break;
            var i = _free[--_freeN];
            var aim = _head[t] + new Vector3((Rand() - 0.5f) * 0.16f, (Rand() - 0.3f) * 0.12f, (Rand() - 0.5f) * 0.16f);
            var p0 = kind == DropKind.Confetti ? origin + new Vector3((Rand() - 0.5f) * 0.5f, (Rand() - 0.5f) * 0.2f, (Rand() - 0.5f) * 0.5f) : origin;
            var v0 = (aim - p0 - 0.5f * Gravity * FlightS * FlightS) / FlightS;
            if (kind == DropKind.Confetti) v0 = new Vector3((Rand() - 0.5f) * 1.5f, -0.5f, (Rand() - 0.5f) * 1.5f);
            _p[i] = p0; _v[i] = v0; _kind[i] = kind; _age[i] = 0f; _squash[i] = 0f; _squashV[i] = 0f; _squashN[i] = Vector3.UnitY; _decal[i] = 0f;
            _col[i] = kind == DropKind.Confetti ? new Vector3(0.3f + 0.7f * Rand(), 0.3f + 0.7f * Rand(), 0.3f + 0.7f * Rand()) : KindColor[(int)kind];
            _hit[i] = false; _active[i] = true; _dirty[i] = true; _born[i] = _frame; _owner[i] = userHash;
            _activeN++; made++; Spawned++;
        }
        _serial++;
        return made;
    }

    /// <summary>직접 투척(UI 수동 투척 · 테스트) — 위치 · 속도 지정 · 빈 슬롯 없으면 −1.</summary>
    public int Launch(DropKind kind, Vector3 p, Vector3 v, int userHash)
    {
        if (_freeN == 0) return -1;
        var i = _free[--_freeN];
        _p[i] = p; _v[i] = v; _kind[i] = kind; _age[i] = 0f; _squash[i] = 0f; _squashV[i] = 0f; _squashN[i] = Vector3.UnitY; _decal[i] = 0f;
        _col[i] = KindColor[(int)kind]; _hit[i] = false; _active[i] = true; _dirty[i] = true; _born[i] = _frame; _owner[i] = userHash;
        _activeN++; Spawned++;
        return i;
    }

    /// <summary>재활용 후보 — 카메라 밖 최고령 우선, 없으면 최고령(무할당).</summary>
    public int OldestSlot(in SplatCamera cam, bool offscreenOnly)
    {
        var best = -1; var bestBorn = long.MaxValue;
        for (var i = 0; i < Capacity; i++)
        {
            if (!_active[i] || _born[i] >= bestBorn) continue;
            if (offscreenOnly && OnScreen(_p[i], cam)) continue;
            best = i; bestBorn = _born[i];
        }
        return best;
    }

    public static bool OnScreen(Vector3 p, in SplatCamera cam)
    {
        var d = p - cam.Position; var z = Vector3.Dot(d, cam.Forward);
        if (z <= cam.Near) return false;
        return MathF.Abs(Vector3.Dot(d, cam.Right)) <= z * cam.TanHalfH * 1.05f && MathF.Abs(Vector3.Dot(d, cam.Up)) <= z * cam.TanHalfV * 1.05f;
    }

    private void Deactivate(int i, bool expired)
    {
        if (!_active[i]) return;
        _active[i] = false; _dirty[i] = true; _activeN--;
        _free[_freeN++] = i;
        if (expired) Expired++;
    }

    public int FreeSlots => _freeN;

    /// <summary>풀 재활용(가드) — 활성 슬롯 즉시 비활성화.</summary>
    public void Recycle(int i) { if ((uint)i < Capacity) Deactivate(i, false); }

    public void Clear() { for (var i = 0; i < Capacity; i++) Deactivate(i, false); _hitHead = _hitTail = 0; }

    // ══════════════════════ 피격 이벤트 링 ══════════════════════
    public bool TryTakeHit(out PropHitEvent e, Span<char> user)
    {
        if (_hitTail == _hitHead) { e = default; return false; }
        var k = _hitTail % HitRing;
        e = _hits[k];
        _hitUser.AsSpan(k * PhysicsSpamPoolingSafetyGuard.UserCap, e.UserLen).CopyTo(user);
        _hitTail++;
        return true;
    }

    /// <summary>스폰 사용자 이름 등록(피격 이벤트 애드리브용 · 해시 ↔ 이름 최근 32).</summary>
    public void RememberUser(int hash, ReadOnlySpan<char> name)
    {
        var k = (int)((uint)hash % 32);
        _nameHash[k] = hash;
        var n = Math.Min(name.Length, PhysicsSpamPoolingSafetyGuard.UserCap);
        name[..n].CopyTo(_names.AsSpan(k * PhysicsSpamPoolingSafetyGuard.UserCap)); _nameLen[k] = n;
    }

    private readonly int[] _nameHash = new int[32], _nameLen = new int[32];
    private readonly char[] _names = new char[32 * PhysicsSpamPoolingSafetyGuard.UserCap];

    private void PushHit(int i, int avatar, int bone, float speed, Vector3 point, bool burst)
    {
        if (_hitHead - _hitTail >= HitRing) _hitTail++;
        var k = _hitHead % HitRing;
        ref var e = ref _hits[k];
        e.Kind = _kind[i]; e.Avatar = (sbyte)avatar; e.Bone = (byte)bone; e.Speed = speed; e.Point = point; e.AtFrame = _frame; e.Burst = burst; e.Garment = _garment[bone];
        e.Region = bone is (int)RagdollJoint.Head or (int)RagdollJoint.HeadTop or (int)RagdollJoint.Neck ? HitRegion.Head
            : bone is >= (int)RagdollJoint.LClavicle and <= (int)RagdollJoint.RHand ? HitRegion.Arm
            : bone >= (int)RagdollJoint.LHip ? HitRegion.Leg : HitRegion.Torso;
        e.UserHash = _owner[i];
        var nk = (int)((uint)_owner[i] % 32);
        e.UserLen = _nameHash[nk] == _owner[i] ? _nameLen[nk] : 0;
        _names.AsSpan(nk * PhysicsSpamPoolingSafetyGuard.UserCap, e.UserLen).CopyTo(_hitUser.AsSpan(k * PhysicsSpamPoolingSafetyGuard.UserCap));
        _hitHead++;
        Hits++;
        if (e.Region == HitRegion.Head) HeadHits++;
        if (e.Garment) GarmentHits++;
    }

    // ══════════════════════ 시뮬레이션(60 fps · 2 서브스텝 · 무할당) ══════════════════════
    public void Step(float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _frame++;
        const int Sub = 2;
        var h = dt / Sub;
        var nC = _avatars * Bones;
        var desk = UnifiedGaussianSceneCompositorEngine.DeskCenter;
        for (var i = 0; i < Capacity; i++)
        {
            if (!_active[i]) continue;
            var kind = _kind[i];
            _age[i] += dt;
            if (_decal[i] > 0f) { _decal[i] -= dt; _dirty[i] = true; if (_decal[i] <= 0f) Deactivate(i, true); continue; }
            if (_age[i] > LifeS) { Deactivate(i, true); continue; }
            var r = KindRadius[(int)kind]; var e = KindRestitution[(int)kind]; var drag = KindDrag[(int)kind];
            var p = _p[i]; var v = _v[i];
            for (var s = 0; s < Sub; s++)
            {
                v += (Gravity - drag * v.Length() * v) * h;
                p += v * h;
                // 아바타 캡슐(의상 셸 포함)
                if (kind != DropKind.Confetti || !_hit[i])
                    for (var c = 0; c < nC; c++)
                    {
                        var b = c % Bones; if (b == 0) continue;
                        var R = _cr[c] + r;
                        var a0 = _ca[c]; var ab = _cb[c] - a0;
                        var tt = Math.Clamp(Vector3.Dot(p - a0, ab) / MathF.Max(ab.LengthSquared(), 1e-8f), 0f, 1f);
                        var q = a0 + ab * tt; var d = p - q; var dist2 = d.LengthSquared();
                        if (dist2 >= R * R) continue;
                        var dist = MathF.Sqrt(dist2);
                        var n = dist > 1e-6f ? d / dist : Vector3.UnitY;
                        var pen = R - dist;
                        if (pen * 1000f > MaxPenetrationMm) MaxPenetrationMm = pen * 1000f;
                        p = q + n * R;
                        var vn = Vector3.Dot(v, n);
                        if (vn < 0f)
                        {
                            var garment = _garment[b];
                            var rest = garment ? 0.5f * (e + _cloth.Restitution) : e;
                            var fr = garment ? _cloth.Friction : 0.25f;
                            var vt = v - vn * n;
                            v = vt * MathF.Max(0f, 1f - fr) - rest * vn * n;
                            var speed = -vn;
                            Squish(i, n, speed);
                            Contacts++;
                            if (!_hit[i] && speed > 0.6f)
                            {
                                _hit[i] = true;
                                var burst = kind is DropKind.Tomato or DropKind.WaterBalloon && speed >= BurstSpeed;
                                PushHit(i, c / Bones, b, speed, q + n * _cr[c], burst);
                                if (burst) { Bursts++; _decal[i] = DecalS; _squash[i] = 0.85f; _squashN[i] = n; v = Vector3.Zero; p = q + n * (_cr[c] + r * 0.2f); }
                            }
                        }
                    }
                // 바닥 · 데스크 상판
                if (p.Y < r) { p.Y = r; if (v.Y < 0f) { Squish(i, Vector3.UnitY, -v.Y); v.Y = -v.Y * e; v.X *= 0.8f; v.Z *= 0.8f; } }
                var dx = p.X - desk.X; var dz = p.Z - desk.Z;
                if (dx * dx + dz * dz < UnifiedGaussianSceneCompositorEngine.DeskRadius * UnifiedGaussianSceneCompositorEngine.DeskRadius && p.Y < desk.Y + r && p.Y > desk.Y - 0.1f)
                { p.Y = desk.Y + r; if (v.Y < 0f) { Squish(i, Vector3.UnitY, -v.Y); v.Y = -v.Y * e; v.X *= 0.8f; v.Z *= 0.8f; } }
                if (_decal[i] > 0f) break;
            }
            // 찌그러짐 임계 감쇠 복원(ω = 30 rad/s)
            if (_decal[i] <= 0f)
            {
                const float W = 30f;
                _squashV[i] += (-W * W * _squash[i] - 2f * W * _squashV[i]) * dt;
                _squash[i] = Math.Clamp(_squash[i] + _squashV[i] * dt, 0f, 0.7f);
            }
            _p[i] = p; _v[i] = v; _dirty[i] = true;
        }
        LastStepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Squish(int i, Vector3 n, float speed)
    {
        var k = _kind[i] switch { DropKind.WaterBalloon => 0.09f, DropKind.Tomato => 0.07f, DropKind.Ball => 0.05f, _ => 0f };
        var s = MathF.Min(0.7f, k * speed);
        if (s > _squash[i]) { _squash[i] = s; _squashN[i] = n; _squashV[i] = 0f; }
        if (_squash[i] > PeakSquash) PeakSquash = _squash[i];
    }

    // ══════════════════════ 스플랫 기록(무할당 · 변경 슬롯만) ══════════════════════
    public void Write(GaussianSplatScene sc, int start)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < Capacity; i++)
        {
            if (!_dirty[i]) continue;
            _dirty[i] = false;
            var o = start + i * SplatsPer;
            if (!_active[i]) { for (var k = 0; k < SplatsPer; k++) sc.Opacity[o + k] = 0f; continue; }
            var kind = _kind[i]; var r = KindRadius[(int)kind]; var col = _col[i];
            var n = _squashN[i]; var sq = _squash[i];
            var along = 1f - sq; var across = 1f / MathF.Sqrt(MathF.Max(along, 0.15f));
            var fade = _decal[i] > 0f ? Math.Clamp(_decal[i] / DecalS, 0f, 1f) : 1f;
            if (kind == DropKind.Confetti)
            {
                // 납작 사각 조각 1 + 나머지 투명
                var t = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY));
                sc.Write(o, _p[i], t, Vector3.Cross(n, t), n, new Vector3(r, r * 0.7f, 0.0015f), 0.95f, col, 8);
                for (var k = 1; k < SplatsPer; k++) sc.Opacity[o + k] = 0f;
                continue;
            }
            for (var k = 0; k < SplatsPer; k++)
            {
                var u = Shell[k];
                var ln = Vector3.Dot(u, n);
                var lt = u - ln * n;
                var pos = _p[i] + (lt * across + n * (ln * along)) * r;
                var nrm = Vector3.Normalize(u);
                var a = Vector3.Normalize(Vector3.Cross(nrm, MathF.Abs(nrm.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY));
                var shade = 0.75f + 0.25f * u.Y;
                var c = kind == DropKind.Ball && (k & 3) == 0 ? new Vector3(0.95f, 0.95f, 0.95f) : col * shade;
                sc.Write(o + k, pos, a, Vector3.Cross(nrm, a), nrm, new Vector3(r * 0.42f * across, r * 0.42f * across, r * 0.12f), 0.92f * fade, c, 8);
            }
        }
        LastWriteUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
