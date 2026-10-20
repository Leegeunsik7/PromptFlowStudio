using System;
using System.Threading;
using System.Threading.Tasks;

#if ANDROID
using Android.App;
using Android.Content;
using Android.Nfc;
using Android.Nfc.Tech;
#endif
#if IOS
using CoreNFC;
using Foundation;
#endif

namespace PromptFlowStudio.Services.Mobile;

/// <summary>NFC 태그 읽기 결과 — NDEF 페이로드(텍스트/URI) · 태그 ID(hex) · 종류.</summary>
public sealed record NfcTagRead(string Payload, string TagId, string Kind);

/// <summary>NFC 쓰기 결과 — 성공 여부 · 사유.</summary>
public sealed record NfcWriteResult(bool Ok, string Message);

/// <summary>
/// NFC 태깅 후 논스톱 콜백 체인 핸들러 — ① BLE 연결 ② 테마/스토리보드 로드 ③ 생체 인증.
///   각 단계는 성공 시 true. null 인 단계는 건너뛴다(선택 구성).
/// </summary>
public sealed record NfcChainHandlers(
    Func<string, CancellationToken, Task<bool>>? BleConnect = null,
    Func<string, CancellationToken, Task<bool>>? LoadStoryboard = null,
    Func<CancellationToken, Task<bool>>? Biometric = null);

/// <summary>체인 진행 단계 통지 — 단계명 · 성공 · 메시지.</summary>
public sealed record NfcChainStep(string Name, bool Ok, string Message);

/// <summary>
/// [PromptFlowStudio] v5.0 Task2 · NFC 현장 태그 쾌속 동기화 시임 — <c>Android.Nfc</c>/iOS <c>CoreNFC</c>(외부 유료 SDK 0).
///   ▸ NDEF 태그를 읽으면 <see cref="OnTagRead"/> 방송 후 <b>논스톱 체인</b>(BLE 연결 → 설비 테마/스토리보드 로드 → 생체 인증)을 순차 실행(<see cref="NfcChain"/>).
///   ▸ 데스크톱은 <see cref="DesktopNfcSyncCommander"/>(<c>IsSupported=false</c>) — <see cref="DesktopNfcSyncCommander.SimulateTagAsync"/>로 체인만 검증. 모바일 MAUI 헤드가 네이티브 대체.
/// </summary>
public interface INfcSyncCommander : IDisposable
{
    /// <summary>NFC 지원/활성 여부(모바일 + NFC 칩).</summary>
    bool IsSupported { get; }
    /// <summary>포그라운드 리더 세션 활성 여부.</summary>
    bool Listening { get; }
    /// <summary>태그 읽힘 방송(페이로드/ID). 구독부가 체인 트리거를 후킹.</summary>
    event Action<NfcTagRead>? OnTagRead;
    /// <summary>체인 단계 진행 방송(UI 로그).</summary>
    event Action<NfcChainStep>? OnChainStep;
    /// <summary>태깅 후 실행할 논스톱 체인 핸들러 등록.</summary>
    void ConfigureChain(NfcChainHandlers handlers);
    /// <summary>포그라운드 NDEF 리더 세션 시작(멱등).</summary>
    Task StartListeningAsync(CancellationToken ct = default);
    /// <summary>리더 세션 정지 + 리소스 해제(멱등).</summary>
    Task StopListeningAsync();
    /// <summary>현재 탭된 태그에 NDEF 텍스트/URI 를 기록.</summary>
    Task<NfcWriteResult> WriteAsync(string payload, bool asUri = false, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] NFC 논스톱 체인 러너(순수 · 테스트 대상) — 태그 페이로드로 BLE→스토리보드→생체 인증을 순차 실행.
///   한 단계라도 없거나 실패하면 사유를 <paramref name="report"/>로 알리고 다음 단계 진행 여부를 결정한다(BLE 실패해도 인증까지는 진행).
/// </summary>
public static class NfcChain
{
    public static async Task RunAsync(NfcChainHandlers handlers, NfcTagRead tag, Action<NfcChainStep>? report, CancellationToken ct = default)
    {
        if (handlers is null || tag is null) return;

        // ① BLE Mac Address 연결 시도(페이로드에 mac= 이 있으면 추출, 없으면 페이로드 전체).
        if (handlers.BleConnect is not null)
        {
            var mac = ExtractMac(tag.Payload);
            var ok = await SafeStep(() => handlers.BleConnect(mac, ct)).ConfigureAwait(false);
            report?.Invoke(new NfcChainStep("BLE 연결", ok, ok ? $"{mac} 연결" : $"{mac} 연결 실패(계속)"));
        }

        // ② 전용 테마/스토리보드 로드.
        if (handlers.LoadStoryboard is not null && !ct.IsCancellationRequested)
        {
            var ok = await SafeStep(() => handlers.LoadStoryboard(tag.Payload, ct)).ConfigureAwait(false);
            report?.Invoke(new NfcChainStep("스토리보드 로드", ok, ok ? "설비 프로필 로드" : "로드 실패(계속)"));
        }

        // ③ 생체 인증 프롬프트.
        if (handlers.Biometric is not null && !ct.IsCancellationRequested)
        {
            var ok = await SafeStep(() => handlers.Biometric(ct)).ConfigureAwait(false);
            report?.Invoke(new NfcChainStep("생체 인증", ok, ok ? "인증 성공" : "인증 실패/취소"));
        }
    }

    /// <summary>NDEF 페이로드에서 BLE MAC(mac=AA:BB:.. 또는 6옥텟)을 추출(없으면 원문).</summary>
    public static string ExtractMac(string? payload)
    {
        var p = (payload ?? "").Trim();
        var idx = p.IndexOf("mac=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var tail = p.Substring(idx + 4);
            var end = tail.IndexOfAny(new[] { ';', '&', ' ', '\n' });
            return (end >= 0 ? tail.Substring(0, end) : tail).Trim();
        }
        return p;
    }

    private static async Task<bool> SafeStep(Func<Task<bool>> step)
    {
        try { return await step().ConfigureAwait(false); }
        catch { return false; }   // 한 단계 예외가 체인을 끊지 않는다(논스톱)
    }
}

/// <summary>데스크톱 폴백 — NFC 미지원(<c>IsSupported=false</c>). <see cref="SimulateTagAsync"/>로 체인 로직만 검증 가능.</summary>
public sealed class DesktopNfcSyncCommander : INfcSyncCommander
{
    private NfcChainHandlers _handlers = new();
    private bool _disposed;

    public bool IsSupported => false;
    public bool Listening { get; private set; }
    public event Action<NfcTagRead>? OnTagRead;
    public event Action<NfcChainStep>? OnChainStep;

    public void ConfigureChain(NfcChainHandlers handlers) => _handlers = handlers ?? new();

    public Task StartListeningAsync(CancellationToken ct = default) { Listening = true; return Task.CompletedTask; }
    public Task StopListeningAsync() { Listening = false; return Task.CompletedTask; }

    public Task<NfcWriteResult> WriteAsync(string payload, bool asUri = false, CancellationToken ct = default)
        => Task.FromResult(new NfcWriteResult(false, "데스크톱은 NFC 쓰기를 지원하지 않습니다(모바일 전용)."));

    /// <summary>데스크톱 테스트용 — 태그 탭을 모의하고 실제 체인을 실행한다.</summary>
    public async Task SimulateTagAsync(string payload, CancellationToken ct = default)
    {
        var tag = new NfcTagRead(payload, "SIMULATED", "text");
        OnTagRead?.Invoke(tag);
        await NfcChain.RunAsync(_handlers, tag, OnChainStep, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;   // 멱등
        OnTagRead = null; OnChainStep = null; Listening = false;
    }
}

#if ANDROID
// ════════════════════════════════════════════════════════════════════════════
//  [MAUI Android 헤드 전용] — NfcAdapter.EnableReaderMode 로 포그라운드 NDEF 리딩 → 체인 실행.
// ════════════════════════════════════════════════════════════════════════════

public sealed class AndroidNfcSyncCommander : Java.Lang.Object, INfcSyncCommander, NfcAdapter.IReaderCallback
{
    private readonly Activity _activity;
    private readonly NfcAdapter? _adapter;
    private NfcChainHandlers _handlers = new();
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    public bool IsSupported => _adapter is not null && _adapter.IsEnabled;
    public bool Listening { get; private set; }
    public event Action<NfcTagRead>? OnTagRead;
    public event Action<NfcChainStep>? OnChainStep;

    public AndroidNfcSyncCommander(Activity activity)
    {
        _activity = activity;
        _adapter = NfcAdapter.GetDefaultAdapter(activity);
    }

    public void ConfigureChain(NfcChainHandlers handlers) => _handlers = handlers ?? new();

    public Task StartListeningAsync(CancellationToken ct = default)
    {
        if (_adapter is null) return Task.CompletedTask;
        _adapter.EnableReaderMode(_activity, this,
            NfcReaderFlags.NfcA | NfcReaderFlags.NfcB | NfcReaderFlags.NfcF | NfcReaderFlags.NfcV | NfcReaderFlags.SkipNdefCheck, null);
        Listening = true;
        return Task.CompletedTask;
    }

    public Task StopListeningAsync()
    {
        try { _adapter?.DisableReaderMode(_activity); } catch { }
        Listening = false;
        return Task.CompletedTask;
    }

    // NfcAdapter.IReaderCallback — 태그 탭 시 호출(백그라운드 스레드).
    public async void OnTagDiscovered(Tag? tag)
    {
        if (tag is null) return;
        var id = BitConverter.ToString(tag.GetId() ?? Array.Empty<byte>()).Replace("-", "");
        var payload = ReadNdefText(tag);
        var read = new NfcTagRead(payload, id, "ndef");
        OnTagRead?.Invoke(read);
        await NfcChain.RunAsync(_handlers, read, OnChainStep, _cts.Token).ConfigureAwait(false);
    }

    private static string ReadNdefText(Tag tag)
    {
        try
        {
            using var ndef = Ndef.Get(tag);
            if (ndef is null) return "";
            ndef.Connect();
            var msg = ndef.NdefMessage;
            var rec = msg?.GetRecords()?.FirstOrDefault();
            var bytes = rec?.GetPayload();
            if (bytes is null || bytes.Length == 0) return "";
            // NDEF Text 레코드: [상태바이트][언어코드][UTF-8 텍스트]
            int langLen = bytes[0] & 0x3F;
            return System.Text.Encoding.UTF8.GetString(bytes, 1 + langLen, bytes.Length - 1 - langLen);
        }
        catch { return ""; }
    }

    public Task<NfcWriteResult> WriteAsync(string payload, bool asUri = false, CancellationToken ct = default)
        => Task.FromResult(new NfcWriteResult(false, "쓰기는 태그 연결 컨텍스트에서 구현하세요(NdefFormatable/Ndef.WriteNdefMessage)."));

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { _cts.Cancel(); _adapter?.DisableReaderMode(_activity); } catch { }
        _cts.Dispose();
        OnTagRead = null; OnChainStep = null;
        base.Dispose();
    }
}
#endif

#if IOS
// ════════════════════════════════════════════════════════════════════════════
//  [MAUI iOS 헤드 전용] — CoreNFC NFCNdefReaderSession 로 NDEF 리딩 → 체인 실행.
// ════════════════════════════════════════════════════════════════════════════

public sealed class IosNfcSyncCommander : NFCNdefReaderSessionDelegate, INfcSyncCommander
{
    private NFCNdefReaderSession? _session;
    private NfcChainHandlers _handlers = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public bool IsSupported => NFCNdefReaderSession.ReadingAvailable;
    public bool Listening { get; private set; }
    public event Action<NfcTagRead>? OnTagRead;
    public event Action<NfcChainStep>? OnChainStep;

    public void ConfigureChain(NfcChainHandlers handlers) => _handlers = handlers ?? new();

    public Task StartListeningAsync(CancellationToken ct = default)
    {
        if (!NFCNdefReaderSession.ReadingAvailable) return Task.CompletedTask;
        _session = new NFCNdefReaderSession(this, null, invalidateAfterFirstRead: false)
        { AlertMessage = "설비 NFC 태그에 아이폰을 가까이 대세요." };
        _session.BeginSession();
        Listening = true;
        return Task.CompletedTask;
    }

    public Task StopListeningAsync() { try { _session?.InvalidateSession(); } catch { } Listening = false; return Task.CompletedTask; }

    public override void DidDetect(NFCNdefReaderSession session, NFCNdefMessage[] messages)
    {
        var rec = messages?.FirstOrDefault()?.Records?.FirstOrDefault();
        var payload = rec?.Payload is { } d && d.Length > 0 ? DecodeText(d.ToArray()) : "";
        var read = new NfcTagRead(payload, "IOS-NDEF", "ndef");
        OnTagRead?.Invoke(read);
        _ = NfcChain.RunAsync(_handlers, read, OnChainStep, _cts.Token);
    }

    public override void DidInvalidate(NFCNdefReaderSession session, NSError error) => Listening = false;

    private static string DecodeText(byte[] bytes)
    {
        try { int langLen = bytes[0] & 0x3F; return System.Text.Encoding.UTF8.GetString(bytes, 1 + langLen, bytes.Length - 1 - langLen); }
        catch { return ""; }
    }

    public Task<NfcWriteResult> WriteAsync(string payload, bool asUri = false, CancellationToken ct = default)
        => Task.FromResult(new NfcWriteResult(false, "iOS 쓰기는 NFCNdefTag 연결 세션에서 구현하세요."));

    protected override void Dispose(bool disposing)
    {
        if (!_disposed) { _disposed = true; try { _cts.Cancel(); _session?.InvalidateSession(); } catch { } _cts.Dispose(); OnTagRead = null; OnChainStep = null; }
        base.Dispose(disposing);
    }
}
#endif
