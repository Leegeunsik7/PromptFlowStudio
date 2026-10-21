using System.Numerics;
using PromptFlowStudio.Services.Autopilot;
using PromptFlowStudio.Services.GraphBridge;

namespace PromptFlowStudio.Services.LivePublish;

[Flags]
public enum PreflightIssue : ushort { None = 0, NotMp4 = 1, AspectRatio = 2, FrameRate = 4, FileSize = 8, AudioRate = 16, AudioChannels = 32, Duration = 64, Resolution = 128 }
[Flags]
public enum LiveTargets : byte { None = 0, YouTube = 1, Instagram = 2, Both = 3 }
public enum LivePublishStage : byte { Idle, UploadingYouTube, PublishingInstagram, Promoting, Reverting, Completed, RolledBack, Aborted }
public enum SafePrivacy : byte { Private, Unlisted }

/// <summary>라이브 배포 원본 규격(인코더 신고값 + MP4 시그니처 검사).</summary>
public struct LiveVideoSpec
{
    public int Width, Height, AudioHz, AudioChannels;
    public float Fps, DurationSec;
    public long Bytes;
}

/// <summary>
/// [PromptFlow Studio v17.79.0] 🛡️ 프리플라이트 규격 검사 &amp; 비공개 드라이런 롤백 가드(<see cref="Validate"/> · <see cref="Step"/> 무할당).
/// <b>프리플라이트</b>: MP4(ftyp) · 9:16(±1 %) · 30/60 fps(29.97/59.94 허용) · ≤ 100 MB · 오디오 44.1/48 kHz 스테레오 · 3~180 초 · ≥ 720×1280 — 위반 비트 플래그.
/// <b>안전 모드(Dry-Run)</b>: YouTube 를 <c>private</c>/<c>unlisted</c> 로 생성해 사전 검수. 안전 모드 해제 + 다중 타깃이면 2 단계 커밋 —
/// YouTube 는 일단 private 스테이징 → Instagram 발행 성공 후에만 public 승격(실패 시 비공개 유지 = 롤백).
/// <b>방송사고 차단</b>: 플랫폼 거부(저작권 · 메타데이터 · 할당량 · 인증 · IG ERROR) 감지 즉시 전 플랫폼 업로드 중단 → 공개 상태였다면 private 복귀 →
/// v17.73 <see cref="RemoteCreatorTelemetryDispatcher"/>(Discord/Telegram 웹훅 큐)로 Critical 알림 디스패치(스택 문자 버퍼 · 무할당).
/// </summary>
public sealed class LivePublishingPreflightRollbackGuard
{
    public const long MaxBytes = 100L * 1024 * 1024;
    public const float MinDurationSec = 3f, MaxDurationSec = 180f, AspectTol = 0.01f;
    public const int MinWidth = 720, MinHeight = 1280;
    private readonly ProductionOAuth2TokenVaultGateway _vault;
    private readonly ProductionResumableVideoUploaderEngine _up;
    private Stream? _src;
    private LiveVideoSpec _spec;
    private string _caption = "";
    private bool _staged, _promoted;

    public LivePublishingPreflightRollbackGuard(ProductionOAuth2TokenVaultGateway vault, ProductionResumableVideoUploaderEngine uploader, RemoteCreatorTelemetryDispatcher? alerts = null)
    {
        _vault = vault; _up = uploader; Alerts = alerts;
    }

    public RemoteCreatorTelemetryDispatcher? Alerts { get; }
    public bool SafeMode { get; set; } = true;
    public SafePrivacy SafePrivacy { get; set; } = SafePrivacy.Private;
    public bool ShareToFeed { get; set; } = true;
    public LiveTargets Targets { get; private set; }
    public LivePublishStage Stage { get; private set; }
    public PreflightIssue LastIssues { get; private set; }
    public PlatformRejection LastRejection { get; private set; }
    public LiveProvider? FailedProvider { get; private set; }
    public long PreflightPassed { get; private set; }
    public long PreflightRejected { get; private set; }
    public long Aborts { get; private set; }
    public long Rollbacks { get; private set; }
    public long Promotions { get; private set; }
    public long Completions { get; private set; }
    public long AlertsDispatched { get; private set; }
    public double LastPreflightUs { get; private set; }
    public string? LastError { get; private set; }
    public bool IsBusy => Stage is LivePublishStage.UploadingYouTube or LivePublishStage.PublishingInstagram or LivePublishStage.Promoting or LivePublishStage.Reverting;
    public string ActivePrivacy => SafeMode ? (SafePrivacy == SafePrivacy.Private ? "private" : "unlisted") : "public";

    public static string StageName(LivePublishStage s) => s switch
    {
        LivePublishStage.UploadingYouTube => "YouTube 이어올리기",
        LivePublishStage.PublishingInstagram => "Instagram 릴스 발행",
        LivePublishStage.Promoting => "공개 승격",
        LivePublishStage.Reverting => "비공개 롤백",
        LivePublishStage.Completed => "배포 완료",
        LivePublishStage.RolledBack => "롤백(비공개 유지)",
        LivePublishStage.Aborted => "배포 중단",
        _ => "대기",
    };

    // ══════════════════════ 프리플라이트(무할당) ══════════════════════
    public static PreflightIssue Validate(in LiveVideoSpec s, ReadOnlySpan<byte> head)
    {
        var f = PreflightIssue.None;
        if (head.Length < 12 || !head.Slice(4, 4).SequenceEqual("ftyp"u8)) f |= PreflightIssue.NotMp4;
        if (s.Width <= 0 || s.Height <= 0 || MathF.Abs((float)s.Width / s.Height - 9f / 16f) > AspectTol * 9f / 16f) f |= PreflightIssue.AspectRatio;
        if (s.Width < MinWidth || s.Height < MinHeight) f |= PreflightIssue.Resolution;
        if (MathF.Abs(s.Fps - 30f) > 0.1f && MathF.Abs(s.Fps - 60f) > 0.1f) f |= PreflightIssue.FrameRate;
        if (s.Bytes <= 0 || s.Bytes > MaxBytes) f |= PreflightIssue.FileSize;
        if (s.AudioHz is not (44100 or 48000)) f |= PreflightIssue.AudioRate;
        if (s.AudioChannels != 2) f |= PreflightIssue.AudioChannels;
        if (s.DurationSec < MinDurationSec || s.DurationSec > MaxDurationSec) f |= PreflightIssue.Duration;
        return f;
    }

    public static int IssueCount(PreflightIssue f) => BitOperations.PopCount((uint)f);

    /// <summary>첫 위반 사유(리터럴 · 무할당).</summary>
    public static string IssueText(PreflightIssue f) =>
        (f & PreflightIssue.NotMp4) != 0 ? "MP4(ftyp) 컨테이너 아님" :
        (f & PreflightIssue.AspectRatio) != 0 ? "종횡비 9:16 아님" :
        (f & PreflightIssue.Resolution) != 0 ? "해상도 720×1280 미만" :
        (f & PreflightIssue.FrameRate) != 0 ? "프레임레이트 30/60 fps 아님" :
        (f & PreflightIssue.FileSize) != 0 ? "용량 0 또는 100 MB 초과" :
        (f & PreflightIssue.AudioRate) != 0 ? "오디오 44.1/48 kHz 아님" :
        (f & PreflightIssue.AudioChannels) != 0 ? "오디오 스테레오(2ch) 아님" :
        (f & PreflightIssue.Duration) != 0 ? "길이 3~180 초 범위 밖" : "규격 통과";

    // ══════════════════════ 배포 ══════════════════════
    /// <summary>프리플라이트 → 계정 확인 → 첫 플랫폼 업로드 시작(비핫패스). 거부 시 즉시 알림.</summary>
    public bool Begin(Stream src, in LiveVideoSpec spec, string title, string caption, LiveTargets targets, double now)
    {
        if (IsBusy) { LastError = "배포 진행 중"; return false; }
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Span<byte> head = stackalloc byte[12];
        src.Position = 0;
        var hn = src.ReadAtLeast(head, 12, throwOnEndOfStream: false);
        var issues = Validate(in spec, head[..hn]);
        if (spec.Bytes != src.Length) issues |= PreflightIssue.FileSize;
        LastPreflightUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastIssues = issues; LastRejection = PlatformRejection.None; FailedProvider = null; _staged = _promoted = false;
        Targets = targets;
        if (issues != PreflightIssue.None)
        {
            PreflightRejected++;
            Stage = LivePublishStage.Aborted;
            LastError = IssueText(issues);
            Alert(AlertSeverity.Warning, "⛔ 프리플라이트 차단 — ", IssueText(issues), IssueCount(issues));
            return false;
        }
        PreflightPassed++;
        if (targets == LiveTargets.None) { LastError = "배포 대상 없음"; Stage = LivePublishStage.Aborted; return false; }
        if ((targets & LiveTargets.YouTube) != 0 && !_vault.Account(LiveProvider.YouTube).IsConnected) return Refuse(LiveProvider.YouTube, "YouTube 계정 미연결");
        if ((targets & LiveTargets.Instagram) != 0 && !_vault.Account(LiveProvider.Instagram).IsConnected) return Refuse(LiveProvider.Instagram, "Instagram 계정 미연결");
        _src = src; _spec = spec; _caption = caption;
        if ((targets & LiveTargets.YouTube) != 0)
        {
            var privacy = SafeMode ? ActivePrivacy : (targets & LiveTargets.Instagram) != 0 ? "private" : "public";
            _staged = !SafeMode && privacy == "private";
            if (!_up.BeginYouTube(src, title, caption, privacy, now, "Shorts,PromptFlow")) return Refuse(LiveProvider.YouTube, _up.LastError ?? "YouTube 시작 실패");
            Stage = LivePublishStage.UploadingYouTube;
        }
        else if (!StartInstagram(now)) return false;
        LastError = null;
        return true;
    }

    private bool Refuse(LiveProvider p, string why)
    {
        LastError = why; FailedProvider = p; Stage = LivePublishStage.Aborted; Aborts++;
        Alert(AlertSeverity.Critical, p == LiveProvider.YouTube ? "🚨 YouTube 배포 거부 — " : "🚨 Instagram 배포 거부 — ", why, 0);
        return false;
    }

    private bool StartInstagram(double now)
    {
        var rs = new ReelsVideoSpec { Width = _spec.Width, Height = _spec.Height, Fps = _spec.Fps, DurationSec = _spec.DurationSec, Bytes = _spec.Bytes };
        if (_up.BeginInstagram(_src!, in rs, _caption, ShareToFeed, now)) { Stage = LivePublishStage.PublishingInstagram; return true; }
        Abort(LiveProvider.Instagram, PlatformRejection.Instagram, _up.LastError ?? "Instagram 시작 실패", now);
        return false;
    }

    /// <summary>틱(무할당) — 업로더 진행 → 단계 전이 · 실패 감지 즉시 중단/롤백/알림.</summary>
    public void Step(double now)
    {
        _up.Step(now);
        switch (Stage)
        {
            case LivePublishStage.UploadingYouTube:
                if (_up.Phase == YouTubeUploadPhase.Failed) Abort(LiveProvider.YouTube, _up.Rejection, _up.LastError ?? "YouTube 실패", now);
                else if (_up.Phase == YouTubeUploadPhase.Completed)
                {
                    if ((Targets & LiveTargets.Instagram) != 0) StartInstagram(now);
                    else Finish(now);
                }
                break;
            case LivePublishStage.PublishingInstagram:
                if (_up.Reels.Phase == ReelsPublishPhase.Failed) Abort(LiveProvider.Instagram, PlatformRejection.Instagram, _up.Reels.LastError ?? "Instagram 실패", now);
                else if (_up.Reels.Phase == ReelsPublishPhase.Published) Finish(now);
                break;
            case LivePublishStage.Promoting:
                if (_up.Phase == YouTubeUploadPhase.Failed) Abort(LiveProvider.YouTube, _up.Rejection, _up.LastError ?? "승격 실패", now);
                else if (_up.Phase == YouTubeUploadPhase.Completed) { _promoted = _up.ConfirmedPrivacy == "public"; Promotions++; Complete(); }
                break;
            case LivePublishStage.Reverting:
                if (_up.Phase is YouTubeUploadPhase.Completed or YouTubeUploadPhase.Failed)
                {
                    _promoted = _up.ConfirmedPrivacy == "public";
                    Stage = LivePublishStage.RolledBack;
                    if (_up.Phase == YouTubeUploadPhase.Failed) Alert(AlertSeverity.Critical, "🚨 비공개 롤백 실패 — 수동 확인 필요: ", _up.LastError ?? "YouTube", 0);
                }
                break;
        }
    }

    private void Finish(double now)
    {
        if (_staged && (Targets & LiveTargets.YouTube) != 0 && _up.HasVideo && _up.BeginPrivacyUpdate("public", now)) { Stage = LivePublishStage.Promoting; return; }
        Complete();
    }

    private void Complete()
    {
        Stage = LivePublishStage.Completed; Completions++;
        Alert(AlertSeverity.Success, SafeMode ? "✅ 드라이런 배포 완료(비공개 검수 대기) — " : "✅ 라이브 배포 완료 — ", Targets == LiveTargets.Both ? "YouTube + Instagram" : Targets == LiveTargets.YouTube ? "YouTube" : "Instagram", 0, CreatorAlertKind.ReelPublished);
    }

    /// <summary>즉시 중단 — 전 플랫폼 업로드 취소 · 공개 영상이면 private 복귀 · Critical 웹훅 알림.</summary>
    public void Abort(LiveProvider p, PlatformRejection reason, string why, double now)
    {
        _up.CancelAll();
        LastRejection = reason; FailedProvider = p; LastError = why; Aborts++;
        Alert(AlertSeverity.Critical, p == LiveProvider.YouTube ? "🚨 YouTube 배포 중단 — " : "🚨 Instagram 배포 중단 — ", ProductionResumableVideoUploaderEngine.RejectionName(reason), (int)reason);
        if (_up.HasVideo && p == LiveProvider.Instagram)
        {
            Rollbacks++;
            if (_promoted && _up.BeginPrivacyUpdate("private", now)) { Stage = LivePublishStage.Reverting; return; }
            Stage = LivePublishStage.RolledBack;
            return;
        }
        Stage = LivePublishStage.Aborted;
    }

    /// <summary>검수 후 수동 승격(private/unlisted → public).</summary>
    public bool Promote(double now)
    {
        if (IsBusy || !_up.HasVideo || !_up.BeginPrivacyUpdate("public", now)) return false;
        Stage = LivePublishStage.Promoting;
        return true;
    }

    /// <summary>수동 롤백(→ private).</summary>
    public bool Rollback(double now)
    {
        if (IsBusy || !_up.HasVideo || !_up.BeginPrivacyUpdate("private", now)) return false;
        Rollbacks++;
        _promoted = true;
        Stage = LivePublishStage.Reverting;
        return true;
    }

    public void Cancel(double now)
    {
        if (IsBusy) Abort(Stage == LivePublishStage.PublishingInstagram ? LiveProvider.Instagram : LiveProvider.YouTube, PlatformRejection.None, "사용자 중단", now);
    }

    private void Alert(AlertSeverity sev, string prefix, string detail, double value, CreatorAlertKind kind = CreatorAlertKind.UploadFailed)
    {
        if (Alerts is null) return;
        Span<char> text = stackalloc char[RemoteCreatorTelemetryDispatcher.TextCap];
        var n = Math.Min(prefix.Length, text.Length);
        prefix.AsSpan(0, n).CopyTo(text);
        var m = Math.Min(detail.Length, text.Length - n);
        detail.AsSpan(0, m).CopyTo(text[n..]);
        try { if (Alerts.Publish(kind, sev, text[..(n + m)], value)) AlertsDispatched++; }
        catch (Exception ex) { PromptFlowStudio.Program.Log("LivePublish.Alert", ex); }
    }
}
