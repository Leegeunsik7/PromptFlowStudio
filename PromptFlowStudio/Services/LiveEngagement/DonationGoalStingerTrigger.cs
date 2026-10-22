namespace PromptFlowStudio.Services.LiveEngagement;

public enum StingerParticleKind : byte { Coin, Sparkle }

/// <summary>v17.86.0 스팅어 파티클(3 차 베지에 경로 · 풀 슬롯).</summary>
public struct StingerParticle
{
    public float X0, Y0, X1, Y1, X2, Y2, X3, Y3;
    public float AgeUs, LifeUs, Size, Phase;
    public StingerParticleKind Kind;
    public bool Alive;
}

/// <summary>
/// [PromptFlow Studio v17.86.0] 후원 골 바 &amp; 파티클 스팅어 트리거(무할당).
///   ▸ 골 바: 목표 금액(기본 ₩100,000) 대비 누적 후원액 → 16.7 ms 고정 스텝 Lerp 채움 · 목표 달성 1 회 자동 스팅어.
///   ▸ 스팅어: 대형 후원(기본 ₩10,000 ~) 수신 시 3 s — 즉시 버스트 + 2 s 연속 방출 → 768 슬롯 풀(골드 코인 · 반짝임) ·
///     3 차 베지에(분출 → 정점 → 낙하) 경로 · 수명은 스팅어 잔여 시간으로 클램프 → 3 s 경과 시 전량 자동 소멸.
///   ▸ <see cref="Render"/>: 골 바 영역 + 생존 파티클 경계 박스 1 개만 합성기에 등록.
/// </summary>
public sealed class DonationGoalStingerTrigger
{
    public const int PoolSize = 768, BurstCount = 180, SpawnPerSec = 120, DonorCap = 32;
    public const long StingerUs = 3_000_000, SpawnWindowUs = 2_000_000, StepUs = 16_667;
    public const float LerpK = 0.12f;

    private readonly StingerParticle[] _pool = new StingerParticle[PoolSize];
    private readonly char[] _donor = new char[DonorCap];
    private uint _seed = 0x9E3779B9u;
    private long _acc, _spawnAcc;
    private int _donorLen, _cursor;

    public long GoalKrw { get; private set; } = 100_000;
    public long StingerThresholdKrw { get; set; } = 10_000;
    public long CurrentKrw { get; private set; }
    public float TargetFill => GoalKrw <= 0 ? 0f : Math.Min(1f, CurrentKrw / (float)GoalKrw);
    public float DisplayFill { get; private set; }
    public bool GoalReached { get; private set; }
    public long StingerLeftUs { get; private set; }
    public long StingerElapsedUs { get; private set; }
    public bool StingerActive => StingerLeftUs > 0 || Active > 0;
    public float OriginX { get; set; } = 960f;
    public float OriginY { get; set; } = 900f;
    public int Active { get; private set; }
    public int PeakActive { get; private set; }
    public long Spawned { get; private set; }
    public long Expired { get; private set; }
    public long Stingers { get; private set; }
    public long Donations { get; private set; }
    public long MaxDonationKrw { get; private set; }
    public long LastDonationKrw { get; private set; }
    public ReadOnlySpan<char> LastDonor => _donor.AsSpan(0, _donorLen);
    public ref readonly StingerParticle Particle(int i) => ref _pool[i];

    // ══════════════════════ 수신 · 트리거 ══════════════════════

    public void SetGoal(long krw)
    {
        GoalKrw = Math.Clamp(krw, 1_000, 100_000_000);
        GoalReached = CurrentKrw >= GoalKrw;
    }

    /// <summary>후원 수신 — 누적 · 대형/목표 달성 시 스팅어. 반환 = 스팅어 발동 여부.</summary>
    public bool OnDonation(long krw, ReadOnlySpan<char> donor)
    {
        if (krw <= 0) return false;
        CurrentKrw += krw; Donations++;
        LastDonationKrw = krw;
        if (krw > MaxDonationKrw) MaxDonationKrw = krw;
        _donorLen = Math.Min(donor.Length, DonorCap);
        donor.Slice(0, _donorLen).CopyTo(_donor);
        var fire = krw >= StingerThresholdKrw;
        if (!GoalReached && CurrentKrw >= GoalKrw) { GoalReached = true; fire = true; }
        if (fire) Trigger();
        return fire;
    }

    /// <summary>스팅어 발동(진행 중이면 3 s 재연장 + 재버스트).</summary>
    public void Trigger()
    {
        StingerLeftUs = StingerUs; StingerElapsedUs = 0; _spawnAcc = 0;
        Stingers++;
        for (var i = 0; i < BurstCount; i++) Spawn();
    }

    private void Spawn()
    {
        var life = Math.Min(StingerLeftUs, 1_200_000 + (long)(1_300_000 * Rand()));
        if (life < 50_000) return;
        // 빈 슬롯 순환 탐색(풀 포화 시 방출 생략)
        for (var n = 0; n < PoolSize; n++)
        {
            ref var p = ref _pool[_cursor];
            _cursor = (_cursor + 1) % PoolSize;
            if (p.Alive) continue;
            var ang = -MathF.PI / 2f + (Rand() - 0.5f) * 2.2f;   // 위쪽 ±63°
            var spd = 380f + 520f * Rand();
            var ox = OriginX + (Rand() - 0.5f) * 80f;
            p.X0 = ox; p.Y0 = OriginY;
            p.X1 = ox + MathF.Cos(ang) * spd * 0.6f; p.Y1 = OriginY + MathF.Sin(ang) * spd;
            p.X2 = ox + MathF.Cos(ang) * spd * 1.1f; p.Y2 = OriginY + MathF.Sin(ang) * spd * 0.9f;
            p.X3 = ox + MathF.Cos(ang) * spd * 1.4f; p.Y3 = OriginY + 140f + 240f * Rand();   // 중력 낙하
            p.AgeUs = 0; p.LifeUs = life;
            p.Kind = Rand() < 0.62f ? StingerParticleKind.Coin : StingerParticleKind.Sparkle;
            p.Size = p.Kind == StingerParticleKind.Coin ? 7f + 7f * Rand() : 2.5f + 3f * Rand();
            p.Phase = Rand() * 6.2831855f;
            p.Alive = true;
            Active++; Spawned++;
            if (Active > PeakActive) PeakActive = Active;
            return;
        }
    }

    /// <summary>파티클 위치(3 차 베지에 · ease-out).</summary>
    public static void Position(in StingerParticle p, out float x, out float y)
    {
        var t = p.LifeUs <= 0 ? 1f : Math.Clamp(p.AgeUs / p.LifeUs, 0f, 1f);
        t = 1f - (1f - t) * (1f - t) * 0.6f - 0.4f * (1f - t);   // 0 → 1 완만 감속
        var u = 1f - t;
        float b0 = u * u * u, b1 = 3f * u * u * t, b2 = 3f * u * t * t, b3 = t * t * t;
        x = b0 * p.X0 + b1 * p.X1 + b2 * p.X2 + b3 * p.X3;
        y = b0 * p.Y0 + b1 * p.Y1 + b2 * p.Y2 + b3 * p.Y3;
    }

    /// <summary>잔존 투명도 0~1(말기 3 차 감쇠).</summary>
    public static float Alpha(in StingerParticle p)
    {
        var t = p.LifeUs <= 0 ? 1f : Math.Clamp(p.AgeUs / p.LifeUs, 0f, 1f);
        var a = 1f - t * t * t;
        if (p.Kind == StingerParticleKind.Sparkle) a *= 0.55f + 0.45f * MathF.Sin(p.Phase + p.AgeUs * 2.4e-5f);
        return Math.Clamp(a, 0f, 1f);
    }

    // ══════════════════════ 틱 · 렌더(무할당 핫패스) ══════════════════════

    public void Tick(long dtUs)
    {
        if (dtUs <= 0) return;
        _acc += dtUs;
        var steps = 0;
        while (_acc >= StepUs && steps < 60)
        {
            _acc -= StepUs; steps++;
            var d = TargetFill - DisplayFill;
            DisplayFill = MathF.Abs(d) < 0.0005f ? TargetFill : DisplayFill + d * LerpK;
        }
        if (_acc > StepUs) _acc = StepUs;

        if (StingerLeftUs > 0)
        {
            StingerElapsedUs += dtUs;
            StingerLeftUs = Math.Max(0, StingerLeftUs - dtUs);
            if (StingerElapsedUs < SpawnWindowUs)
            {
                _spawnAcc += dtUs * SpawnPerSec;
                while (_spawnAcc >= 1_000_000) { _spawnAcc -= 1_000_000; Spawn(); }
            }
        }
        if (Active == 0) return;
        for (var i = 0; i < PoolSize; i++)
        {
            ref var p = ref _pool[i];
            if (!p.Alive) continue;
            p.AgeUs += dtUs;
            if (p.AgeUs < p.LifeUs && StingerLeftUs > 0) continue;
            p.Alive = false; Active--; Expired++;
        }
    }

    /// <summary>골 바(트랙 · 골드 채움 · 달성 시 녹색) + 파티클 경계 박스 국소 기록.</summary>
    public void Render(ZeroAllocAlphaOverlayCompositorEngine c, in PixelRect goalBar)
    {
        var g = c.AddRegion(goalBar);
        if (!g.IsEmpty)
        {
            c.FillRect(g, ZeroAllocAlphaOverlayCompositorEngine.Premul(ZeroAllocAlphaOverlayCompositorEngine.Rgba(8, 12, 24, 180)));
            var inner = new PixelRect(g.X + 6, g.Y + 6, g.W - 12, g.H - 12);
            var fill = GoalReached ? ZeroAllocAlphaOverlayCompositorEngine.Rgba(34, 197, 94, 255) : ZeroAllocAlphaOverlayCompositorEngine.Rgba(250, 190, 30, 255);
            c.FillRect(new PixelRect(inner.X, inner.Y, (int)(inner.W * Math.Clamp(DisplayFill, 0f, 1f)), inner.H), fill);
        }
        if (Active == 0) return;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (var i = 0; i < PoolSize; i++)
        {
            ref readonly var p = ref _pool[i];
            if (!p.Alive) continue;
            Position(p, out var x, out var y);
            if (x - p.Size < minX) minX = x - p.Size;
            if (y - p.Size < minY) minY = y - p.Size;
            if (x + p.Size > maxX) maxX = x + p.Size;
            if (y + p.Size > maxY) maxY = y + p.Size;
        }
        var box = c.AddRegion(new PixelRect((int)minX - 2, (int)minY - 2, (int)(maxX - minX) + 5, (int)(maxY - minY) + 5));
        if (box.IsEmpty) return;
        var gold = ZeroAllocAlphaOverlayCompositorEngine.Rgba(255, 200, 40, 255);
        var rim = ZeroAllocAlphaOverlayCompositorEngine.Rgba(255, 236, 140, 255);
        var spark = ZeroAllocAlphaOverlayCompositorEngine.Rgba(255, 255, 230, 255);
        for (var i = 0; i < PoolSize; i++)
        {
            ref readonly var p = ref _pool[i];
            if (!p.Alive) continue;
            Position(p, out var x, out var y);
            var k = (uint)(255f * Alpha(p));
            if (k == 0) continue;
            if (p.Kind == StingerParticleKind.Coin)
            {
                // 회전 코인 = 위상에 따른 반지름 맥동 + 하이라이트
                var r = p.Size * (0.75f + 0.25f * MathF.Abs(MathF.Cos(p.Phase + p.AgeUs * 8e-6f)));
                c.FillCircle(x, y, r, ZeroAllocAlphaOverlayCompositorEngine.Fade(gold, k));
                c.FillCircle(x - r * 0.3f, y - r * 0.3f, r * 0.35f, ZeroAllocAlphaOverlayCompositorEngine.Fade(rim, k));
            }
            else c.FillCircle(x, y, p.Size, ZeroAllocAlphaOverlayCompositorEngine.Fade(spark, k));
        }
    }

    private float Rand()
    {
        _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
        return (_seed & 0xFFFFFF) / 16777216f;
    }

    public void Reset()
    {
        Array.Clear(_pool);
        CurrentKrw = 0; DisplayFill = 0; GoalReached = false;
        StingerLeftUs = StingerElapsedUs = 0; _acc = _spawnAcc = 0; _cursor = 0; _donorLen = 0;
        Active = PeakActive = 0; Spawned = Expired = Stingers = Donations = MaxDonationKrw = LastDonationKrw = 0;
    }
}
