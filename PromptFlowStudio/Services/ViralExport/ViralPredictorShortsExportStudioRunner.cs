using System.Globalization;
using PromptFlowStudio.Services.LiveClipper;

namespace PromptFlowStudio.Services.ViralExport;

/// <summary>
/// [PromptFlow Studio v17.85.0] 📊 바이럴 예측 스코어러 &amp; MP4 익스포터 러너.
///   ▸ 대상: v17.84 오토 클리퍼 패키징 완료본(<see cref="PackagedShort"/>) — 미연결/빈 큐 시 <see cref="Rehearse"/> 로 리허설 클립 생성.
///   ▸ <see cref="Analyze"/>: 클립 메타(스파이크 · 효과음 큐 · 자막 단어 · 해시 시드) → 60 fps 바이럴/표정 신호 → 4 축 스코어 · S/A/B/C · 썸네일 3 종 · 훅 카피 3 종.
///   ▸ <see cref="ExportAsync"/>: 링 재슬라이스 → <see cref="FastShortsMuxingHardwareExporterEngine"/> 백그라운드 fMP4 인메모리 렌더(진행률 · CTS 취소).
/// </summary>
public sealed class ViralPredictorShortsExportStudioRunner : IDisposable
{
    public const int Fps = 60, ThumbLeadMs = 300;

    private readonly object _gate = new();
    private readonly LiveToShortsAutoClipperStudioRunner? _own;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private uint _seed;
    private string? _dataUrl;

    public ViralPredictorShortsExportStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(LiveToShortsAutoClipperStudioRunner)) as LiveToShortsAutoClipperStudioRunner) { }

    public ViralPredictorShortsExportStudioRunner(LiveToShortsAutoClipperStudioRunner? clipper = null)
    {
        if (clipper is null) { _own = new LiveToShortsAutoClipperStudioRunner(); clipper = _own; }
        Clipper = clipper;
    }

    public event Action? OnChange;
    public LiveToShortsAutoClipperStudioRunner Clipper { get; }
    public FastShortsMuxingHardwareExporterEngine Exporter { get; } = new();
    public AiViralPotentialScoringEngine Scorer { get; } = new();
    public HighCtrThumbnailHookGenerator Thumbs { get; } = new();
    public bool HasSelection { get; private set; }
    public PackagedShort Selected { get; private set; }
    public ViralScore Score { get; private set; }
    public bool Exporting => _task is { IsCompleted: false };
    public float Progress => Exporter.Progress;
    public string? LastAction { get; private set; }
    public long Analyses { get; private set; }
    public long PriorityFlags { get; private set; }
    public int CandidateCount { get { var n = 0; for (var i = 0; i < Clipper.Packager.ResultCount; i++) if (Usable(Clipper.Packager.Result(i).Status)) n++; return n; } }

    public static bool Usable(ShortStatus s) => s is ShortStatus.Ready or ShortStatus.Dispatched;

    /// <summary>리허설 — 클리퍼 40 s 전진 + 즉석 클립(직전 30 s) → 최신 완료본 분석.</summary>
    public bool Rehearse()
    {
        if (!Clipper.Running) Clipper.Advance(40);
        Clipper.InstantClip();
        var ok = SelectLatest();
        LastAction = ok ? "🎬 리허설 클립 생성 · 분석 완료" : "⚠️ 리허설 클립 생성 실패";
        Raise();
        return ok;
    }

    public bool SelectLatest()
    {
        for (var i = 0; i < Clipper.Packager.ResultCount; i++)
        {
            var r = Clipper.Packager.Result(i);
            if (Usable(r.Status)) return Select(r.Id);
        }
        return false;
    }

    public bool Select(long id)
    {
        for (var i = 0; i < Clipper.Packager.ResultCount; i++)
        {
            var r = Clipper.Packager.Result(i);
            if (r.Id != id || !Usable(r.Status)) continue;
            lock (_gate) { Selected = r; HasSelection = true; _dataUrl = null; }
            Analyze();
            return true;
        }
        return false;
    }

    // ══════════════════════ VPE-2/3 분석 ══════════════════════

    /// <summary>선택 클립 → 60 fps 신호 합성 → 스코어 · 썸네일 · 카피(신호 생성 경로 무할당).</summary>
    public ViralScore Analyze()
    {
        if (!HasSelection) return default;
        var s = Selected;
        Scorer.Reset(); Thumbs.Reset();
        _seed = (uint)(s.Hash ^ (s.Hash >> 32)) | 1u;
        var durUs = Math.Max(1_000_000L, s.EndUs - s.StartUs);
        var spikeUs = Math.Clamp(s.SpikeUs - s.StartUs, 0, durUs);
        var wordUs = Math.Max(0, spikeUs - 1_000_000);
        var frames = (int)(durUs * Fps / 1_000_000);
        long nextEvent = 1_800_000; var brollNext = true;
        var emo = AvatarEmotionTag.Neutral;
        for (var k = 0; k < frames; k++)
        {
            var t = k * 1_000_000L / Fps;
            var near = MathF.Exp(-MathF.Abs((t - spikeUs) / 1e6f) / 1.5f);   // 스파이크 근접도
            var loopPhase = MathF.Cos(MathF.PI * 2f * t / durUs);           // 머리/꼬리 일치(루프)
            var f = new ViralFrame
            {
                TUs = t,
                PitchHz = 170f + 40f * MathF.Sin(t / 1e6f * 2.3f) + 120f * near + 6f * Rand(),
                Rms = 0.05f + 0.35f * near + 0.02f * Rand() + 0.01f * loopPhase,
                Motion = 0.2f + 0.6f * near + 0.05f * loopPhase,
                Luma = 0.45f + 0.05f * loopPhase,
            };
            if (s.Manual && t < 3_000_000) f.PitchHz += 60f * MathF.Sin(t / 1e6f * 9f);   // 수동 클립 = 진행자 멘트 도입
            // 키네틱 줌 펀치 = v17.76 휘쉬/임팩트/단어 팝 큐 시각
            if (Hit(t, spikeUs - 120_000) || Hit(t, spikeUs)) f.ZoomPunch = true;
            for (var w = 0; w < s.Words; w++) if (Hit(t, wordUs + w * 400_000L)) f.ZoomPunch = true;
            // 시각 환기 — 1.6–2.4 s 지터 B-Roll ↔ 스티커 교차
            if (t >= nextEvent) { if (brollNext) f.BRollCut = true; else f.Sticker = true; brollNext = !brollNext; nextEvent = t + 1_600_000 + (long)(800_000 * Rand()); }
            // 감정 — 스파이크 −1.5 s 놀람 · 스파이크 흥분 · +5 s 행복 · 종료 2 s 전 중립 복귀(루프)
            emo = t >= durUs - 2_000_000 ? AvatarEmotionTag.Neutral : t >= spikeUs + 5_000_000 ? AvatarEmotionTag.Happy : t >= spikeUs ? AvatarEmotionTag.Excited : t >= spikeUs - 1_500_000 ? AvatarEmotionTag.Surprise : AvatarEmotionTag.Neutral;
            f.Emotion = emo;
            Scorer.Push(f);
            Thumbs.Push(new FaceFrame { TUs = t, JawOpen = Math.Clamp(0.15f + 0.8f * near + 0.05f * Rand(), 0f, 1f), Smile = Math.Clamp(0.25f + 0.5f * near * (t > spikeUs ? 1f : 0.6f), 0f, 1f), Motion = f.Motion });
        }
        var score = Scorer.Evaluate();
        Thumbs.Compose(InstantShortsPackagingPipelineGuard.Captions[Math.Clamp(s.Caption, 0, InstantShortsPackagingPipelineGuard.Captions.Length - 1)],
            (int)Math.Max(1, (s.EndUs - s.StartUs) / 1_000_000), (int)(100 + s.Score * 250));
        Score = score; Analyses++;
        if (score.Priority) PriorityFlags++;
        LastAction = "📊 #" + s.Id.ToString(CultureInfo.InvariantCulture) + " 예측 " + score.Total.ToString("0", CultureInfo.InvariantCulture) + "점 · " + AiViralPotentialScoringEngine.GradeName(score.Grade) + (score.Priority ? " · 🚩 우선 배포" : null);
        Raise();
        return score;
    }

    private static bool Hit(long t, long at) => at >= 0 && t <= at && at < t + 1_000_000L / Fps;

    private float Rand()
    {
        _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
        return (_seed & 0xFFFFFF) / 16777216f;
    }

    // ══════════════════════ VPE-1 fMP4 인출 ══════════════════════

    /// <summary>동기 인출(호출 스레드) — 링 재슬라이스 → fMP4.</summary>
    public ExportStatus ExportNow(CancellationToken ct = default)
    {
        if (!HasSelection) return ExportStatus.Failed;
        var s = Selected;
        ExportStatus st;
        if (!Clipper.Ring.TrySlice(s.StartUs, s.EndUs, out var slice) || slice.StartPtsUs != s.StartUs) st = ExportStatus.Expired;
        else st = Exporter.Export(Clipper.Ring, slice, ShortsOverlay.From(s), s.Id, ct);
        lock (_gate) _dataUrl = null;
        var r = Exporter.Last;
        LastAction = st == ExportStatus.Ready
            ? "🎬 fMP4 인출 완료 — " + (r.Bytes / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MB · " + r.RenderMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms · 디스크 I/O 0"
            : "⚠️ 인출 " + FastShortsMuxingHardwareExporterEngine.StatusName(st);
        Raise();
        return st;
    }

    /// <summary>백그라운드 인출(BelowNormal 우선순위 무관 스레드풀) — 진행률 <see cref="Progress"/>.</summary>
    public Task<ExportStatus> ExportAsync()
    {
        if (Exporting || !HasSelection) return Task.FromResult(ExportStatus.Failed);
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        LastAction = "⏳ fMP4 인메모리 렌더 중…";
        Raise();
        var t = Task.Run(() =>
        {
            try { return ExportNow(ct); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("ViralExport.Export", ex); return ExportStatus.Failed; }
        });
        _task = t;
        return t;
    }

    public void CancelExport()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        LastAction = "⏹ 인출 취소 요청";
        Raise();
    }

    /// <summary>다운로드 링크(data: URL · UI 경로 1 회 생성 · 32 MB 상한).</summary>
    public string? DownloadUrl()
    {
        lock (_gate)
        {
            if (_dataUrl is not null) return _dataUrl;
            var mem = Exporter.Output;
            if (mem.IsEmpty || mem.Length > 32 * 1024 * 1024) return null;
            return _dataUrl = "data:video/mp4;base64," + Convert.ToBase64String(mem.Span);
        }
    }

    public string FileName() => "pfs_short_" + (HasSelection ? Selected.Id : 0).ToString(CultureInfo.InvariantCulture) + "_" + AiViralPotentialScoringEngine.GradeName(Score.Grade) + ".mp4";

    public bool DispatchSelected()
    {
        if (!HasSelection) return false;
        var ok = Clipper.Dispatch(Selected.Id);
        LastAction = ok ? "🚀 #" + Selected.Id.ToString(CultureInfo.InvariantCulture) + " 우선 배포 큐 이관" : "⚠️ 이관 불가(이미 이관/쿼터)";
        Raise();
        return ok;
    }

    public void Reset()
    {
        CancelExport();
        Scorer.Reset(); Thumbs.Reset();
        lock (_gate) { HasSelection = false; Selected = default; Score = default; _dataUrl = null; }
        Analyses = PriorityFlags = 0;
        LastAction = "♻ 초기화";
        Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ViralExport.OnChange", ex); }
    }

    public string Summary() =>
        !HasSelection ? "대기" :
        "#" + Selected.Id.ToString(CultureInfo.InvariantCulture) + " · " + Score.Total.ToString("0", CultureInfo.InvariantCulture) + "점 " + AiViralPotentialScoringEngine.GradeName(Score.Grade) +
        " · 인출 " + Exporter.Exports.ToString(CultureInfo.InvariantCulture) + " · " + FastShortsMuxingHardwareExporterEngine.StatusName(Exporter.Last.Status);

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _task?.Wait(1000); } catch (AggregateException) { }
        _cts?.Dispose(); _cts = null;
        _own?.Dispose();
        OnChange = null;
    }
}
