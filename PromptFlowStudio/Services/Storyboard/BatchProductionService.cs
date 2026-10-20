using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>배치 항목 상태 — 대기/생성중/완료/실패/건너뜀.</summary>
public enum BatchItemStatus { Queued, Running, Completed, Failed, Skipped }

/// <summary>
/// [PromptFlowStudio] 대량 양산 배치 1건 — 한 편의 숏츠(제목 + 대본/주제)와 그 런타임 상태.
///   ⚠ 실행 중 상태가 제자리 갱신되는 '살아있는' 엔티티라 record 가 아니라 mutable class.
/// </summary>
public sealed class BatchItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>숏츠 제목(프로젝트 제목/커버 문구/파일명에 사용).</summary>
    public string Title { get; set; } = "";
    /// <summary>대본 또는 주제 한 줄 — 오토파서(<see cref="IStoryboardScriptWizardService"/>) 입력.</summary>
    public string Script { get; set; } = "";

    // ── 런타임 상태 ──
    public BatchItemStatus Status { get; set; } = BatchItemStatus.Queued;
    public int Percent { get; set; }
    public string StatusText { get; set; } = "대기";
    /// <summary>완성된 최종 영상 경로(성공 시).</summary>
    public string? FinalVideoPath { get; set; }
    /// <summary>생성된 커버 썸네일 경로(있을 때).</summary>
    public string? CoverPath { get; set; }
    /// <summary>메타데이터 텍스트 저장 경로(있을 때).</summary>
    public string? MetadataPath { get; set; }
    /// <summary>저장된 프로젝트 JSON 경로(있을 때).</summary>
    public string? ProjectPath { get; set; }
    public string? Error { get; set; }

    /// <summary>더 이상 처리하지 않는 종료 상태인지.</summary>
    public bool IsTerminal => Status is BatchItemStatus.Completed or BatchItemStatus.Failed or BatchItemStatus.Skipped;
}

/// <summary>배치 렌더링 옵션(전 항목 공통 적용). UI 에서 설정 후 <see cref="BatchProductionService.StartAsync"/> 로 전달.</summary>
public sealed record BatchOptions
{
    public string Resolution { get; init; } = "1080x1920";   // 기본 세로 쇼츠
    public int CutCount { get; init; } = 5;                    // 항목당 컷 수(3~5)
    public double Speed { get; init; } = 1.0;
    public bool Fade { get; init; }
    public bool SmartSfx { get; init; }
    public bool AutoCover { get; init; } = true;              // 제목으로 커버 썸네일 자동 생성
    public bool AutoMetadata { get; init; } = true;          // 유튜브 메타데이터 자동 생성·저장
    public bool UseOfflineTts { get; init; }
    public string? WatermarkPath { get; init; }              // 전 항목 공통 브랜드 워터마크
    public string? BgmPath { get; init; }                    // 전 항목 공통 BGM
}

/// <summary>
/// [PromptFlowStudio] 멀티 숏츠 대량 자동 양산 배치 팩토리(관찰 가능 싱글턴) — CSV/TXT 대량 입력을 큐로 받아
///   백그라운드에서 <b>순차</b>로 한 편씩 양산한다. 각 항목: 대본 오토파싱 → 스토리보드 연쇄 생성/병합(<see cref="StoryboardJobManager"/>)
///   → 커버 썸네일 → 유튜브 메타데이터 → 프로젝트 저장.
///   ▸ Graceful: 한 항목이 실패해도 배치는 멈추지 않고 다음 항목으로 진행(개별 Skip/Failed 표시).
///   ▸ 일시정지: 항목 경계에서 적용(진행 중 항목은 완료 후 정지 — 생성 비용/API 호출 낭비 방지).
///   ▸ 취소: 남은 항목은 대기 유지, 진행 중 항목은 중단(건너뜀).
///   ⚠ 내부적으로 <see cref="StoryboardJobManager"/>(싱글턴 파이프라인)를 순차 재사용한다 — 배치 실행 중에는 단일 컷/스토리보드
///     화면의 수동 생성과 겹치지 않도록 UI 가 안내(각 항목 시작 전 파이프라인이 비는 것을 대기).
///   순수 BCL(외부 NuGet 0). OnChange 는 백그라운드 스레드에서 올 수 있어 컴포넌트가 InvokeAsync 로 마샬링한다.
/// </summary>
public sealed class BatchProductionService
{
    private readonly IStoryboardScriptWizardService _wizard;
    private readonly StoryboardJobManager _jobs;
    private readonly IShortsCoverService _cover;
    private readonly IYoutubeMetadataService _meta;
    private readonly StoryboardRepository _repo;
    private readonly ILocalMediaService _media;

    private readonly object _gate = new();
    private readonly List<BatchItem> _queue = new();
    private CancellationTokenSource? _cts;

    public BatchProductionService(IStoryboardScriptWizardService wizard, StoryboardJobManager jobs,
                                  IShortsCoverService cover, IYoutubeMetadataService meta,
                                  StoryboardRepository repo, ILocalMediaService media)
    {
        _wizard = wizard;
        _jobs = jobs;
        _cover = cover;
        _meta = meta;
        _repo = repo;
        _media = media;
    }

    /// <summary>큐/진행 상태 변경 방송(구독: 컴포넌트 OnInitialized / 해지: Dispose).</summary>
    public event Action? OnChange;
    /// <summary>배치 전체 종료 시 1회 방송.</summary>
    public event Action? OnCompleted;

    // ── 상태 ──
    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    /// <summary>일시정지 예약됨(진행 중 항목 완료 후 멈춤).</summary>
    public bool PauseRequested { get; private set; }
    /// <summary>현재 처리 중 항목 제목(표시용).</summary>
    public string? CurrentTitle { get; private set; }
    /// <summary>현재 처리 순번(1-based, 표시용).</summary>
    public int CurrentIndex { get; private set; }
    /// <summary>배치 레벨 상태 메시지.</summary>
    public string? StatusMessage { get; private set; }

    /// <summary>생성 엔진이 구성되어 있어야 배치 시작 가능.</summary>
    public bool Enabled => _jobs.Enabled;

    /// <summary>큐 스냅샷(렌더용 · 스레드 안전 복사본).</summary>
    public IReadOnlyList<BatchItem> Items { get { lock (_gate) return _queue.ToList(); } }

    public int TotalCount { get { lock (_gate) return _queue.Count; } }
    public int QueuedCount { get { lock (_gate) return _queue.Count(i => i.Status == BatchItemStatus.Queued); } }
    public int CompletedCount { get { lock (_gate) return _queue.Count(i => i.Status == BatchItemStatus.Completed); } }
    public int FailedCount { get { lock (_gate) return _queue.Count(i => i.Status is BatchItemStatus.Failed or BatchItemStatus.Skipped); } }

    /// <summary>전체 진행률(0–100) — 종료 항목 100% + 진행 중 항목의 부분 진행률 평균.</summary>
    public int OverallPercent
    {
        get
        {
            lock (_gate)
            {
                if (_queue.Count == 0) return 0;
                double sum = _queue.Sum(i => i.IsTerminal ? 100 : (i.Status == BatchItemStatus.Running ? i.Percent : 0));
                return (int)Math.Round(sum / _queue.Count);
            }
        }
    }

    private void Notify() => OnChange?.Invoke();

    // ── 큐 편집 ──────────────────────────────────────────────────────────

    /// <summary>
    /// CSV/TXT 텍스트를 파싱해 대기열에 추가한다. 각 줄 = 한 편.
    ///   ▸ 구분자(탭 우선, 없으면 콤마)가 있으면 [제목, 대본]으로 분리(간단 따옴표 인식) — 없으면 줄 전체를 대본, 제목은 앞부분 축약.
    ///   ▸ 첫 줄이 헤더(title/제목/topic/주제/script/대본…)면 건너뜀. 빈 줄 무시. 반환값 = 추가된 항목 수.
    /// </summary>
    public int ImportFromText(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var added = 0;
        var first = true;
        var newItems = new List<BatchItem>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var (title, script) = SplitRow(line);
            if (first && IsHeader(title)) { first = false; continue; }   // 헤더 행 스킵(첫 데이터 행에서만)
            first = false;
            if (string.IsNullOrWhiteSpace(script)) continue;
            newItems.Add(new BatchItem { Title = title, Script = script });
            added++;
        }
        if (added > 0)
        {
            lock (_gate) _queue.AddRange(newItems);
            Notify();
        }
        return added;
    }

    /// <summary>수동으로 한 편 추가(제목/대본). 대본만 있으면 제목은 자동 축약.</summary>
    public void AddManual(string? title, string? script)
    {
        var s = (script ?? "").Trim();
        var t = (title ?? "").Trim();
        if (s.Length == 0 && t.Length == 0) return;
        if (s.Length == 0) s = t;
        if (t.Length == 0) t = Trunc(s, 30);
        lock (_gate) _queue.Add(new BatchItem { Title = t, Script = s });
        Notify();
    }

    /// <summary>대기 중(미시작) 항목 제거. 실행 중 항목/종료 항목은 유지.</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            var it = _queue.FirstOrDefault(x => x.Id == id);
            if (it is null || it.Status == BatchItemStatus.Running) return;
            _queue.Remove(it);
        }
        Notify();
    }

    /// <summary>큐 전체 비우기(실행 중이 아닐 때만).</summary>
    public void ClearAll()
    {
        if (IsRunning) return;
        lock (_gate) _queue.Clear();
        StatusMessage = null;
        Notify();
    }

    /// <summary>완료/실패/건너뜀 항목만 목록에서 제거(대기·진행 중은 유지).</summary>
    public void ClearFinished()
    {
        lock (_gate) _queue.RemoveAll(x => x.IsTerminal);
        Notify();
    }

    // ── 실행 제어 ────────────────────────────────────────────────────────

    /// <summary>배치 대량 양산 시작(백그라운드 순차). 이미 실행 중이거나 대기 항목이 없으면 무시.</summary>
    public async Task StartAsync(BatchOptions opt)
    {
        if (IsRunning) return;
        if (!_jobs.Enabled) { StatusMessage = "생성 엔진이 구성되지 않았습니다(API 키/엔진 설정 필요)."; Notify(); return; }
        List<BatchItem> pending;
        lock (_gate) pending = _queue.Where(i => i.Status == BatchItemStatus.Queued).ToList();
        if (pending.Count == 0) { StatusMessage = "대기 중인 항목이 없습니다."; Notify(); return; }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true; IsPaused = false; PauseRequested = false;
        StatusMessage = $"대량 양산 시작 — 총 {pending.Count}편";
        Notify();

        var idx = 0;
        try
        {
            foreach (var item in pending)
            {
                ct.ThrowIfCancellationRequested();

                // 일시정지 게이트(항목 경계) — 예약된 정지를 여기서 적용.
                if (PauseRequested)
                {
                    IsPaused = true; PauseRequested = false;
                    StatusMessage = "일시정지됨 — 재개를 누르면 다음 항목부터 계속합니다.";
                    Notify();
                }
                while (IsPaused && !ct.IsCancellationRequested)
                    await Task.Delay(300, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                // 인터랙티브 파이프라인이 비어 있어야 안전하게 재사용(수동 생성과 충돌 방지).
                while ((_jobs.IsRunning || _jobs.IsMerging) && !ct.IsCancellationRequested)
                    await Task.Delay(300, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                idx++;
                CurrentIndex = idx;
                CurrentTitle = item.Title;
                StatusMessage = $"양산 중 ({idx}/{pending.Count}) — {item.Title}";
                Notify();

                await ProcessItemAsync(item, opt, ct).ConfigureAwait(false);
            }
            StatusMessage = $"대량 양산 완료 — 성공 {CompletedCount} · 실패 {FailedCount} / 총 {pending.Count}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"양산 취소됨 — 성공 {CompletedCount} · 실패 {FailedCount} (남은 항목은 대기 유지)";
        }
        catch (Exception ex)
        {
            Program.Log("BatchProductionService.StartAsync", ex);
            StatusMessage = "양산 중 오류: " + Short(ex);
        }
        finally
        {
            IsRunning = false; IsPaused = false; PauseRequested = false;
            CurrentTitle = null; CurrentIndex = 0;
            Notify();
            OnCompleted?.Invoke();
        }
    }

    /// <summary>일시정지 요청(진행 중 항목 완료 후 멈춤). 이미 항목 대기 상태면 즉시 정지.</summary>
    public void Pause()
    {
        if (!IsRunning || IsPaused) return;
        PauseRequested = true;
        StatusMessage = "일시정지 예약됨 — 현재 항목 완료 후 정지합니다.";
        Notify();
    }

    /// <summary>일시정지 해제(다음 항목부터 재개).</summary>
    public void Resume()
    {
        if (!IsRunning) return;
        IsPaused = false; PauseRequested = false;
        StatusMessage = "재개 — 다음 항목부터 계속합니다.";
        Notify();
    }

    /// <summary>배치 취소(남은 항목 대기 유지 · 진행 중 항목 중단).</summary>
    public void Cancel()
    {
        if (!IsRunning) return;
        try { _jobs.Cancel(); } catch { /* 진행 중 항목 중단 시도 */ }
        _cts?.Cancel();
    }

    // ── 항목 처리 ────────────────────────────────────────────────────────

    private async Task ProcessItemAsync(BatchItem item, BatchOptions opt, CancellationToken ct)
    {
        item.Status = BatchItemStatus.Running;
        item.Percent = 0;
        item.StatusText = "대본 분석";
        item.Error = null;
        Notify();

        // 진행률 미러링 — 이 항목이 도는 동안 JobManager 진행률을 항목 카드에 반영.
        void OnJobsChanged()
        {
            if (item.Status != BatchItemStatus.Running) return;
            item.Percent = _jobs.OverallPercent;
            item.StatusText = _jobs.IsMerging ? $"컷 병합 중… {_jobs.MergePercent}%" : $"컷 생성 중… {_jobs.OverallPercent}%";
            Notify();
        }

        try
        {
            // ① 대본 오토파싱 → 컷 초안.
            var cuts = _wizard.Generate(item.Script, Math.Clamp(opt.CutCount, 3, 5));
            if (cuts.Count == 0)
            {
                item.Status = BatchItemStatus.Skipped;
                item.StatusText = "건너뜀(빈 대본)";
                item.Error = "대본에서 생성할 컷을 만들지 못했습니다.";
                Notify();
                return;
            }

            // ② 프로젝트 구성.
            var project = BuildProject(item, cuts, opt);

            // ③ 스토리보드 연쇄 생성 + 자동 병합(진행률 미러링).
            _jobs.OnChange += OnJobsChanged;
            try { await _jobs.StartAsync(project, parallel: false).ConfigureAwait(false); }
            finally { _jobs.OnChange -= OnJobsChanged; }
            ct.ThrowIfCancellationRequested();

            // 성공 판정 — 모든 컷 완료여야 성공(일부 실패면 개별 Failed 로 마감하고 다음 항목).
            var allDone = project.Scenes.Count > 0 && project.Scenes.All(s => s.Status == SceneStatus.Completed);
            item.FinalVideoPath = project.FinalVideoPath
                                  ?? project.Scenes.FirstOrDefault(s => !string.IsNullOrEmpty(s.LocalPath))?.LocalPath;

            if (!allDone || item.FinalVideoPath is null)
            {
                item.Status = BatchItemStatus.Failed;
                item.StatusText = "실패(일부 컷 미완성)";
                var failed = project.Scenes.Count(s => s.Status == SceneStatus.Failed);
                item.Error = failed > 0 ? $"{failed}개 컷 생성 실패" : "최종 영상이 생성되지 않았습니다.";
                Notify();
                return;
            }

            // ④ 커버 썸네일(선택) — 최종 영상 1초 프레임 위에 제목 합성.
            if (opt.AutoCover && !string.IsNullOrWhiteSpace(project.CoverTitle) && _cover.IsAvailable)
            {
                item.StatusText = "커버 썸네일 생성 중…"; Notify();
                var coverOut = Path.Combine(Path.GetDirectoryName(item.FinalVideoPath)!, "cover.jpg");
                try
                {
                    var cp = await _cover.GenerateAsync(item.FinalVideoPath, project.CoverTitle, coverOut, ct: ct).ConfigureAwait(false);
                    if (cp is not null) item.CoverPath = cp;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 커버 실패는 항목 성공에 영향 없음 */ }
            }

            // ⑤ 유튜브 메타데이터(선택) — 제목/설명/해시태그를 .txt 로 저장.
            if (opt.AutoMetadata)
            {
                item.StatusText = "메타데이터 생성 중…"; Notify();
                try
                {
                    var md = _meta.Generate(project);
                    if (md.HasContent)
                        item.MetadataPath = SaveMetadata(item, md);
                }
                catch { /* 메타데이터 실패는 항목 성공에 영향 없음 */ }
            }

            // ⑥ 프로젝트 저장(재편집 가능).
            try { item.ProjectPath = _repo.Save(project); } catch { /* 저장 실패 무시 */ }

            item.Status = BatchItemStatus.Completed;
            item.Percent = 100;
            item.StatusText = "완료";
            Notify();
        }
        catch (OperationCanceledException)
        {
            item.Status = BatchItemStatus.Skipped;
            item.StatusText = "취소됨";
            Notify();
            throw;   // 배치 루프로 전파(전체 취소)
        }
        catch (Exception ex)
        {
            Program.Log("BatchProductionService.ProcessItem", ex);
            item.Status = BatchItemStatus.Failed;
            item.StatusText = "실패";
            item.Error = Short(ex);
            Notify();
            // ⚠ 개별 항목 실패는 삼키고 다음 항목으로(Graceful) — throw 하지 않는다.
        }
    }

    /// <summary>배치 항목 → 스토리보드 프로젝트(옵션 반영). RunWizard 와 동일 규칙(세로면 720*1280 컷).</summary>
    private static StoryboardProject BuildProject(BatchItem item, IReadOnlyList<WizardCut> cuts, BatchOptions opt)
    {
        var (w, h) = StoryboardProject.ParseResolution(opt.Resolution);
        var size = h > w ? "720*1280" : "1280*720";
        var project = new StoryboardProject
        {
            Title = string.IsNullOrWhiteSpace(item.Title) ? "무제 숏츠" : item.Title,
            ExportResolution = opt.Resolution,
            PlaybackSpeed = opt.Speed,
            UseFadeEffect = opt.Fade,
            EnableSmartSfx = opt.SmartSfx,
            UseOfflineTts = opt.UseOfflineTts,
            WatermarkImagePath = opt.WatermarkPath,
            BgmPath = opt.BgmPath,
            CoverTitle = opt.AutoCover ? (item.Title ?? "").Trim() : "",
        };
        foreach (var c in cuts)
            project.Scenes.Add(new StoryboardScene
            {
                Prompt = string.IsNullOrWhiteSpace(c.CameraMotion) ? c.Prompt : $"{c.Prompt}, {c.CameraMotion}",
                VoiceText = c.VoiceText,
                Size = size,
            });
        return project;
    }

    /// <summary>메타데이터를 최종 영상 옆 <c>metadata.txt</c> 로 저장하고 경로를 반환(실패 시 null).</summary>
    private string SaveMetadata(BatchItem item, YoutubeMetadata md)
    {
        try
        {
            var dir = item.FinalVideoPath is not null
                ? Path.GetDirectoryName(item.FinalVideoPath)!
                : Path.Combine(_media.MediaRoot, "_batch");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "metadata.txt");
            var sb = new StringBuilder();
            sb.AppendLine("【제목】").AppendLine(md.Title).AppendLine();
            sb.AppendLine("【설명】").AppendLine(md.Description).AppendLine();
            sb.AppendLine("【해시태그】").AppendLine(md.Hashtags);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex) { Program.Log("BatchProductionService.SaveMetadata", ex); return item.MetadataPath!; }
    }

    // ── CSV/TXT 파싱 유틸 ────────────────────────────────────────────────

    /// <summary>한 줄을 (제목, 대본)으로 분리 — 탭 우선, 없으면 콤마. 구분자 없으면 대본=줄전체·제목=축약. 간단 따옴표 인식.</summary>
    private static (string Title, string Script) SplitRow(string line)
    {
        var fields = SplitFields(line);
        if (fields.Count >= 2)
        {
            var title = fields[0].Trim();
            var script = string.Join(" ", fields.Skip(1)).Trim();   // 3열 이상이면 나머지를 대본으로 합침
            if (script.Length == 0) script = title;
            if (title.Length == 0) title = Trunc(script, 30);
            return (title, script);
        }
        return (Trunc(line, 30), line);
    }

    /// <summary>탭 우선, 없으면 콤마로 필드 분리. 따옴표로 감싼 필드 안의 구분자는 보존(간이 CSV).</summary>
    private static List<string> SplitFields(string line)
    {
        var delim = line.Contains('\t') ? '\t' : ',';
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }   // "" → 리터럴 "
                else inQuotes = !inQuotes;
            }
            else if (c == delim && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    private static readonly HashSet<string> HeaderWords = new(StringComparer.OrdinalIgnoreCase)
    { "title", "제목", "topic", "주제", "script", "대본", "name", "이름", "no", "번호", "순번" };

    /// <summary>첫 필드가 헤더 키워드면 헤더 행으로 간주(첫 데이터 행에서만 스킵).</summary>
    private static bool IsHeader(string firstField) => HeaderWords.Contains(firstField.Trim());

    private static string Trunc(string s, int n)
    {
        s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }

    private static string Short(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Length <= 120 ? m : m[..120] + "…";
    }
}
