using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Services.Interactive;

/// <summary>v16.26.0 정규화 바운딩 박스(0~1 · 좌상단 기준).</summary>
public readonly record struct HotspotBox(float X, float Y, float W, float H)
{
    public bool IsValid => W > 0.005f && H > 0.005f && X >= 0 && Y >= 0 && X + W <= 1.0001f && Y + H <= 1.0001f;
    public float CenterX => X + W * 0.5f;
    public float CenterY => Y + H * 0.5f;

    /// <summary>화면 안으로 클램프(크기 최소 1% · 최대 100%).</summary>
    public HotspotBox Clamp()
    {
        var w = Math.Clamp(W, 0.01f, 1f);
        var h = Math.Clamp(H, 0.01f, 1f);
        return new(Math.Clamp(X, 0f, 1f - w), Math.Clamp(Y, 0f, 1f - h), w, h);
    }
}

/// <summary>v16.26.0 핫스팟 키프레임(시각 초 + 박스).</summary>
public readonly record struct HotspotKeyframe(double T, HotspotBox Box);

/// <summary>v16.26.0 상품 태그 1건 — 노드(영상) · 상품 · 구매 링크(https) · 쿠폰 · 가격 · 강조색 · 노출 구간 · 이동 경로 키프레임.</summary>
public sealed class ShoppableTag
{
    public string Id { get; set; } = "tag_" + Guid.NewGuid().ToString("N")[..6];
    public string NodeId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Url { get; set; } = "";
    public string? CouponCode { get; set; }
    public string? CouponText { get; set; }
    public string? PriceText { get; set; }
    public string Color { get; set; } = "FF4F8B";
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; } = 5;
    public List<HotspotKeyframe> Keyframes { get; set; } = new();
    /// <summary>v16.19.0 브랜드 캠페인 출처(있으면).</summary>
    public string? CampaignName { get; set; }
}

/// <summary>v16.26.0 자동 추적 결과.</summary>
public sealed record HotspotTrackResult(bool Ok, int Keyframes, double MeanConfidence, string Message);

/// <summary>
/// [PromptFlowStudio] v16.26.0 🛍️ 인-비디오 쇼핑 태그 &amp; 핫스팟 오버레이 엔진 — 피사체/제품 이동 경로 추적 → 터치형 핫스팟 · 구매 링크/쿠폰 팝업 레이어.
///   ▸ 브랜드 연동: v16.19.0 <see cref="CommercialBrandCampaignService"/> 이력(제품 앵커 · 세이프존 · 스케일 · CTA)으로 초기 핫스팟 박스 시드(<see cref="SeedFromCampaign"/>).
///   ▸ 추적: ffmpeg 로 저해상도(160px) gray 프레임을 5fps 파이프 → 블록 매칭 SAD 템플릿 추적(<see cref="TrackStep"/> · Span · 무할당 · 서브샘플 2px)
///     + 템플릿 점진 갱신(드리프트 억제) · 신뢰도 하한 미만이면 직전 박스 유지 · 키프레임 단순화(이동 1.5% 미만 생략).
///   ▸ 보간: <see cref="Interpolate"/> 이진 탐색 + 선형 보간(무할당 · 프레임당 호출). 오버레이 출력: SVG 프리뷰 · player.html 데이터 · ffmpeg drawbox 번인(비인터랙티브 플랫폼용).
///   ▸ 보안: 링크는 https(또는 loopback http)만 · javascript:/data: 거부 · 표시 문자열은 플레이어에서 textContent 로만 사용.
/// </summary>
public sealed class ShoppableVideoOverlayEngine
{
    public const int TrackWidth = 160;
    public const int TrackFps = 5;
    public const int SearchRadius = 12;
    public const int MaxTrackFrames = 600;
    public const int MaxTags = 64;
    public const float MinConfidence = 0.55f;
    public const float SimplifyEpsilon = 0.015f;
    public static readonly TimeSpan TrackTimeout = TimeSpan.FromSeconds(90);

    private readonly Func<string?> _ffmpeg;
    private readonly CommercialBrandCampaignService? _brand;
    private readonly object _gate = new();
    private readonly List<ShoppableTag> _tags = new();
    private int _busy, _tracked;

    public ShoppableVideoOverlayEngine(IVideoMergeService merge, CommercialBrandCampaignService? brand = null)
        : this(() => merge.ResolvedFfmpegPath, brand) { }

    public ShoppableVideoOverlayEngine(Func<string?> ffmpeg, CommercialBrandCampaignService? brand)
    {
        _ffmpeg = ffmpeg;
        _brand = brand;
    }

    public event Action? OnChange;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public int TrackedCount => Volatile.Read(ref _tracked);
    public string? LastAction { get; private set; }
    public CommercialBrandCampaignService? Brand => _brand;
    public IReadOnlyList<ShoppableTag> Tags { get { lock (_gate) return _tags.ToArray(); } }
    public int TagCount { get { lock (_gate) return _tags.Count; } }

    // ══════════════════════ 태그 관리 ══════════════════════

    public bool AddTag(ShoppableTag tag)
    {
        if (tag is null) return false;
        lock (_gate)
        {
            if (_tags.Count >= MaxTags) return false;
            foreach (var t in _tags) if (t.Id == tag.Id) return false;
            if (tag.Keyframes.Count == 0) tag.Keyframes.Add(new(tag.StartSeconds, new HotspotBox(0.6f, 0.55f, 0.3f, 0.3f)));
            _tags.Add(tag);
        }
        LastAction = "🏷 태그 추가 · " + tag.ProductName;
        Raise();
        return true;
    }

    public void RemoveTag(string id)
    {
        lock (_gate) _tags.RemoveAll(t => t.Id == id);
        Raise();
    }

    public void ClearTags()
    {
        lock (_gate) _tags.Clear();
        Raise();
    }

    public void ReplaceTags(IEnumerable<ShoppableTag> tags)
    {
        lock (_gate)
        {
            _tags.Clear();
            foreach (var t in tags) { if (_tags.Count >= MaxTags) break; _tags.Add(t); }
        }
        Raise();
    }

    /// <summary>편집 후 UI 갱신 알림.</summary>
    public void Touch() => Raise();

    // ══════════════════════ 순수 헬퍼(테스트 대상) ══════════════════════

    /// <summary>구매 링크 검증 — https 절대 URL(또는 loopback http) · 2048자 이하 · 공백/따옴표/꺾쇠 금지.</summary>
    public static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
        foreach (var ch in url) if (char.IsWhiteSpace(ch) || ch is '"' or '\'' or '<' or '>' or '`' || char.IsControl(ch)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback);
    }

    /// <summary>태그 검증 → 사유(정상 null).</summary>
    public static string? Validate(ShoppableTag t)
    {
        if (string.IsNullOrWhiteSpace(t.ProductName)) return "상품명이 비었습니다.";
        if (!IsSafeUrl(t.Url)) return "구매 링크는 https:// 주소여야 합니다.";
        if (!(t.EndSeconds > t.StartSeconds) || t.StartSeconds < 0) return "노출 구간이 올바르지 않습니다.";
        if (t.Keyframes.Count == 0) return "핫스팟 위치가 없습니다.";
        foreach (var k in t.Keyframes) if (!k.Box.IsValid || !double.IsFinite(k.T)) return "핫스팟 박스가 화면 밖입니다.";
        return null;
    }

    /// <summary>시각 <paramref name="t"/>의 박스(키프레임 시간순 가정 · 이진 탐색 + 선형 보간 · 무할당). 비면 default.</summary>
    public static HotspotBox Interpolate(ReadOnlySpan<HotspotKeyframe> keys, double t)
    {
        if (keys.Length == 0) return default;
        if (t <= keys[0].T) return keys[0].Box;
        if (t >= keys[^1].T) return keys[^1].Box;
        int lo = 0, hi = keys.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (keys[mid].T <= t) lo = mid; else hi = mid;
        }
        var a = keys[lo]; var b = keys[hi];
        var span = b.T - a.T;
        var f = span <= 1e-9 ? 0f : (float)((t - a.T) / span);
        return new(a.Box.X + (b.Box.X - a.Box.X) * f, a.Box.Y + (b.Box.Y - a.Box.Y) * f,
                   a.Box.W + (b.Box.W - a.Box.W) * f, a.Box.H + (b.Box.H - a.Box.H) * f);
    }

    /// <summary>
    /// 블록 매칭 1스텝(무할당) — <paramref name="template"/>(tw×th gray)를 <paramref name="frame"/>(w×h) 안 (px,py) 주변 ±radius 에서 SAD 최소 위치 탐색(2px 서브샘플).
    /// 반환: 신뢰도(0~1 · 1=완전 일치), out 최적 좌상단.
    /// </summary>
    public static float TrackStep(ReadOnlySpan<byte> frame, int w, int h, ReadOnlySpan<byte> template, int tw, int th, int px, int py, int radius, out int bx, out int by)
    {
        bx = px; by = py;
        if (tw <= 0 || th <= 0 || tw > w || th > h || frame.Length < w * h || template.Length < tw * th) return 0f;
        long best = long.MaxValue;
        var x0 = Math.Max(0, px - radius); var x1 = Math.Min(w - tw, px + radius);
        var y0 = Math.Max(0, py - radius); var y1 = Math.Min(h - th, py + radius);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                long sad = 0;
                for (var ty = 0; ty < th && sad <= best; ty += 2)
                {
                    var fr = frame.Slice((y + ty) * w + x, tw);
                    var tr = template.Slice(ty * tw, tw);
                    for (var tx = 0; tx < tw; tx += 2) sad += Math.Abs(fr[tx] - tr[tx]);
                }
                // 동률이면 이동 거리 작은 쪽(정지 피사체 떨림 억제)
                if (sad < best || (sad == best && Math.Abs(x - px) + Math.Abs(y - py) < Math.Abs(bx - px) + Math.Abs(by - py)))
                {
                    best = sad; bx = x; by = y;
                }
            }
        }
        var samples = ((th + 1) / 2) * ((tw + 1) / 2);
        var mean = samples == 0 ? 255.0 : (double)best / samples;
        return (float)Math.Clamp(1.0 - mean / 64.0, 0, 1);
    }

    /// <summary>템플릿 추출(프레임 → tw×th 복사 · 무할당).</summary>
    public static void CopyTemplate(ReadOnlySpan<byte> frame, int w, int x, int y, int tw, int th, Span<byte> dst)
    {
        for (var ty = 0; ty < th; ty++) frame.Slice((y + ty) * w + x, tw).CopyTo(dst.Slice(ty * tw, tw));
    }

    /// <summary>템플릿 점진 갱신(EWMA 1/8 · 드리프트 억제 · 무할당).</summary>
    public static void BlendTemplate(ReadOnlySpan<byte> frame, int w, int x, int y, int tw, int th, Span<byte> tpl)
    {
        for (var ty = 0; ty < th; ty++)
        {
            var fr = frame.Slice((y + ty) * w + x, tw);
            var tr = tpl.Slice(ty * tw, tw);
            for (var tx = 0; tx < tw; tx++) tr[tx] = (byte)((tr[tx] * 7 + fr[tx] + 4) >> 3);
        }
    }

    /// <summary>키프레임 단순화 — 직전 유지 키 대비 중심/크기 변화가 ε 미만이면 생략(첫/마지막 유지).</summary>
    public static List<HotspotKeyframe> Simplify(IReadOnlyList<HotspotKeyframe> keys, float eps = SimplifyEpsilon)
    {
        var res = new List<HotspotKeyframe>(Math.Min(keys.Count, 64));
        for (var i = 0; i < keys.Count; i++)
        {
            if (i == 0 || i == keys.Count - 1) { res.Add(keys[i]); continue; }
            var p = res[^1].Box; var c = keys[i].Box;
            if (Math.Abs(p.CenterX - c.CenterX) >= eps || Math.Abs(p.CenterY - c.CenterY) >= eps || Math.Abs(p.W - c.W) >= eps || Math.Abs(p.H - c.H) >= eps)
                res.Add(keys[i]);
        }
        return res;
    }

    /// <summary>
    /// v16.19.0 브랜드 캠페인 레이아웃 → 초기 핫스팟 박스(제품 앵커/세이프존/스케일 재현 · 플로팅 ±6px 여유 포함).
    /// </summary>
    public static HotspotBox CampaignBox(int width, int height, ProductAnchor anchor, double productScale)
    {
        var lay = SponsorVideoCompositorService.LayoutOf(width, height, productScale);
        var s = lay.Safe;
        var pw = lay.ProductW;
        var ph = pw;   // 누끼 종횡비 미상 → 정사각 근사
        var pad = 6;
        var x = anchor switch
        {
            ProductAnchor.BottomLeft or ProductAnchor.Left => s.X,
            ProductAnchor.BottomCenter or ProductAnchor.Center => s.X + (s.W - pw) / 2,
            _ => s.Right - pw,
        };
        var y = anchor switch
        {
            ProductAnchor.Center or ProductAnchor.Left or ProductAnchor.Right => s.Y + (s.H - ph) / 2,
            _ => Math.Max(s.Y, lay.CtaY - ph - pad),
        };
        return new HotspotBox((float)(x - pad) / width, (float)(y - pad) / height, (float)(pw + pad * 2) / width, (float)(ph + pad * 2) / height).Clamp();
    }

    /// <summary>브랜드 캠페인 결과 → 상품 태그(노드 지정 · 링크/쿠폰은 사용자 입력 필요).</summary>
    public static ShoppableTag SeedFromCampaign(BrandCampaignResult campaign, string nodeId, int width = 1080, int height = 1920,
        ProductAnchor anchor = ProductAnchor.BottomRight, double productScale = 0.38, double duration = 5)
    {
        var box = CampaignBox(width, height, anchor, productScale);
        var name = campaign.Script?.Headline is { Length: > 0 } h ? h : campaign.Name;
        return new ShoppableTag
        {
            NodeId = nodeId, ProductName = name, CampaignName = campaign.Name, StartSeconds = 0, EndSeconds = Math.Max(1, duration),
            CouponText = campaign.Script?.Cta,
            Keyframes = { new HotspotKeyframe(0, box) },
        };
    }

    /// <summary>시각 t 의 SVG 오버레이(프리뷰 · 노출 구간 태그만 · 속성값은 숫자/검증된 색만).</summary>
    public static string BuildSvg(IReadOnlyList<ShoppableTag> tags, string nodeId, double t, int width, int height)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(512);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(width.ToString(inv)).Append(' ').Append(height.ToString(inv)).Append("\">");
        foreach (var tag in tags)
        {
            if (tag.NodeId != nodeId || t < tag.StartSeconds || t > tag.EndSeconds) continue;
            var b = Interpolate(tag.Keyframes.ToArray(), t);
            var color = SponsorVideoCompositorService.Hex(tag.Color);
            sb.Append("<rect x=\"").Append((b.X * width).ToString("0.#", inv)).Append("\" y=\"").Append((b.Y * height).ToString("0.#", inv))
              .Append("\" width=\"").Append((b.W * width).ToString("0.#", inv)).Append("\" height=\"").Append((b.H * height).ToString("0.#", inv))
              .Append("\" rx=\"12\" fill=\"#").Append(color).Append("\" fill-opacity=\"0.12\" stroke=\"#").Append(color).Append("\" stroke-width=\"4\"/>");
            sb.Append("<circle cx=\"").Append((b.CenterX * width).ToString("0.#", inv)).Append("\" cy=\"").Append((b.CenterY * height).ToString("0.#", inv))
              .Append("\" r=\"14\" fill=\"#").Append(color).Append("\"/>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// ffmpeg drawbox 번인 필터(비인터랙티브 플랫폼용) — 키프레임 구간별 선형 이동 식 + enable 구간. 태그 없으면 "null".
    /// </summary>
    public static string BuildDrawboxFilter(IReadOnlyList<ShoppableTag> tags, string nodeId)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256);
        foreach (var tag in tags)
        {
            if (tag.NodeId != nodeId || tag.Keyframes.Count == 0) continue;
            var keys = tag.Keyframes;
            var color = SponsorVideoCompositorService.Hex(tag.Color);
            var segs = keys.Count == 1 ? 1 : keys.Count - 1;
            for (var i = 0; i < segs; i++)
            {
                var a = keys[i]; var b = keys.Count == 1 ? keys[0] : keys[i + 1];
                var ta = i == 0 ? Math.Min(a.T, tag.StartSeconds) : a.T;
                var tb = i == segs - 1 ? Math.Max(b.T, tag.EndSeconds) : b.T;
                ta = Math.Max(ta, tag.StartSeconds); tb = Math.Min(tb, tag.EndSeconds);
                if (tb <= ta) continue;
                var d = Math.Max(1e-3, b.T - a.T);
                if (sb.Length > 0) sb.Append(',');
                sb.Append("drawbox=x='iw*(").Append(Lerp(a.Box.X, b.Box.X, a.T, d)).Append(")':y='ih*(").Append(Lerp(a.Box.Y, b.Box.Y, a.T, d))
                  .Append(")':w='iw*").Append(a.Box.W.ToString("0.####", inv)).Append("':h='ih*").Append(a.Box.H.ToString("0.####", inv))
                  .Append("':color=0x").Append(color).Append("@0.9:t=4:enable='between(t,").Append(ta.ToString("0.###", inv)).Append(',').Append(tb.ToString("0.###", inv)).Append(")'");
            }
        }
        return sb.Length == 0 ? "null" : sb.ToString();

        static string Lerp(float v0, float v1, double t0, double d)
        {
            var inv = CultureInfo.InvariantCulture;
            if (Math.Abs(v1 - v0) < 1e-5f) return v0.ToString("0.####", inv);
            return v0.ToString("0.####", inv) + "+(" + (v1 - v0).ToString("0.####", inv) + ")*clip((t-" + t0.ToString("0.###", inv) + ")/" + d.ToString("0.###", inv) + "\\,0\\,1)";
        }
    }

    // ══════════════════════ 자동 추적 ══════════════════════

    /// <summary>
    /// 영상에서 태그 피사체 자동 추적 — 태그 시작 시각의 첫 키프레임 박스를 템플릿으로 노출 구간을 5fps 추적해 키프레임 교체.
    /// ffmpeg 부재/디코드 실패/동시 실행은 Graceful 실패(기존 키프레임 유지 · 예외 0).
    /// </summary>
    public async Task<HotspotTrackResult> TrackAsync(ShoppableTag tag, string videoPath, CancellationToken ct = default)
    {
        if (tag.Keyframes.Count == 0) return new(false, 0, 0, "초기 핫스팟 박스가 없습니다.");
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return new(false, 0, 0, "노드 영상 파일이 없습니다.");
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null) return new(false, 0, 0, "ffmpeg 실행 파일을 찾을 수 없습니다.");
        if (Interlocked.Exchange(ref _busy, 1) == 1) return new(false, 0, 0, "다른 추적이 진행 중입니다.");
        LastAction = "🎯 추적 중 · " + tag.ProductName;
        Raise();
        try
        {
            var size = await ProbeSizeAsync(ffmpeg, videoPath, ct).ConfigureAwait(false);
            if (size is null) return new(false, 0, 0, "영상 해상도를 읽지 못했습니다.");
            var w = TrackWidth;
            var h = Math.Max(16, (int)Math.Round(TrackWidth * (double)size.Value.H / size.Value.W) & ~1);
            var start = Math.Max(0, tag.StartSeconds);
            var dur = Math.Max(0.5, tag.EndSeconds - start);

            var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-v", "error", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-t", dur.ToString("0.###", CultureInfo.InvariantCulture),
                         "-i", videoPath, "-vf", "fps=" + TrackFps.ToString(CultureInfo.InvariantCulture) + ",scale=" + w.ToString(CultureInfo.InvariantCulture) + ":" + h.ToString(CultureInfo.InvariantCulture),
                         "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1" })
                psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return new(false, 0, 0, "ffmpeg 시작 실패");
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TrackTimeout);
            _ = proc.StandardError.ReadToEndAsync(d.Token);

            var frameBytes = w * h;
            var frame = ArrayPool<byte>.Shared.Rent(frameBytes);
            var tpl = ArrayPool<byte>.Shared.Rent(frameBytes);
            try
            {
                var init = tag.Keyframes[0].Box.Clamp();
                var tw = Math.Max(4, (int)(init.W * w)); var th = Math.Max(4, (int)(init.H * h));
                tw = Math.Min(tw, w); th = Math.Min(th, h);
                var px = Math.Clamp((int)(init.X * w), 0, w - tw); var py = Math.Clamp((int)(init.Y * h), 0, h - th);
                var keys = new List<HotspotKeyframe>(64);
                double confSum = 0; var n = 0;
                var s = proc.StandardOutput.BaseStream;
                for (var i = 0; i < MaxTrackFrames; i++)
                {
                    if (!await ReadExactAsync(s, frame, frameBytes, d.Token).ConfigureAwait(false)) break;
                    var tt = start + (double)i / TrackFps;
                    if (i == 0)
                    {
                        CopyTemplate(frame.AsSpan(0, frameBytes), w, px, py, tw, th, tpl.AsSpan(0, tw * th));
                        keys.Add(new(tt, init));
                        continue;
                    }
                    var conf = TrackStep(frame.AsSpan(0, frameBytes), w, h, tpl.AsSpan(0, tw * th), tw, th, px, py, SearchRadius, out var nx, out var ny);
                    confSum += conf; n++;
                    if (conf >= MinConfidence)
                    {
                        px = nx; py = ny;
                        BlendTemplate(frame.AsSpan(0, frameBytes), w, px, py, tw, th, tpl.AsSpan(0, tw * th));
                    }
                    keys.Add(new(tt, new HotspotBox((float)px / w, (float)py / h, (float)tw / w, (float)th / h).Clamp()));
                }
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                if (keys.Count < 2) return new(false, keys.Count, 0, "추적할 프레임이 부족합니다.");
                var simple = Simplify(keys);
                lock (_gate) { tag.Keyframes.Clear(); tag.Keyframes.AddRange(simple); }
                Interlocked.Increment(ref _tracked);
                var mean = n == 0 ? 1 : confSum / n;
                LastAction = "🎯 추적 완료 · " + tag.ProductName + " · 키 " + simple.Count.ToString(CultureInfo.InvariantCulture);
                return new(true, simple.Count, mean, mean < MinConfidence ? "추적 신뢰도가 낮습니다 — 박스를 확인하세요." : "추적 완료");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
                ArrayPool<byte>.Shared.Return(tpl);
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(false, 0, 0, "취소됨"); }
        catch (Exception ex) { Program.Log("Shoppable.Track", ex); return new(false, 0, 0, "추적 실패: " + ex.Message); }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Raise();
        }
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        var off = 0;
        while (off < count)
        {
            var r = await s.ReadAsync(buf.AsMemory(off, count - off), ct).ConfigureAwait(false);
            if (r <= 0) return false;
            off += r;
        }
        return true;
    }

    /// <summary>ffmpeg -i stderr 로 해상도/길이 조회(실패 null).</summary>
    internal static async Task<(int W, int H, double Duration)?> ProbeAsync(string ffmpeg, string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
            using var p = new Process { StartInfo = psi };
            if (!p.Start()) return null;
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromSeconds(15));
            var err = await p.StandardError.ReadToEndAsync(d.Token).ConfigureAwait(false);
            try { await p.WaitForExitAsync(d.Token).ConfigureAwait(false); } catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
            if (!CommercialProductSegmenterService.TryParseVideoSize(err, out var w, out var h)) return null;
            return (w, h, InteractiveChoiceBranchingService.ParseDuration(err));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Program.Log("Shoppable.Probe", ex); return null; }
    }

    private static async Task<(int W, int H)?> ProbeSizeAsync(string ffmpeg, string path, CancellationToken ct)
        => await ProbeAsync(ffmpeg, path, ct).ConfigureAwait(false) is { } p ? (p.W, p.H) : null;

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Shoppable.OnChange", ex); }
        }
    }
}
