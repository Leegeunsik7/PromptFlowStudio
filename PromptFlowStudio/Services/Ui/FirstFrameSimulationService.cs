using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Ui;

/// <summary>v16.11.0 첫 프레임 시뮬레이션 상태.</summary>
public enum FirstFrameKind { Empty, Pending, T2v, Anchor }

/// <summary>
/// v16.11.0 예상 첫 프레임 스냅숏(불변). <see cref="FrameUrl"/>=T2V 시뮬레이션 SVG(또는 Pending 중 직전 프레임),
///   <see cref="AnchorUrl"/>+<see cref="OverlayUrl"/>=앵커 합성(앵커 이미지 위에 카메라 무빙·구도 가이드 오버레이 레이어).
/// </summary>
public sealed record FirstFrameSimulation(
    FirstFrameKind Kind, string? FrameUrl, string? AnchorUrl, string? OverlayUrl,
    bool Portrait, int CameraIndex, long Version, bool FromCache, double ElapsedMs)
{
    public static readonly FirstFrameSimulation None = new(FirstFrameKind.Empty, null, null, null, false, -1, 0, false, 0);

    /// <summary>구도 어스펙트 라벨.</summary>
    public string AspectLabel => Portrait ? "9:16" : "16:9";

    /// <summary>카메라 무빙 라벨(미지정이면 "Static").</summary>
    public string CameraLabel => (uint)CameraIndex < (uint)WanCameraStyleStudio.Cameras.Length ? WanCameraStyleStudio.Cameras[CameraIndex].Label : "Static";

    /// <summary>카메라 무빙 → 미리보기 CSS 모션 클래스(앵커 합성 레이어 애니메이션 힌트).</summary>
    public string MotionCss => FirstFrameSimulationService.MotionCssOf(CameraIndex);

    /// <summary>표시할 프레임이 있는지(Pending 중 직전 프레임 포함).</summary>
    public bool HasFrame => Kind == FirstFrameKind.Anchor ? AnchorUrl is not null : FrameUrl is not null;
}

/// <summary>
/// [PromptFlowStudio] v16.11.0 🎬 실시간 첫 프레임 시뮬레이션 엔진 — 영상 생성 전 '예상 첫 프레임' 캔버스를 즉시 구성.
///   ▸ T2V: 프롬프트 입력을 500ms 디바운스(<see cref="SafeDebouncer"/>) 후 경량 절차적 SVG 프레임 생성(무API·무GPU) · 동일 입력은 16슬롯 캐시 즉시 히트.
///     입력 중에는 <see cref="FirstFrameKind.Pending"/>(직전 프레임 유지 + 스켈레톤)로 즉시 전환.
///   ▸ I2V/앵커: 디바운스 없이 동기 합성 — 앵커 이미지 레이어 + 카메라 무빙 글리프·구도 가이드 오버레이(16:9/9:16) → 0.1초 예산 내 바인딩.
///     (SVG-in-img 는 외부 이미지를 로드하지 못하므로 앵커는 HTML 레이어, 오버레이만 SVG.)
///   ▸ 무주입 · 순수 BCL — 컴포넌트가 직접 생성/Dispose(모바일 서브컴포넌트 안전). <see cref="Changed"/>는 백그라운드 스레드에서 올 수 있음(InvokeAsync 필수).
///   ▸ 오래된 디바운스 결과가 새 상태(앵커 등)를 덮지 않도록 버전 비교 후에만 반영.
/// </summary>
public sealed class FirstFrameSimulationService : IDisposable
{
    /// <summary>T2V 프롬프트 디바운스(ms).</summary>
    public const int DebounceMs = 500;

    /// <summary>앵커 합성 바인딩 예산(ms).</summary>
    public const int AnchorBudgetMs = 100;

    private const int CacheSlots = 16;

    private readonly SafeDebouncer _debouncer;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly StringBuilder _sb = new(2048);                 // SVG 조립 버퍼(재사용 · _gate 보호)
    private readonly string?[] _cacheKeys = new string?[CacheSlots];
    private readonly string?[] _cacheUrls = new string?[CacheSlots];
    private int _cacheNext;
    private string _lastKey = "";
    private string _pendingPrompt = "", _pendingAccent = "";      // Flush 용 대기 입력
    private string? _pendingSub;
    private long _version;
    private long _renders, _cacheHits;
    private volatile FirstFrameSimulation _current = FirstFrameSimulation.None;

    /// <param name="debounce">테스트용 디바운스 오버라이드(미지정 시 500ms).</param>
    public FirstFrameSimulationService(Action<Exception>? onError = null, TimeSpan? debounce = null)
    {
        _debouncer = new SafeDebouncer(onError);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(DebounceMs);
    }

    /// <summary>현재 시뮬레이션 스냅숏.</summary>
    public FirstFrameSimulation Current => _current;

    /// <summary>스냅숏 변경 알림(백그라운드 스레드 가능 · 구독자 예외 격리).</summary>
    public event Action? Changed;

    /// <summary>실제 프레임 렌더 횟수(캐시 히트 제외).</summary>
    public long Renders => Interlocked.Read(ref _renders);

    /// <summary>캐시 히트 횟수.</summary>
    public long CacheHits => Interlocked.Read(ref _cacheHits);

    /// <summary>
    /// 입력 반영 — 앵커는 즉시 합성, T2V 는 캐시 히트면 즉시 · 미스면 Pending 후 디바운스 렌더. 동일 입력은 무시(멱등).
    /// </summary>
    /// <param name="accent">강조색(정체성 칩 색).</param>
    /// <param name="sub">프레임 부제(정체성 요약 등).</param>
    public FirstFrameSimulation Update(string? prompt, string? size, string? anchorUrl, string? accent = null, string? sub = null)
    {
        var p = (prompt ?? "").Trim();
        var a = string.IsNullOrWhiteSpace(anchorUrl) ? null : anchorUrl;
        var portrait = IsPortrait(size);
        var cam = WanCameraStyleStudio.ActiveIndex(p, WanCameraStyleStudio.Cameras);
        var acc = string.IsNullOrWhiteSpace(accent) ? "#7c5cff" : accent!;
        var key = string.Concat(portrait ? "P|" : "L|", a ?? "", "|", acc, "|", sub ?? "", "|", p);

        lock (_gate)
        {
            if (key == _lastKey) return _current;
            _lastKey = key;
            var version = ++_version;

            if (a is not null)
            {
                _debouncer.Cancel();
                var sw = Stopwatch.StartNew();
                var overlay = BuildOverlayLocked(portrait, cam);
                Interlocked.Increment(ref _renders);
                return Publish(new FirstFrameSimulation(FirstFrameKind.Anchor, null, a, overlay, portrait, cam, version, false, sw.Elapsed.TotalMilliseconds));
            }
            if (p.Length == 0)
            {
                _debouncer.Cancel();
                return Publish(FirstFrameSimulation.None with { Version = version, Portrait = portrait });
            }
            if (TryCacheLocked(key, out var hit))
            {
                _debouncer.Cancel();
                Interlocked.Increment(ref _cacheHits);
                return Publish(new FirstFrameSimulation(FirstFrameKind.T2v, hit, null, null, portrait, cam, version, true, 0));
            }

            // 미스 → 직전 프레임 유지한 채 Pending(스켈레톤) · 500ms 뒤 렌더(후속 입력이 오면 이전 예약 취소)
            var prev = _current.Kind is FirstFrameKind.T2v or FirstFrameKind.Pending ? _current.FrameUrl : null;
            _pendingPrompt = p; _pendingAccent = acc; _pendingSub = sub;
            var pending = Publish(new FirstFrameSimulation(FirstFrameKind.Pending, prev, null, null, portrait, cam, version, false, 0));
            _ = _debouncer.Schedule(_debounce, _ =>
            {
                RenderT2v(key, p, portrait, cam, acc, sub, version);
                return Task.CompletedTask;
            });
            return pending;
        }
    }

    /// <summary>대기 중인 디바운스를 즉시 실행(1-Click 생성 직전 확정 · 테스트). 렌더되면 true.</summary>
    public bool Flush()
    {
        lock (_gate)
        {
            var cur = _current;
            if (cur.Kind != FirstFrameKind.Pending) return false;
            _debouncer.Cancel();
            return RenderT2vLocked(_lastKey, _pendingPrompt, cur.Portrait, cur.CameraIndex, _pendingAccent, _pendingSub, cur.Version);
        }
    }

    private void RenderT2v(string key, string prompt, bool portrait, int cam, string accent, string? sub, long version)
    {
        lock (_gate) RenderT2vLocked(key, prompt, portrait, cam, accent, sub, version);
    }

    private bool RenderT2vLocked(string key, string prompt, bool portrait, int cam, string accent, string? sub, long version)
    {
        if (version != _version) return false;   // 그 사이 새 입력/앵커 → 폐기
        var sw = Stopwatch.StartNew();
        var url = BuildFrameLocked(prompt, portrait, cam, accent, sub);
        StoreCacheLocked(key, url);
        Interlocked.Increment(ref _renders);
        Publish(new FirstFrameSimulation(FirstFrameKind.T2v, url, null, null, portrait, cam, version, false, sw.Elapsed.TotalMilliseconds));
        return true;
    }

    private FirstFrameSimulation Publish(FirstFrameSimulation s)
    {
        _current = s;
        var h = Changed;
        if (h is not null)
        {
            try { h(); } catch { /* 구독자 예외 격리 */ }
        }
        return s;
    }

    private bool TryCacheLocked(string key, out string url)
    {
        for (var i = 0; i < CacheSlots; i++)
            if (string.Equals(_cacheKeys[i], key, StringComparison.Ordinal)) { url = _cacheUrls[i]!; return true; }
        url = "";
        return false;
    }

    private void StoreCacheLocked(string key, string url)
    {
        _cacheKeys[_cacheNext] = key;
        _cacheUrls[_cacheNext] = url;
        _cacheNext = (_cacheNext + 1) % CacheSlots;
    }

    /// <summary>"가로*세로" / "가로x세로" → 세로형(9:16) 여부. 파싱 불가면 가로(16:9).</summary>
    public static bool IsPortrait(string? size)
    {
        if (string.IsNullOrEmpty(size)) return false;
        var s = size.AsSpan();
        var i = s.IndexOfAny('*', 'x', 'X');
        return i > 0
            && int.TryParse(s[..i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(s[(i + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            && h > w;
    }

    /// <summary>카메라 인덱스 → 미리보기 모션 CSS 클래스.</summary>
    public static string MotionCssOf(int camera) => camera switch
    {
        0 => "ffs-cam-orbit",
        1 => "ffs-cam-fpv",
        2 => "ffs-cam-crane",
        3 => "ffs-cam-dolly",
        4 => "ffs-cam-pan",
        5 => "ffs-cam-tilt",
        _ => "ffs-cam-static",
    };

    // ── SVG 조립(재사용 StringBuilder · 고정 좌표 정수 Append) ─────────────────────────

    private string BuildFrameLocked(string prompt, bool portrait, int cam, string accent, string? sub)
    {
        int w = portrait ? 360 : 640, h = portrait ? 640 : 360;
        var sb = _sb.Clear();
        Open(sb, w, h);
        sb.Append("<defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='").Append(Attr(accent))
          .Append("'/><stop offset='1' stop-color='#0b0d14'/></linearGradient></defs>");
        sb.Append("<rect width='").Append(w).Append("' height='").Append(h).Append("' fill='url(#g)'/>");
        Guides(sb, w, h);
        CameraGlyph(sb, w, h, cam);
        var label = prompt.Length > 34 ? string.Concat(prompt.AsSpan(0, 34), "…") : prompt;
        Text(sb, w / 2, h - 74, 20, "800", "#ffffff", "sans-serif", label);
        Text(sb, w / 2, h - 48, 11, "600", "rgba(255,255,255,.62)", "monospace", sub ?? "예상 첫 프레임 · T2V 시뮬레이션");
        Badge(sb, w, portrait, cam);
        return Close(sb);
    }

    private string BuildOverlayLocked(bool portrait, int cam)
    {
        int w = portrait ? 360 : 640, h = portrait ? 640 : 360;
        var sb = _sb.Clear();
        Open(sb, w, h);
        Guides(sb, w, h);
        CameraGlyph(sb, w, h, cam);
        Badge(sb, w, portrait, cam);
        return Close(sb);
    }

    private static void Open(StringBuilder sb, int w, int h)
        => sb.Append("<svg xmlns='http://www.w3.org/2000/svg' width='").Append(w).Append("' height='").Append(h)
             .Append("' viewBox='0 0 ").Append(w).Append(' ').Append(h).Append("'>");

    private static string Close(StringBuilder sb)
    {
        sb.Append("</svg>");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return string.Concat("data:image/svg+xml;base64,", Convert.ToBase64String(bytes));
    }

    // 3분할 구도 가이드 + 세이프 프레임.
    private static void Guides(StringBuilder sb, int w, int h)
    {
        sb.Append("<g stroke='rgba(255,255,255,.16)' stroke-width='1' fill='none'>");
        sb.Append("<rect x='12' y='12' width='").Append(w - 24).Append("' height='").Append(h - 24).Append("' rx='12'/>");
        Line(sb, w / 3, 12, w / 3, h - 12); Line(sb, 2 * w / 3, 12, 2 * w / 3, h - 12);
        Line(sb, 12, h / 3, w - 12, h / 3); Line(sb, 12, 2 * h / 3, w - 12, 2 * h / 3);
        sb.Append("</g>");
    }

    private static void Line(StringBuilder sb, int x1, int y1, int x2, int y2)
        => sb.Append("<line x1='").Append(x1).Append("' y1='").Append(y1).Append("' x2='").Append(x2).Append("' y2='").Append(y2).Append("'/>");

    // 카메라 무빙 글리프(중앙) — Orbit 궤도 · FPV 스웁 · Crane 상승 · Dolly 중첩 프레임 · Pan 수평 · Tilt 기울기.
    private static void CameraGlyph(StringBuilder sb, int w, int h, int cam)
    {
        int cx = w / 2, cy = h / 2 - 20, r = Math.Min(w, h) / 6;
        sb.Append("<g stroke='#ffffff' stroke-opacity='.85' stroke-width='3' fill='none' stroke-linecap='round'>");
        switch (cam)
        {
            case 0:
                sb.Append("<ellipse cx='").Append(cx).Append("' cy='").Append(cy).Append("' rx='").Append(r * 2).Append("' ry='").Append(r / 2).Append("' stroke-dasharray='8 6'/>");
                sb.Append("<path d='M").Append(cx + r * 2 - 10).Append(' ').Append(cy - 8).Append(" l10 8 l-12 4'/>");
                break;
            case 1:
                sb.Append("<path d='M").Append(cx - r * 2).Append(' ').Append(cy - r).Append(" Q").Append(cx).Append(' ').Append(cy + r).Append(' ').Append(cx + r * 2).Append(' ').Append(cy - r / 2).Append("' stroke-dasharray='10 6'/>");
                break;
            case 2:
                Line(sb, cx, cy + r, cx, cy - r);
                sb.Append("<path d='M").Append(cx - 12).Append(' ').Append(cy - r + 12).Append(" L").Append(cx).Append(' ').Append(cy - r).Append(" L").Append(cx + 12).Append(' ').Append(cy - r + 12).Append("'/>");
                break;
            case 3:
                for (var k = 1; k <= 3; k++)
                    sb.Append("<rect x='").Append(cx - r * k / 2 * 16 / 9).Append("' y='").Append(cy - r * k / 2).Append("' width='").Append(r * k * 16 / 9).Append("' height='").Append(r * k).Append("' stroke-opacity='").Append(k == 3 ? ".35" : ".8").Append("'/>");
                break;
            case 4:
                Line(sb, cx - r * 2, cy, cx + r * 2, cy);
                sb.Append("<path d='M").Append(cx + r * 2 - 12).Append(' ').Append(cy - 10).Append(" L").Append(cx + r * 2).Append(' ').Append(cy).Append(" L").Append(cx + r * 2 - 12).Append(' ').Append(cy + 10).Append("'/>");
                break;
            case 5:
                sb.Append("<rect x='").Append(cx - r).Append("' y='").Append(cy - r * 9 / 16).Append("' width='").Append(r * 2).Append("' height='").Append(r * 9 / 8).Append("' transform='rotate(-12 ").Append(cx).Append(' ').Append(cy).Append(")'/>");
                break;
            default:
                sb.Append("<circle cx='").Append(cx).Append("' cy='").Append(cy).Append("' r='").Append(r / 2).Append("' fill='rgba(255,255,255,.14)'/>");
                sb.Append("<polygon points='").Append(cx - 10).Append(',').Append(cy - 14).Append(' ').Append(cx - 10).Append(',').Append(cy + 14).Append(' ').Append(cx + 14).Append(',').Append(cy).Append("' fill='#ffffff' stroke='none'/>");
                break;
        }
        sb.Append("</g>");
    }

    // 우상단 배지: "16:9 · Orbit".
    private static void Badge(StringBuilder sb, int w, bool portrait, int cam)
    {
        var label = (uint)cam < (uint)WanCameraStyleStudio.Cameras.Length ? WanCameraStyleStudio.Cameras[cam].Label : "Static";
        sb.Append("<rect x='").Append(w - 150).Append("' y='20' width='130' height='24' rx='12' fill='rgba(0,0,0,.55)'/>");
        sb.Append("<text x='").Append(w - 85).Append("' y='36' font-family='monospace' font-size='11' font-weight='700' fill='#7dd3fc' text-anchor='middle'>")
          .Append(portrait ? "9:16" : "16:9").Append(" · ").Append(label).Append("</text>");
    }

    private static void Text(StringBuilder sb, int x, int y, int size, string weight, string fill, string family, string value)
    {
        sb.Append("<text x='").Append(x).Append("' y='").Append(y).Append("' font-family='").Append(family).Append("' font-size='").Append(size)
          .Append("' font-weight='").Append(weight).Append("' fill='").Append(fill).Append("' text-anchor='middle'>");
        Escape(sb, value);
        sb.Append("</text>");
    }

    private static void Escape(StringBuilder sb, string v)
    {
        foreach (var c in v)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\'': sb.Append("&apos;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    // 색상 속성은 #hex / rgb() 만 허용(속성 주입 차단).
    private static string Attr(string color)
    {
        foreach (var c in color)
            if (!(char.IsLetterOrDigit(c) || c is '#' or '(' or ')' or ',' or '.' or ' ' or '%')) return "#7c5cff";
        return color;
    }

    public void Dispose()
    {
        Changed = null;
        _debouncer.Dispose();
    }
}
