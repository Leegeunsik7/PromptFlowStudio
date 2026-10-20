using System;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Services.Wan;

/// <summary>워크스페이스에서 본 작업의 큰 단계(UI 배지·스텝 강조용). Rendering(원격 생성) 뒤 Downloading(로컬 자산화)이 온다.</summary>
public enum WanJobPhase { Idle, Submitting, Queued, Rendering, Downloading, Completed, Failed, Canceled }

/// <summary>
/// [PromptFlowStudio] 영상생성 '작업 상태'(관찰 가능 싱글턴) — 원격 생성(제출→폴링)과 로컬 자산화(스트리밍 다운로드)를
///   하나의 상태 머신으로 오케스트레이션하고 UI 에 노출한다. SessionState 와 동일 패턴(private set + <see cref="OnChange"/>).
///   흐름: Submitting → Queued → Rendering → <b>Downloading</b> → Completed(실패 시 Failed / 취소 시 Canceled).
///   ▸ 두 종류의 진행률을 흡수하려 <see cref="IProgress{T}"/> 를 2개 명시적 구현 —
///     <see cref="WanProgress"/>(원격 생성) 와 <see cref="double"/>(로컬 다운로드).
///   ▸ Graceful: 다운로드가 실패해도 작업은 Completed 로 두고 원격 URL(24h)로 재생 폴백한다(<see cref="IsLocal"/>=false).
///   ⚠ OnChange 는 백그라운드(폴링/다운로드) 스레드에서 올 수 있다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링할 것.
///   ⚠ 단일 인터랙티브 컨텍스트(BlazorWebView · 단일 사용자)라 싱글턴이 자연스럽다.
/// </summary>
public sealed class WanJobState : IProgress<WanProgress>, IProgress<double>
{
    private readonly IWanApiService _wan;
    private readonly ILocalMediaService _media;
    private CancellationTokenSource? _cts;

    public WanJobState(IWanApiService wan, ILocalMediaService media, IWanConfigService config)
    {
        _wan = wan;
        _media = media;
        // 설정(API 키/BaseUrl) 변경을 UI 로 릴레이 — 저장 즉시 경고 배너가 사라지고 생성 버튼이 활성화된다.
        config.OnConfigChanged += () => OnChange?.Invoke();
    }

    /// <summary>상태 변경 브로드캐스트(구독: 컴포넌트 OnInitialized / 해지: Dispose).</summary>
    public event Action? OnChange;

    public bool Enabled => _wan.Enabled;
    public WanJobPhase Phase { get; private set; } = WanJobPhase.Idle;

    /// <summary>진행 중(제출/대기/렌더링/다운로드) 여부 — 입력·버튼 잠금 판단.</summary>
    public bool IsBusy => Phase is WanJobPhase.Submitting or WanJobPhase.Queued
        or WanJobPhase.Rendering or WanJobPhase.Downloading;

    public int Percent { get; private set; }
    public string StatusText { get; private set; } = "대기 중";
    public TimeSpan Elapsed { get; private set; }
    public string? TaskId { get; private set; }

    /// <summary>&lt;video src&gt; 에 바인딩할 재생 URL — 성공 시 로컬 가상호스트, 실패 시 원격 폴백.</summary>
    public string? VideoUrl { get; private set; }
    /// <summary>로컬 저장 절대경로(성공 시). 실패/폴백 시 null.</summary>
    public string? LocalPath { get; private set; }
    /// <summary>원격 원본 URL(24h) — 정보 표시·폴백용.</summary>
    public string? RemoteUrl { get; private set; }
    /// <summary>true=로컬 파일 재생, false=원격 폴백 재생.</summary>
    public bool IsLocal { get; private set; }
    public string? Error { get; private set; }
    public string? LastPrompt { get; private set; }

    /// <summary>
    /// 새 생성 작업 시작(진행 중이면 무시). 원격 생성 완료 후 자동으로 로컬 다운로드까지 진행한다.
    ///   호출부(컴포넌트)는 fire-and-forget(<c>_ = StartAsync(req)</c>)로 부르고 진행은 <see cref="OnChange"/>로 받는다.
    /// </summary>
    public async Task StartAsync(WanVideoRequest request)
    {
        if (IsBusy) return;
        if (!_wan.Enabled) { Fail("Wan API 키가 구성되지 않았습니다 — 관리자 설정 › 영상(Wan) 에서 키를 등록하세요."); return; }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        LastPrompt = request.Prompt;
        VideoUrl = null; LocalPath = null; RemoteUrl = null; IsLocal = false;
        Error = null; TaskId = null; Percent = 0; Elapsed = TimeSpan.Zero;
        SetPhase(WanJobPhase.Submitting, "생성 요청 제출 중…");

        try
        {
            // ── 1) 원격 생성(제출→폴링) · 진행은 IProgress<WanProgress>(this) 로 흡수 ──
            var gen = await _wan.GenerateAsync(request, this, _cts.Token).ConfigureAwait(false);
            if (gen.Status == WanTaskStatus.Canceled) { SetPhase(WanJobPhase.Canceled, "작업이 취소되었습니다."); return; }
            if (!gen.Ok || gen.VideoUrl is null) { Fail(gen.Message); return; }

            TaskId = gen.TaskId;
            RemoteUrl = gen.VideoUrl;   // 원격(24h) 보관 — 다운로드 실패 시 폴백

            // ── 2) 로컬 자산화(스트리밍 다운로드) · 진행은 IProgress<double>(this) 로 흡수 ──
            Percent = 0;
            SetPhase(WanJobPhase.Downloading, "완성 영상 다운로드 중…");
            var jobId = gen.TaskId ?? Guid.NewGuid().ToString("N");
            var dl = await _media.DownloadAsync(jobId, gen.VideoUrl, this, _cts.Token).ConfigureAwait(false);

            if (_cts.Token.IsCancellationRequested)
            {
                SetPhase(WanJobPhase.Canceled, "작업이 취소되었습니다.");
                return;
            }

            // ── 3) 완료 방송(로컬 확정 or 원격 폴백) ──
            if (dl.Ok && dl.LocalPath is not null)
            {
                LocalPath = dl.LocalPath;
                VideoUrl  = dl.PlaybackUrl;   // 로컬 가상호스트 URL(디스크 직접 스트리밍)
                IsLocal   = true;
                Percent   = 100;
                SetPhase(WanJobPhase.Completed, $"완성! 로컬 저장됨 · {FormatBytes(dl.Bytes)}");
            }
            else
            {
                // Graceful fallback — 다운로드 실패해도 작업은 완료(원격 URL로 재생)
                LocalPath = null;
                VideoUrl  = dl.PlaybackUrl;   // = 원격 URL(폴백)
                IsLocal   = false;
                Percent   = 100;
                SetPhase(WanJobPhase.Completed, "완성 · 원격 링크로 재생(로컬 저장 실패 — 링크 24시간 후 만료)");
            }
        }
        catch (OperationCanceledException) { SetPhase(WanJobPhase.Canceled, "작업이 취소되었습니다."); }
        catch (Exception ex) { Fail("예상치 못한 오류: " + ex.Message); }
    }

    /// <summary>진행 중 작업 취소(폴링·다운로드 중단). 원격 서버 태스크 자체는 계속될 수 있다.</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>완료/실패 후 초기 화면으로(진행 중이면 무시).</summary>
    public void Reset()
    {
        if (IsBusy) return;
        Phase = WanJobPhase.Idle; Percent = 0; StatusText = "대기 중";
        VideoUrl = null; LocalPath = null; RemoteUrl = null; IsLocal = false;
        Error = null; TaskId = null; Elapsed = TimeSpan.Zero;
        OnChange?.Invoke();
    }

    // ── IProgress<WanProgress> — 원격 생성(제출/폴링) 진행 보고 ──
    void IProgress<WanProgress>.Report(WanProgress p)
    {
        Percent = p.Percent;
        StatusText = p.Phase;
        Elapsed = p.Elapsed;
        if (p.TaskId is not null) TaskId = p.TaskId;
        Phase = p.Status switch
        {
            WanTaskStatus.Pending  => WanJobPhase.Queued,
            WanTaskStatus.Running  => WanJobPhase.Rendering,
            WanTaskStatus.Failed   => WanJobPhase.Failed,
            WanTaskStatus.Canceled => WanJobPhase.Canceled,
            _                      => Phase,   // Succeeded 는 StartAsync 가 Downloading 으로 이어받음 · Unknown 유지
        };
        OnChange?.Invoke();
    }

    // ── IProgress<double> — 로컬 다운로드 진행 보고(0–100 · Downloading 단계에서만) ──
    void IProgress<double>.Report(double pct)
    {
        Percent = (int)Math.Clamp(pct, 0, 100);
        StatusText = $"완성 영상 다운로드 중… {Percent}%";
        OnChange?.Invoke();
    }

    private void SetPhase(WanJobPhase phase, string text)
    {
        Phase = phase; StatusText = text;
        OnChange?.Invoke();
    }

    private void Fail(string message)
    {
        Error = message; Phase = WanJobPhase.Failed; StatusText = message;
        OnChange?.Invoke();
    }

    /// <summary>바이트를 사람이 읽는 단위로(B/KB/MB/GB).</summary>
    private static string FormatBytes(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double b = bytes; int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }
}
