using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Publishing;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 🚀 SNS 원클릭 발행 모달 뷰모델 — 합본(<c>full.mp4</c>) → YouTube Shorts · Instagram Reels 동시 발행.
///   ▸ <see cref="Open"/>: 훅 문장 → 제목(<c>#Shorts #AI</c>) · 씬 대본 → 타임스탬프 설명 · 태그 · 릴스 캡션 자동 채움(편집 가능).
///   ▸ 공개 범위 기본값 private(권장) · 예약은 YouTube 만(publishAt, private 강제).
///   ▸ <see cref="StartAsync"/>: 선택 채널 병렬 전송 · 채널별 진행률 · 취소(CTS) · 실패는 결과로 격리(한 채널 실패가 다른 채널을 막지 않음).
/// </summary>
public sealed class PublishViewModel : IDisposable
{
    public static readonly (string Key, string Label)[] PrivacyOptions =
    {
        ("private", "🔒 비공개 (권장)"),
        ("unlisted", "🔗 일부공개"),
        ("public", "🌐 즉시공개"),
    };

    private readonly IYouTubeShortsPublisher _yt;
    private readonly IInstagramReelsPublisher _ig;
    private CancellationTokenSource? _cts;
    private int _disposed;

    public PublishViewModel(IYouTubeShortsPublisher yt, IInstagramReelsPublisher ig)
    {
        _yt = yt ?? throw new ArgumentNullException(nameof(yt));
        _ig = ig ?? throw new ArgumentNullException(nameof(ig));
    }

    /// <summary>상태 변경 통지(백그라운드 스레드 가능 — 구독자는 InvokeAsync(StateHasChanged)).</summary>
    public event Action? OnChange;

    public bool IsOpen { get; private set; }
    public string VideoPath { get; private set; } = "";
    public string? SrtPath { get; private set; }
    public bool ToYouTube { get; set; } = true;
    public bool ToInstagram { get; set; } = true;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string TagsText { get; set; } = "";
    public string Caption { get; set; } = "";
    public string Privacy { get; set; } = "private";
    public bool Schedule { get; set; }
    public DateTime ScheduleLocal { get; set; } = DateTime.Now.AddHours(1);
    /// <summary>릴스용 공개 HTTPS URL(있으면 Graph API 직결, 없으면 웹훅/로컬 브릿지).</summary>
    public string PublicVideoUrl { get; set; } = "";
    /// <summary>릴스 공개 게시 동의(로컬 브릿지 경로 필수).</summary>
    public bool InstagramConsent { get; set; }

    public bool YouTubeEnabled => _yt.Enabled;
    public bool InstagramEnabled => _ig.Enabled;
    public bool IsBusy { get; private set; }
    public double YouTubeProgress { get; private set; }
    public double InstagramProgress { get; private set; }
    public string? YouTubeStatus { get; private set; }
    public string? InstagramStatus { get; private set; }
    public ShortsPublishResult? YouTubeResult { get; private set; }
    public ReelsPublishOutcome? InstagramResult { get; private set; }

    /// <summary>선택 채널 진행률 평균(0~1).</summary>
    public double OverallProgress => (ToYouTube, ToInstagram) switch
    {
        (true, true) => (YouTubeProgress + InstagramProgress) / 2,
        (true, false) => YouTubeProgress,
        (false, true) => InstagramProgress,
        _ => 0,
    };

    public bool CanStart => !IsBusy && (ToYouTube || ToInstagram) && Title.Trim().Length > 0 && File.Exists(VideoPath);

    /// <summary>모달 열기 — 합본 경로 + 훅 + 씬 대본으로 메타데이터 자동 생성(이전 결과 초기화).</summary>
    public void Open(string videoPath, string? hook, IReadOnlyList<ScriptBeat>? beats)
    {
        if (IsBusy) { IsOpen = true; Notify(); return; }
        VideoPath = videoPath ?? "";
        SrtPath = YouTubeShortsPublisher.ResolveSrtPath(VideoPath);
        var meta = YouTubeShortsPublisher.BuildMetadata(hook, beats);
        Title = meta.Title;
        Description = meta.Description;
        TagsText = string.Join(", ", meta.Tags);
        Caption = SnsPublishFormatter.BuildReelsCaption(hook, beats, meta.Tags);
        Privacy = "private";
        Schedule = false;
        ScheduleLocal = DateTime.Now.AddHours(1);
        InstagramConsent = false;
        YouTubeProgress = InstagramProgress = 0;
        YouTubeStatus = InstagramStatus = null;
        YouTubeResult = null;
        InstagramResult = null;
        IsOpen = true;
        Notify();
    }

    /// <summary>모달 닫기 — 전송 중이면 백그라운드 계속(다시 열어 진행률 확인).</summary>
    public void Close()
    {
        IsOpen = false;
        Notify();
    }

    /// <summary>업로드 시작 — 선택 채널 병렬 전송. 모든 선택 채널 성공 시 true.</summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (!CanStart) return false;
        IsBusy = true;
        YouTubeProgress = InstagramProgress = 0;
        YouTubeResult = null;
        InstagramResult = null;
        YouTubeStatus = ToYouTube ? "대기" : null;
        InstagramStatus = ToInstagram ? "대기" : null;
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        Notify();

        try
        {
            var ytTask = ToYouTube ? RunYouTubeAsync(token) : Task.FromResult(true);
            var igTask = ToInstagram ? RunInstagramAsync(token) : Task.FromResult(true);
            var results = await Task.WhenAll(ytTask, igTask).ConfigureAwait(false);
            return results[0] && results[1];
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task<bool> RunYouTubeAsync(CancellationToken ct)
    {
        YouTubeStatus = "업로드 중…";
        Notify();
        var tags = SnsPublishFormatter.ParseTags(TagsText);
        DateTime? at = Schedule ? ScheduleLocal.ToUniversalTime() : null;
        var meta = new ShortsMetadata(Title.Trim(), Description, tags, at is null ? Privacy : "private", at);
        try
        {
            var r = await _yt.PublishAsync(VideoPath, meta, SrtPath, new InlineProgress(p => { YouTubeProgress = Math.Clamp(p, 0, 1); Notify(); }), ct).ConfigureAwait(false);
            YouTubeResult = r;
            YouTubeStatus = r.Message;
            if (r.Ok) YouTubeProgress = 1;
            return r.Ok;
        }
        catch (Exception ex)
        {
            Program.Log("PublishViewModel.YouTube", ex);
            YouTubeStatus = "업로드 실패: " + ex.Message;
            return false;
        }
        finally { Notify(); }
    }

    private async Task<bool> RunInstagramAsync(CancellationToken ct)
    {
        InstagramStatus = "발행 중…";
        Notify();
        try
        {
            var url = string.IsNullOrWhiteSpace(PublicVideoUrl) ? null : PublicVideoUrl.Trim();
            var r = await _ig.PublishAsync(VideoPath, url, Caption ?? "", InstagramConsent,
                new InlineProgress(p => { InstagramProgress = Math.Clamp(p, 0, 1); Notify(); }), ct).ConfigureAwait(false);
            InstagramResult = r;
            InstagramStatus = r.Message;
            if (r.Ok) InstagramProgress = 1;
            return r.Ok;
        }
        catch (Exception ex)
        {
            Program.Log("PublishViewModel.Instagram", ex);
            InstagramStatus = "발행 실패: " + ex.Message;
            return false;
        }
        finally { Notify(); }
    }

    private void Notify()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { Program.Log("PublishViewModel.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        OnChange = null;
        Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>동기 진행률 중계(Progress&lt;T&gt; 의 SynchronizationContext 포스팅 회피 — 순서 보장).</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
