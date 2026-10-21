namespace PromptFlowStudio.Services.VisualHooks;

[Flags]
public enum ReelsPlatforms : byte { None = 0, InstagramReels = 1, TikTok = 2, YouTubeShorts = 4, All = 7 }

public enum OverlayKind : byte { Caption, Sticker, Badge }

public enum ReframeVerdict : byte { Inside, Shifted, Shrunk }

/// <summary>정규화(0..1 · 9:16 프레임 기준) 오버레이 사각형 — 값 형식.</summary>
public struct HookBox
{
    public float X, Y, W, H;
    public HookBox(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    public readonly float Right => X + W;
    public readonly float Bottom => Y + H;
    public readonly float Cx => X + W * 0.5f;
    public readonly float Cy => Y + H * 0.5f;
    public static HookBox Centered(float cx, float cy, float w, float h) => new(cx - w * 0.5f, cy - h * 0.5f, w, h);
    public readonly bool Intersects(in HookBox o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    public readonly HookBox Inflate(float k) => Centered(Cx, Cy, W * k, H * k);
}

/// <summary>플랫폼 시스템 UI 마진(프레임 비율).</summary>
public readonly record struct SafeMargins(float Top, float Bottom, float Left, float Right);

/// <summary>
/// [PromptFlow Studio v17.77.0] 📐 숏폼 플랫폼 UI 세이프존 다이내믹 리프레이밍 가드(<see cref="Clamp"/>/<see cref="ClampAll"/> 무할당 · 분기 몇 개 = 0 ms).
/// Instagram Reels · TikTok 시스템 UI(상단 15 % 상태/탭 · 하단 22 % 캡션/음원 · 우측 18 % 좋아요/댓글/공유 레일 · 좌측 4 %) 를 내장하고,
/// 대상 플랫폼 합집합의 가장 엄격한 마진으로 안전 사각형을 만든다. v17.74 동적 자막 · v17.77 스티커 박스가 경계를 넘으면
/// 크기는 유지한 채 중심축만 안쪽으로 시프트하고(안전 사각형보다 크면 중심 고정 축소), 스티커가 자막과 겹치면 자막 위(공간 부족 시 아래)로 분리한다.
/// </summary>
public sealed class ReelsUiSafeZoneReframingGuard
{
    /// <summary>플랫폼별 마진 — [0] Instagram Reels · [1] TikTok · [2] YouTube Shorts.</summary>
    public static readonly SafeMargins[] PlatformMargins =
    {
        new(0.15f, 0.22f, 0.04f, 0.18f),
        new(0.15f, 0.22f, 0.04f, 0.18f),
        new(0.12f, 0.20f, 0.04f, 0.16f),
    };

    private ReelsPlatforms _targets;
    private SafeMargins _active;
    private HookBox _safe;

    public ReelsUiSafeZoneReframingGuard(ReelsPlatforms targets = ReelsPlatforms.InstagramReels | ReelsPlatforms.TikTok) => Targets = targets;

    /// <summary>대상 플랫폼(합집합 → 최대 마진).</summary>
    public ReelsPlatforms Targets
    {
        get => _targets;
        set
        {
            _targets = value == ReelsPlatforms.None ? ReelsPlatforms.InstagramReels : value;
            float t = 0, b = 0, l = 0, r = 0;
            for (var i = 0; i < PlatformMargins.Length; i++)
            {
                if (((int)_targets & (1 << i)) == 0) continue;
                var m = PlatformMargins[i];
                t = Math.Max(t, m.Top); b = Math.Max(b, m.Bottom); l = Math.Max(l, m.Left); r = Math.Max(r, m.Right);
            }
            _active = new SafeMargins(t, b, l, r);
            _safe = new HookBox(l, t, 1f - l - r, 1f - t - b);
        }
    }

    public SafeMargins Active => _active;
    public HookBox SafeRect => _safe;
    /// <summary>오버레이 간 최소 분리 간격(프레임 높이 비).</summary>
    public float Gap { get; set; } = 0.012f;
    public long Checks { get; private set; }
    public long Shifts { get; private set; }
    public long Shrinks { get; private set; }
    public long Separations { get; private set; }
    public double LastClampUs { get; private set; }

    public bool IsSafe(in HookBox b) =>
        b.X >= _safe.X - 1e-5f && b.Y >= _safe.Y - 1e-5f && b.Right <= _safe.Right + 1e-5f && b.Bottom <= _safe.Bottom + 1e-5f;

    /// <summary>경계 침범 시 중심축 시프트(필요 시 축소) — 무할당.</summary>
    public ReframeVerdict Clamp(ref HookBox b)
    {
        Checks++;
        var v = ReframeVerdict.Inside;
        if (b.W > _safe.W || b.H > _safe.H)
        {
            var k = Math.Min(_safe.W / b.W, _safe.H / b.H);
            b = b.Inflate(k);
            v = ReframeVerdict.Shrunk; Shrinks++;
        }
        // 축소 후 부동소수 오차로 max < min 가능 → Clamp 대신 Max(Min) (최소 경계 우선)
        var nx = Math.Max(_safe.X, Math.Min(b.X, _safe.Right - b.W));
        var ny = Math.Max(_safe.Y, Math.Min(b.Y, _safe.Bottom - b.H));
        if (nx != b.X || ny != b.Y)
        {
            b.X = nx; b.Y = ny;
            if (v == ReframeVerdict.Inside) v = ReframeVerdict.Shifted;
            Shifts++;
        }
        return v;
    }

    /// <summary>전체 클램프 + 비자막 오버레이 ↔ 자막 충돌 분리. 반환 = 변경된 박스 수.</summary>
    public int ClampAll(Span<HookBox> boxes, ReadOnlySpan<OverlayKind> kinds)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var changed = 0;
        for (var i = 0; i < boxes.Length; i++) if (Clamp(ref boxes[i]) != ReframeVerdict.Inside) changed++;
        for (var c = 0; c < boxes.Length; c++)
        {
            if (kinds[c] != OverlayKind.Caption) continue;
            var cap = boxes[c];
            for (var i = 0; i < boxes.Length; i++)
            {
                if (i == c || kinds[i] == OverlayKind.Caption || !boxes[i].Intersects(in cap)) continue;
                ref var b = ref boxes[i];
                var above = cap.Y - Gap - b.H;
                b.Y = above >= _safe.Y ? above : Math.Min(cap.Bottom + Gap, _safe.Bottom - b.H);
                Separations++; changed++;
            }
        }
        LastClampUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return changed;
    }

    /// <summary>정규화 → 픽셀 사각형(반열림 [x0,x1)).</summary>
    public static void ToPixels(in HookBox b, int w, int h, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = Math.Clamp((int)MathF.Round(b.X * w), 0, w); x1 = Math.Clamp((int)MathF.Round(b.Right * w), 0, w);
        y0 = Math.Clamp((int)MathF.Round(b.Y * h), 0, h); y1 = Math.Clamp((int)MathF.Round(b.Bottom * h), 0, h);
    }

    public static string PlatformName(ReelsPlatforms p) => p switch
    {
        ReelsPlatforms.InstagramReels => "Instagram Reels",
        ReelsPlatforms.TikTok => "TikTok",
        ReelsPlatforms.YouTubeShorts => "YouTube Shorts",
        _ => "복합",
    };
}
