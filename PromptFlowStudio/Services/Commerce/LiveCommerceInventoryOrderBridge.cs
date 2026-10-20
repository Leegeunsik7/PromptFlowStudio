using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.Commerce;

public enum CommerceProvider : byte { Shopify, WooCommerce, CustomRest }

public enum CommerceEventKind : byte { Order, LowStock, SoldOut, Restock, SurgeStart, SurgeEnd, Rejected }

/// <summary>커머스 이벤트(값형 · 링 버퍼).</summary>
public readonly record struct CommerceEvent(double TimeMs, CommerceEventKind Kind, int Slot, int Qty, int Remaining, string? Buyer);

/// <summary>상품(카탈로그 · 가격 · 옵션 · 소구점 · FAQ · 외부 ID).</summary>
public sealed class CommerceProduct
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? SalePrice { get; set; }
    public List<string> Options { get; set; } = new();
    public List<string> SellingPoints { get; set; } = new();
    public List<(string Q, string A)> Faqs { get; set; } = new();
    public string? ExternalId { get; set; }
    public string? PurchaseUrl { get; set; }
    public uint Color { get; set; } = 0x6366F1;
    public int InitialStock { get; set; } = 100;
    public int LowStockThreshold { get; set; } = 10;
}

/// <summary>
/// [PromptFlow Studio v16.48.0] 📦 실시간 상품/재고/주문 연동 브릿지.
///   ▸ 무할당 수신 경로: 슬롯 정수 인덱스 재고(Interlocked CAS 예약 · 초과 판매 차단) · 주문 링 버퍼 · 초당 주문 60초 창 · 임계 교차 1회 트리거(임박/품절/재입고) · 주문 폭주(분당 임계 · 히스테리시스) — 구독자 Action 직접 호출.
///   ▸ 외부 API 폴링: Shopify Admin REST(X-Shopify-Access-Token · variants.inventory_quantity) · WooCommerce v3(Basic 컨슈머 키 · stock_quantity) · 커스텀 REST([{sku, stock}]) —
///     타임아웃 · 지수 백오프 · 스키마 즉시 검증 · 자격 증명 환경변수(PFS_SHOPIFY_*, PFS_WOO_*, PFS_COMMERCE_*).
///   ▸ 웹훅: Shopify(X-Shopify-Hmac-Sha256) · WooCommerce(X-WC-Webhook-Signature) Base64 HMAC-SHA256 FixedTimeEquals 검증 → 주문 생성 이벤트 · 주문 ID 멱등(상한 집합).
/// </summary>
public sealed class LiveCommerceInventoryOrderBridge : IDisposable
{
    public const int MaxProducts = 64, EventCap = 512, WindowSec = 60, SeenCap = 4096;
    public const string ShopifyTokenVar = "PFS_SHOPIFY_TOKEN", ShopifySecretVar = "PFS_SHOPIFY_WEBHOOK_SECRET", WooKeyVar = "PFS_WOO_KEY", WooSecretVar = "PFS_WOO_SECRET", WooWebhookVar = "PFS_WOO_WEBHOOK_SECRET", CustomKeyVar = "PFS_COMMERCE_API_KEY";

    private readonly CommerceProduct?[] _products = new CommerceProduct?[MaxProducts];
    private readonly int[] _stock = new int[MaxProducts], _sold = new int[MaxProducts];
    private readonly byte[] _level = new byte[MaxProducts];   // 0 정상 · 1 임박 · 2 품절
    private readonly CommerceEvent[] _events = new CommerceEvent[EventCap];
    private readonly int[] _perSec = new int[WindowSec];
    private readonly HashSet<string> _seenOrders = new(SeenCap + 1, StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly HttpClient _http;
    private readonly Func<string, string?> _env;
    private int _count, _evHead, _evCount, _windowSum;
    private long _secStamp = -1, _orders, _units, _rejected, _webhooks, _badSignatures;
    private bool _surge;
    private decimal _revenue;
    private int _backoffFails;
    private long _backoffUntil;

    public LiveCommerceInventoryOrderBridge() : this(null, null) { }

    public LiveCommerceInventoryOrderBridge(IServiceProvider sp) : this(null, null) { }

    public LiveCommerceInventoryOrderBridge(HttpMessageHandler? handler, Func<string, string?>? env)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>이벤트 구독(수신 스레드에서 직접 호출 · 예외 격리).</summary>
    public event Action<CommerceEvent>? OnEvent;
    public event Action? OnChange;

    public CommerceProvider Provider { get; set; } = CommerceProvider.CustomRest;
    public string BaseUrl { get; set; } = "";
    public int SurgePerMinute { get; set; } = 30;
    public int PollTimeoutMs { get; set; } = 5000;
    public int ProductCount => _count;
    public long Orders => Interlocked.Read(ref _orders);
    public long Units => Interlocked.Read(ref _units);
    public long Rejected => Interlocked.Read(ref _rejected);
    public long Webhooks => Interlocked.Read(ref _webhooks);
    public long BadSignatures => Interlocked.Read(ref _badSignatures);
    public decimal Revenue { get { lock (_gate) return _revenue; } }
    public bool Surge => _surge;
    public int OrdersPerMinute { get { lock (_gate) return _windowSum; } }
    public bool InBackoff => Environment.TickCount64 < Interlocked.Read(ref _backoffUntil);
    public string? LastAction { get; private set; }

    public CommerceProduct Product(int slot) => _products[slot]!;
    public int StockOf(int slot) => Volatile.Read(ref _stock[slot]);
    public int SoldOf(int slot) => Volatile.Read(ref _sold[slot]);
    public bool IsSoldOut(int slot) => Volatile.Read(ref _stock[slot]) <= 0;

    public int SlotOf(string sku)
    {
        for (var i = 0; i < _count; i++) if (string.Equals(_products[i]!.Sku, sku, StringComparison.Ordinal)) return i;
        return -1;
    }

    public int SlotOfExternal(string externalId)
    {
        for (var i = 0; i < _count; i++) if (string.Equals(_products[i]!.ExternalId, externalId, StringComparison.Ordinal)) return i;
        return -1;
    }

    public int AddProduct(CommerceProduct p)
    {
        lock (_gate)
        {
            var existing = SlotOf(p.Sku);
            if (existing >= 0) { _products[existing] = p; return existing; }
            if (_count >= MaxProducts) return -1;
            _products[_count] = p;
            _stock[_count] = p.InitialStock;
            _level[_count] = (byte)(p.InitialStock <= 0 ? 2 : p.InitialStock <= p.LowStockThreshold ? 1 : 0);
            return _count++;
        }
    }

    public int CopyEvents(Span<CommerceEvent> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _evCount);
            for (var i = 0; i < n; i++) dst[i] = _events[(_evHead - 1 - i + EventCap) % EventCap];
            return n;
        }
    }

    // ══════════════════════ 무할당 수신 경로 ══════════════════════

    /// <summary>재고 예약(CAS · 초과 판매 차단). 반환 = 성공.</summary>
    public bool TryReserve(int slot, int qty)
    {
        if ((uint)slot >= (uint)_count || qty <= 0) return false;
        ref var s = ref _stock[slot];
        while (true)
        {
            var cur = Volatile.Read(ref s);
            if (cur < qty) return false;
            if (Interlocked.CompareExchange(ref s, cur - qty, cur) == cur) return true;
        }
    }

    /// <summary>주문 1건 수신(예약 · 집계 · 임계/폭주 트리거). 반환 = 수락. 무할당.</summary>
    public bool RecordOrder(int slot, int qty, string? buyer, double nowMs)
    {
        if (!TryReserve(slot, qty))
        {
            Interlocked.Increment(ref _rejected);
            Emit(new CommerceEvent(nowMs, CommerceEventKind.Rejected, slot, qty, (uint)slot < (uint)_count ? StockOf(slot) : 0, buyer));
            return false;
        }
        Interlocked.Add(ref _sold[slot], qty);
        Interlocked.Increment(ref _orders);
        Interlocked.Add(ref _units, qty);
        var remaining = StockOf(slot);
        var p = _products[slot]!;
        var surgeStart = false;
        lock (_gate)
        {
            _revenue += (p.SalePrice ?? p.Price) * qty;
            Bump(nowMs);
            if (!_surge && _windowSum >= SurgePerMinute) { _surge = true; surgeStart = true; }
        }
        Emit(new CommerceEvent(nowMs, CommerceEventKind.Order, slot, qty, remaining, buyer));
        CheckLevel(slot, remaining, nowMs);
        if (surgeStart) Emit(new CommerceEvent(nowMs, CommerceEventKind.SurgeStart, slot, 0, remaining, null));
        return true;
    }

    /// <summary>주기 틱(초 경과 반영 · 폭주 해제 히스테리시스 = 임계의 50% 미만).</summary>
    public void Tick(double nowMs)
    {
        bool end;
        lock (_gate)
        {
            Advance(nowMs);
            end = _surge && _windowSum < SurgePerMinute / 2;
            if (end) _surge = false;
        }
        if (end) Emit(new CommerceEvent(nowMs, CommerceEventKind.SurgeEnd, -1, 0, 0, null));
    }

    private void Advance(double nowMs)
    {
        var sec = (long)(nowMs / 1000);
        if (_secStamp < 0) { _secStamp = sec; return; }
        var gap = sec - _secStamp;
        if (gap <= 0) return;
        if (gap >= WindowSec) { Array.Clear(_perSec); _windowSum = 0; }
        else for (var k = 1; k <= gap; k++) { var i = (int)((_secStamp + k) % WindowSec); _windowSum -= _perSec[i]; _perSec[i] = 0; }
        _secStamp = sec;
    }

    private void Bump(double nowMs)
    {
        Advance(nowMs);
        _perSec[(int)(Math.Max(0, _secStamp) % WindowSec)]++;
        _windowSum++;
    }

    /// <summary>외부 재고 동기(절대값) — 임계 교차 트리거.</summary>
    public void SetStock(int slot, int stock, double nowMs)
    {
        if ((uint)slot >= (uint)_count) return;
        Volatile.Write(ref _stock[slot], Math.Max(0, stock));
        CheckLevel(slot, Math.Max(0, stock), nowMs);
    }

    private void CheckLevel(int slot, int remaining, double nowMs)
    {
        var p = _products[slot]!;
        var lvl = (byte)(remaining <= 0 ? 2 : remaining <= p.LowStockThreshold ? 1 : 0);
        byte prev;
        lock (_gate) { prev = _level[slot]; _level[slot] = lvl; }
        if (lvl == prev) return;
        if (lvl == 2) Emit(new CommerceEvent(nowMs, CommerceEventKind.SoldOut, slot, 0, 0, null));
        else if (lvl == 1 && prev == 0) Emit(new CommerceEvent(nowMs, CommerceEventKind.LowStock, slot, 0, remaining, null));
        else if (lvl < prev && prev == 2) Emit(new CommerceEvent(nowMs, CommerceEventKind.Restock, slot, 0, remaining, null));
    }

    private void Emit(in CommerceEvent e)
    {
        lock (_gate)
        {
            _events[_evHead] = e;
            _evHead = (_evHead + 1) % EventCap;
            if (_evCount < EventCap) _evCount++;
        }
        try { OnEvent?.Invoke(e); } catch (Exception ex) { PromptFlowStudio.Program.Log("Commerce.OnEvent", ex); }
    }

    // ══════════════════════ 웹훅 ══════════════════════

    public static string ComputeSignature(ReadOnlySpan<byte> body, string secret)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, mac);
        return Convert.ToBase64String(mac);
    }

    public static bool VerifySignature(ReadOnlySpan<byte> body, string? signatureBase64, string? secret)
    {
        if (string.IsNullOrEmpty(signatureBase64) || string.IsNullOrEmpty(secret)) return false;
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, expected);
        Span<byte> given = stackalloc byte[64];
        if (!Convert.TryFromBase64String(signatureBase64, given, out var n) || n != 32) return false;
        return CryptographicOperations.FixedTimeEquals(expected, given[..32]);
    }

    /// <summary>주문 생성 웹훅 처리(서명 검증 → 멱등 → 품목별 주문 기록). 반환 = 기록된 품목 수(-1 = 서명 실패 · -2 = 스키마 위반 · 0 = 중복).</summary>
    public int HandleWebhook(CommerceProvider provider, byte[] body, string? signature, double nowMs)
    {
        var secret = _env(provider == CommerceProvider.Shopify ? ShopifySecretVar : WooWebhookVar);
        if (!VerifySignature(body, signature, secret)) { Interlocked.Increment(ref _badSignatures); LastAction = "⛔ 웹훅 서명 불일치"; Raise(); return -1; }
        Interlocked.Increment(ref _webhooks);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("id", out var idEl) || !r.TryGetProperty("line_items", out var items) || items.ValueKind != JsonValueKind.Array) return -2;
            var id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText() : idEl.GetString() ?? "";
            lock (_gate)
            {
                if (!_seenOrders.Add(provider + ":" + id)) return 0;
                if (_seenOrders.Count > SeenCap) _seenOrders.Clear();
            }
            var buyer = BuyerName(provider, r);
            var n = 0;
            foreach (var it in items.EnumerateArray())
            {
                if (!it.TryGetProperty("quantity", out var q) || !q.TryGetInt32(out var qty) || qty <= 0) return n == 0 ? -2 : n;
                var slot = -1;
                if (it.TryGetProperty("sku", out var sku) && sku.ValueKind == JsonValueKind.String) slot = SlotOf(sku.GetString()!);
                if (slot < 0 && it.TryGetProperty("product_id", out var pid)) slot = SlotOfExternal(pid.ValueKind == JsonValueKind.Number ? pid.GetRawText() : pid.GetString() ?? "");
                if (slot < 0) continue;
                RecordOrder(slot, qty, buyer, nowMs);
                n++;
            }
            LastAction = "🔔 웹훅 주문 · " + Label(provider) + " · " + n.ToString(CultureInfo.InvariantCulture) + "품목";
            Raise();
            return n;
        }
        catch (JsonException) { return -2; }
    }

    private static string? BuyerName(CommerceProvider p, JsonElement r)
    {
        if (p == CommerceProvider.Shopify && r.TryGetProperty("customer", out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("first_name", out var fn) && fn.ValueKind == JsonValueKind.String) return fn.GetString();
        if (r.TryGetProperty("billing", out var b) && b.ValueKind == JsonValueKind.Object && b.TryGetProperty("first_name", out var bn) && bn.ValueKind == JsonValueKind.String) return bn.GetString();
        return r.TryGetProperty("buyer", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
    }

    /// <summary>방송 표시용 구매자 마스킹(첫 글자 + **).</summary>
    public static string MaskBuyer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "익명";
        var t = name.Trim();
        return t.Length == 1 ? t + "*" : t[0] + new string('*', Math.Min(2, t.Length - 1)) + (t.Length > 3 ? t[^1].ToString() : "");
    }

    // ══════════════════════ 폴링 ══════════════════════

    /// <summary>재고 동기 1회(공급자별 요청 · 스키마 검증 · 백오프). 반환 = 갱신 상품 수(-1 = 백오프/실패/설정 없음).</summary>
    public async Task<int> PollInventoryAsync(double nowMs, CancellationToken ct = default)
    {
        if (InBackoff) return -1;
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback)) { LastAction = "⚠ 커머스 API URL(https) 필요"; return -1; }
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(PollTimeoutMs);
        try
        {
            var updated = 0;
            if (Provider == CommerceProvider.CustomRest)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "inventory"));
                if (_env(CustomKeyVar) is { Length: > 0 } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                var arr = await GetJsonAsync(req, d.Token).ConfigureAwait(false);
                if (arr.ValueKind != JsonValueKind.Array) throw new FormatException("재고 응답 스키마 불일치(배열 아님)");
                foreach (var e in arr.EnumerateArray())
                {
                    if (!e.TryGetProperty("sku", out var s) || s.ValueKind != JsonValueKind.String || !e.TryGetProperty("stock", out var st) || !st.TryGetInt32(out var stock)) throw new FormatException("재고 항목 스키마 불일치(sku/stock)");
                    var slot = SlotOf(s.GetString()!);
                    if (slot >= 0) { SetStock(slot, stock, nowMs); updated++; }
                }
            }
            else
            {
                for (var i = 0; i < _count; i++)
                {
                    var p = _products[i]!;
                    if (string.IsNullOrEmpty(p.ExternalId)) continue;
                    var stock = Provider == CommerceProvider.Shopify ? await ShopifyStockAsync(baseUri, p.ExternalId, d.Token).ConfigureAwait(false) : await WooStockAsync(baseUri, p.ExternalId, d.Token).ConfigureAwait(false);
                    SetStock(i, stock, nowMs);
                    updated++;
                }
            }
            Interlocked.Exchange(ref _backoffFails, 0);
            LastAction = "🔄 재고 동기 · " + Label(Provider) + " · " + updated.ToString(CultureInfo.InvariantCulture) + "건";
            Raise();
            return updated;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or InvalidOperationException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            var fails = Interlocked.Increment(ref _backoffFails);
            Interlocked.Exchange(ref _backoffUntil, Environment.TickCount64 + Math.Min(60_000, 500L << Math.Min(fails, 7)));
            LastAction = "⚠ 재고 동기 실패(백오프) · " + ex.GetType().Name;
            Raise();
            return -1;
        }
    }

    private async Task<int> ShopifyStockAsync(Uri baseUri, string id, CancellationToken ct)
    {
        var token = _env(ShopifyTokenVar);
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Shopify 토큰 없음(" + ShopifyTokenVar + ")");
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "admin/api/2024-07/products/" + Uri.EscapeDataString(id) + ".json"));
        req.Headers.Add("X-Shopify-Access-Token", token);
        var r = await GetJsonAsync(req, ct).ConfigureAwait(false);
        if (!r.TryGetProperty("product", out var p) || !p.TryGetProperty("variants", out var vs) || vs.ValueKind != JsonValueKind.Array) throw new FormatException("Shopify 스키마 불일치(product.variants)");
        var sum = 0;
        foreach (var v in vs.EnumerateArray())
        {
            if (!v.TryGetProperty("inventory_quantity", out var q) || !q.TryGetInt32(out var n)) throw new FormatException("Shopify 스키마 불일치(inventory_quantity)");
            sum += Math.Max(0, n);
        }
        return sum;
    }

    private async Task<int> WooStockAsync(Uri baseUri, string id, CancellationToken ct)
    {
        var key = _env(WooKeyVar); var secret = _env(WooSecretVar);
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret)) throw new InvalidOperationException("WooCommerce 키 없음(" + WooKeyVar + "/" + WooSecretVar + ")");
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "wp-json/wc/v3/products/" + Uri.EscapeDataString(id)));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":" + secret)));
        var r = await GetJsonAsync(req, ct).ConfigureAwait(false);
        if (!r.TryGetProperty("stock_quantity", out var q) || !q.TryGetInt32(out var n)) throw new FormatException("WooCommerce 스키마 불일치(stock_quantity)");
        return Math.Max(0, n);
    }

    private async Task<JsonElement> GetJsonAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    // ══════════════════════ 데모 ══════════════════════

    private static readonly string[] DemoBuyers = { "김민지", "이서준", "박하은", "최도윤", "정지우", "강서연", "조예준", "윤수아", "Emma", "Liam" };

    /// <summary>데모 주문 트래픽(시드 고정 · 초당 rate 건 기대). 반환 = 수락 건수.</summary>
    public int SimulateTraffic(Random rng, int slot, double rate, double dtSec, double nowMs)
    {
        var expect = rate * dtSec;
        var n = (int)expect + (rng.NextDouble() < expect - (int)expect ? 1 : 0);
        var ok = 0;
        for (var i = 0; i < n; i++) if (RecordOrder(slot, 1 + (rng.Next(10) == 0 ? 1 : 0), DemoBuyers[rng.Next(DemoBuyers.Length)], nowMs)) ok++;
        Tick(nowMs);
        return ok;
    }

    public static string Label(CommerceProvider p) => p switch { CommerceProvider.Shopify => "Shopify", CommerceProvider.WooCommerce => "WooCommerce", _ => "REST API" };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Commerce.OnChange", ex); } }

    public void Dispose() => _http.Dispose();
}
