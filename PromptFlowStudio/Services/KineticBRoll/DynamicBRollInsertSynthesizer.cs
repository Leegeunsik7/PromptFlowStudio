using System.Text.RegularExpressions;
using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.VisualHooks;

namespace PromptFlowStudio.Services.KineticBRoll;

public enum BRollKind : byte { Money, Car, Ai, Explosion, Phone, Chart, Emphasis }

public enum BRollLayout : byte { TopSplit, Pip, FullCut, AngleCut }

/// <summary>키워드 적중(값 형식) — 단어 인덱스 · 종류 · 대본 문자 오프셋.</summary>
public struct BRollHit
{
    public int Word, CharStart, CharLength;
    public BRollKind Kind;
}

/// <summary>B-Roll 인서트(값 형식) — 시작/끝 ms · J 리드 · L 테일 · 종류 · 레이아웃 · 근거 단어.</summary>
public struct BRollInsert
{
    public float StartMs, EndMs, LeadMs, TailMs;
    public BRollKind Kind;
    public BRollLayout Layout;
    public int Word;
    public readonly float DurationMs => EndMs - StartMs;
}

/// <summary>B-Roll 영상 공급자 — (종류 · 클립 내 ms) 프레임을 dst(0xAARRGGBB) 에 기록(무할당 계약).</summary>
public interface IBRollSource
{
    void Render(BRollKind kind, float localMs, Span<uint> dst, int w, int h, bool lowCost);
}

/// <summary>
/// [PromptFlow Studio v17.78.0] 🎞️ AI 키워드 감응형 다이내믹 B-Roll 인젝터 합성기(<see cref="Detect"/>/<see cref="PushText"/>/<see cref="Composite"/> 무할당).
/// <b>감지</b>: 토큰(공백/구두점 분리)마다 소문자 FNV-1a 해시 → 128 칸 선형 탐사 표(문자열 재검증) · 한글 꼬리 조사/어미 1~4 자 제거 재시도("돈을" → 돈 · "폭발했습니다" → 폭발) ·
/// 금액 패턴(<c>\d+(만|억|천)?(원|달러)</c> · <c>$\d+</c>) 은 정적 컴파일 정규식 <c>IsMatch(span)</c> — 토큰당 할당 0 · 초당 수백 회 스트림 처리.
/// <b>스케줄</b>: 키워드 단어 시작 − J 리드(120 ms · 그림이 단어에 착지) ~ 1.5~2.5 s(문장 잔여 L 테일 × 강도 가중) · 직전 인서트와 600 ms 간격 · 짧아지면 기각.
/// <b>믹싱</b>: 종류별 레이아웃 — 상단 스플릿(B-Roll 상단 · 화자 얼굴 밴드 하단) · PiP 팝업(스프링 스케일 · 세이프존 클램프) · 풀 컷 · 멀티 앵글(안면 1.6× 클로즈업) —
/// 140 ms 크로스페이드 인/아웃 · 사전 할당 B-Roll/스크래치 버퍼만 사용.
/// </summary>
public sealed class DynamicBRollInsertSynthesizer
{
    public const int Cap = 64, TableSize = 128, TokenCap = 48, Kinds = 7;
    public const float MinDurMs = 1500f, MaxDurMs = 2500f, LeadMs = 120f, MinGapMs = 600f, FadeMs = 140f, PipW = 0.46f, AngleZoom = 1.6f;

    private static readonly Regex MoneyAmount = new(@"^(?:\d[\d,]*(?:만|억|천|백)?(?:원|달러|불)|\$\d[\d,.]*[kKmM]?)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly (string Word, BRollKind Kind, float Weight)[] Lexicon =
    {
        ("돈", BRollKind.Money, 1f), ("현금", BRollKind.Money, 1f), ("수익", BRollKind.Money, 0.8f), ("부자", BRollKind.Money, 0.9f), ("월급", BRollKind.Money, 0.7f), ("money", BRollKind.Money, 1f), ("cash", BRollKind.Money, 1f),
        ("자동차", BRollKind.Car, 1f), ("슈퍼카", BRollKind.Car, 1f), ("car", BRollKind.Car, 1f), ("tesla", BRollKind.Car, 1f),
        ("ai", BRollKind.Ai, 1f), ("인공지능", BRollKind.Ai, 1f), ("로봇", BRollKind.Ai, 0.8f), ("gpt", BRollKind.Ai, 1f), ("claude", BRollKind.Ai, 1f),
        ("폭발", BRollKind.Explosion, 1f), ("터졌", BRollKind.Explosion, 0.9f), ("대박", BRollKind.Explosion, 0.7f), ("boom", BRollKind.Explosion, 1f),
        ("스마트폰", BRollKind.Phone, 1f), ("아이폰", BRollKind.Phone, 1f), ("앱", BRollKind.Phone, 0.7f), ("phone", BRollKind.Phone, 1f), ("app", BRollKind.Phone, 0.8f),
        ("매출", BRollKind.Chart, 1f), ("성장", BRollKind.Chart, 0.9f), ("조회수", BRollKind.Chart, 1f), ("구독자", BRollKind.Chart, 0.9f), ("growth", BRollKind.Chart, 1f),
        ("진짜", BRollKind.Emphasis, 0.5f), ("비밀", BRollKind.Emphasis, 0.8f), ("충격", BRollKind.Emphasis, 0.9f), ("secret", BRollKind.Emphasis, 0.8f),
    };

    private static readonly ulong[] KeyTable = new ulong[TableSize];
    private static readonly short[] IdxTable = new short[TableSize];

    static DynamicBRollInsertSynthesizer()
    {
        for (var i = 0; i < Lexicon.Length; i++)
        {
            var h = Hash(Lexicon[i].Word);
            var p = (int)(h & (TableSize - 1));
            while (IdxTable[p] != 0) p = (p + 1) & (TableSize - 1);
            KeyTable[p] = h; IdxTable[p] = (short)(i + 1);
        }
    }

    private readonly BRollInsert[] _ins = new BRollInsert[Cap];
    private readonly char[] _carry = new char[TokenCap];
    private int _n, _carryLen;
    private uint[] _broll = Array.Empty<uint>(), _scratch = Array.Empty<uint>();
    private int _bw, _bh;

    public DynamicBRollInsertSynthesizer(IBRollSource? source = null) => Source = source ?? new ProceduralSplatPropBRollSource();

    public IBRollSource Source { get; set; }
    public ReelsUiSafeZoneReframingGuard? SafeZone { get; set; }
    public HookBox Face { get; set; } = new(0.36f, 0.22f, 0.28f, 0.17f);
    public int Count => _n;
    public ref readonly BRollInsert At(int i) => ref _ins[i];
    public long TokensScanned { get; private set; }
    public long KeywordHits { get; private set; }
    public long RegexHits { get; private set; }
    public long Rejected { get; private set; }
    public long StreamHits { get; private set; }
    public long Composited { get; private set; }
    public long PixelsShaded => (Source as ProceduralSplatPropBRollSource)?.PixelsShaded ?? 0;
    public double LastScanUs { get; private set; }
    public double LastCompositeUs { get; private set; }

    public static string KindName(BRollKind k) => k switch
    {
        BRollKind.Money => "돈", BRollKind.Car => "자동차", BRollKind.Ai => "AI", BRollKind.Explosion => "폭발",
        BRollKind.Phone => "스마트폰", BRollKind.Chart => "성장 차트", _ => "강조",
    };

    public static string KindIcon(BRollKind k) => k switch
    {
        BRollKind.Money => "💵", BRollKind.Car => "🏎️", BRollKind.Ai => "🤖", BRollKind.Explosion => "💥", BRollKind.Phone => "📱", BRollKind.Chart => "📈", _ => "🎥",
    };

    public static string LayoutName(BRollLayout l) => l switch { BRollLayout.TopSplit => "상단 스플릿", BRollLayout.Pip => "PiP 팝업", BRollLayout.FullCut => "풀 컷", _ => "멀티 앵글" };

    public static BRollLayout LayoutFor(BRollKind k) => k switch
    {
        BRollKind.Money or BRollKind.Phone => BRollLayout.Pip,
        BRollKind.Car or BRollKind.Ai or BRollKind.Chart => BRollLayout.TopSplit,
        BRollKind.Explosion => BRollLayout.FullCut,
        _ => BRollLayout.AngleCut,
    };

    private static ulong Hash(ReadOnlySpan<char> s)
    {
        var h = 14695981039346656037UL;
        for (var i = 0; i < s.Length; i++) { h ^= char.ToLowerInvariant(s[i]); h *= 1099511628211UL; }
        return h;
    }

    private static int Lookup(ReadOnlySpan<char> s)
    {
        var h = Hash(s);
        var p = (int)(h & (TableSize - 1));
        for (var k = 0; k < TableSize; k++)
        {
            var e = IdxTable[(p + k) & (TableSize - 1)];
            if (e == 0) return -1;
            if (KeyTable[(p + k) & (TableSize - 1)] == h && s.Equals(Lexicon[e - 1].Word.AsSpan(), StringComparison.OrdinalIgnoreCase)) return e - 1;
        }
        return -1;
    }

    private static bool IsHangul(char c) => c is >= '가' and <= '힣';

    private static bool IsSep(char c) => char.IsWhiteSpace(c) || c is '.' or ',' or '!' or '?' or '"' or '\'' or '(' or ')' or '~' or '…' or '·';

    /// <summary>토큰 분류(무할당) — 금액 정규식 → 해시(원형 · 한글 조사/어미 1~4 자 제거).</summary>
    public bool TryClassify(ReadOnlySpan<char> token, out BRollKind kind, out float weight)
    {
        kind = BRollKind.Emphasis; weight = 0;
        TokensScanned++;
        if (token.IsEmpty) return false;
        if ((char.IsDigit(token[0]) || token[0] == '$') && MoneyAmount.IsMatch(token)) { kind = BRollKind.Money; weight = 1f; RegexHits++; return true; }
        var e = Lookup(token);
        for (var strip = 1; e < 0 && strip <= 4 && token.Length - strip >= 1 && IsHangul(token[token.Length - strip]); strip++) e = Lookup(token[..^strip]);
        if (e < 0) return false;
        kind = Lexicon[e].Kind; weight = Lexicon[e].Weight; KeywordHits++;
        return true;
    }

    /// <summary>대본 일괄 감지 — hits 에 기록 · 반환 = 적중 수.</summary>
    public int Detect(ReadOnlySpan<char> text, Span<BRollHit> hits)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = 0; var word = 0;
        for (var i = 0; i < text.Length;)
        {
            while (i < text.Length && IsSep(text[i])) i++;
            var s = i;
            while (i < text.Length && !IsSep(text[i])) i++;
            if (i == s) break;
            if (TryClassify(text[s..i], out var k, out _) && n < hits.Length) hits[n++] = new BRollHit { Word = word, CharStart = s, CharLength = i - s, Kind = k };
            word++;
        }
        LastScanUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return n;
    }

    /// <summary>실시간 스트림 조각 투입(조각 경계에 걸친 토큰은 고정 버퍼로 이월) — 확정 토큰 적중 시 nowMs 기준 인서트 예약. 반환 = 신규 인서트 수.</summary>
    public int PushText(ReadOnlySpan<char> chunk, float nowMs)
    {
        var added = 0;
        for (var i = 0; i < chunk.Length; i++)
        {
            var c = chunk[i];
            if (!IsSep(c)) { if (_carryLen < TokenCap) _carry[_carryLen++] = c; continue; }
            if (_carryLen > 0 && FlushToken(nowMs)) added++;
        }
        return added;
    }

    /// <summary>스트림 종료 — 남은 토큰 확정.</summary>
    public int EndStream(float nowMs) => _carryLen > 0 && FlushToken(nowMs) ? 1 : 0;

    private bool FlushToken(float nowMs)
    {
        var ok = TryClassify(_carry.AsSpan(0, _carryLen), out var k, out var wgt);
        _carryLen = 0;
        if (!ok) return false;
        StreamHits++;
        return Schedule(-1, nowMs + LeadMs, MaxDurMs, k, wgt);
    }

    public void Clear() { _n = 0; _carryLen = 0; }

    /// <summary>v17.52 타임라인 단어 → 키워드 인서트 일괄 예약(J 리드 · 1.5~2.5 s · L 테일). 반환 = 예약 수.</summary>
    public int ScheduleFromTimeline(RealtimeWordByWordMotionTypographyEngine timeline)
    {
        var added = 0;
        for (var i = 0; i < timeline.WordCount; i++)
        {
            if (!TryClassify(Trim(timeline.WordText(i)), out var k, out var wgt)) continue;
            ref readonly var w = ref timeline.Word(i);
            var sentenceEnd = timeline.TotalMs;
            for (var j = i; j < timeline.WordCount; j++)
            {
                var t = timeline.WordText(j);
                if (t.Length > 0 && t[^1] is '.' or '!' or '?') { sentenceEnd = timeline.Word(j).EndMs; break; }
            }
            if (Schedule(i, w.StartMs, sentenceEnd - w.StartMs + LeadMs, k, wgt)) added++;
        }
        return added;
    }

    private static ReadOnlySpan<char> Trim(ReadOnlySpan<char> s)
    {
        var a = 0; var b = s.Length;
        while (a < b && IsSep(s[a])) a++;
        while (b > a && IsSep(s[b - 1])) b--;
        return s[a..b];
    }

    /// <summary>인서트 예약(정렬 삽입) — 시작 = 단어 − J 리드 · 길이 = 1.5 s + (문장 잔여 − 1.5 s) × (0.5 + 0.5 × 강도) ≤ 2.5 s · 간격/겹침 위반 시 단축 · 1.5 s 미만이면 기각.</summary>
    public bool Schedule(int word, float wordStartMs, float sentenceMs, BRollKind kind, float weight)
    {
        var start = MathF.Max(0f, wordStartMs - LeadMs);
        // 문장 잔여(L 테일 상한) × 강도 가중(0.5~1.0) — 짧은 문장 · 약한 키워드는 1.5 s 쪽으로
        var dur = MinDurMs + (Math.Clamp(sentenceMs, MinDurMs, MaxDurMs) - MinDurMs) * (0.5f + 0.5f * Math.Clamp(weight, 0f, 1f));
        var pos = _n;
        while (pos > 0 && _ins[pos - 1].StartMs > start) pos--;
        if (pos > 0 && _ins[pos - 1].EndMs + MinGapMs > start) { Rejected++; return false; }
        if (pos < _n) dur = MathF.Min(dur, _ins[pos].StartMs - MinGapMs - start);
        if (dur < MinDurMs - 0.5f || _n == Cap) { Rejected++; return false; }
        Array.Copy(_ins, pos, _ins, pos + 1, _n - pos);
        var lead = wordStartMs - start;
        _ins[pos] = new BRollInsert { StartMs = start, EndMs = start + dur, LeadMs = lead, TailMs = MathF.Max(0f, start + dur - wordStartMs - 400f), Kind = kind, Layout = LayoutFor(kind), Word = word };
        _n++;
        return true;
    }

    /// <summary>시각 → 활성 인서트(이진 탐색) · 없으면 −1.</summary>
    public int ActiveAt(float tMs)
    {
        int lo = 0, hi = _n - 1, k = -1;
        while (lo <= hi) { var m = (lo + hi) >> 1; if (_ins[m].StartMs <= tMs) { k = m; lo = m + 1; } else hi = m - 1; }
        return k >= 0 && tMs < _ins[k].EndMs ? k : -1;
    }

    /// <summary>크로스페이드 엔벌로프(0..1).</summary>
    public static float Envelope(in BRollInsert b, float tMs)
    {
        var a = Math.Clamp((tMs - b.StartMs) / FadeMs, 0f, 1f);
        var z = Math.Clamp((b.EndMs - tMs) / FadeMs, 0f, 1f);
        return MathF.Min(a, z);
    }

    /// <summary>해상도별 B-Roll/스크래치 버퍼(크기 변경 시에만 할당).</summary>
    public void Configure(int w, int h)
    {
        if (w == _bw && h == _bh) return;
        _broll = new uint[w * h]; _scratch = new uint[w * h]; _bw = w; _bh = h;
    }

    /// <summary>제자리 합성(0xAARRGGBB) — 반환 = 활성 인서트 인덱스(−1 = 원샷 유지).</summary>
    public int Composite(Span<uint> frame, int w, int h, float tMs, bool lowCost = false)
    {
        var k = ActiveAt(tMs);
        if (k < 0 || _bw != w || _bh != h) return -1;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ref readonly var b = ref _ins[k];
        var env = Envelope(in b, tMs);
        var a8 = (uint)(env * 255f + 0.5f);
        var local = tMs - b.StartMs;
        switch (b.Layout)
        {
            case BRollLayout.FullCut:
                Source.Render(b.Kind, local, _broll, w, h, lowCost);
                BlendRect(frame, w, _broll, w, 0, 0, w, h, 0, 0, a8);
                break;
            case BRollLayout.TopSplit:
            {
                var half = h / 2;
                // 하단 = 화자 얼굴 밴드(안면 중심 기준 원본 절반 높이) · 상단 = B-Roll 상단 절반(슬라이드 인)
                frame.CopyTo(_scratch);
                var fy = (int)(Face.Cy * h) - half / 2;
                fy = Math.Clamp(fy, 0, h - half);
                Source.Render(b.Kind, local, _broll, w, h, lowCost);
                var slide = (int)((1f - env) * half);
                BlendRect(frame, w, _scratch, w, 0, fy, w, half, 0, half, a8);
                BlendRect(frame, w, _broll, w, 0, h / 4 + slide, w, half - slide, 0, 0, a8);
                for (var x = 0; x < w; x++) frame[half * w + x] = Lerp(frame[half * w + x], 0xFFFFFFFFu, a8);
                break;
            }
            case BRollLayout.Pip:
            {
                Source.Render(b.Kind, local, _broll, w, h, lowCost);
                var pop = MathF.Min(1f, ProceduralSplatPropBRollSource.Spring(local));   // 스프링 팝(오버슈트는 상자 상한으로 절삭)
                var pw = (int)(PipW * w * pop); var ph = (int)(pw * 0.75f);
                if (pw < 4 || ph < 4) break;
                var box = HookBox.Centered(0.70f, 0.30f, PipW, PipW * 0.75f * w / h);
                SafeZone?.Clamp(ref box);
                var cx = (int)(box.Cx * w); var cy = (int)(box.Cy * h);
                var x0 = cx - pw / 2; var y0 = cy - ph / 2;
                ScaleBlend(frame, w, h, _broll, w, h, x0, y0, pw, ph, a8);
                Border(frame, w, h, x0, y0, pw, ph, a8);
                break;
            }
            default:
            {
                // 멀티 앵글: 안면 중심 1.6× 클로즈업 크롭(최근접) — 원샷 → 타이트 앵글 컷
                frame.CopyTo(_scratch);
                var cw = w / AngleZoom; var ch = h / AngleZoom;
                var ox = Math.Clamp(Face.Cx * w - cw * 0.5f, 0f, w - cw); var oy = Math.Clamp(Face.Cy * h - ch * 0.42f, 0f, h - ch);
                for (var y = 0; y < h; y++)
                {
                    var sy = Math.Min(h - 1, (int)(oy + y / AngleZoom));
                    var row = frame.Slice(y * w, w);
                    for (var x = 0; x < w; x++) row[x] = Lerp(_scratch[y * w + x], _scratch[sy * w + Math.Min(w - 1, (int)(ox + x / AngleZoom))], a8);
                }
                break;
            }
        }
        Composited++;
        LastCompositeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return k;
    }

    private static void BlendRect(Span<uint> dst, int dw, ReadOnlySpan<uint> src, int sw, int sx, int sy, int cw, int ch, int dx, int dy, uint a8)
    {
        for (var y = 0; y < ch; y++)
        {
            var s = src.Slice((sy + y) * sw + sx, cw); var d = dst.Slice((dy + y) * dw + dx, cw);
            if (a8 >= 255) { s.CopyTo(d); continue; }
            for (var x = 0; x < cw; x++) d[x] = Lerp(d[x], s[x], a8);
        }
    }

    private static void ScaleBlend(Span<uint> dst, int w, int h, ReadOnlySpan<uint> src, int sw, int sh, int x0, int y0, int pw, int ph, uint a8)
    {
        // 소스 중앙 4:3 영역을 pw×ph 로 최근접 축소
        var srcH = (int)(sw * 0.75f); var srcY = (sh - srcH) / 2;
        for (var y = Math.Max(0, y0); y < Math.Min(h, y0 + ph); y++)
        {
            var sy = srcY + (y - y0) * srcH / ph;
            var row = dst.Slice(y * w, w);
            for (var x = Math.Max(0, x0); x < Math.Min(w, x0 + pw); x++) row[x] = Lerp(row[x], src[sy * sw + (x - x0) * sw / pw], a8);
        }
    }

    private static void Border(Span<uint> dst, int w, int h, int x0, int y0, int pw, int ph, uint a8)
    {
        for (var t = 0; t < 2; t++)
        {
            for (var x = Math.Max(0, x0 - t); x < Math.Min(w, x0 + pw + t); x++)
            {
                if ((uint)(y0 - 1 - t) < (uint)h) dst[(y0 - 1 - t) * w + x] = Lerp(dst[(y0 - 1 - t) * w + x], 0xFFFFFFFFu, a8);
                if ((uint)(y0 + ph + t) < (uint)h) dst[(y0 + ph + t) * w + x] = Lerp(dst[(y0 + ph + t) * w + x], 0xFFFFFFFFu, a8);
            }
            for (var y = Math.Max(0, y0 - t); y < Math.Min(h, y0 + ph + t); y++)
            {
                if ((uint)(x0 - 1 - t) < (uint)w) dst[y * w + x0 - 1 - t] = Lerp(dst[y * w + x0 - 1 - t], 0xFFFFFFFFu, a8);
                if ((uint)(x0 + pw + t) < (uint)w) dst[y * w + x0 + pw + t] = Lerp(dst[y * w + x0 + pw + t], 0xFFFFFFFFu, a8);
            }
        }
    }

    /// <summary>a8(0..255) 채널별 보간.</summary>
    public static uint Lerp(uint a, uint b, uint a8)
    {
        if (a8 == 0) return a;
        if (a8 >= 255) return b;
        var ia = 255u - a8;
        var r = (((a >> 16) & 0xFF) * ia + ((b >> 16) & 0xFF) * a8 + 127) / 255;
        var g = (((a >> 8) & 0xFF) * ia + ((b >> 8) & 0xFF) * a8 + 127) / 255;
        var bl = ((a & 0xFF) * ia + (b & 0xFF) * a8 + 127) / 255;
        return 0xFF000000u | r << 16 | g << 8 | bl;
    }
}

/// <summary>
/// v17.78.0 인메모리 3DGS 소품 클로즈업 B-Roll(디스크 I/O 0) — 종류별 결정적 가우시안 64 개(돈다발 · 자동차 · AI 칩 · 폭발 구각 · 스마트폰 · 성장 막대 · 스튜디오 스포트)를
/// Y 축 궤도 회전(0.9 rad/s) + 돌리 인(카메라 3.4 → 2.6) 원근 투영 → 깊이 삽입 정렬(사전 할당) 뒤에서 앞으로 등방 가우시안 스플랫(α = 0.85·e^(−r²/2σ²)).
/// </summary>
public sealed class ProceduralSplatPropBRollSource : IBRollSource
{
    public const int Splats = 64;
    private readonly float[][] _px = new float[DynamicBRollInsertSynthesizer.Kinds][], _py = new float[DynamicBRollInsertSynthesizer.Kinds][], _pz = new float[DynamicBRollInsertSynthesizer.Kinds][], _ps = new float[DynamicBRollInsertSynthesizer.Kinds][];
    private readonly uint[][] _pc = new uint[DynamicBRollInsertSynthesizer.Kinds][];
    private readonly float[] _sx = new float[Splats], _sy = new float[Splats], _sr = new float[Splats], _sz = new float[Splats];
    private readonly int[] _order = new int[Splats];

    public ProceduralSplatPropBRollSource()
    {
        var rng = 0x1778u;
        float R() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return (rng & 0xFFFFFF) / 16777216f; }
        for (var k = 0; k < DynamicBRollInsertSynthesizer.Kinds; k++)
        {
            _px[k] = new float[Splats]; _py[k] = new float[Splats]; _pz[k] = new float[Splats]; _ps[k] = new float[Splats]; _pc[k] = new uint[Splats];
            for (var i = 0; i < Splats; i++)
            {
                float x, y, z, s; uint c;
                switch ((BRollKind)k)
                {
                    case BRollKind.Money:   // 지폐 다발 3 층 × 격자
                        x = (i % 8 - 3.5f) * 0.16f; y = (i / 8 % 3 - 1) * 0.12f + (i / 24) * 0.05f; z = (i / 8 % 2 - 0.5f) * 0.3f; s = 0.11f;
                        c = (i & 3) == 0 ? 0xFFBBF7D0u : 0xFF22C55Eu; break;
                    case BRollKind.Car:     // 차체 타원 + 바퀴 4
                        if (i < 8) { x = (i % 2 == 0 ? -0.45f : 0.45f); y = -0.22f; z = (i / 2 % 2 == 0 ? -0.25f : 0.25f); s = 0.12f; c = 0xFF111827u; }
                        else { var a = R() * 6.2832f; x = MathF.Cos(a) * 0.62f; z = MathF.Sin(a) * 0.28f; y = R() * 0.22f - 0.08f; s = 0.10f; c = y > 0.08f ? 0xFF93C5FDu : 0xFFDC2626u; }
                        break;
                    case BRollKind.Ai:      // 칩 격자 + 핀
                        x = (i % 8 - 3.5f) * 0.11f; z = (i / 8 - 3.5f) * 0.11f; y = 0f; s = 0.07f;
                        c = (i % 8 is 0 or 7) || (i / 8 is 0 or 7) ? 0xFF67E8F9u : ((i % 8 + i / 8) & 1) == 0 ? 0xFF1E3A8Au : 0xFF6366F1u; break;
                    case BRollKind.Explosion:   // 구각(시간 팽창은 렌더에서)
                    {
                        var u = R() * 2 - 1; var a = R() * 6.2832f; var r = MathF.Sqrt(1 - u * u);
                        var rad = 0.35f + R() * 0.25f; x = r * MathF.Cos(a) * rad; y = u * rad; z = r * MathF.Sin(a) * rad; s = 0.14f;
                        c = rad > 0.5f ? 0xFFF97316u : rad > 0.42f ? 0xFFFACC15u : 0xFFFEF3C7u; break;
                    }
                    case BRollKind.Phone:   // 세로 판 + 화면
                        x = (i % 4 - 1.5f) * 0.12f; y = (i / 4 - 7.5f) * 0.06f; z = 0f; s = 0.08f;
                        c = (i % 4 is 0 or 3) || i / 4 is 0 or 15 ? 0xFF0F172Au : 0xFF38BDF8u; break;
                    case BRollKind.Chart:   // 막대 4 개(점증)
                    {
                        var bar = i % 4; var lvl = i / 4;
                        x = (bar - 1.5f) * 0.28f; y = -0.5f + lvl * 0.07f; z = 0f; s = 0.09f;
                        if (lvl > 3 + bar * 4) { y = -2f; s = 0f; }
                        c = bar == 3 ? 0xFF4ADE80u : 0xFF22C55Eu; break;
                    }
                    default:                // 스튜디오 스포트
                        x = (R() - 0.5f) * 1.2f; y = (R() - 0.5f) * 1.2f; z = (R() - 0.5f) * 1.2f; s = 0.12f; c = 0xFFF8FAFCu; break;
                }
                _px[k][i] = x; _py[k][i] = y; _pz[k][i] = z; _ps[k][i] = s; _pc[k][i] = c;
            }
        }
    }

    public long SplatsDrawn { get; private set; }
    public long PixelsShaded { get; private set; }

    public static float Spring(float ms)
    {
        if (ms <= 0) return 0f;
        var t = ms / 1000.0; const double W = 2 * Math.PI * 4.0, Z = 0.45; var wd = W * Math.Sqrt(1 - Z * Z);
        return (float)(1 - Math.Exp(-Z * W * t) * (Math.Cos(wd * t) + Z * W / wd * Math.Sin(wd * t)));
    }

    public void Render(BRollKind kind, float localMs, Span<uint> dst, int w, int h, bool lowCost)
    {
        var k = (int)kind;
        // 배경: 종류별 어두운 스튜디오 그라디언트
        var tint = _pc[k][Splats / 2];
        for (var y = 0; y < h; y++)
        {
            var f = 0.10f + 0.12f * (1f - y / (float)h);
            var c = 0xFF000000u | (uint)(((tint >> 16) & 0xFF) * f) << 16 | (uint)(((tint >> 8) & 0xFF) * f) << 8 | (uint)((tint & 0xFF) * f);
            dst.Slice(y * w, w).Fill(c);
        }
        var t = localMs / 1000f;
        var ang = t * 0.9f + 0.4f; var ca = MathF.Cos(ang); var sa = MathF.Sin(ang);
        var cam = 3.4f - 0.8f * Math.Clamp(t / 2.5f, 0f, 1f);
        var grow = kind == BRollKind.Explosion ? 0.6f + 1.4f * Math.Clamp(t / 0.8f, 0f, 1f) : 1f;
        var focal = w * 1.9f;
        var n = 0;
        var step = lowCost ? 2 : 1;
        for (var i = 0; i < Splats; i += step)
        {
            if (_ps[k][i] <= 0) continue;
            var x = _px[k][i] * grow; var y = _py[k][i] * grow; var z = _pz[k][i] * grow;
            var rx = x * ca + z * sa; var rz = -x * sa + z * ca + cam;
            if (rz < 0.3f) continue;
            _sx[n] = w * 0.5f + rx / rz * focal; _sy[n] = h * 0.5f - y / rz * focal; _sr[n] = _ps[k][i] * grow / rz * focal * 0.5f; _sz[n] = rz; _order[n] = i;
            // 깊이 내림차순 삽입 정렬(뒤 → 앞)
            var j = n++;
            while (j > 0 && _sz[j - 1] < _sz[j])
            {
                (_sz[j - 1], _sz[j]) = (_sz[j], _sz[j - 1]); (_sx[j - 1], _sx[j]) = (_sx[j], _sx[j - 1]); (_sy[j - 1], _sy[j]) = (_sy[j], _sy[j - 1]);
                (_sr[j - 1], _sr[j]) = (_sr[j], _sr[j - 1]); (_order[j - 1], _order[j]) = (_order[j], _order[j - 1]);
                j--;
            }
        }
        long shaded = 0;
        for (var q = 0; q < n; q++)
        {
            var sig = MathF.Max(_sr[q], 0.8f); var rad = (int)MathF.Ceiling(sig * 2.5f);
            var c = _pc[k][_order[q]];
            int x0 = Math.Max(0, (int)_sx[q] - rad), x1 = Math.Min(w - 1, (int)_sx[q] + rad), y0 = Math.Max(0, (int)_sy[q] - rad), y1 = Math.Min(h - 1, (int)_sy[q] + rad);
            var inv2 = 1f / (2f * sig * sig);
            for (var y = y0; y <= y1; y++)
            {
                var dy = y + 0.5f - _sy[q];
                var row = dst.Slice(y * w, w);
                for (var x = x0; x <= x1; x++)
                {
                    var dx = x + 0.5f - _sx[q];
                    var a = 0.85f * MathF.Exp(-(dx * dx + dy * dy) * inv2);
                    if (a < 0.01f) continue;
                    row[x] = DynamicBRollInsertSynthesizer.Lerp(row[x], c, (uint)(a * 255f));
                    shaded++;
                }
            }
        }
        SplatsDrawn += n; PixelsShaded += shaded + (long)w * h;
    }
}
