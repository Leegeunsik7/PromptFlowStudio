namespace PromptFlowStudio.Services.StationMaster;

public enum StationProgramKind : byte { Talkshow, LiveCommerce, RhythmConcert, ReelsReview, Ambient }
public enum EpgEvent : byte { None, Preroll, Take, Extended }

/// <summary>v17.70.0 편성 슬롯(값 형식 · 절대 분 · 링 재순환).</summary>
public struct EpgSlot
{
    public long StartMin;
    public int DurMin, Extensions;
    public StationProgramKind Kind, AltKind, Aired;
    /// <summary>시청자 트래픽(동시 접속 지표) 하한 — 미달 시 대체 편성(<see cref="AltKind"/>).</summary>
    public float MinTraffic;
    public short Title;
    public readonly long EndMin => StartMin + DurMin;
}

/// <summary>
/// [PromptFlow Studio v17.70.0] 📡 24/7 무인 자동화 마스터 EPG 편성 스케줄러 엔진(무할당 · 고정 링).
///   ▸ 24 h 타임테이블(토크쇼 · 라이브 커머스 · 리듬 액션 콘서트 · 릴스 리뷰 · 앰비언트) = 절대 분 슬롯 링(Capacity 96) — 슬롯 종료 시 헤드 전진 + 같은 슬롯을 +1440 분으로 꼬리 재삽입(무한 24/7 순환).
///   ▸ 방송국 시계(ms · 배속) <see cref="Step"/> → 다음 슬롯 시작 <see cref="PrerollMs"/> 전 = Preroll(트래픽 판정 · 대체 편성 결정 · PVW 준비) → 경계 = Take(MCR 디졸브).
///   ▸ 트래픽 적응: 프리롤 시 트래픽 &lt; MinTraffic → AltKind · 현재 프로그램 트래픽 ≥ <see cref="ExtendTraffic"/> → 다음 슬롯 앞 15 분 잠식 연장(슬롯당 1 회 · 다음 슬롯 ≥ 15 분 유지).
/// </summary>
public sealed class MasterStationEpgSchedulerEngine
{
    public const int Capacity = 96, DayMin = 1440, ExtendMin = 15, Kinds = 5;

    private readonly EpgSlot[] _ring = new EpgSlot[Capacity];
    private readonly long[] _airedBy = new long[Kinds];
    private int _head, _count;
    private long _nowMs;
    private bool _prerolled;
    private StationProgramKind _prerollKind;

    public static readonly string[] Titles =
    {
        "모닝 토크 라이브", "브런치 라이브 커머스", "점심 리듬 액션 콘서트", "오후 바이럴 릴스 리뷰", "골든 타임 토크쇼", "프라임 라이브 커머스",
        "심야 리듬 액션 콘서트", "주간 릴스 하이라이트", "새벽 앰비언트 라운지", "스페셜 라이브 커머스", "토크쇼 재방송", "릴스 결산",
    };

    public long PrerollMs { get; set; } = 2_000;
    public float ExtendTraffic { get; set; } = 0.9f;
    public int Count => _count;
    public long NowMs => _nowMs;
    public long NowMin => _nowMs / 60_000;
    public int MinuteOfDay => (int)(NowMin % DayMin);
    public ref readonly EpgSlot Current => ref _ring[_head];
    public ref readonly EpgSlot Next => ref _ring[(_head + 1) % Capacity];
    /// <summary>현재부터 i 번째 슬롯(0 = 방송 중).</summary>
    public ref readonly EpgSlot At(int i) => ref _ring[(_head + i) % Capacity];
    public StationProgramKind OnAir { get; private set; } = StationProgramKind.Ambient;
    public StationProgramKind PrerollKind => _prerollKind;
    public bool Prerolled => _prerolled;
    public long Takes { get; private set; }
    public long Prerolls { get; private set; }
    public long AltSwaps { get; private set; }
    public long Extensions { get; private set; }
    public long Rotations { get; private set; }
    public long DaysRolled => _count > 0 ? Rotations / _count : 0;
    public long Steps { get; private set; }
    public long AiredBy(StationProgramKind k) => _airedBy[(int)k];
    public double RemainingMs => Math.Max(0, Current.EndMin * 60_000 - _nowMs);
    public float Progress => Current.DurMin > 0 ? (float)Math.Clamp((_nowMs - Current.StartMin * 60_000) / (Current.DurMin * 60_000.0), 0, 1) : 0f;

    public static string KindName(StationProgramKind k) => k switch
    {
        StationProgramKind.Talkshow => "토크쇼", StationProgramKind.LiveCommerce => "라이브 커머스", StationProgramKind.RhythmConcert => "리듬 액션 콘서트",
        StationProgramKind.ReelsReview => "릴스 리뷰", _ => "앰비언트",
    };

    public static string KindIcon(StationProgramKind k) => k switch
    {
        StationProgramKind.Talkshow => "🎙️", StationProgramKind.LiveCommerce => "🛍️", StationProgramKind.RhythmConcert => "🎵", StationProgramKind.ReelsReview => "🎞️", _ => "🌌",
    };

    // ══════════════════════ 편성(비핫패스) ══════════════════════
    /// <summary>기본 24 h 편성(12 블록 · 합 1440 분) 로드 → 방송국 시계 = startMinuteOfDay.</summary>
    public void LoadDefaultDay(int startMinuteOfDay = 0)
    {
        Span<(int Dur, StationProgramKind K, StationProgramKind Alt, float Min, short T)> d = stackalloc (int, StationProgramKind, StationProgramKind, float, short)[]
        {
            (120, StationProgramKind.Talkshow, StationProgramKind.Ambient, 0.10f, 0),        // 06:00
            (120, StationProgramKind.LiveCommerce, StationProgramKind.Talkshow, 0.25f, 1),  // 08:00
            (90, StationProgramKind.RhythmConcert, StationProgramKind.ReelsReview, 0.20f, 2), // 10:00
            (90, StationProgramKind.ReelsReview, StationProgramKind.Talkshow, 0.10f, 3),     // 11:30
            (120, StationProgramKind.Talkshow, StationProgramKind.ReelsReview, 0.15f, 4),    // 13:00
            (120, StationProgramKind.LiveCommerce, StationProgramKind.Talkshow, 0.30f, 5),   // 15:00
            (120, StationProgramKind.RhythmConcert, StationProgramKind.ReelsReview, 0.25f, 6), // 17:00
            (120, StationProgramKind.ReelsReview, StationProgramKind.Talkshow, 0.10f, 7),    // 19:00
            (120, StationProgramKind.LiveCommerce, StationProgramKind.ReelsReview, 0.35f, 9), // 21:00
            (120, StationProgramKind.Talkshow, StationProgramKind.Ambient, 0.15f, 10),       // 23:00
            (240, StationProgramKind.Ambient, StationProgramKind.Ambient, 0f, 8),            // 01:00
            (60, StationProgramKind.ReelsReview, StationProgramKind.Ambient, 0.05f, 11),     // 05:00
        };
        _count = 0; _head = 0;
        var at = 6 * 60L;
        for (var i = 0; i < d.Length; i++) { Append(at, d[i].Dur, d[i].K, d[i].Alt, d[i].Min, d[i].T); at += d[i].Dur; }
        Seek(startMinuteOfDay);
        Rotations = 0;
    }

    /// <summary>슬롯 추가(꼬리 · 연속성 = 호출자 책임) — 링 포화 시 false.</summary>
    public bool Append(long startMin, int durMin, StationProgramKind kind, StationProgramKind alt, float minTraffic, short title)
    {
        if (_count >= Capacity || durMin <= 0) return false;
        ref var s = ref _ring[(_head + _count) % Capacity];
        s = default; s.StartMin = startMin; s.DurMin = durMin; s.Kind = kind; s.AltKind = alt; s.MinTraffic = minTraffic; s.Title = title; s.Aired = kind;
        _count++;
        return true;
    }

    /// <summary>방송국 시계를 그날의 분으로 이동(편성 시작 시각 기준 회전) — 해당 슬롯을 방송 중으로.</summary>
    public void Seek(int minuteOfDay)
    {
        if (_count == 0) return;
        var first = _ring[_head].StartMin;
        var target = first + ((minuteOfDay - first % DayMin) % DayMin + DayMin) % DayMin;
        _nowMs = target * 60_000;
        var guard = Capacity * 2;
        while (_ring[_head].EndMin <= target && guard-- > 0) Rotate();
        _prerolled = false;
        OnAir = _ring[_head].Kind;
        _ring[_head].Aired = OnAir;
    }

    public void SetClockMs(long ms) { _nowMs = ms; }

    private void Rotate()
    {
        var s = _ring[_head];
        _head = (_head + 1) % Capacity;
        s.StartMin += DayMin; s.DurMin -= ExtendMin * s.Extensions; s.Extensions = 0; s.Aired = s.Kind;   // 연장 원복
        // 연장으로 잠식된 시작/길이 원복: 다음 날 같은 시각 그리드 유지(직전 슬롯 종료 = 시작)
        var prevEnd = _ring[(_head + _count - 2 + Capacity) % Capacity].EndMin;
        if (_count > 1 && prevEnd != s.StartMin) { s.DurMin += (int)(s.StartMin - prevEnd); s.StartMin = prevEnd; }
        _ring[(_head + _count - 1) % Capacity] = s;
        Rotations++;
    }

    // ══════════════════════ 틱(무할당) ══════════════════════
    /// <summary>방송국 시계 진행(dtMs · 배속 반영 후) + 현재 트래픽(0~1) → 이벤트.</summary>
    public EpgEvent Step(long dtMs, float traffic)
    {
        Steps++;
        if (_count == 0) return EpgEvent.None;
        _nowMs += dtMs;
        ref var cur = ref _ring[_head];
        // 경계 → Take(배속으로 프리롤을 건너뛴 경우 같은 틱에 판정)
        if (_nowMs >= cur.EndMin * 60_000)
        {
            if (!_prerolled) Decide(traffic);
            Rotate();
            ref var now = ref _ring[_head];
            now.Aired = _prerollKind;
            OnAir = _prerollKind; _airedBy[(int)OnAir]++;
            _prerolled = false; Takes++;
            return EpgEvent.Take;
        }
        // 연장(현재 트래픽 과열 · 슬롯당 1 회 · 프리롤 전)
        if (!_prerolled && traffic >= ExtendTraffic && cur.Extensions == 0 && OnAir != StationProgramKind.Ambient && _count > 1)
        {
            ref var nx = ref _ring[(_head + 1) % Capacity];
            if (nx.DurMin >= 2 * ExtendMin && cur.EndMin * 60_000 - _nowMs <= PrerollMs + 60_000)
            {
                cur.DurMin += ExtendMin; cur.Extensions++; nx.StartMin += ExtendMin; nx.DurMin -= ExtendMin; Extensions++;
                return EpgEvent.Extended;
            }
        }
        if (!_prerolled && _nowMs >= cur.EndMin * 60_000 - PrerollMs) { Decide(traffic); return EpgEvent.Preroll; }
        return EpgEvent.None;
    }

    private void Decide(float traffic)
    {
        ref readonly var nx = ref _ring[(_head + 1) % Capacity];
        _prerollKind = traffic < nx.MinTraffic ? nx.AltKind : nx.Kind;
        if (_prerollKind != nx.Kind) AltSwaps++;
        _prerolled = true; Prerolls++;
    }

    /// <summary>편성 총 분(링 전 슬롯 합) — 기본 편성 = 1440.</summary>
    public int TotalMinutes() { var t = 0; for (var i = 0; i < _count; i++) t += _ring[(_head + i) % Capacity].DurMin; return t; }

    /// <summary>링 연속성(슬롯 i 종료 = i+1 시작) 검증.</summary>
    public bool Contiguous() { for (var i = 0; i + 1 < _count; i++) if (At(i).EndMin != At(i + 1).StartMin) return false; return true; }
}
