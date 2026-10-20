using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>오디오 특징으로부터 휴리스틱 분류한 반응 강도 단계.
///   ⚠ 음성/표정 기반 감정 인식이 아니다 - <see cref="MultimodalAudioSyncEngine"/> 이 제공하는 것은 진폭·도미넌트 주파수·비트뿐이라
///   그 3값의 결정적 규칙으로 "반응 강도"를 4단계로 매핑한다(오버레이 연출 강도 선택용).</summary>
public enum ReactionAffect : byte { Calm = 0, Engaged = 1, Excited = 2, Hype = 3 }

/// <summary>오버레이 파티클 1개(가변 struct - 고정 배열 슬롯 안에서 제자리 갱신, 힙 할당 없음).</summary>
public struct OverlayParticle
{
    public float X, Y;        // 정규화 좌표(0..1)
    public float Vx, Vy;      // 초당 정규화 속도
    public float Life;        // 남은 수명(ms) - 0 이하면 빈 슬롯
    public float Size;        // 정규화 크기
    public byte Hue;          // 0..255 색상 인덱스(반응 단계별 팔레트)
}

/// <summary>v12.2.0-Phase4: <see cref="LiveStreamVirtualCamPipe.AttachOverlay"/> 결선 계약 - 송출 직전 RGBA32 프레임에 가산 합성.
///   구현체는 송출 스레드에서 호출되므로 스레드 안전해야 하며, <see cref="HasVisibleContent"/> 가 false 면 파이프는 복사 없이 통과시킨다.</summary>
public interface IVirtualCamOverlay
{
    bool HasVisibleContent { get; }
    int RenderOverlayInto(Span<byte> rgba, int width, int height);
}

/// <summary>1틱에 주입되는 반응 임펄스(값 형식).</summary>
public readonly record struct ReactionImpulse(int Spawn, float Strength, ReactionAffect Affect);

/// <summary>렌더러/HUD 가 읽는 오버레이 상태 스냅샷(값 형식).</summary>
public readonly record struct ReactionOverlayFrame(long Tick, ReactionAffect Affect, int ActiveParticles, float BounceScale, float BadgeAlpha);

/// <summary>
/// [PromptFlow Studio v12.2.0-Phase3] 스트리머 리액션 오버레이 파이프라인 — 파티클·감정(반응 강도) 뱃지·실시간 바운스를
///   고정 스텝(<see cref="StepMs"/>=10ms)으로 적분한다.
///   ▸ Zero-Alloc: 파티클은 생성 시 1회 할당한 고정 용량 struct 배열 슬롯을 재사용(Span 제자리 갱신), 난수는 xorshift32 값 상태,
///     래스터화는 호출측 RGBA 버퍼(Span&lt;byte&gt;)에 직접 가산 합성 → <see cref="Step"/>/<see cref="RenderInto"/>/<see cref="CopyParticles"/> 힙 할당 0.
///   ▸ 스레드 모델: 단일 스텝 소유자(<see cref="StreamerReactionEngine"/> 가 락으로 직렬화) 전제 - 이 클래스 자체는 락을 갖지 않는다.
/// </summary>
public sealed class ReactionOverlayPipeline
{
    public const float StepMs = 10f;
    public const int DefaultCapacity = 512;

    private const float Gravity = 0.9f;           // 정규화 좌표/초²
    private const float BounceStiffness = 900f;   // 스프링 상수(1/초²) - 약 4.8Hz 고유진동
    private const float BounceDamping = 18f;      // 감쇠(1/초) - 수 회 흔들린 뒤 정지
    private const float BadgeDecayPerMs = 1f / 1500f;

    private readonly OverlayParticle[] _particles;
    private uint _rng;
    private int _active;
    private float _bounce;       // 바운스 변위(0=정지) - 표시 스케일 = 1 + _bounce
    private float _bounceVel;
    private float _badgeAlpha;
    private ReactionAffect _affect;
    private long _tick;
    private long _spawned, _dropped;

    public ReactionOverlayPipeline(int capacity = DefaultCapacity, uint seed = 0x9E3779B9u)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _particles = new OverlayParticle[capacity];
        _rng = seed == 0 ? 1u : seed;
    }

    public int Capacity => _particles.Length;
    public int ActiveParticles => _active;
    public long Spawned => _spawned;
    /// <summary>용량 초과로 생성하지 못한 파티클 수(예외 없이 드롭 + 집계).</summary>
    public long Dropped => _dropped;
    public ReactionAffect Affect => _affect;
    public ReactionOverlayFrame Snapshot => new(_tick, _affect, _active, 1f + _bounce, _badgeAlpha);

    /// <summary>고정 10ms 스텝 1회 - 임펄스 반영(파티클 생성·바운스 킥·뱃지 점등) 후 전 파티클 적분.</summary>
    public void Step(in ReactionImpulse impulse)
    {
        _tick++;
        const float dt = StepMs / 1000f;

        if (impulse.Spawn > 0 || impulse.Strength > 0)
        {
            SpawnBurst(impulse.Spawn, impulse.Strength, impulse.Affect);
            _bounceVel += impulse.Strength * 6f;   // 비트 강도 비례 킥
            if (impulse.Affect >= _affect || _badgeAlpha <= 0.05f) _affect = impulse.Affect;
            _badgeAlpha = 1f;
        }

        // 감쇠 스프링(반암시적 오일러 - 10ms 고정 스텝에서 안정)
        _bounceVel += (-BounceStiffness * _bounce - BounceDamping * _bounceVel) * dt;
        _bounce += _bounceVel * dt;
        if (MathF.Abs(_bounce) < 1e-4f && MathF.Abs(_bounceVel) < 1e-3f) { _bounce = 0; _bounceVel = 0; }

        _badgeAlpha = MathF.Max(0f, _badgeAlpha - BadgeDecayPerMs * StepMs);
        if (_badgeAlpha <= 0f) _affect = ReactionAffect.Calm;

        var span = _particles.AsSpan();
        var alive = 0;
        for (var i = 0; i < span.Length; i++)
        {
            ref var p = ref span[i];
            if (p.Life <= 0f) continue;
            p.Life -= StepMs;
            if (p.Life <= 0f || p.Y > 1.2f) { p.Life = 0f; continue; }
            p.Vy += Gravity * dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            alive++;
        }
        _active = alive;
    }

    private void SpawnBurst(int count, float strength, ReactionAffect affect)
    {
        if (count <= 0) return;
        var span = _particles.AsSpan();
        var hueBase = (int)affect * 64;
        var cursor = 0;
        for (var n = 0; n < count; n++)
        {
            while (cursor < span.Length && span[cursor].Life > 0f) cursor++;
            if (cursor >= span.Length) { _dropped += count - n; return; }
            ref var p = ref span[cursor];
            // 화면 하단 중앙에서 위로 부채꼴 분출
            p.X = 0.5f + (NextUnit() - 0.5f) * 0.2f;
            p.Y = 0.95f;
            p.Vx = (NextUnit() - 0.5f) * (0.4f + strength);
            p.Vy = -(0.6f + NextUnit() * (0.6f + strength));
            p.Life = 600f + NextUnit() * 900f;
            p.Size = 0.006f + NextUnit() * 0.01f * (1f + strength);
            p.Hue = (byte)(hueBase + (int)(NextUnit() * 63f));
            _spawned++;
            _active++;
        }
    }

    // xorshift32 - 값 상태라 할당 0, 시드 고정 시 완전 결정적(테스트 재현성).
    private float NextUnit()
    {
        var x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _rng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    /// <summary>활성 파티클을 호출측 버퍼로 복사(렌더 스레드 전달용). 복사 개수 반환 - 버퍼가 작으면 앞에서부터 잘림.</summary>
    public int CopyParticles(Span<OverlayParticle> destination)
    {
        var src = _particles.AsSpan();
        var n = 0;
        for (var i = 0; i < src.Length && n < destination.Length; i++)
            if (src[i].Life > 0f) destination[n++] = src[i];
        return n;
    }

    /// <summary>RGBA32 버퍼에 파티클을 가산 합성(포화 덧셈) - <see cref="LiveStreamVirtualCamPipe.SubmitFrame"/> 직전 프레임에 얹는 용도.
    ///   바운스 스케일은 화면 중심 기준 파티클 좌표 확대로 반영. 버퍼 길이가 width*height*4 미만이면 아무것도 하지 않고 0 반환.</summary>
    public int RenderInto(Span<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) return 0;
        var scale = 1f + _bounce;
        var drawn = 0;
        var src = _particles.AsSpan();
        for (var i = 0; i < src.Length; i++)
        {
            ref readonly var p = ref src[i];
            if (p.Life <= 0f) continue;
            var cx = (int)((0.5f + (p.X - 0.5f) * scale) * width);
            var cy = (int)((0.5f + (p.Y - 0.5f) * scale) * height);
            var r = Math.Max(1, (int)(p.Size * width * scale));
            var alpha = Math.Clamp(p.Life / 600f, 0f, 1f);
            HueToRgb(p.Hue, out var cr, out var cg, out var cb);
            var ar = (int)(cr * alpha); var ag = (int)(cg * alpha); var ab = (int)(cb * alpha);
            var x0 = Math.Max(0, cx - r); var x1 = Math.Min(width - 1, cx + r);
            var y0 = Math.Max(0, cy - r); var y1 = Math.Min(height - 1, cy + r);
            if (x0 > x1 || y0 > y1) continue;
            // v15.0.0: A 레인 255 포화 가산 = 불투명 고정 → 픽셀당 4바이트 단일 포화 덧셈으로 SIMD 벡터화.
            var pattern = RgbaSimdKernels.PackRgba(ar, ag, ab, 255);
            for (var y = y0; y <= y1; y++)
                RgbaSimdKernels.AddSaturate(rgba.Slice((y * width + x0) * 4, (x1 - x0 + 1) * 4), pattern);
            drawn++;
        }
        return drawn;
    }

    // 6구간 HSV(S=V=1) 근사 - 정수 연산만.
    private static void HueToRgb(byte hue, out int r, out int g, out int b)
    {
        var h = hue * 6;           // 0..1530
        var seg = h >> 8;          // 0..5
        var f = h & 0xFF;
        switch (seg)
        {
            case 0: r = 255; g = f; b = 0; break;
            case 1: r = 255 - f; g = 255; b = 0; break;
            case 2: r = 0; g = 255; b = f; break;
            case 3: r = 0; g = 255 - f; b = 255; break;
            case 4: r = f; g = 0; b = 255; break;
            default: r = 255; g = 0; b = 255 - f; break;
        }
    }

    /// <summary>전 슬롯 비우기·바운스/뱃지 정지(용량 배열은 재사용).</summary>
    public void Reset()
    {
        Array.Clear(_particles);
        _active = 0; _bounce = 0; _bounceVel = 0; _badgeAlpha = 0; _affect = ReactionAffect.Calm;
        _tick = 0; _spawned = 0; _dropped = 0;
    }
}

/// <summary>
/// [v15.0.0] RGBA32 포화 가산 커널(Zero-Alloc · 순수 BCL Intrinsics).
///   ▸ 분기: AVX2(32B) → SSE2/NEON(16B) → <see cref="Vector{T}"/>(HW 가속 시) → 스칼라. 모든 경로는 픽셀 단위 꼬리를 스칼라로 마감해 비트 동일.
///   ▸ 픽셀(4B) 정렬 입력 전제 - 길이의 4 미만 나머지 바이트는 건드리지 않는다. 빅엔디언은 스칼라 고정.
/// </summary>
public static class RgbaSimdKernels
{
    /// <summary>R,G,B,A(0..255)를 메모리 바이트 순서(리틀엔디언) uint 로 패킹.</summary>
    public static uint PackRgba(int r, int g, int b, int a)
        => (uint)(r & 0xFF) | (uint)(g & 0xFF) << 8 | (uint)(b & 0xFF) << 16 | (uint)(a & 0xFF) << 24;

    /// <summary>전 픽셀에 <paramref name="pattern"/>(RGBA) 포화 덧셈 - 최적 HW 경로 자동 선택.</summary>
    public static void AddSaturate(Span<byte> rgba, uint pattern)
    {
        var n = rgba.Length & ~3;
        var i = 0;
        if (BitConverter.IsLittleEndian && n >= 16)
        {
            ref var p = ref MemoryMarshal.GetReference(rgba);
            if (Avx2.IsSupported && n >= 32)
            {
                var add = Vector256.Create(pattern).AsByte();
                for (; i <= n - 32; i += 32)
                    Avx2.AddSaturate(Vector256.LoadUnsafe(ref p, (nuint)i), add).StoreUnsafe(ref p, (nuint)i);
            }
            if (Sse2.IsSupported)
            {
                var add = Vector128.Create(pattern).AsByte();
                for (; i <= n - 16; i += 16)
                    Sse2.AddSaturate(Vector128.LoadUnsafe(ref p, (nuint)i), add).StoreUnsafe(ref p, (nuint)i);
            }
            else if (AdvSimd.IsSupported)
            {
                var add = Vector128.Create(pattern).AsByte();
                for (; i <= n - 16; i += 16)
                    AdvSimd.AddSaturate(Vector128.LoadUnsafe(ref p, (nuint)i), add).StoreUnsafe(ref p, (nuint)i);
            }
            else
            {
                i = AddSaturateVectorCore(rgba[..n], pattern);
            }
        }
        AddSaturateScalar(rgba[i..n], pattern);
    }

    /// <summary>이식형 <see cref="Vector{T}"/> 경로(HW 미가속이면 전량 스칼라) - 플랫폼 독립 회귀 검증용으로 공개.</summary>
    public static void AddSaturateVector(Span<byte> rgba, uint pattern)
    {
        var n = rgba.Length & ~3;
        var i = BitConverter.IsLittleEndian ? AddSaturateVectorCore(rgba[..n], pattern) : 0;
        AddSaturateScalar(rgba[i..n], pattern);
    }

    /// <summary>기준 스칼라 경로(비트 정합 기준선).</summary>
    public static void AddSaturateScalar(Span<byte> rgba, uint pattern)
    {
        int ar = (byte)pattern, ag = (byte)(pattern >> 8), ab = (byte)(pattern >> 16), aa = (byte)(pattern >> 24);
        for (var o = 0; o + 3 < rgba.Length; o += 4)
        {
            rgba[o] = (byte)Math.Min(255, rgba[o] + ar);
            rgba[o + 1] = (byte)Math.Min(255, rgba[o + 1] + ag);
            rgba[o + 2] = (byte)Math.Min(255, rgba[o + 2] + ab);
            rgba[o + 3] = (byte)Math.Min(255, rgba[o + 3] + aa);
        }
    }

    // 랩어라운드 합 < 원본 ⇔ 오버플로 → 마스크(0xFF) OR 로 255 포화. 처리한 바이트 수(Vector<byte>.Count 배수 = 4 배수) 반환.
    private static int AddSaturateVectorCore(Span<byte> rgba, uint pattern)
    {
        if (!Vector.IsHardwareAccelerated) return 0;
        var w = Vector<byte>.Count;
        var add = new Vector<uint>(pattern).As<uint, byte>();
        ref var p = ref MemoryMarshal.GetReference(rgba);
        var i = 0;
        for (; i <= rgba.Length - w; i += w)
        {
            ref var at = ref Unsafe.Add(ref p, i);
            var v = Unsafe.ReadUnaligned<Vector<byte>>(ref at);
            var sum = v + add;
            Unsafe.WriteUnaligned(ref at, sum | Vector.LessThan(sum, v));
        }
        return i;
    }
}
