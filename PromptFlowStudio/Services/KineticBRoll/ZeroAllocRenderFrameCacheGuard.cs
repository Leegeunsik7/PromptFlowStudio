namespace PromptFlowStudio.Services.KineticBRoll;

/// <summary>
/// [PromptFlow Studio v17.78.0] 🧊 인메모리 렌더 프레임 캐시 &amp; 무손실 스위프트 가드(<see cref="TryGetSprite"/>/<see cref="AcquireSprite"/>/<see cref="TryGetFrame"/>/<see cref="AcquireFrame"/> 무할당).
/// <b>스프라이트 풀</b>: 단어 SDF 래스터(채움 α + 외곽선 α) 를 고정 슬랩(48 슬롯 × 256×72) 에 보관 — 64-bit 키(FNV-1a(단어) ⊕ px) 선형 탐사 표 + LRU 틱 축출.
/// <b>프레임 링</b>: 정적 배경 씬 버퍼 4 슬롯(키 = 씬 · 프리셋 · 해상도) — 적중 시 재래스터 대신 블록 복사.
/// <b>회계</b>: 래스터 연산(픽셀 표본) 소비/절감을 누적해 절감률을 공시하고, <see cref="BeginFrame"/>/<see cref="EndFrame"/> 로 16.7 ms 예산을 감시 —
/// 연속 3 프레임 초과 시 저비용 모드(<see cref="Degraded"/> · 최근접 표본 · 스플랫 반감) 진입, 30 프레임 연속 여유 시 복귀.
/// </summary>
public sealed class ZeroAllocRenderFrameCacheGuard
{
    public const int SpriteSlots = 48, SpriteMaxW = 256, SpriteMaxH = 72, FrameSlots = 4, TableSize = 128, DegradeAfter = 3, RecoverAfter = 30;
    public const double BudgetUs = 1e6 / 60;

    private readonly byte[] _fill = new byte[SpriteSlots * SpriteMaxW * SpriteMaxH];
    private readonly byte[] _line = new byte[SpriteSlots * SpriteMaxW * SpriteMaxH];
    private readonly ulong[] _spriteKey = new ulong[SpriteSlots];
    private readonly int[] _spriteW = new int[SpriteSlots], _spriteH = new int[SpriteSlots];
    private readonly long[] _spriteUse = new long[SpriteSlots], _spriteCost = new long[SpriteSlots];
    private readonly short[] _table = new short[TableSize];   // 키 → 슬롯 + 1(0 = 빈칸)
    private readonly ulong[] _frameKey = new ulong[FrameSlots];
    private readonly long[] _frameUse = new long[FrameSlots];
    private uint[][] _frames = Array.Empty<uint[]>();
    private int _fw, _fh, _over, _good;
    private long _tick, _frameStart;

    public ZeroAllocRenderFrameCacheGuard() => Clear();

    /// <summary>false = 캐시 우회(비교 계측용 · 매 요청 미스).</summary>
    public bool Enabled { get; set; } = true;
    public bool Degraded { get; private set; }
    public long SpriteHits { get; private set; }
    public long SpriteMisses { get; private set; }
    public long SpriteEvictions { get; private set; }
    public long FrameHits { get; private set; }
    public long FrameMisses { get; private set; }
    public long RasterOpsSpent { get; private set; }
    public long RasterOpsSaved { get; private set; }
    public long Frames { get; private set; }
    public long OverBudget { get; private set; }
    public long DegradeEntries { get; private set; }
    public double LastFrameUs { get; private set; }
    public double MaxFrameUs { get; private set; }
    public int FrameWidth => _fw;
    public int FrameHeight => _fh;
    /// <summary>래스터 연산 절감률(0..1).</summary>
    public double Savings => RasterOpsSaved + RasterOpsSpent == 0 ? 0 : (double)RasterOpsSaved / (RasterOpsSaved + RasterOpsSpent);
    public int SpritesResident { get { var n = 0; for (var i = 0; i < SpriteSlots; i++) if (_spriteKey[i] != 0) n++; return n; } }

    /// <summary>프레임 링 해상도 설정 — 변경 시에만 할당(비핫패스).</summary>
    public void Configure(int w, int h)
    {
        if (w == _fw && h == _fh) return;
        _frames = new uint[FrameSlots][];
        for (var i = 0; i < FrameSlots; i++) _frames[i] = new uint[w * h];
        _fw = w; _fh = h;
        Array.Clear(_frameKey); Array.Clear(_frameUse);
    }

    public void Clear()
    {
        Array.Clear(_spriteKey); Array.Clear(_table); Array.Clear(_spriteUse); Array.Clear(_frameKey); Array.Clear(_frameUse);
        SpriteHits = SpriteMisses = SpriteEvictions = FrameHits = FrameMisses = RasterOpsSpent = RasterOpsSaved = 0;
    }

    public void ResetStats()
    {
        SpriteHits = SpriteMisses = SpriteEvictions = FrameHits = FrameMisses = RasterOpsSpent = RasterOpsSaved = 0;
        Frames = OverBudget = 0; MaxFrameUs = 0;
    }

    /// <summary>FNV-1a 64(UTF-16 · 대소문자 보존) ⊕ 정수 변형자 — 0 은 빈칸 표식이라 회피.</summary>
    public static ulong Key(ReadOnlySpan<char> s, int variant)
    {
        var h = 14695981039346656037UL;
        for (var i = 0; i < s.Length; i++) { h ^= s[i]; h *= 1099511628211UL; }
        h ^= (ulong)(uint)variant * 0x9E3779B97F4A7C15UL;
        return h == 0 ? 1 : h;
    }

    // ══════════════════════ 스프라이트 풀 ══════════════════════
    public bool TryGetSprite(ulong key, out int slot)
    {
        slot = -1;
        if (!Enabled) { SpriteMisses++; return false; }
        var h = (int)(key & (TableSize - 1));
        for (var p = 0; p < TableSize; p++)
        {
            var e = _table[(h + p) & (TableSize - 1)];
            if (e == 0) break;
            if (e > 0 && _spriteKey[e - 1] == key)
            {
                slot = e - 1; _spriteUse[slot] = ++_tick; SpriteHits++; RasterOpsSaved += _spriteCost[slot];
                return true;
            }
        }
        SpriteMisses++;
        return false;
    }

    /// <summary>미스 후 슬롯 확보(LRU 축출) — 반환 슬롯의 채움/외곽선 영역은 0 으로 지워진 상태.</summary>
    public int AcquireSprite(ulong key, int w, int h)
    {
        w = Math.Clamp(w, 1, SpriteMaxW); h = Math.Clamp(h, 1, SpriteMaxH);
        var slot = 0; var oldest = long.MaxValue;
        for (var i = 0; i < SpriteSlots; i++)
        {
            if (_spriteKey[i] == 0) { slot = i; break; }
            if (_spriteUse[i] < oldest) { oldest = _spriteUse[i]; slot = i; }
        }
        if (_spriteKey[slot] != 0) { Unlink(_spriteKey[slot]); SpriteEvictions++; }
        _spriteKey[slot] = key; _spriteW[slot] = w; _spriteH[slot] = h; _spriteUse[slot] = ++_tick; _spriteCost[slot] = 0;
        Fill(slot).Clear(); Outline(slot).Clear();
        if (Enabled)
        {
            var p = (int)(key & (TableSize - 1));
            while (_table[p] > 0) p = (p + 1) & (TableSize - 1);
            _table[p] = (short)(slot + 1);
        }
        return slot;
    }

    private void Unlink(ulong key)
    {
        var h = (int)(key & (TableSize - 1));
        for (var p = 0; p < TableSize; p++)
        {
            var i = (h + p) & (TableSize - 1);
            var e = _table[i];
            if (e == 0) return;
            if (e > 0 && _spriteKey[e - 1] == key) { _table[i] = -1; return; }   // 묘비(탐사 사슬 보존)
        }
    }

    public Span<byte> Fill(int slot) => _fill.AsSpan(slot * SpriteMaxW * SpriteMaxH, _spriteW[slot] * _spriteH[slot]);
    public Span<byte> Outline(int slot) => _line.AsSpan(slot * SpriteMaxW * SpriteMaxH, _spriteW[slot] * _spriteH[slot]);
    public int SpriteWidth(int slot) => _spriteW[slot];
    public int SpriteHeight(int slot) => _spriteH[slot];

    /// <summary>슬롯 래스터 비용 기록(적중 시 절감으로 회계).</summary>
    public void CommitSprite(int slot, long rasterOps) { _spriteCost[slot] = rasterOps; RasterOpsSpent += rasterOps; }

    // ══════════════════════ 정적 프레임 링 ══════════════════════
    public bool TryGetFrame(ulong key, out ReadOnlySpan<uint> frame)
    {
        frame = default;
        if (Enabled)
            for (var i = 0; i < _frames.Length; i++)
                if (_frameKey[i] == key)
                {
                    _frameUse[i] = ++_tick; FrameHits++; RasterOpsSaved += (long)_fw * _fh;
                    frame = _frames[i];
                    return true;
                }
        FrameMisses++;
        return false;
    }

    /// <summary>LRU 프레임 슬롯 확보 — 호출자가 래스터 후 <see cref="CommitFrame"/>.</summary>
    public Span<uint> AcquireFrame(ulong key)
    {
        var slot = 0; var oldest = long.MaxValue;
        for (var i = 0; i < _frames.Length; i++) if (_frameUse[i] < oldest) { oldest = _frameUse[i]; slot = i; }
        _frameKey[slot] = Enabled ? key : 0; _frameUse[slot] = ++_tick;
        return _frames[slot];
    }

    public void CommitFrame(long rasterOps) => RasterOpsSpent += rasterOps;

    /// <summary>캐시 대상이 아닌 동적 래스터(아바타 · 스플랫 등) 비용.</summary>
    public void Spend(long rasterOps) => RasterOpsSpent += rasterOps;

    // ══════════════════════ 16.7 ms 예산 가드 ══════════════════════
    public void BeginFrame() => _frameStart = System.Diagnostics.Stopwatch.GetTimestamp();

    public double EndFrame()
    {
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - _frameStart) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        Frames++; LastFrameUs = us;
        if (us > MaxFrameUs) MaxFrameUs = us;
        if (us > BudgetUs)
        {
            OverBudget++; _good = 0;
            if (++_over >= DegradeAfter && !Degraded) { Degraded = true; DegradeEntries++; }
        }
        else
        {
            _over = 0;
            if (Degraded && ++_good >= RecoverAfter) { Degraded = false; _good = 0; }
        }
        return us;
    }

    /// <summary>테스트/운영 강제 진입·복귀.</summary>
    public void ForceDegraded(bool on) { Degraded = on; _over = 0; _good = 0; }
}
