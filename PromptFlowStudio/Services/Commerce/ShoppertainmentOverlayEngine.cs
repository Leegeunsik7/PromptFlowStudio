using System.Globalization;
using System.Text.Json;
using PromptFlowStudio.Services.Newsroom;

namespace PromptFlowStudio.Services.Commerce;

/// <summary>
/// [PromptFlow Studio v16.48.0] 🎪 쇼퍼테인먼트 3D 오버레이 &amp; 타임세일 팝업 엔진.
///   ▸ 타임세일: 3D 압출 7-세그먼트 mm:ss 카운트다운(깊이 레이어 · 마지막 10초 적색 점멸) · 진행 바 · 특가 라벨 · 종료 자동 해제.
///   ▸ 구매 알림 토스트: 닉네임 마스킹 · 최대 5개 스택 · 슬라이드인/홀드/페이드 · 마스크는 수신 시 1회 래스터.
///   ▸ 3D 상품 회전 카드: Y축 회전 원근 투영(열 단위 텍스처 매핑 · 램버트 셰이딩 · 뒷면 브랜드 컬러) · 잔여 재고 게이지(임박 시 적색).
///   ▸ 스마트폰 구매 QR(ISO 18004 · 쿠폰 파라미터 포함 URL · 콰이어트 존) · 쿠폰 배너.
///   ▸ Render(RGBA · 시각) 워밍업 후 무할당 · OBS 연동용 상태 JSON.
/// </summary>
public sealed class ShoppertainmentOverlayEngine
{
    public const int ToastCap = 5, TexW = 96, TexH = 128;
    public const double ToastSlideMs = 300, ToastHoldMs = 3200, ToastFadeMs = 500;

    // 7-세그먼트 비트(a b c d e f g)
    private static readonly byte[] Seg = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };

    private readonly object _gate = new();
    private readonly TextMask?[] _toastMask = new TextMask?[ToastCap];
    private readonly double[] _toastAt = new double[ToastCap];
    private readonly byte[] _tex = new byte[TexW * TexH * 3];
    private int _toastHead;
    private double _saleEnd = double.NaN, _saleStart;
    private TextMask? _saleLabel, _priceMask, _couponMask, _qrLabel, _stockLabel, _nameMask;
    private QrCode? _qr;
    private string? _qrUrl, _productName, _coupon;
    private uint _color = 0x6366F1;
    private int _stock, _stockMax = 1, _lowThreshold;
    private long _frames, _toasts;

    public event Action? OnChange;

    public bool ShowCard { get; set; } = true;
    public bool ShowQr { get; set; } = true;
    public float RotationSpeed { get; set; } = 0.9f;
    public long Frames => Interlocked.Read(ref _frames);
    public long Toasts => Interlocked.Read(ref _toasts);
    public bool SaleActive(double nowMs) => !double.IsNaN(_saleEnd) && nowMs < _saleEnd;
    public double SaleRemainingMs(double nowMs) => double.IsNaN(_saleEnd) ? 0 : Math.Max(0, _saleEnd - nowMs);
    public QrCode? Qr => _qr;
    public string? QrUrl => _qrUrl;
    public string? LastAction { get; private set; }

    // ══════════════════════ 상태 ══════════════════════

    /// <summary>방송 상품 지정 → 회전 카드 텍스처(브랜드 그라데이션 + 이름) · 구매 QR(쿠폰 파라미터) 생성.</summary>
    public void SetProduct(CommerceProduct p, int stock, string? coupon)
    {
        var url = BuildPurchaseUrl(p.PurchaseUrl ?? "https://shop.example.com/p/" + Uri.EscapeDataString(p.Sku), coupon);
        var qr = QrCodeEncoder.Encode(url, QrEcc.Medium);
        var name = NewsMotionGraphicsOverlayEngine.Rasterize(p.Name.Length > 10 ? p.Name[..10] : p.Name, 14, true);
        lock (_gate)
        {
            _productName = p.Name; _color = p.Color; _qr = qr; _qrUrl = url; _stockMax = Math.Max(1, p.InitialStock); _lowThreshold = p.LowStockThreshold;
            _nameMask = name;
            BuildTexture(p.Color, name);
        }
        _qrLabel ??= NewsMotionGraphicsOverlayEngine.Rasterize("스캔하고 바로 구매", 13, true);
        _stockLabel ??= NewsMotionGraphicsOverlayEngine.Rasterize("잔여 재고", 12, true);
        SetStock(stock);
        LastAction = "🛍 오버레이 상품 · " + p.Name;
        Raise();
    }

    public static string BuildPurchaseUrl(string baseUrl, string? coupon)
    {
        if (string.IsNullOrEmpty(coupon)) return baseUrl + (baseUrl.Contains('?') ? "&" : "?") + "utm_source=pfs_live";
        return baseUrl + (baseUrl.Contains('?') ? "&" : "?") + "coupon=" + Uri.EscapeDataString(coupon) + "&utm_source=pfs_live";
    }

    private void BuildTexture(uint col, TextMask name)
    {
        int r0 = (int)(col >> 16) & 255, g0 = (int)(col >> 8) & 255, b0 = (int)col & 255;
        for (var y = 0; y < TexH; y++)
            for (var x = 0; x < TexW; x++)
            {
                var o = (y * TexW + x) * 3;
                var k = 0.55f + 0.45f * (1 - y / (float)TexH);
                var band = y > TexH * 0.72f;
                _tex[o] = (byte)(band ? 250 : r0 * k); _tex[o + 1] = (byte)(band ? 250 : g0 * k); _tex[o + 2] = (byte)(band ? 250 : b0 * k);
                var dx = x - TexW / 2f; var dy = y - TexH * 0.38f;
                if (dx * dx + dy * dy < 22 * 22) { _tex[o] = (byte)Math.Min(255, _tex[o] + 60); _tex[o + 1] = (byte)Math.Min(255, _tex[o + 1] + 60); _tex[o + 2] = (byte)Math.Min(255, _tex[o + 2] + 60); }
            }
        var tx = Math.Max(0, (TexW - name.Width) / 2); var ty = (int)(TexH * 0.78f);
        for (var y = 0; y < name.Height && ty + y < TexH; y++)
            for (var x = 0; x < name.Width && tx + x < TexW; x++)
            {
                var a = name.Alpha[y * name.Width + x];
                if (a == 0) continue;
                var o = ((ty + y) * TexW + tx + x) * 3;
                for (var c = 0; c < 3; c++) _tex[o + c] = (byte)((_tex[o + c] * (255 - a) + 20 * a) / 255);
            }
    }

    public void SetStock(int stock) { lock (_gate) _stock = Math.Max(0, stock); }

    public void StartTimeSale(double nowMs, double durationSec, decimal salePrice)
    {
        var label = NewsMotionGraphicsOverlayEngine.Rasterize("⚡ TIME SALE", 16, true);
        var price = NewsMotionGraphicsOverlayEngine.Rasterize(AiLiveShowHostEngine.Won(salePrice), 18, true);
        lock (_gate) { _saleStart = nowMs; _saleEnd = nowMs + durationSec * 1000; _saleLabel = label; _priceMask = price; }
        LastAction = "⚡ 타임세일 " + durationSec.ToString("0", CultureInfo.InvariantCulture) + "초 · " + AiLiveShowHostEngine.Won(salePrice);
        Raise();
    }

    public void StopTimeSale() { lock (_gate) _saleEnd = double.NaN; Raise(); }

    public void SetCoupon(string? code, int percent)
    {
        var m = string.IsNullOrEmpty(code) ? null : NewsMotionGraphicsOverlayEngine.Rasterize("🎟 " + code + "  " + percent.ToString(CultureInfo.InvariantCulture) + "% OFF", 15, true);
        lock (_gate) { _coupon = code; _couponMask = m; }
        Raise();
    }

    /// <summary>구매 알림 토스트(마스킹 닉네임 · 수량).</summary>
    public void PushBuyer(string? buyer, string product, int qty, double nowMs)
    {
        var text = "🛒 " + LiveCommerceInventoryOrderBridge.MaskBuyer(buyer) + "님 " + (product.Length > 10 ? product[..10] : product) + " " + qty.ToString(CultureInfo.InvariantCulture) + "개 구매!";
        var m = NewsMotionGraphicsOverlayEngine.Rasterize(text, 14, true);
        lock (_gate)
        {
            _toastMask[_toastHead] = m; _toastAt[_toastHead] = nowMs;
            _toastHead = (_toastHead + 1) % ToastCap;
        }
        Interlocked.Increment(ref _toasts);
    }

    public int ActiveToasts(double nowMs)
    {
        var n = 0;
        lock (_gate) for (var i = 0; i < ToastCap; i++) if (_toastMask[i] is not null && nowMs - _toastAt[i] < ToastSlideMs + ToastHoldMs + ToastFadeMs) n++;
        return n;
    }

    // ══════════════════════ 합성(무할당) ══════════════════════

    public void Render(Span<byte> rgba, int w, int h, int stride, double nowMs)
    {
        lock (_gate)
        {
            var s = h / 360f;
            int S(float v) => (int)(v * s);
            // 타임세일
            if (!double.IsNaN(_saleEnd))
            {
                var remain = _saleEnd - nowMs;
                if (remain <= 0) _saleEnd = double.NaN;
                else
                {
                    int px = S(10), py = S(10), pw = S(150), ph = S(66);
                    var last = remain < 10_000;
                    var flash = last && ((int)(nowMs / 250) & 1) == 0;
                    NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, px, py, pw, ph, 0x0B1220, 225);
                    NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, px, py, pw, S(3), flash ? 0xFFFFFFu : 0xEF4444u, 255);
                    if (_saleLabel is { } sl) NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, sl, px + S(6), py + S(5), 0xFBBF24, 255, 0);
                    var secs = (int)Math.Ceiling(remain / 1000);
                    var mm = Math.Min(99, secs / 60); var ss = secs % 60;
                    var dw = S(16); var dh = S(28); var dx = px + S(8); var dy = py + S(24);
                    var col = last ? (flash ? 0xFFFFFFu : 0xEF4444u) : 0xFDE047u;
                    Digit3D(rgba, w, h, stride, mm / 10, dx, dy, dw, dh, col); dx += dw + S(5);
                    Digit3D(rgba, w, h, stride, mm % 10, dx, dy, dw, dh, col); dx += dw + S(4);
                    NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, dx, dy + dh / 4, S(3), S(3), col, 255);
                    NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, dx, dy + dh * 3 / 4 - S(3), S(3), S(3), col, 255); dx += S(7);
                    Digit3D(rgba, w, h, stride, ss / 10, dx, dy, dw, dh, col); dx += dw + S(5);
                    Digit3D(rgba, w, h, stride, ss % 10, dx, dy, dw, dh, col);
                    if (_priceMask is { } pm) NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, pm, px + pw - pm.Width - S(6), py + S(5), 0xFFFFFF, 255, 0);
                    var prog = (float)(remain / Math.Max(1, _saleEnd - _saleStart));
                    NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, px, py + ph - S(4), (int)(pw * prog), S(4), last ? 0xEF4444u : 0x22C55Eu, 255);
                }
            }
            // 쿠폰 배너
            if (_couponMask is { } cm)
            {
                var cx = w - cm.Width - S(22); var cy = S(10);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, cx - S(8), cy, cm.Width + S(16), cm.Height + S(8), 0xDB2777, 235);
                NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, cm, cx, cy + S(4), 0xFFFFFF, 255, 0);
            }
            // 구매 토스트(최신 위)
            var row = 0;
            for (var k = 1; k <= ToastCap; k++)
            {
                var i = (_toastHead - k + ToastCap) % ToastCap;
                if (_toastMask[i] is not { } tm) continue;
                var age = nowMs - _toastAt[i];
                if (age < 0 || age > ToastSlideMs + ToastHoldMs + ToastFadeMs) continue;
                var slide = age < ToastSlideMs ? 1 - age / ToastSlideMs : 0;
                var alpha = age > ToastSlideMs + ToastHoldMs ? (int)(235 * (1 - (age - ToastSlideMs - ToastHoldMs) / ToastFadeMs)) : 235;
                var tx = S(10) - (int)(slide * (tm.Width + S(30))); var ty = S(92) + row * (tm.Height + S(10));
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, tx, ty, tm.Width + S(14), tm.Height + S(6), 0x111827, alpha);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, tx, ty, S(3), tm.Height + S(6), 0x22C55E, alpha);
                NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, tm, tx + S(8), ty + S(3), 0xFFFFFF, alpha, 0);
                row++;
            }
            // 3D 회전 카드 + 재고 게이지
            if (ShowCard && _productName is not null)
            {
                int cw = S(70), ch = S(94), ccx = S(60), ccy = h - S(80);
                RenderCard(rgba, w, h, stride, ccx, ccy, cw, ch, nowMs / 1000.0 * RotationSpeed);
                var gx = ccx - S(45); var gy = ccy + ch / 2 + S(10); var gw = S(90);
                var low = _stock <= _lowThreshold;
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, gx, gy, gw, S(8), 0x1F2937, 230);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, gx, gy, (int)(gw * Math.Clamp(_stock / (float)_stockMax, 0, 1)), S(8), low ? 0xEF4444u : 0x22C55Eu, 255);
                if (_stockLabel is { } stl) NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, stl, gx, gy + S(10), low ? 0xFCA5A5u : 0xE5E7EBu, 255, 0);
            }
            // QR
            if (ShowQr && _qr is { } q)
            {
                var mod = Math.Max(2, S(110) / (q.Size + 8));
                var full = (q.Size + 8) * mod;
                int qx = w - full - S(10), qy = h - full - S(28);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, qx, qy, full, full, 0xFFFFFF, 255);
                for (var y = 0; y < q.Size; y++)
                    for (var x = 0; x < q.Size; x++)
                        if (q.Modules[y * q.Size + x]) NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, qx + (x + 4) * mod, qy + (y + 4) * mod, mod, mod, 0x000000, 255);
                if (_qrLabel is { } ql)
                {
                    NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, qx, qy + full, full, ql.Height + S(6), 0xDB2777, 240);
                    NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, ql, qx + Math.Max(0, (full - ql.Width) / 2), qy + full + S(3), 0xFFFFFF, 255, 0);
                }
            }
        }
        Interlocked.Increment(ref _frames);
    }

    private static void Digit3D(Span<byte> d, int w, int h, int stride, int digit, int x, int y, int dw, int dh, uint col)
    {
        var depth = Math.Max(2, dw / 5);
        for (var layer = depth; layer >= 0; layer--)
        {
            var shade = layer == 0 ? col : Darken(col, 0.35f + 0.25f * (1 - layer / (float)depth));
            Segments(d, w, h, stride, Seg[digit], x + layer, y + layer, dw, dh, shade);
        }
    }

    private static uint Darken(uint c, float k) => (uint)((int)(((c >> 16) & 255) * k) << 16 | (int)(((c >> 8) & 255) * k) << 8 | (int)((c & 255) * k));

    private static void Segments(Span<byte> d, int w, int h, int stride, byte bits, int x, int y, int dw, int dh, uint col)
    {
        var t = Math.Max(2, dw / 5); var half = dh / 2;
        if ((bits & 1) != 0) NewsMotionGraphicsOverlayEngine.FillRect(d, w, h, stride, x + t, y, dw - 2 * t, t, col, 255);
        if ((bits & 2) != 0) NewsMotionGraphicsOverlayEngine.FillRect(d, w, h, stride, x + dw - t, y + t, t, half - t, col, 255);
        if ((bits & 4) != 0) NewsMotionGraphicsOverlayEngine.FillRect(d, w, h, stride, x + dw - t, y + half, t, half - t, col, 255);
        if ((bits & 8) != 0) NewsMotionGraphicsOverlayEngine.FillRect(d, w, h, stride, x + t, y + dh - t, dw - 2 * t, t, col, 255);
        if ((bits & 16) != 0) NewsMotionGraphicsOverlayEngine.FillRect(d, w, h, stride, x, y + half, t, half - t, col, 255);
        if ((bits & 32) != 0) NewsMotionGraphicsOverlayEngine.FillRect(d, w, h, stride, x, y + t, t, half - t, col, 255);
        if ((bits & 64) != 0) NewsMotionGraphicsOverlayEngine.FillRect(d, w, h, stride, x + t, y + half - t / 2, dw - 2 * t, t, col, 255);
    }

    /// <summary>Y축 회전 원근 카드(열 단위 텍스처 매핑 · 셰이딩 · 뒷면 브랜드 컬러).</summary>
    private void RenderCard(Span<byte> d, int w, int h, int stride, int cx, int cy, int cw, int ch, double angle)
    {
        var c = (float)Math.Cos(angle); var sn = (float)Math.Sin(angle);
        var front = c >= 0;
        var shade = 0.35f + 0.65f * MathF.Abs(c);
        var samples = cw * 2;
        var prevX = int.MinValue;
        for (var i = 0; i <= samples; i++)
        {
            var u = i / (float)samples * 2 - 1;                    // -1 ~ 1
            var persp = 1f / (1f + u * sn * 0.35f);
            var sx = cx + (int)(u * c * cw / 2f * persp);
            if (sx == prevX) continue;
            prevX = sx;
            if ((uint)sx >= (uint)w) continue;
            var colH = (int)(ch * persp);
            var top = cy - colH / 2;
            var texX = (int)((front ? (u + 1) / 2 : (1 - u) / 2) * (TexW - 1));
            for (var y = 0; y < colH; y++)
            {
                var py = top + y;
                if ((uint)py >= (uint)h) continue;
                var o = py * stride + sx * 4;
                if (front)
                {
                    var ty = Math.Min(TexH - 1, y * TexH / Math.Max(1, colH));
                    var t = (ty * TexW + texX) * 3;
                    d[o] = (byte)(_tex[t] * shade); d[o + 1] = (byte)(_tex[t + 1] * shade); d[o + 2] = (byte)(_tex[t + 2] * shade);
                }
                else
                {
                    d[o] = (byte)(((_color >> 16) & 255) * shade * 0.6f); d[o + 1] = (byte)(((_color >> 8) & 255) * shade * 0.6f); d[o + 2] = (byte)((_color & 255) * shade * 0.6f);
                }
                d[o + 3] = 255;
            }
        }
    }

    // ══════════════════════ OBS 상태 ══════════════════════

    public string StateJson(double nowMs)
    {
        lock (_gate)
        {
            using var ms = new MemoryStream();
            using (var j = new Utf8JsonWriter(ms))
            {
                j.WriteStartObject();
                j.WriteString("product", _productName);
                j.WriteNumber("stock", _stock);
                j.WriteBoolean("timeSale", !double.IsNaN(_saleEnd) && nowMs < _saleEnd);
                j.WriteNumber("remainingMs", Math.Round(double.IsNaN(_saleEnd) ? 0 : Math.Max(0, _saleEnd - nowMs)));
                j.WriteString("coupon", _coupon);
                j.WriteString("qrUrl", _qrUrl);
                j.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Shopper.OnChange", ex); } }
}
