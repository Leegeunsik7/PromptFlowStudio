using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.SmartPublishing;

namespace PromptFlowStudio.Services.VisualHooks;

public enum StickerKind : byte { Money, Exclaim, Skull, Fire, Heart }

/// <summary>스티커 인스턴스(값 형식) — 종류 · 기준 박스(배율 1) · 탄생 프레임.</summary>
public struct StickerInstance
{
    public StickerKind Kind;
    public HookBox Box;
    public long BornFrame;
    public bool Live;
}

/// <summary>
/// [PromptFlow Studio v17.77.0] ✨ 절차적 벡터 스티커 &amp; 리액션 오버레이 합성기(<see cref="Render"/> 무할당 · 디스크 I/O 0).
/// 이미지 파일 없이 3차 베지에 윤곽(원 = 4 호 κ 0.5523)을 16 분할 평탄화 → 4× 서브 스캔라인 non-zero 와인딩 래스터라이저(가로 해석적 부분 피복)로
/// 돈다발 · 느낌표 · 해골 · 불꽃 · 하트 5 종 96² 스프라이트를 생성자에서 1 회 합성한다.
/// 등장 모션은 감쇠 스프링(ω 3.2 Hz · ζ 0.32) 계단 응답 해석해 s(t) = 1 − e^(−ζωt)(cos ω_d t + ζω/ω_d · sin ω_d t) 로
/// 프레임 번호만으로 결정적(스크럽 가능)이며 ≈ 1.35× 오버슈트 후 정착 · 감쇠 회전 흔들림 · 마지막 12 프레임 페이드.
/// 배치 박스는 오버슈트 피크 크기로 <see cref="ReelsUiSafeZoneReframingGuard"/> 에 통과시켜 UI 세이프존을 침범하지 않는다.
/// </summary>
public sealed class ProceduralVectorStickerOverlaySynthesizer
{
    public const int Size = 96, Kinds = 5, Cap = 16, LifeFrames = 72, FadeFrames = 12, Fps = 60, Flatten = 16, SubRows = 4;
    public const double OmegaHz = 3.2, Zeta = 0.32;
    public const float WobbleDeg = 12f;
    /// <summary>스프링 계단 응답 최대 오버슈트 배율(해석해).</summary>
    public static readonly float PeakOvershoot = (float)(1 + Math.Exp(-Math.PI * Zeta / Math.Sqrt(1 - Zeta * Zeta)));

    private static readonly string[][] Keywords =
    {
        new[] { "돈", "대박", "부자", "수익", "money", "cash", "$", "₩", "💰" },
        new[] { "!", "진짜", "헐", "실화", "충격", "wow", "omg" },
        new[] { "ㅋㅋ", "미쳤", "죽", "lol", "skull", "💀" },
        new[] { "불", "핫", "레전드", "fire", "hot", "🔥" },
        new[] { "사랑", "좋아", "귀여", "love", "❤" },
    };

    private readonly uint[][] _sprite = new uint[Kinds][];
    private readonly StickerInstance[] _inst = new StickerInstance[Cap];
    private int _next;
    // 래스터라이저 작업 버퍼(생성자 전용)
    private float[] _ex0 = new float[1024], _ey0 = new float[1024], _ex1 = new float[1024], _ey1 = new float[1024];
    private int _en;
    private float _px, _py, _sx, _sy;

    public ProceduralVectorStickerOverlaySynthesizer()
    {
        for (var k = 0; k < Kinds; k++) { _sprite[k] = new uint[Size * Size]; Build((StickerKind)k, _sprite[k]); }
    }

    public ReelsUiSafeZoneReframingGuard? Guard { get; set; }
    public ReadOnlySpan<uint> Sprite(StickerKind k) => _sprite[(int)k];
    public ref readonly StickerInstance Instance(int i) => ref _inst[i];
    public long Spawned { get; private set; }
    public long Reframed { get; private set; }
    public long Rendered { get; private set; }
    public long DiskReads => 0;
    public double LastRenderUs { get; private set; }

    public static string KindName(StickerKind k) => k switch { StickerKind.Money => "돈다발", StickerKind.Exclaim => "느낌표", StickerKind.Skull => "해골", StickerKind.Fire => "불꽃", _ => "하트" };
    public static string KindIcon(StickerKind k) => k switch { StickerKind.Money => "💸", StickerKind.Exclaim => "❗", StickerKind.Skull => "💀", StickerKind.Fire => "🔥", _ => "❤️" };

    /// <summary>음성/자막 키워드 → 스티커 종류(무할당 · 대소문자 무시).</summary>
    public static bool TryClassify(ReadOnlySpan<char> word, out StickerKind kind)
    {
        for (var k = 0; k < Keywords.Length; k++)
        {
            var list = Keywords[k];
            for (var i = 0; i < list.Length; i++)
                if (word.Contains(list[i].AsSpan(), StringComparison.OrdinalIgnoreCase)) { kind = (StickerKind)k; return true; }
        }
        kind = StickerKind.Exclaim; return false;
    }

    /// <summary>스프링 계단 응답 배율(나이 프레임).</summary>
    public static float SpringScale(double ageFrames)
    {
        if (ageFrames <= 0) return 0f;
        var t = ageFrames / Fps; var w = 2 * Math.PI * OmegaHz; var wd = w * Math.Sqrt(1 - Zeta * Zeta);
        return (float)(1 - Math.Exp(-Zeta * w * t) * (Math.Cos(wd * t) + Zeta * w / wd * Math.Sin(wd * t)));
    }

    public static float WobbleRad(double ageFrames)
    {
        var t = ageFrames / Fps; var w = 2 * Math.PI * OmegaHz; var wd = w * Math.Sqrt(1 - Zeta * Zeta);
        return (float)(WobbleDeg * Math.PI / 180 * Math.Exp(-Zeta * w * t) * Math.Sin(wd * t));
    }

    public void Clear() { Array.Clear(_inst); _next = 0; }

    public int LiveCount(long frame)
    {
        var n = 0;
        for (var i = 0; i < Cap; i++) if (_inst[i].Live && frame >= _inst[i].BornFrame && frame < _inst[i].BornFrame + LifeFrames) n++;
        return n;
    }

    /// <summary>스폰(링 풀 · 가장 오래된 슬롯 재사용) — 오버슈트 피크 박스로 세이프존 클램프.</summary>
    public int Spawn(StickerKind kind, float cx, float cy, float size, long frame)
    {
        var box = HookBox.Centered(cx, cy, size, size * 9f / 16f);   // 9:16 프레임에서 정사각 픽셀
        if (Guard is not null)
        {
            var peak = box.Inflate(PeakOvershoot);
            if (Guard.Clamp(ref peak) != ReframeVerdict.Inside) Reframed++;
            box = HookBox.Centered(peak.Cx, peak.Cy, peak.W / PeakOvershoot, peak.H / PeakOvershoot);
        }
        var i = _next; _next = (_next + 1) % Cap;
        _inst[i] = new StickerInstance { Kind = kind, Box = box, BornFrame = frame, Live = true };
        Spawned++;
        return i;
    }

    /// <summary>v17.52 자막 단어 키워드 → 단어 시작 프레임에 스폰(좌/우 상단 교대).</summary>
    public int CollectCaptions(RealtimeWordByWordMotionTypographyEngine typo, double offsetMs = 0)
    {
        var n = 0;
        for (var i = 0; i < typo.WordCount; i++)
        {
            if (!TryClassify(typo.WordText(i), out var k)) continue;
            Spawn(k, (n & 1) == 0 ? 0.28f : 0.70f, 0.30f + 0.06f * (n % 3), 0.24f, KineticZoomPunchCutEngine.FrameOf(offsetMs + typo.Word(i).StartMs, Fps));
            n++;
        }
        return n;
    }

    /// <summary>v17.76 SFX — 차칭 = 돈다발 · 임팩트 = 느낌표(중앙 상단).</summary>
    public int CollectSfx(ProceduralViralSoundFxSynthesizer sfx, double offsetMs = 0)
    {
        var n = 0;
        for (var i = 0; i < sfx.CueCount; i++)
        {
            ref readonly var q = ref sfx.Cue(i);
            if (q.Kind == ViralSfxKind.Chaching) { Spawn(StickerKind.Money, 0.62f, 0.52f, 0.26f, KineticZoomPunchCutEngine.FrameOf(offsetMs + q.TimeMs, Fps)); n++; }
            else if (q.Kind == ViralSfxKind.Impact) { Spawn(StickerKind.Exclaim, 0.5f, 0.24f, 0.2f, KineticZoomPunchCutEngine.FrameOf(offsetMs + q.TimeMs, Fps)); n++; }
        }
        return n;
    }

    /// <summary>활성 스티커 합성(제자리 · 0xAARRGGBB) — 역회전 최근접 표본화 + 알파 블렌드 · 무할당.</summary>
    public int Render(Span<uint> dst, int w, int h, long frame)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var drawn = 0;
        for (var i = 0; i < Cap; i++)
        {
            ref readonly var s = ref _inst[i];
            if (!s.Live) continue;
            var age = frame - s.BornFrame;
            if (age < 0 || age >= LifeFrames) continue;
            var sc = SpringScale(age + 1);
            if (sc <= 0.02f) continue;
            var fade = age >= LifeFrames - FadeFrames ? (LifeFrames - age) / (float)FadeFrames : 1f;
            var rot = WobbleRad(age + 1);
            var cs = MathF.Cos(rot); var sn = MathF.Sin(rot);
            var half = s.Box.W * w * sc * 0.5f;                 // 정사각 픽셀 반경
            var cx = s.Box.Cx * w; var cy = s.Box.Cy * h;
            var inv = Size / (2f * half);
            var r = (int)MathF.Ceiling(half * 1.4143f);
            var xa = Math.Max(0, (int)cx - r); var xb = Math.Min(w - 1, (int)cx + r);
            var ya = Math.Max(0, (int)cy - r); var yb = Math.Min(h - 1, (int)cy + r);
            var spr = _sprite[(int)s.Kind];
            var a8 = (uint)(fade * 256f);
            for (var y = ya; y <= yb; y++)
            {
                var dy = y + 0.5f - cy;
                var row = dst.Slice(y * w, w);
                for (var x = xa; x <= xb; x++)
                {
                    var dx = x + 0.5f - cx;
                    var u = (int)((cs * dx + sn * dy) * inv + Size * 0.5f);
                    var v = (int)((-sn * dx + cs * dy) * inv + Size * 0.5f);
                    if ((uint)u >= Size || (uint)v >= Size) continue;
                    var p = spr[v * Size + u];
                    var a = ((p >> 24) * a8) >> 8;
                    if (a == 0) continue;
                    row[x] = Blend(row[x], p, a);
                }
            }
            drawn++;
        }
        Rendered += drawn;
        LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return drawn;
    }

    /// <summary>스트레이트 알파 블렌드(a 0..255).</summary>
    public static uint Blend(uint dst, uint src, uint a)
    {
        // 채널별 정확 /255(패킹 나눗셈은 상위 채널 나머지가 하위로 번짐)
        var ia = 255u - a;
        var r = (((src >> 16) & 0xFFu) * a + ((dst >> 16) & 0xFFu) * ia + 127u) / 255u;
        var g = (((src >> 8) & 0xFFu) * a + ((dst >> 8) & 0xFFu) * ia + 127u) / 255u;
        var b = ((src & 0xFFu) * a + (dst & 0xFFu) * ia + 127u) / 255u;
        return 0xFF000000u | r << 16 | g << 8 | b;
    }

    // ══════════════════════ 절차적 벡터 정의 ══════════════════════
    private void Build(StickerKind k, uint[] spr)
    {
        switch (k)
        {
            case StickerKind.Money:
                Poly(0.14f, 0.16f, 0.94f, 0.30f, 0.88f, 0.68f, 0.08f, 0.54f); Fill(spr, 0xFF14532Du);
                Poly(0.04f, 0.30f, 0.94f, 0.30f, 0.94f, 0.84f, 0.04f, 0.84f); Fill(spr, 0xFF166534u);
                Poly(0.08f, 0.34f, 0.90f, 0.34f, 0.90f, 0.80f, 0.08f, 0.80f); Fill(spr, 0xFF22C55Eu);
                Circle(0.49f, 0.57f, 0.15f); Fill(spr, 0xFF15803Du);
                Circle(0.49f, 0.57f, 0.10f); Fill(spr, 0xFFBBF7D0u);
                // '$' — S 자 베지에 획(두께 = 평행 윤곽 2 개)
                Move(0.56f, 0.50f); Cubic(0.52f, 0.46f, 0.43f, 0.47f, 0.43f, 0.52f); Cubic(0.43f, 0.58f, 0.55f, 0.56f, 0.55f, 0.62f); Cubic(0.55f, 0.67f, 0.46f, 0.68f, 0.42f, 0.64f);
                Line(0.44f, 0.62f); Cubic(0.47f, 0.65f, 0.52f, 0.64f, 0.52f, 0.62f); Cubic(0.52f, 0.59f, 0.40f, 0.60f, 0.40f, 0.52f); Cubic(0.40f, 0.44f, 0.52f, 0.43f, 0.58f, 0.48f); Close();
                Poly(0.475f, 0.42f, 0.505f, 0.42f, 0.505f, 0.72f, 0.475f, 0.72f); Fill(spr, 0xFF14532Du);
                break;
            case StickerKind.Exclaim:
                Poly(0.34f, 0.04f, 0.66f, 0.04f, 0.60f, 0.68f, 0.40f, 0.68f); Circle(0.5f, 0.84f, 0.125f); Fill(spr, 0xFF7C2D12u);
                Poly(0.38f, 0.08f, 0.62f, 0.08f, 0.565f, 0.64f, 0.435f, 0.64f); Circle(0.5f, 0.84f, 0.09f); Fill(spr, 0xFFFACC15u);
                Poly(0.42f, 0.11f, 0.47f, 0.11f, 0.455f, 0.40f, 0.43f, 0.40f); Fill(spr, 0xFFFEF9C3u);
                break;
            case StickerKind.Skull:
                Circle(0.5f, 0.42f, 0.36f); Poly(0.30f, 0.58f, 0.70f, 0.58f, 0.66f, 0.90f, 0.34f, 0.90f); Fill(spr, 0xFF1F2937u);
                Circle(0.5f, 0.42f, 0.33f); Poly(0.33f, 0.60f, 0.67f, 0.60f, 0.63f, 0.87f, 0.37f, 0.87f); Fill(spr, 0xFFF1F5F9u);
                Circle(0.37f, 0.45f, 0.095f); Circle(0.63f, 0.45f, 0.095f); Poly(0.5f, 0.56f, 0.545f, 0.66f, 0.455f, 0.66f); Fill(spr, 0xFF111827u);
                Poly(0.43f, 0.74f, 0.45f, 0.74f, 0.45f, 0.86f, 0.43f, 0.86f); Poly(0.49f, 0.74f, 0.51f, 0.74f, 0.51f, 0.86f, 0.49f, 0.86f); Poly(0.55f, 0.74f, 0.57f, 0.74f, 0.57f, 0.86f, 0.55f, 0.86f); Fill(spr, 0xFF475569u);
                break;
            case StickerKind.Fire:
                Move(0.5f, 0.02f); Cubic(0.64f, 0.22f, 0.90f, 0.36f, 0.87f, 0.63f); Cubic(0.85f, 0.86f, 0.68f, 0.98f, 0.5f, 0.98f); Cubic(0.32f, 0.98f, 0.14f, 0.86f, 0.13f, 0.63f);
                Cubic(0.12f, 0.44f, 0.28f, 0.32f, 0.33f, 0.16f); Cubic(0.42f, 0.28f, 0.44f, 0.36f, 0.44f, 0.42f); Cubic(0.50f, 0.30f, 0.53f, 0.16f, 0.5f, 0.02f); Close(); Fill(spr, 0xFFEA580Cu);
                Move(0.5f, 0.12f); Cubic(0.62f, 0.30f, 0.84f, 0.40f, 0.82f, 0.64f); Cubic(0.80f, 0.84f, 0.66f, 0.94f, 0.5f, 0.94f); Cubic(0.34f, 0.94f, 0.19f, 0.84f, 0.18f, 0.64f);
                Cubic(0.17f, 0.48f, 0.30f, 0.38f, 0.34f, 0.27f); Cubic(0.42f, 0.36f, 0.45f, 0.44f, 0.46f, 0.50f); Cubic(0.52f, 0.36f, 0.53f, 0.24f, 0.5f, 0.12f); Close(); Fill(spr, 0xFFFB923Cu);
                Move(0.5f, 0.44f); Cubic(0.58f, 0.56f, 0.71f, 0.64f, 0.69f, 0.78f); Cubic(0.67f, 0.90f, 0.59f, 0.94f, 0.5f, 0.94f); Cubic(0.41f, 0.94f, 0.32f, 0.90f, 0.31f, 0.78f);
                Cubic(0.31f, 0.66f, 0.44f, 0.58f, 0.5f, 0.44f); Close(); Fill(spr, 0xFFFDE047u);
                break;
            default:
                Move(0.5f, 0.92f); Cubic(0.14f, 0.68f, 0.0f, 0.46f, 0.06f, 0.27f); Cubic(0.12f, 0.06f, 0.42f, 0.03f, 0.5f, 0.25f);
                Cubic(0.58f, 0.03f, 0.88f, 0.06f, 0.94f, 0.27f); Cubic(1.0f, 0.46f, 0.86f, 0.68f, 0.5f, 0.92f); Close(); Fill(spr, 0xFF9F1239u);
                Move(0.5f, 0.86f); Cubic(0.18f, 0.64f, 0.05f, 0.45f, 0.11f, 0.29f); Cubic(0.16f, 0.12f, 0.42f, 0.10f, 0.5f, 0.31f);
                Cubic(0.58f, 0.10f, 0.84f, 0.12f, 0.89f, 0.29f); Cubic(0.95f, 0.45f, 0.82f, 0.64f, 0.5f, 0.86f); Close(); Fill(spr, 0xFFF43F5Eu);
                Circle(0.29f, 0.31f, 0.065f); Fill(spr, 0xD0FFE4E6u);
                break;
        }
    }

    private void Move(float x, float y) { _px = _sx = x * Size; _py = _sy = y * Size; }

    private void Line(float x, float y) { Edge(_px, _py, x * Size, y * Size); _px = x * Size; _py = y * Size; }

    private void Close() { if (_px != _sx || _py != _sy) Edge(_px, _py, _sx, _sy); _px = _sx; _py = _sy; }

    private void Cubic(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        float ax = _px, ay = _py, bx = c1x * Size, by = c1y * Size, cx = c2x * Size, cy = c2y * Size, dx = x * Size, dy = y * Size;
        var lx = ax; var ly = ay;
        for (var i = 1; i <= Flatten; i++)
        {
            var t = i / (float)Flatten; var mt = 1 - t;
            var nx = mt * mt * mt * ax + 3 * mt * mt * t * bx + 3 * mt * t * t * cx + t * t * t * dx;
            var ny = mt * mt * mt * ay + 3 * mt * mt * t * by + 3 * mt * t * t * cy + t * t * t * dy;
            Edge(lx, ly, nx, ny); lx = nx; ly = ny;
        }
        _px = dx; _py = dy;
    }

    private void Poly(params float[] xy)
    {
        Move(xy[0], xy[1]);
        for (var i = 2; i < xy.Length; i += 2) Line(xy[i], xy[i + 1]);
        Close();
    }

    private void Circle(float cx, float cy, float r)
    {
        const float K = 0.5523f;
        var k = r * K;
        Move(cx + r, cy);
        Cubic(cx + r, cy + k, cx + k, cy + r, cx, cy + r);
        Cubic(cx - k, cy + r, cx - r, cy + k, cx - r, cy);
        Cubic(cx - r, cy - k, cx - k, cy - r, cx, cy - r);
        Cubic(cx + k, cy - r, cx + r, cy - k, cx + r, cy);
        Close();
    }

    private void Edge(float x0, float y0, float x1, float y1)
    {
        if (y0 == y1) return;
        if (_en == _ex0.Length)
        {
            Array.Resize(ref _ex0, _en * 2); Array.Resize(ref _ey0, _en * 2); Array.Resize(ref _ex1, _en * 2); Array.Resize(ref _ey1, _en * 2);
        }
        _ex0[_en] = x0; _ey0[_en] = y0; _ex1[_en] = x1; _ey1[_en] = y1; _en++;
    }

    /// <summary>누적 윤곽 → non-zero 와인딩 4× 서브 스캔라인 채움(가로 해석적 부분 피복) → 스프라이트에 색 합성 · 윤곽 초기화.</summary>
    private void Fill(uint[] spr, uint color)
    {
        var cov = new float[Size + 1];
        var xs = new float[_en]; var ws = new int[_en];
        var ca = color >> 24;
        for (var y = 0; y < Size; y++)
        {
            Array.Clear(cov);
            for (var s = 0; s < SubRows; s++)
            {
                var sy = y + (s + 0.5f) / SubRows;
                var n = 0;
                for (var e = 0; e < _en; e++)
                {
                    float y0 = _ey0[e], y1 = _ey1[e];
                    var up = y1 > y0;
                    var lo = up ? y0 : y1; var hi = up ? y1 : y0;
                    if (sy < lo || sy >= hi) continue;
                    var x = _ex0[e] + (sy - y0) * (_ex1[e] - _ex0[e]) / (y1 - y0);
                    var j = n++;
                    while (j > 0 && xs[j - 1] > x) { xs[j] = xs[j - 1]; ws[j] = ws[j - 1]; j--; }
                    xs[j] = x; ws[j] = up ? 1 : -1;
                }
                var wind = 0; var start = 0f;
                for (var i = 0; i < n; i++)
                {
                    var prev = wind; wind += ws[i];
                    if (prev == 0 && wind != 0) start = xs[i];
                    else if (prev != 0 && wind == 0) Span(cov, start, xs[i], 1f / SubRows);
                }
            }
            for (var x = 0; x < Size; x++)
            {
                var c = Math.Min(1f, cov[x]);
                if (c <= 0) continue;
                var a = (uint)(c * ca + 0.5f);
                var o = spr[y * Size + x];
                var oa = o >> 24;
                var na = a + oa * (255u - a) / 255u;                  // 스트레이트 알파 over
                var rgb = na == 0 ? 0u : Blend(o, color, a * 255u / na) & 0xFFFFFFu;
                spr[y * Size + x] = (na << 24) | rgb;
            }
        }
        _en = 0;
    }

    private static void Span(float[] cov, float a, float b, float w)
    {
        a = Math.Clamp(a, 0, Size); b = Math.Clamp(b, 0, Size);
        if (b <= a) return;
        var ia = (int)a; var ib = (int)b;
        if (ia == ib) { cov[ia] += (b - a) * w; return; }
        cov[ia] += (ia + 1 - a) * w;
        for (var i = ia + 1; i < ib; i++) cov[i] += w;
        cov[ib] += (b - ib) * w;
    }
}
