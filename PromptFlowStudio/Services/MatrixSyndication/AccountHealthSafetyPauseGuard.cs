using PromptFlowStudio.Services.Autopilot;

namespace PromptFlowStudio.Services.MatrixSyndication;

public enum PauseReason : byte { None, ZeroReach, ApiRejected }

/// <summary>v17.81.0 배포 후 감시 슬롯(채널 · 콘텐츠 · 발행 시각 · 최신 도달 수).</summary>
public struct HealthWatch
{
    public int Channel, ContentId;
    public long PublishedUnix, Reach;
    public bool Open;
}

/// <summary>
/// [PromptFlow Studio v17.81.0] 🛑 계정 건전성 감시 &amp; 안전 정지 가드(Human-in-the-Loop).
///   ▸ 발행 직후 감시 슬롯 등록 → <see cref="WatchSec"/>(30 분) 경과 시 도달 0 이면 Zero Reach · API 거절(401/403/429/5xx 아닌 4xx 포함) 수신 즉시 → 해당 계정 큐만 동결.
///   ▸ 동결 시 v17.73 <see cref="RemoteCreatorTelemetryDispatcher"/>(Discord/Telegram)로 Critical 알림 · 스택 문자 버퍼(무할당).
///   ▸ 다른 계정으로의 우회 재라우팅 경로 없음 — 동결 항목은 같은 채널에 Held 로만 남는다.
///   ▸ 자동 해제 타이머 없음: 관리자가 <see cref="Approve"/>(같은 채널 재개) 또는 <see cref="Cancel"/>(해당 채널 대기분 폐기)를 누르기 전까지 동결 유지.
/// </summary>
public sealed class AccountHealthSafetyPauseGuard
{
    public const int WatchCap = 64;
    public const long WatchSec = 30 * 60;

    private readonly CompliantMultiAccountMatrixRouterEngine _router;
    private readonly HealthWatch[] _w = new HealthWatch[WatchCap];
    private readonly PauseReason[] _reason = new PauseReason[CompliantMultiAccountMatrixRouterEngine.MaxChannels];
    private readonly long[] _pausedAt = new long[CompliantMultiAccountMatrixRouterEngine.MaxChannels];
    private readonly int[] _lastStatus = new int[CompliantMultiAccountMatrixRouterEngine.MaxChannels];

    public AccountHealthSafetyPauseGuard(CompliantMultiAccountMatrixRouterEngine router, RemoteCreatorTelemetryDispatcher? alerts = null)
    {
        _router = router; Alerts = alerts;
    }

    public RemoteCreatorTelemetryDispatcher? Alerts { get; }
    public long Pauses { get; private set; }
    public long ZeroReachTrips { get; private set; }
    public long ApiTrips { get; private set; }
    public long Approvals { get; private set; }
    public long Cancels { get; private set; }
    public long AlertsDispatched { get; private set; }
    public long WatchOverflow { get; private set; }
    public int OpenWatches { get { var n = 0; for (var i = 0; i < WatchCap; i++) if (_w[i].Open) n++; return n; } }
    public ref readonly HealthWatch Watch(int i) => ref _w[i];
    public PauseReason Reason(int channel) => _reason[channel];
    public long PausedAt(int channel) => _pausedAt[channel];
    public int LastStatus(int channel) => _lastStatus[channel];
    public bool AwaitingHuman(int channel) => _reason[channel] != PauseReason.None;
    public int AwaitingCount { get { var n = 0; for (var i = 0; i < _router.ChannelCount; i++) if (_reason[i] != PauseReason.None) n++; return n; } }

    public static string ReasonName(PauseReason r) => r switch { PauseReason.ZeroReach => "30분 도달 0", PauseReason.ApiRejected => "API 거절", _ => "정상" };

    /// <summary>API 거절로 볼 상태 코드 — 인증/권한/레이트/정책 위반(4xx). 5xx 는 일시 장애로 보고 동결하지 않는다.</summary>
    public static bool IsRejection(int status) => status is >= 400 and < 500;

    // ══════════════════════ 감시(무할당) ══════════════════════
    public bool Track(int channel, int contentId, long publishedUnix)
    {
        for (var i = 0; i < WatchCap; i++)
        {
            if (_w[i].Open) continue;
            _w[i] = new HealthWatch { Channel = channel, ContentId = contentId, PublishedUnix = publishedUnix, Reach = 0, Open = true };
            return true;
        }
        WatchOverflow++;
        return false;
    }

    public void ObserveReach(int channel, int contentId, long reach)
    {
        for (var i = 0; i < WatchCap; i++)
            if (_w[i].Open && _w[i].Channel == channel && _w[i].ContentId == contentId) { _w[i].Reach = Math.Max(_w[i].Reach, reach); return; }
    }

    /// <summary>플랫폼 API 응답 보고 — 거절이면 즉시 동결. 반환 = 이번 호출로 동결됐는지.</summary>
    public bool ReportApi(int channel, int status, long now)
    {
        _lastStatus[channel] = status;
        if (!IsRejection(status)) return false;
        ApiTrips++;
        return Trip(channel, PauseReason.ApiRejected, now, status);
    }

    /// <summary>감시 창 평가 — 30 분 경과 슬롯 마감(도달 0 = 동결). 반환 = 새로 동결된 채널 수.</summary>
    public int Evaluate(long now)
    {
        var tripped = 0;
        for (var i = 0; i < WatchCap; i++)
        {
            ref var w = ref _w[i];
            if (!w.Open || now - w.PublishedUnix < WatchSec) continue;
            w.Open = false;
            if (w.Reach > 0) continue;
            ZeroReachTrips++;
            if (Trip(w.Channel, PauseReason.ZeroReach, now, 0)) tripped++;
        }
        return tripped;
    }

    private bool Trip(int channel, PauseReason reason, long now, int status)
    {
        if (_reason[channel] != PauseReason.None) return false;
        _reason[channel] = reason; _pausedAt[channel] = now;
        _router.Freeze(channel);
        Pauses++;
        Alert(channel, reason, status);
        return true;
    }

    // ══════════════════════ 관리자 확인(Human-in-the-Loop) ══════════════════════
    /// <summary>관리자 승인 — 같은 채널 Held 항목 재개 · 감시 슬롯 정리. 반환 = 재개 항목 수(동결 아님 = −1).</summary>
    public int Approve(int channel)
    {
        if (_reason[channel] == PauseReason.None) return -1;
        _reason[channel] = PauseReason.None;
        CloseWatches(channel);
        Approvals++;
        return _router.Thaw(channel);
    }

    /// <summary>관리자 취소 — 해당 채널 대기 항목 전부 폐기(타 채널 이관 없음). 반환 = 폐기 수(동결 아님 = −1).</summary>
    public int Cancel(int channel)
    {
        if (_reason[channel] == PauseReason.None) return -1;
        _reason[channel] = PauseReason.None;
        CloseWatches(channel);
        Cancels++;
        return _router.CancelChannel(channel);
    }

    private void CloseWatches(int channel) { for (var i = 0; i < WatchCap; i++) if (_w[i].Channel == channel) _w[i].Open = false; }

    private void Alert(int channel, PauseReason reason, int status)
    {
        if (Alerts is null) return;
        var c = _router.Channel(channel);
        Span<char> text = stackalloc char[RemoteCreatorTelemetryDispatcher.TextCap];
        var n = 0;
        Append(text, ref n, "🛑 안전 정지 — ");
        Append(text, ref n, c.Name);
        Append(text, ref n, " (");
        Append(text, ref n, c.Handle);
        Append(text, ref n, ") · ");
        Append(text, ref n, ReasonName(reason));
        if (status > 0) { Append(text, ref n, " HTTP "); if (status.TryFormat(text[n..], out var w, default, System.Globalization.CultureInfo.InvariantCulture)) n += w; }
        Append(text, ref n, " · 관리자 확인 대기(자동 재개/우회 없음)");
        var kind = reason == PauseReason.ApiRejected ? CreatorAlertKind.UploadFailed : CreatorAlertKind.Info;
        try { if (Alerts.Publish(kind, AlertSeverity.Critical, text[..n], channel)) AlertsDispatched++; }
        catch (Exception ex) { PromptFlowStudio.Program.Log("MatrixSyndication.Alert", ex); }
    }

    private static void Append(Span<char> dst, ref int n, string s)
    {
        var m = Math.Min(s.Length, dst.Length - n);
        s.AsSpan(0, m).CopyTo(dst[n..]);
        n += m;
    }
}
