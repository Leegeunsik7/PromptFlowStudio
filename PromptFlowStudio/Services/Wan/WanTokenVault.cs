using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;   // #if WINDOWS 구간 ProtectedData(DPAPI · WindowsDesktop 공유 프레임워크 · 외부 NuGet 0)
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using PromptFlowStudio.Services.Security;
using PromptFlowStudio.Services.Telemetry;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlowStudio] v15.11.0 다중 클라우드 Key Vault — 프로바이더(DashScope/SiliconFlow/Custom)별 다중 API 키 순환 큐.
///   ▸ <see cref="UpdateProviderKeys"/>: 관리자 UI 입력 즉시 호출 — 프로바이더별 슬롯 버퍼를 재사용(용량 내 0B)하며 공백 제외·중복 제거 후 원자적 교체.
///   ▸ <see cref="TryGetNextKey"/>: 활성 키 라운드로빈 순환(0B) — 호출마다 다음 키로 커서 전진(쿼터 분산).
///   ▸ <see cref="MarkExhausted"/>/<see cref="RotateKey"/>: 402(영구 소진)·429(쿨다운) 키를 소진 표시하고 다음 활성 키로 즉시 전환(0B).
///     소진 상태는 키 목록 갱신 시 동일 키에 승계(더블 버퍼 스왑 · 0B) — 키 문자열이 바뀌면 새 키로 간주되어 초기화.
///   ▸ v15.12.0 영구 저장: <c>%LocalAppData%\PromptFlowStudio\Config\vault.enc</c> — JSON(UTF-8) → <b>DPAPI</b>(CurrentUser) 바이너리.
///     비Windows(모바일 링크)는 <see cref="LocalCipher"/>(AES-256-GCM) 스텁. 변경마다 백그라운드 저장(중복 요청 합류) · .tmp → 교체(원자적).
///     변경 시 <see cref="OnKeysChanged"/> 발행(구독자 예외 격리) · 저장 결과는 <see cref="IsPersisted"/>/<see cref="OnPersisted"/>.
///   ▸ v15.14.0 프로바이더 엔드포인트(<see cref="SetProviderEndpoint"/>): Custom BaseUrl 등을 키와 함께 암호화 보관({Keys, Endpoints} · 레거시 키-전용 포맷 자동 인식).
///   ▸ v15.15.0 Lock-Free: 링은 시퀀스 락(seqlock) — 순환/조회는 낙관적 읽기 + 시퀀스 검증(락 0 · 재시도),
///     커서는 CAS 전진(경합 시 키 이중 배정 0), 재구성·소진 표시는 CAS 게이트 단일 작성자.
///     변경 신호는 <see cref="LockFreeBroadcast{T}"/>(스냅샷 배열 0B) + <see cref="Subscribe"/> Channel 비동기 구독(UI 블로킹 0).
/// </summary>
public sealed class WanTokenVault
{
    public const string DashScope = "DashScope";
    public const string SiliconFlow = "SiliconFlow";
    public const string Custom = "Custom";

    /// <summary>UI 기본 노출 순서.</summary>
    public static readonly string[] Providers = { DashScope, SiliconFlow, Custom };

    /// <summary>429 기본 쿨다운(경과 후 자동 복귀).</summary>
    public static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(60);

    private const string KeyPurpose = "PromptFlowStudio.WanTokenVault.v1";
#if WINDOWS
    private static readonly byte[] Entropy = "PromptFlowStudio.WanTokenVault.v1"u8.ToArray();
#endif

    /// <summary>기본 저장 경로(%LocalAppData%\PromptFlowStudio\Config\vault.enc).</summary>
    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Config", "vault.enc");

    /// <summary>현재 플랫폼의 디스크 암호화 방식 표기.</summary>
    public static string CipherName =>
#if WINDOWS
        "DPAPI";
#else
        "AES-256-GCM";
#endif

    private readonly ConcurrentDictionary<string, KeyRing> _rings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _ioGate = new();   // 파일 I/O 직렬화 전용(핫패스 아님) · v16.11.0: 프로세스 전역 — 다른 인스턴스의 백그라운드 저장(.tmp→교체) 중 로드 경합 차단
    private readonly LockFreeBroadcast<string> _keysHub = new();
    private readonly LockFreeBroadcast<bool> _persistHub = new();
    private readonly LockFreeBroadcast<VaultSignal> _signals = new();
    private readonly string? _storePath;
    private int _saveQueued;
    private long _version;
    private volatile bool _persisted;

    /// <summary>DI 기본 — 기본 경로에서 복원하고 변경 시 자동 저장.</summary>
    public WanTokenVault() : this(DefaultStorePath) { }

    /// <summary><paramref name="storePath"/> 가 null 이면 인메모리 전용(디스크 I/O 없음).</summary>
    public WanTokenVault(string? storePath)
    {
        _storePath = storePath;
        if (storePath is not null) LoadFromDisk();
    }

    /// <summary>저장 경로(인메모리 전용이면 null).</summary>
    public string? StorePath => _storePath;

    /// <summary>키 목록·소진 상태 변경 통지(인자: 프로바이더명 · 발행 스레드 동기 호출 · 예외 격리).</summary>
    public event Action<string>? OnKeysChanged
    {
        add => _keysHub.Add(value);
        remove => _keysHub.Remove(value);
    }

    /// <summary>디스크 저장 시도 완료 통지(인자: 성공 여부).</summary>
    public event Action<bool>? OnPersisted
    {
        add => _persistHub.Add(value);
        remove => _persistHub.Remove(value);
    }

    /// <summary>
    /// 비동기 Pub-Sub 구독 — 키 변경·저장 완료 신호를 Channel 로 수신(발행자 비차단 · 느린 소비자는 오래된 신호 드롭).
    /// <paramref name="ct"/> 취소 시 구독 해제 + 리더 완료.
    /// </summary>
    public ChannelReader<VaultSignal> Subscribe(CancellationToken ct) => _signals.Subscribe(ct);

    /// <summary>현재 Channel 구독자 수.</summary>
    public int ChannelSubscribers => _signals.ChannelSubscribers;

    /// <summary>격리된 동기 구독자 예외 누적.</summary>
    public long SubscriberFaults => _keysHub.Faults + _persistHub.Faults;

    /// <summary>최근 저장/복원이 암호화 디스크본과 일치하면 true.</summary>
    public bool IsPersisted => _persisted;

    /// <summary>변경 누적 카운터(동기화 감지용).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>프로바이더 키 목록을 통째로 교체하고 순환 큐를 즉시 갱신한다. 공백 키는 무시, 중복은 1개만 유지.</summary>
    public void UpdateProviderKeys(string provider, IEnumerable<string> keys)
    {
        ApplyKeys(provider, keys);
        ScheduleSave();
        RaiseChanged(provider);
    }

    private void ApplyKeys(string provider, IEnumerable<string> keys)
    {
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("provider 가 비어 있습니다.", nameof(provider));
        ArgumentNullException.ThrowIfNull(keys);

        // 배열/List — 인덱스 순회(열거자 박싱 회피). 그 외 시퀀스는 작성 구간 밖에서 1회 물질화.
        var list = keys as IList<string> ?? new List<string>(keys);
        _rings.GetOrAdd(provider, static _ => new KeyRing()).Rebuild(list);
        Interlocked.Increment(ref _version);
    }

    private void RaiseChanged(string provider)
    {
        _keysHub.Publish(provider);                                                   // 스냅샷 순회 0B · 예외 격리
        _signals.Publish(new VaultSignal(VaultSignalKind.KeysChanged, provider, false));   // Channel 구독자(비차단)
        ZenithEventSource.Log.OnVaultSignal(VaultSignalKind.KeysChanged, provider, false);  // v15.17.0 ETW(0B)
    }

    // ══ 프로바이더 엔드포인트(Custom BaseUrl) ══════════════════════════════
    /// <summary>
    /// 프로바이더 BaseUrl 설정(빈 값 = 해제). http/https 절대 URL 만 허용 · 끝 '/' 제거 정규화.
    /// 유효하지 않으면 false(기존 값 유지). 변경 시 백그라운드 암호화 저장 + <see cref="OnKeysChanged"/>.
    /// </summary>
    public bool SetProviderEndpoint(string provider, string? url)
    {
        if (string.IsNullOrWhiteSpace(provider)) return false;
        if (string.IsNullOrWhiteSpace(url))
        {
            if (!_endpoints.TryRemove(provider, out _)) return true;
        }
        else
        {
            if (!TryNormalizeEndpoint(url, out var norm)) return false;
            if (_endpoints.TryGetValue(provider, out var cur) && string.Equals(cur, norm, StringComparison.Ordinal)) return true;
            _endpoints[provider] = norm;
        }
        Interlocked.Increment(ref _version);
        ScheduleSave();
        RaiseChanged(provider);
        return true;
    }

    /// <summary>프로바이더 BaseUrl(미설정이면 null).</summary>
    public string? GetProviderEndpoint(string provider)
        => provider is not null && _endpoints.TryGetValue(provider, out var u) ? u : null;

    /// <summary>http/https 절대 URL 검증 + 끝 '/' 제거.</summary>
    public static bool TryNormalizeEndpoint(string? url, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        var t = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.Host.Length == 0) return false;
        normalized = t;
        return true;
    }

    /// <summary>라운드로빈으로 다음 <b>활성(미소진)</b> 키를 꺼낸다(0B). 활성 키가 없으면 false.</summary>
    public bool TryGetNextKey(string provider, out string key)
    {
        key = "";
        return provider is not null && _rings.TryGetValue(provider, out var ring) && ring.TryNext(out key);
    }

    /// <summary>
    /// <paramref name="key"/> 를 소진 처리. <paramref name="cooldown"/> 이 null 이면 영구(402 · 키 재입력 시 해제), 아니면 해당 시간 뒤 자동 복귀(429).
    /// 대상 키가 없으면 false.
    /// </summary>
    public bool MarkExhausted(string provider, string key, TimeSpan? cooldown = null)
    {
        if (provider is null || key is null || !_rings.TryGetValue(provider, out var ring)) return false;
        bool hit = ring.Mark(key, cooldown is TimeSpan cd ? Environment.TickCount64 + (long)cd.TotalMilliseconds : long.MaxValue);
        if (hit)
        {
            Interlocked.Increment(ref _version);
            RaiseChanged(provider);
        }
        return hit;
    }

    /// <summary>키가 현재 소진 상태인지(쿨다운 경과 시 false · 미등록 키는 false).</summary>
    public bool IsExhausted(string provider, string key)
    {
        if (provider is null || key is null || !_rings.TryGetValue(provider, out var ring)) return false;
        return ring.IsExhausted(key.Trim(), Environment.TickCount64);
    }

    /// <summary>
    /// 실패한 <paramref name="failedKey"/> 다음 위치부터 <b>활성</b> 키를 찾아 전환(0B). 대체 활성 키가 없으면 false.
    /// ⚠ 소진 표시는 <see cref="MarkExhausted"/> 가 담당(호출부가 먼저 표시).
    /// </summary>
    public bool RotateKey(string provider, string failedKey, out string nextKey)
    {
        nextKey = "";
        return provider is not null && _rings.TryGetValue(provider, out var ring) && ring.TryRotate(failedKey, out nextKey);
    }

    // ══ 영구 저장(DPAPI 암호화) ═════════════════════════════════════════
    /// <summary>현재 키 목록을 JSON → DPAPI 암호화하여 저장(.tmp → 교체). 인메모리 전용이면 no-op. 실패는 false(예외 없음).</summary>
    public bool SaveToDisk()
    {
        if (_storePath is null) return false;
        bool ok;
        byte[]? plain = null;
        try
        {
            // v16.11.0: 스냅숏~기록을 한 임계구역으로 — 백그라운드 저장이 갱신 전 스냅숏을 늦게 기록해 최신 저장본을 덮던 경합 차단.
            lock (_ioGate)
            {
                var snap = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var kv in _rings) snap[kv.Key] = kv.Value.Snapshot();
                var eps = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in _endpoints) eps[kv.Key] = kv.Value;
                plain = JsonSerializer.SerializeToUtf8Bytes(new VaultFile(snap, eps), WanJsonContext.Default.VaultFile);
                var sealedBytes = Seal(plain);
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                var tmp = _storePath + ".tmp";
                File.WriteAllBytes(tmp, sealedBytes);
                File.Move(tmp, _storePath, overwrite: true);
            }
            ok = true;
        }
        catch { ok = false; }
        finally
        {
            if (plain is not null) SecureSecretVault.Wipe(plain.AsSpan());   // 평문 버퍼 즉시 소거
        }
        _persisted = ok;
        RaisePersisted(ok);
        return ok;
    }

    /// <summary>암호화 저장본을 복호화해 복원. 파일 없음·타 사용자/PC·손상 시 false(기존 상태 유지 · 예외 없음).</summary>
    public bool LoadFromDisk()
    {
        if (_storePath is null) return false;
        byte[]? plain = null;
        try
        {
            byte[] sealedBytes;
            lock (_ioGate)
            {
                if (!File.Exists(_storePath)) return false;
                sealedBytes = File.ReadAllBytes(_storePath);
            }
            plain = Open(sealedBytes);
            if (plain is null || plain.Length == 0) return false;
            // v15.16.0: WanJsonContext 소스 생성 역직렬화(리플렉션 0).
            // v15.14.0 {Keys, Endpoints} · 레거시(v15.12.0) = 루트가 곧 키 사전.
            var file = JsonSerializer.Deserialize(plain, WanJsonContext.Default.VaultFile);
            var keys = file?.Keys ?? (file?.Endpoints is null ? JsonSerializer.Deserialize(plain, WanJsonContext.Default.KeyMap) : null);
            if (keys is null && file?.Endpoints is null) return false;
            if (keys is not null) foreach (var kv in keys)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null) continue;
                var list = new List<string>(kv.Value.Length);
                foreach (var e in kv.Value)
                    if (e is not null) list.Add(e);
                ApplyKeys(kv.Key, list);
                RaiseChanged(kv.Key);
            }
            if (file?.Endpoints is { } eps)
            {
                foreach (var kv in eps)
                    if (TryNormalizeEndpoint(kv.Value, out var norm))
                        _endpoints[kv.Key] = norm;
            }
            _persisted = true;
            return true;
        }
        catch { return false; }
        finally
        {
            if (plain is not null) SecureSecretVault.Wipe(plain.AsSpan());
        }
    }

    private static byte[] Seal(byte[] plain)
    {
#if WINDOWS
        return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
#else
        return Encoding.ASCII.GetBytes(LocalCipher.Protect(Encoding.UTF8.GetString(plain), KeyPurpose));
#endif
    }

    private static byte[]? Open(byte[] sealedBytes)
    {
#if WINDOWS
        try { return ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { /* 이전 포맷(LocalCipher base64 봉투) 마이그레이션 시도 */ }
#endif
        var json = LocalCipher.Unprotect(Encoding.ASCII.GetString(sealedBytes), KeyPurpose);
        return json.Length == 0 ? null : Encoding.UTF8.GetBytes(json);
    }

    private void RaisePersisted(bool ok)
    {
        _persistHub.Publish(ok);
        _signals.Publish(new VaultSignal(VaultSignalKind.Persisted, null, ok));
        ZenithEventSource.Log.OnVaultSignal(VaultSignalKind.Persisted, null, ok);
    }

    // 백그라운드 저장 — 대기 중인 저장이 있으면 합류(연속 타이핑 시 1회로 수렴).
    private void ScheduleSave()
    {
        if (_storePath is null) return;
        if (Interlocked.Exchange(ref _saveQueued, 1) == 1) return;
        ThreadPool.UnsafeQueueUserWorkItem(static v =>
        {
            Volatile.Write(ref v._saveQueued, 0);
            v.SaveToDisk();   // 예외는 내부에서 격리
        }, this, preferLocal: false);
    }

    /// <summary>등록 키 개수(소진 포함).</summary>
    public int GetKeyCount(string provider)
    {
        return provider is not null && _rings.TryGetValue(provider, out var ring) ? ring.Count : 0;
    }

    /// <summary>활성(미소진) 키 개수.</summary>
    public int GetActiveKeyCount(string provider)
    {
        return provider is not null && _rings.TryGetValue(provider, out var ring) ? ring.ActiveCount(Environment.TickCount64) : 0;
    }

    /// <summary>현재 키 목록을 호출자 버퍼에 복사(UI 초기 로드용).</summary>
    public void CopyKeys(string provider, List<string> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        if (provider is null || !_rings.TryGetValue(provider, out var ring)) return;
        ring.CopyTo(destination);
    }

    internal sealed record VaultFile(Dictionary<string, string[]>? Keys, Dictionary<string, string>? Endpoints);

    /// <summary>
    /// 슬롯/소진기한 더블 버퍼 + 시퀀스 락(seqlock) — 락 0.
    ///   ▸ 작성자(재구성·소진 표시): CAS 게이트 단일화 → 시퀀스 홀수(작성 중) → Back 에 쌓고 스왑(용량 내 0B · 동일 키 소진 상태 승계) → 짝수.
    ///   ▸ 독자(순환/조회): 짝수 시퀀스에서 낙관적 읽기 → 재확인, 변했으면 재시도(재사용 버퍼의 찢어진 읽기 폐기).
    ///   ▸ 커서는 CAS 전진 — 동시 TryNext 는 서로 다른 슬롯을 받는다(라운드로빈 정확성 유지).
    /// </summary>
    private sealed class KeyRing
    {
        private int _seq;      // 짝수 = 안정 · 홀수 = 재구성 중
        private int _gate;     // 0 = 비어 있음 · 1 = 작성자(재구성·소진 표시) 점유(CAS)
        private string[] _slots = new string[4];
        private long[] _until = new long[4];   // TickCount64 기준 소진 해제 시각(0 = 활성 · MaxValue = 영구 소진)
        private int _count;
        private int _cursor;

        private string[] _backSlots = new string[4];
        private long[] _backUntil = new long[4];
        private int _backCount;

        public int Count
        {
            get
            {
                var spin = new SpinWait();
                while (true)
                {
                    int s = BeginRead(ref spin);
                    int n = Volatile.Read(ref _count);
                    if (Validate(s)) return n;
                    spin.SpinOnce();
                }
            }
        }

        // ── seqlock 프리미티브 ──────────────────────────────────────────
        private int BeginRead(ref SpinWait spin)
        {
            while (true)
            {
                int s = Volatile.Read(ref _seq);
                if ((s & 1) == 0) return s;
                spin.SpinOnce();
            }
        }

        private bool Validate(int s)
        {
            Interlocked.MemoryBarrier();   // 앞선 일반 읽기가 재확인 뒤로 밀리지 않게
            return Volatile.Read(ref _seq) == s;
        }

        // 한 세대의 (슬롯, 기한, 개수) — 개수는 두 배열 길이로 클램프(세대 혼합 읽기도 범위 안전 · 검증에서 폐기).
        private int Load(out string[] slots, out long[] until)
        {
            slots = Volatile.Read(ref _slots);
            until = Volatile.Read(ref _until);
            int n = Volatile.Read(ref _count);
            if (n > slots.Length) n = slots.Length;
            if (n > until.Length) n = until.Length;
            return n;
        }

        private static int IndexOf(string[] slots, int count, string key)
        {
            for (int i = 0; i < count; i++)
                if (string.Equals(slots[i], key, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary><paramref name="from"/> 부터 한 바퀴 돌며 활성 키 인덱스(<paramref name="skip"/> 제외). 없으면 -1.</summary>
        private static int FindActive(string[] slots, long[] until, int count, int from, string? skip, long now)
        {
            if (count == 0) return -1;
            if ((uint)from >= (uint)count) from = 0;
            for (int n = 0; n < count; n++)
            {
                int i = from + n;
                if (i >= count) i -= count;
                if (Volatile.Read(ref until[i]) > now) continue;
                if (skip is not null && string.Equals(slots[i], skip, StringComparison.Ordinal)) continue;
                return i;
            }
            return -1;
        }

        // ── 독자 ────────────────────────────────────────────────────────
        public bool TryNext(out string key)
        {
            var spin = new SpinWait();
            while (true)
            {
                int s = BeginRead(ref spin);
                int n = Load(out var slots, out var until);
                int cur = Volatile.Read(ref _cursor);
                int idx = FindActive(slots, until, n, cur, null, Environment.TickCount64);
                string? k = idx >= 0 ? slots[idx] : null;
                if (!Validate(s)) { spin.SpinOnce(); continue; }
                if (k is null) { key = ""; return false; }
                // 커서 CAS — 동시 순환과 겹치거나 전진 직후 재구성되면 재시도(같은 키 이중 배정 0).
                if (Interlocked.CompareExchange(ref _cursor, idx + 1 == n ? 0 : idx + 1, cur) == cur && Validate(s))
                {
                    key = k;
                    return true;
                }
                spin.SpinOnce();
            }
        }

        public bool TryRotate(string? failedKey, out string nextKey)
        {
            var spin = new SpinWait();
            while (true)
            {
                int s = BeginRead(ref spin);
                int n = Load(out var slots, out var until);
                int idx = -1;
                if (n > 0)
                {
                    int at = failedKey is null ? -1 : IndexOf(slots, n, failedKey);
                    int from = at < 0 ? Volatile.Read(ref _cursor) : (at + 1 == n ? 0 : at + 1);
                    idx = FindActive(slots, until, n, from, failedKey, Environment.TickCount64);
                }
                string? k = idx >= 0 ? slots[idx] : null;
                if (!Validate(s)) { spin.SpinOnce(); continue; }
                if (k is null) { nextKey = ""; return false; }
                Volatile.Write(ref _cursor, idx + 1 == n ? 0 : idx + 1);
                if (!Validate(s)) { spin.SpinOnce(); continue; }
                nextKey = k;
                return true;
            }
        }

        /// <summary>
        /// 소진 기한 기록(402/429 시에만 · 드묾) — 작성자 게이트를 잠깐 점유해 재구성과 직렬화.
        /// 낙관적 쓰기는 재사용 버퍼(다른 세대)의 엉뚱한 슬롯을 오염시킬 수 있어 금지. 독자는 계속 무대기(시퀀스 불변).
        /// </summary>
        public bool Mark(string key, long until)
        {
            EnterGate();
            try
            {
                int at = IndexOf(_slots, _count, key);
                if (at >= 0) Interlocked.Exchange(ref _until[at], until);
                return at >= 0;
            }
            finally { Volatile.Write(ref _gate, 0); }
        }

        public bool IsExhausted(string key, long now)
        {
            var spin = new SpinWait();
            while (true)
            {
                int s = BeginRead(ref spin);
                int n = Load(out var slots, out var until);
                int at = IndexOf(slots, n, key);
                bool dead = at >= 0 && Volatile.Read(ref until[at]) > now;
                if (Validate(s)) return dead;
                spin.SpinOnce();
            }
        }

        public int ActiveCount(long now)
        {
            var spin = new SpinWait();
            while (true)
            {
                int s = BeginRead(ref spin);
                int n = Load(out _, out var until), active = 0;
                for (int i = 0; i < n; i++) if (Volatile.Read(ref until[i]) <= now) active++;
                if (Validate(s)) return active;
                spin.SpinOnce();
            }
        }

        public void CopyTo(List<string> destination)
        {
            var spin = new SpinWait();
            while (true)
            {
                destination.Clear();
                int s = BeginRead(ref spin);
                int n = Load(out var slots, out _);
                for (int i = 0; i < n; i++) destination.Add(slots[i]);
                if (Validate(s)) return;
                spin.SpinOnce();
            }
        }

        public string[] Snapshot()
        {
            var spin = new SpinWait();
            while (true)
            {
                int s = BeginRead(ref spin);
                int n = Load(out var slots, out _);
                var arr = n == 0 ? Array.Empty<string>() : new string[n];
                Array.Copy(slots, arr, n);
                if (Validate(s)) return arr;
                spin.SpinOnce();
            }
        }

        // ── 작성자 ──────────────────────────────────────────────────────
        /// <summary>목록 재구성(공백 제외 · 트림 · 중복 제거 · 동일 키 소진 상태 승계). 작성 구간 전체를 홀수 시퀀스로 덮어 소진 표시 경합 차단.</summary>
        public void Rebuild(IList<string> keys)
        {
            EnterGate();
            Interlocked.Increment(ref _seq);   // 홀수 — 독자는 대기/재시도
            try
            {
                _backCount = 0;
                for (int i = 0; i < keys.Count; i++) AddUnique(keys[i]);
                (_slots, _backSlots) = (_backSlots, _slots);
                (_until, _backUntil) = (_backUntil, _until);
                _count = _backCount;
                Array.Clear(_backSlots);                          // 이전 세대 키 참조 해제
                Array.Clear(_slots, _count, _slots.Length - _count);
                if (_cursor >= _count) _cursor = 0;
            }
            finally
            {
                Interlocked.Increment(ref _seq);   // 짝수 — 새 세대 공개
                Volatile.Write(ref _gate, 0);
            }
        }

        private void EnterGate()
        {
            var spin = new SpinWait();
            while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0) spin.SpinOnce();
        }

        private void AddUnique(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var k = raw.Trim();   // 앞뒤 공백 없으면 동일 인스턴스(0B)
            for (int i = 0; i < _backCount; i++)
                if (string.Equals(_backSlots[i], k, StringComparison.Ordinal)) return;
            if (_backCount == _backSlots.Length)
            {
                Array.Resize(ref _backSlots, _backSlots.Length * 2);
                Array.Resize(ref _backUntil, _backUntil.Length * 2);
            }
            int prev = IndexOf(_slots, _count, k);
            _backUntil[_backCount] = prev >= 0 ? Interlocked.Read(ref _until[prev]) : 0;
            _backSlots[_backCount++] = k;
        }
    }
}
