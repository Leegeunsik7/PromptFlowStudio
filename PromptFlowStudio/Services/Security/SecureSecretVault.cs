using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;   // SHA256(전 플랫폼) · #if WINDOWS 구간의 ProtectedData 도 동일 네임스페이스(Windows Desktop 공유 프레임워크 제공 · 외부 NuGet 0)
using System.Text;
using System.Threading;

namespace PromptFlowStudio.Services.Security;

/// <summary>시크릿이 실제로 어디서 왔는지 — HUD/감사 표시용.</summary>
public enum SecretSource { EnvironmentVariable, DpapiVault, GeneratedAndSealed, InsecureFallback }

/// <summary>1개 키에 대한 최근 해석 결과 — 값 자체는 절대 포함하지 않는다(디스플레이 안전).</summary>
public sealed record SecretResolution(string KeyName, SecretSource Source, bool IsSecure, DateTimeOffset At, string Note);

/// <summary>
/// v10.5.0-F1 보안 시크릿 격리 볼트 — CLAUDE.md Rule 7(시크릿 하드코딩 금지·Graceful Fallback) 소급 적용.
///   ▸ 우선순위: <b>환경변수</b>(<c>PROMPTFLOW_SECRET_{KEY}</c>, Base64 · CI/운영 오버라이드 최우선) →
///     <b>Windows DPAPI</b>(<see cref="ProtectedData"/>, CurrentUser 스코프 · <c>%LocalAppData%\PromptFlowStudio\Keys\</c>
///     에 봉인 보관 · 최초 실행 시 32바이트 난수 자동 생성+봉인) → 그마저 불가하면(모바일/Linux·DPAPI 예외)
///     <b>결정적 비보안 폴백</b>(이전 하드코딩 리터럴과 동일 안전 수준의 자리표시자 · HUD 에 경고로 노출 · 앱 중단 없음).
///   ▸ Zero-Alloc 메모리 세척: 복호화 과정의 임시 버퍼는 <see cref="Wipe(Span{byte})"/> 로 즉시 0 값 세척(반환되는
///     키 자체는 호출자가 계속 사용해야 하므로 세척 대상에서 제외 — 완전한 Zero-Residency 는 별도 마일스톤 과제).
///   ▸ DPAPI 는 데스크톱(net8.0-windows) 전용 API 라 <c>#if WINDOWS</c> 격리 — 모바일 소스링크 컴파일 안전.
/// </summary>
public sealed class SecureSecretVault
{
    private const string EnvPrefix = "PROMPTFLOW_SECRET_";
    /// <summary>OperationsCanaryHealthChecker 전용 프로브 키 — 감사 링버퍼(32건)·Resolved 에서 격리, <see cref="CanaryResolved"/> 로만 집계.</summary>
    public const string CanaryKeyName = "__OPS_CANARY_PROBE__";
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(3);   // Rule7: 디스크 I/O 정체 방지 타임아웃
#if WINDOWS
    // DPAPI 추가 엔트로피 — 비밀이 아니라 "이 앱이 봉인했다"는 식별 용도(다른 앱의 동일 사용자 프로필 봉인과 구분).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PromptFlowStudio.SecureSecretVault.v1");
#endif

    private readonly object _gate = new();
    private readonly List<SecretResolution> _history = new();
    private long _resolved, _generated, _insecureFallbacks, _canaryResolved;

    public event Action? OnChange;

    public string? LastAction { get; private set; }
    public long Resolved => Interlocked.Read(ref _resolved);
    public long CanaryResolved => Interlocked.Read(ref _canaryResolved);
    public long Generated => Interlocked.Read(ref _generated);
    public long InsecureFallbacks => Interlocked.Read(ref _insecureFallbacks);
    public string VaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Keys");
    public IReadOnlyList<SecretResolution> History { get { lock (_gate) return new List<SecretResolution>(_history); } }

    /// <summary>키 이름으로 시크릿을 해석 — 환경변수→DPAPI 보관값→(최초 실행)신규 생성+봉인→비보안 결정적 폴백 순.</summary>
    public byte[] ResolveOrCreate(string keyName, CancellationToken ct = default)
    {
        var envName = EnvPrefix + Sanitize(keyName);
        var envVal = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(envVal))
        {
            try
            {
                var fromEnv = Convert.FromBase64String(envVal);
                Record(keyName, SecretSource.EnvironmentVariable, true, $"환경변수 {envName} 재정의 사용");
                return fromEnv;
            }
            catch (FormatException) { /* 잘못된 Base64 — 아래 단계로 폴백 */ }
        }

#if WINDOWS
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(IoTimeout);
        try
        {
            var path = VaultPath(keyName);
            if (File.Exists(path))
            {
                cts.Token.ThrowIfCancellationRequested();
                var sealedBytes = File.ReadAllBytes(path);
                var plain = ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser);
                Wipe(sealedBytes);   // 봉인 원문(암호문) 임시 버퍼 세척(복호화 결과와 별개 버퍼)
                Record(keyName, SecretSource.DpapiVault, true, "DPAPI 봉인 보관값 복호화");
                return plain;
            }

            cts.Token.ThrowIfCancellationRequested();
            var fresh = RandomNumberGenerator.GetBytes(32);
            var sealedFresh = ProtectedData.Protect(fresh, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(VaultRoot);
            File.WriteAllBytes(path, sealedFresh);
            Wipe(sealedFresh);
            Interlocked.Increment(ref _generated);
            Record(keyName, SecretSource.GeneratedAndSealed, true, "신규 난수 생성 후 DPAPI 봉인 저장(최초 실행)");
            return fresh;
        }
        catch (OperationCanceledException) { /* 타임아웃 — 아래 Graceful 폴백 */ }
        catch (CryptographicException) { /* 다른 사용자/머신 프로필에서 봉인된 값 등 복호화 불가 — 폴백 */ }
        catch (IOException) { /* 파일 잠금/권한 등 — 폴백 */ }
        catch (UnauthorizedAccessException) { /* ACL 거부 — IOException 계열이 아니라 누락 시 호출측(서명·카나리)까지 전파됨 — 폴백 */ }
#endif
        Interlocked.Increment(ref _insecureFallbacks);
        var fallback = DeriveInsecureFallback(keyName);
        Record(keyName, SecretSource.InsecureFallback, false, $"⚠ 비보안 결정적 폴백 — 프로덕션 배포 전 {envName} 환경변수 설정 필요");
        return fallback;
    }

    // 진짜 시크릿이 아니라 "기존 하드코딩 리터럴과 동일 안전 수준"의 결정적 자리표시자(회귀 없음 · 앱 중단 방지).
    private static byte[] DeriveInsecureFallback(string keyName) => SHA256.HashData(Encoding.UTF8.GetBytes("promptflow-insecure-fallback::" + keyName));

    /// <summary>
    /// v10.5.0-F2: 제3자 발급 API 크리덴셜(예: ComfyUI 원격 게이트웨이 키) 전용 조회 — <see cref="ResolveOrCreate"/>
    /// 와 달리 <b>자동 생성/DPAPI 봉인을 하지 않는다</b>(외부에서 발급한 값을 우리가 대신 만들 수 없으므로).
    /// 환경변수에 없으면 null(Graceful — 호출측이 무인증 모드로 폴백).
    /// </summary>
    public string? TryGetExternalCredential(string keyName)
    {
        var val = Environment.GetEnvironmentVariable(EnvPrefix + Sanitize(keyName));
        if (string.IsNullOrEmpty(val)) return null;
        Record(keyName, SecretSource.EnvironmentVariable, true, "외부 API 크리덴셜(환경변수) 사용");
        return val;
    }

    private string VaultPath(string keyName) => Path.Combine(VaultRoot, Sanitize(keyName).ToLowerInvariant() + ".key");

    private static string Sanitize(string keyName)
    {
        Span<char> buf = stackalloc char[keyName.Length];
        for (var i = 0; i < keyName.Length; i++)
        {
            var c = char.ToUpperInvariant(keyName[i]);
            buf[i] = char.IsAsciiLetterOrDigit(c) ? c : '_';
        }
        return new string(buf);
    }

    private void Record(string keyName, SecretSource source, bool isSecure, string note)
    {
        if (string.Equals(keyName, CanaryKeyName, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _canaryResolved);   // 카나리 핑은 사용자 접근 이력(링버퍼·LastAction·OnChange)을 잠식하지 않음
            return;
        }
        lock (_gate)
        {
            _history.Insert(0, new SecretResolution(keyName, source, isSecure, DateTimeOffset.UtcNow, note));
            if (_history.Count > 32) _history.RemoveAt(_history.Count - 1);
            LastAction = $"{keyName} → {source}" + (isSecure ? "" : " ⚠");
        }
        Interlocked.Increment(ref _resolved);
        OnChange?.Invoke();
    }

    /// <summary>Zero-Alloc 메모리 세척(규칙6) — 사용 후 임시 바이트 버퍼를 즉시 0 값으로 채움.</summary>
    public static void Wipe(Span<byte> secret) => secret.Clear();

    /// <summary>Zero-Alloc 메모리 세척(규칙6) — 임시 문자 버퍼(예: 인코딩 스크래치)를 즉시 0 값으로 채움.</summary>
    public static void Wipe(Span<char> secret) => secret.Clear();

    public void ResetHistory()
    {
        lock (_gate) { _history.Clear(); LastAction = "이력 초기화(디스크 봉인값은 유지)"; }
        OnChange?.Invoke();
    }
}
