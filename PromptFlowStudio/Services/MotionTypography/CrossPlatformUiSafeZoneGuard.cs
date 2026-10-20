namespace PromptFlowStudio.Services.MotionTypography;

/// <summary>정수 픽셀 사각형(반열림 [X, X+W) × [Y, Y+H)).</summary>
public struct PxRect
{
    public int X, Y, W, H;
    public PxRect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
    public readonly int Right => X + W;
    public readonly int Bottom => Y + H;
    public readonly bool Overlaps(in PxRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    public readonly bool Contains(in PxRect o) => o.X >= X && o.Y >= Y && o.Right <= Right && o.Bottom <= Bottom;
    public readonly bool IsEmpty => W <= 0 || H <= 0;
}

/// <summary>
/// [PromptFlow Studio v17.52.0] 🛡 3대 플랫폼 시스템 UI 세이프 존 가드(<see cref="Fit"/> · <see cref="Violates"/> 무할당).
/// 1080×1920 기준 데드존(가로 비례 축척): <b>Instagram Reels</b> 상단 헤더 0~384(v2.0 세이프존 20%) · 우측 아이콘 레일 x ≥ 936, y 960~1440 · 하단 캡션/오디오 1440~;
/// <b>TikTok</b> 상단 LIVE/탭 0~288(15%) · 우측 패널 x ≥ 900, y 760~1344 · 하단 캡션/음악 1344~(70%);
/// <b>YouTube Shorts</b> 상단 0~288 · 우상단 ⋮ x ≥ 960, y 0~320 · 우측 레일 x ≥ 920, y 860~1536 · 하단 제목/구독 1536~(80%).
/// 활성 플랫폼 데드존 합집합을 8 px 격자에 투영하고 최대 빈 직사각형(히스토그램 스택)을 구해 공통 세이프 존으로 삼은 뒤 내부 여백 24 px 를 둔다.
/// <see cref="Fit"/> 은 요소 박스를 세이프 존 폭에 맞춰 축소(배율 반환)하고 Y(필요 시 X)를 클램프해 데드존 침범을 0 으로 만든다.
/// </summary>
public sealed class CrossPlatformUiSafeZoneGuard
{
    public const int BaseW = 1080, BaseH = 1920, Grid = 8, Margin = 24;
    public const int Instagram = 1, TikTok = 2, YouTube = 4, All = 7;
    private static readonly int[] Zones =
    {
        // 플랫폼 비트, x, y, w, h (1080×1920)
        Instagram, 0, 0, 1080, 384,   Instagram, 936, 960, 144, 480,   Instagram, 0, 1440, 1080, 480,
        TikTok, 0, 0, 1080, 288,      TikTok, 900, 760, 180, 584,      TikTok, 0, 1344, 1080, 576,
        YouTube, 0, 0, 1080, 288,     YouTube, 960, 0, 120, 320,       YouTube, 920, 860, 160, 676,   YouTube, 0, 1536, 1080, 384,
    };
    private readonly PxRect[] _dead = new PxRect[Zones.Length / 5];
    private int _deadN;
    private bool[] _cells = Array.Empty<bool>();
    private int[] _hist = Array.Empty<int>(), _stack = Array.Empty<int>();

    public CrossPlatformUiSafeZoneGuard(int width = BaseW, int height = BaseH, int mask = All) => Configure(width, height, mask);

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Mask { get; private set; }
    public PxRect Safe { get; private set; }
    public int DeadCount => _deadN;
    public ref readonly PxRect Dead(int i) => ref _dead[i];
    public long Fits { get; private set; }
    public long Shrinks { get; private set; }
    public long Moves { get; private set; }
    public long Violations { get; private set; }

    /// <summary>출력 해상도 · 플랫폼 마스크 재계산(비핫패스).</summary>
    public void Configure(int width, int height, int mask)
    {
        Width = width; Height = height; Mask = mask & All;
        float sx = (float)width / BaseW, sy = (float)height / BaseH;
        _deadN = 0;
        for (var i = 0; i < Zones.Length; i += 5)
        {
            if ((Zones[i] & Mask) == 0) continue;
            _dead[_deadN++] = new PxRect((int)MathF.Floor(Zones[i + 1] * sx), (int)MathF.Floor(Zones[i + 2] * sy), (int)MathF.Ceiling(Zones[i + 3] * sx), (int)MathF.Ceiling(Zones[i + 4] * sy));
        }
        int gw = (width + Grid - 1) / Grid, gh = (height + Grid - 1) / Grid;
        if (_cells.Length < gw * gh) { _cells = new bool[gw * gh]; _hist = new int[gw + 1]; _stack = new int[gw + 2]; }
        for (var y = 0; y < gh; y++)
            for (var x = 0; x < gw; x++)
            {
                var c = new PxRect(x * Grid, y * Grid, Grid, Grid);
                var free = true;
                for (var k = 0; k < _deadN && free; k++) if (_dead[k].Overlaps(in c)) free = false;
                _cells[y * gw + x] = free;
            }
        // 최대 빈 직사각형(행별 히스토그램 + 단조 스택)
        Array.Clear(_hist);
        long best = 0; PxRect r = default;
        for (var y = 0; y < gh; y++)
        {
            for (var x = 0; x < gw; x++) _hist[x] = _cells[y * gw + x] ? _hist[x] + 1 : 0;
            _hist[gw] = 0;
            var sp = 0;
            for (var x = 0; x <= gw; x++)
            {
                while (sp > 0 && _hist[_stack[sp - 1]] >= _hist[x])
                {
                    var hgt = _hist[_stack[--sp]];
                    var left = sp == 0 ? 0 : _stack[sp - 1] + 1;
                    long area = (long)hgt * (x - left);
                    if (area > best) { best = area; r = new PxRect(left * Grid, (y - hgt + 1) * Grid, (x - left) * Grid, hgt * Grid); }
                }
                _stack[sp++] = x;
            }
        }
        r.W = Math.Min(r.W, width - r.X); r.H = Math.Min(r.H, height - r.Y);
        Safe = new PxRect(r.X + Margin, r.Y + Margin, Math.Max(r.W - 2 * Margin, 0), Math.Max(r.H - 2 * Margin, 0));
    }

    public bool Violates(in PxRect box)
    {
        for (var k = 0; k < _deadN; k++) if (_dead[k].Overlaps(in box)) return true;
        return box.X < 0 || box.Y < 0 || box.Right > Width || box.Bottom > Height;
    }

    /// <summary>요소 박스를 세이프 존에 맞춤(무할당) — 폭/높이 초과 시 축소 배율, 위치 클램프. 반환 = 배율(≤ 1).</summary>
    public float Fit(ref PxRect box)
    {
        Fits++;
        var s = Safe;
        var scale = 1f;
        if (box.W > s.W || box.H > s.H)
        {
            scale = MathF.Min((float)s.W / Math.Max(box.W, 1), (float)s.H / Math.Max(box.H, 1));
            var cx = box.X + box.W / 2; var cy = box.Y + box.H / 2;
            box.W = (int)(box.W * scale); box.H = (int)(box.H * scale);
            box.X = cx - box.W / 2; box.Y = cy - box.H / 2;
            Shrinks++;
        }
        int nx = Math.Clamp(box.X, s.X, s.Right - box.W), ny = Math.Clamp(box.Y, s.Y, s.Bottom - box.H);
        if (nx != box.X || ny != box.Y) Moves++;
        box.X = nx; box.Y = ny;
        return scale;
    }

    /// <summary>실제 그려진 박스 사후 검증(위반 집계).</summary>
    public bool Audit(in PxRect drawn)
    {
        if (drawn.IsEmpty || !Violates(in drawn)) return true;
        Violations++;
        return false;
    }
}
