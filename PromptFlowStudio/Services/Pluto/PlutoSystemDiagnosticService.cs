using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🩺 Step 28 — 통합 Pluto 진단 스냅샷(Markdown · 클립보드 복사용).</summary>
public interface IPlutoSystemDiagnosticService
{
    /// <summary>9 허브 Drive 동기화 · 충돌 · 실측 YouTube · 기획 적중률 · 4축 파레토 선두 · 제목 프레이밍 · 오답 노트 활성 가드(감쇠 잔여 벌점)를 Markdown 으로 집계.
    /// 섹션별 실패는 해당 섹션에 경고만 남기고 나머지는 계속([부분 구제]) · 취소만 전파.</summary>
    Task<string> BuildSnapshotMarkdownAsync(CancellationToken ct = default);

    /// <summary>
    /// 💾 Step 29 — 진단 스냅샷을 <c>pluto_diagnostic_{yyyyMMdd_HHmmss}.md</c>(UTF-8 BOM)로 원자 저장.
    /// <paramref name="directory"/> 생략 → 로컬 앱 데이터 진단 폴더. 기존 파일 · 디렉터리 · 심볼릭 링크는 절대 덮지 않음(같은 초 → <c>_2</c> 접미).
    /// </summary>
    Task<PlutoDiagnosticExportResult> ExportSnapshotMarkdownFileAsync(string? directory = null, CancellationToken ct = default);
}

/// <summary>💾 Step 29 — 진단 스냅샷 파일 저장 결과(메시지에는 절대 경로를 넣지 않음).</summary>
public sealed record PlutoDiagnosticExportResult(bool Ok, string? FullPath, string? FileName, long Bytes, string MessageKorean)
{
    public static PlutoDiagnosticExportResult Fail(string message) => new(false, null, null, 0, message);
}

/// <summary>🩺 진단 스냅샷 원천(순수 렌더러 입력) — null = 해당 허브 미연결.</summary>
public sealed record PlutoDiagnosticInputs(DateTime GeneratedAtUtc)
{
    public PlutoDriveHealthReport? Drive { get; init; }
    public IReadOnlyList<PlutoConflictCopy>? Conflicts { get; init; }
    public int PendingConflictResolutions { get; init; }
    public PlutoRealizedYouTubeSummary? Realized { get; init; }
    public PlutoBriefHitRateSummary? HitRate { get; init; }
    public IReadOnlyList<PlutoParetoModalityStatus>? ParetoModalities { get; init; }
    public IReadOnlyList<PlutoMistakeRuleSnapshot>? MistakeRules { get; init; }
    public PlutoMistakeDecayStatus? MistakeDecay { get; init; }
    /// <summary>📖 Step 38 — 시리즈 용어집 요약(null = 미연결).</summary>
    public PlutoGlossarySummary? Glossary { get; init; }
    /// <summary>🛰️ Step 38 — 최근 자율 활동(최신 순 · null = 미연결).</summary>
    public IReadOnlyList<PlutoActivityEntry>? RecentActivity { get; init; }
    /// <summary>섹션 키 → 수집 실패 사유.</summary>
    public IReadOnlyDictionary<string, string>? Errors { get; init; }
}

/// <summary>
/// 🩺 <see cref="IPlutoSystemDiagnosticService"/> 구현(순수 BCL · 결정론 Markdown).
///   ▸ 🔒 공유 안전 — 절대 경로(사용자 폴더 · 드라이브 문자)는 파일명만 남기고 가림 · 표 셀의 <c>|</c> · 개행 이스케이프(크리에이터 입력 훅 문자열이 표를 깨지 않도록).
/// </summary>
public sealed class PlutoSystemDiagnosticService : IPlutoSystemDiagnosticService
{
    public const string SectionDrive = "drive", SectionRealized = "realized", SectionPareto = "pareto", SectionMistakes = "mistakes";
    public const string SectionGlossary = "glossary", SectionActivity = "activity";
    /// <summary>🛰️ Step 38 — 최근 자율 활동 표 최대 행.</summary>
    public const int MaxActivityRows = 10;
    /// <summary>📖 Step 38 — 용어집 범위 표 최대 행.</summary>
    public const int MaxGlossaryScopeRows = 10;
    public const string GlossaryHeading = "## 5. 📖 시리즈 용어집 요약";
    public const string ActivityHeading = "## 6. 🛰️ 최근 자율 활동 피드";
    public const string Title = "# 🩺 Pluto 진단 스냅샷";
    public const string RedactedPathPrefix = "‹path›/";
    /// <summary>오답 노트 활성 가드 표 최대 행.</summary>
    public const int MaxMistakeRows = 30;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IPlutoDriveHealthService? _drive;
    private readonly IPlutoRenderCatalogService? _catalog;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private readonly IPlutoAutonomousBrainSummaryService? _brain;
    private readonly Func<DateTime> _utcNow;

    public PlutoSystemDiagnosticService(IPlutoDriveHealthService? drive, IPlutoRenderCatalogService? catalog, IPlutoMistakeNotebookService? notebook,
        IPlutoAutonomousBrainSummaryService? brain, Func<DateTime>? utcNow = null)
    {
        _drive = drive;
        _catalog = catalog;
        _notebook = notebook;
        _brain = brain;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
    }

    /// <summary>📖 Step 38 — 시리즈 용어집 원천(미설정 → 섹션 '미연결').</summary>
    public IPlutoSeriesArcMemoryService? SeriesArc { get; init; }
    /// <summary>🛰️ Step 38 — 자율 활동 피드 원천(미설정 → 섹션 '미연결').</summary>
    public IPlutoAutonomousActivityFeedService? ActivityFeed { get; init; }

    public async Task<string> BuildSnapshotMarkdownAsync(CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        PlutoDriveHealthReport? drive = null;
        IReadOnlyList<PlutoConflictCopy>? conflicts = null;
        var pending = 0;
        if (_drive is not null)
        {
            try
            {
                drive = _drive.CheckHealth();
                conflicts = _drive.ListConflicts();
                pending = _drive.PendingConflictResolutions;
            }
            catch (Exception ex) { errors[SectionDrive] = ex.Message; }
        }

        PlutoRealizedYouTubeSummary? realized = null;
        PlutoBriefHitRateSummary? hitRate = null;
        if (_catalog is not null)
        {
            try
            {
                var clips = await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false);
                realized = PlutoRealizedYouTubeSummary.Compute(clips);
                hitRate = PlutoBriefHitRateSummary.Compute(clips);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors[SectionRealized] = ex.Message; }
        }

        IReadOnlyList<PlutoParetoModalityStatus>? pareto = null;
        if (_brain is not null)
        {
            try
            {
                var bs = await _brain.GetSummaryAsync(ct).ConfigureAwait(false);
                pareto = bs.ParetoModalities;
                if (realized is null && bs.RealizedYouTube.LinkedVideoCount > 0) realized = bs.RealizedYouTube;
                if (hitRate is null && (bs.BriefHitRate.HasEvidence || bs.BriefHitRate.PendingClips > 0)) hitRate = bs.BriefHitRate;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors[SectionPareto] = ex.Message; }
        }

        IReadOnlyList<PlutoMistakeRuleSnapshot>? rules = null;
        PlutoMistakeDecayStatus? decay = null;
        if (_notebook is not null)
        {
            try
            {
                await _notebook.EnsureLoadedAsync(ct).ConfigureAwait(false);
                rules = _notebook.GetActiveMistakeRules(MaxMistakeRows);
                decay = _notebook.GetDecayStatus();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors[SectionMistakes] = ex.Message; }
        }

        PlutoGlossarySummary? glossary = null;
        if (SeriesArc is { } arc)
        {
            try
            {
                await arc.EnsureLoadedAsync(ct).ConfigureAwait(false);
                glossary = arc.GetGlossarySummary(MaxGlossaryScopeRows);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors[SectionGlossary] = ex.Message; }
        }

        IReadOnlyList<PlutoActivityEntry>? activity = null;
        if (ActivityFeed is { } feed)
        {
            try { activity = feed.GetRecent(MaxActivityRows); }
            catch (Exception ex) { errors[SectionActivity] = ex.Message; }
        }

        return BuildMarkdown(new PlutoDiagnosticInputs(_utcNow())
        {
            Drive = drive, Conflicts = conflicts, PendingConflictResolutions = pending, Realized = realized, HitRate = hitRate,
            ParetoModalities = pareto, MistakeRules = rules, MistakeDecay = decay, Glossary = glossary, RecentActivity = activity, Errors = errors,
        });
    }

    /// <summary>🩺 Markdown 렌더(순수 · 결정론) — 4개 섹션 고정 순서 · 미연결/실패 섹션도 제목은 항상 출력.</summary>
    public static string BuildMarkdown(PlutoDiagnosticInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new StringBuilder(2048);
        sb.Append(Title).Append('\n').Append('\n');
        sb.Append("- 생성: ").Append(input.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append(" UTC\n\n");

        // 1) ☁️ Drive
        sb.Append("## 1. ☁️ Drive 동기화 · 충돌\n\n");
        AppendError(sb, input, SectionDrive);
        if (input.Drive is { } d)
        {
            int plutoTotal = 0, plutoHealthy = 0;
            foreach (var it in d.Items)
                if (string.Equals(it.Category, PlutoDriveHealthService.CategoryPluto, StringComparison.Ordinal)) { plutoTotal++; if (it.Status == PlutoDriveFileStatus.Healthy) plutoHealthy++; }
            sb.Append("- 드라이브: ").Append(d.IsDriveMounted ? "연결됨" : "미연결").Append(" · 전체 ").Append(d.HealthyCount.ToString(Inv)).Append('/').Append(d.TotalCount.ToString(Inv))
              .Append(" 정상 · Pluto 허브 ").Append(plutoHealthy.ToString(Inv)).Append('/').Append(plutoTotal.ToString(Inv)).Append(" 정상\n");
            if (!string.IsNullOrEmpty(d.SettingsMirrorError)) sb.Append("- ⚠️ 설정 미러 오류: ").Append(Cell(d.SettingsMirrorError)).Append('\n');
            if (!string.IsNullOrEmpty(d.MemberMirrorError)) sb.Append("- ⚠️ 회원 미러 오류: ").Append(Cell(d.MemberMirrorError)).Append('\n');
            if (d.Items.Count > 0)
            {
                sb.Append("\n| 허브 | 상태 | 크기 | 수정(UTC) | 비고 |\n|---|---|---|---|---|\n");
                foreach (var it in d.Items)
                {
                    if (!string.Equals(it.Category, PlutoDriveHealthService.CategoryPluto, StringComparison.Ordinal)) continue;
                    sb.Append("| ").Append(Cell(it.FileName)).Append(" | ").Append(StatusText(it.Status)).Append(" | ").Append(it.SizeBytes.ToString(Inv)).Append(" B | ")
                      .Append(it.LastModifiedUtc is { } m ? m.UtcDateTime.ToString("yyyy-MM-dd HH:mm", Inv) : "—").Append(" | ").Append(Cell(it.ErrorDetail ?? "")).Append(" |\n");
                }
            }
            AppendCompanions(sb, d.Companions);
        }
        else if (!HasError(input, SectionDrive)) sb.Append("- 미연결\n");
        var conflicts = input.Conflicts ?? Array.Empty<PlutoConflictCopy>();
        sb.Append("- ⚔️ 미해결 충돌: ").Append(conflicts.Count.ToString(Inv)).Append("건");
        if (input.PendingConflictResolutions > 0) sb.Append(" · 적용 대기 ").Append(input.PendingConflictResolutions.ToString(Inv)).Append("건");
        sb.Append('\n');
        foreach (var c in conflicts)
            sb.Append("  - ").Append(Cell(c.HubFileName)).Append(" · ").Append(c.CapturedUtc.ToString("yyyy-MM-dd HH:mm", Inv)).Append(" UTC · ")
              .Append(c.SizeBytes.ToString(Inv)).Append(" B").Append(c.IsValidJson ? "" : " · ❌ 손상").Append(c.IsMergeable ? " · 병합 가능" : "").Append('\n');
        sb.Append('\n');

        // 2) 📺 실측 YouTube · 기획 적중률
        sb.Append("## 2. 📺 실측 YouTube 성과 · 기획 적중률\n\n");
        AppendError(sb, input, SectionRealized);
        if (input.Realized is { } r)
        {
            sb.Append("- 연결 영상 ").Append(r.LinkedVideoCount.ToString(Inv)).Append("편 · 실측 ").Append(r.MeasuredVideoCount.ToString(Inv)).Append("편 · 조회 ").Append(r.TotalViews.ToString(Inv)).Append('\n');
            if (r.HasRealizedData)
                sb.Append("- 가중 CTR ").Append(r.WeightedCtrPct.ToString("0.0", Inv)).Append("% · 3초 유지 ").Append(r.WeightedHookPct.ToString("0.0", Inv))
                  .Append("% · 완주 ").Append(r.WeightedCompletionPct.ToString("0.0", Inv)).Append("%\n");
        }
        else sb.Append("- 실측 데이터 없음\n");
        sb.Append("- 🎯 기획 적중률: ").Append(Cell((input.HitRate ?? PlutoBriefHitRateSummary.Empty).ToLabel())).Append("\n\n");
        // 🌐 Step 32 — 로캘별 분해(KO · EN · JA · ES 고정 행 · 미분기 로캘도 노출)
        sb.Append(LocaleHeading).Append("\n\n| 로캘 | 연결 | 실측 | 가중 CTR | 3초 | 완주 | 선두 제목 |\n|---|---|---|---|---|---|---|\n");
        var localeRows = input.Realized is { Locales.Count: > 0 } rl ? rl.Locales : PlutoLocalePerformanceRow.ComputeAll(null);
        foreach (var row in localeRows)
        {
            sb.Append("| ").Append(row.ShortLabel).Append(" (").Append(Cell(row.Locale)).Append(") | ").Append(row.LinkedCount.ToString(Inv)).Append(" | ").Append(row.MeasuredCount.ToString(Inv)).Append(" | ");
            if (row.MeasuredCount > 0)
                sb.Append(row.WeightedCtrPct.ToString("0.0", Inv)).Append("% | ").Append(row.WeightedHookPct.ToString("0.0", Inv)).Append("% | ").Append(row.WeightedCompletionPct.ToString("0.0", Inv)).Append("% | ");
            else sb.Append("— | — | — | ");
            sb.Append(Cell(row.LeadingFraming)).Append(row.FramingEvidenced ? "" : " (전역 사전)").Append(" |\n");
        }
        sb.Append('\n');

        // 3) ⚖️ 4축 파레토 · 제목 프레이밍
        sb.Append("## 3. ⚖️ 4축 파레토 선두 · 제목 프레이밍\n\n");
        AppendError(sb, input, SectionPareto);
        sb.Append("| 축 | 선두 | 3초 | 완주 | CTR | 프런티어/근거 | 🎣 |\n|---|---|---|---|---|---|---|\n");
        var kinds = new[] { PlutoPatternKind.Hook, PlutoPatternKind.Look, PlutoPatternKind.BgmMood, PlutoPatternKind.StyleTag };
        foreach (var k in kinds)
        {
            PlutoParetoModalityStatus? st = null;
            if (input.ParetoModalities is { } pm)
                for (var i = 0; i < pm.Count; i++) if (pm[i].Kind == k) { st = pm[i]; break; }
            sb.Append("| ").Append(KindText(k)).Append(" | ");
            if (st?.Leader is { } ld)
                sb.Append(Cell(ld.Label)).Append(" | ").Append(ld.Objectives.Hook.ToString("0.0", Inv)).Append(" | ").Append(ld.Objectives.Completion.ToString("0.0", Inv)).Append(" | ")
                  .Append(ld.Objectives.Ctr.ToString("0.0", Inv)).Append(" | ");
            else sb.Append("근거 없음 | — | — | — | ");
            sb.Append((st?.FrontierSize ?? 0).ToString(Inv)).Append('/').Append((st?.EvidencedCount ?? 0).ToString(Inv)).Append(" | ").Append((st?.ClickbaitCount ?? 0).ToString(Inv)).Append(" |\n");
        }
        if (input.Realized is { HasFramingLeader: true } rf)
            sb.Append("\n- 🏷️ 선두 제목 프레이밍: ").Append(Cell(rf.LeadingFraming)).Append(" · 축소 CTR ").Append(rf.LeadingFramingCtrPct.ToString("0.0", Inv)).Append("% · 표본 ")
              .Append(rf.LeadingFramingSamples.ToString(Inv)).Append("건\n\n");
        else sb.Append("\n- 🏷️ 선두 제목 프레이밍: 실측 귀속 부족\n\n");

        // 4) 🛡️ 오답 노트
        sb.Append("## 4. 🛡️ 오답 노트 활성 가드\n\n");
        AppendError(sb, input, SectionMistakes);
        if (input.MistakeDecay is { } md)
            sb.Append("- 추적 ").Append(md.TrackedCount.ToString(Inv)).Append(" · 가드 ").Append(md.ActiveGuardCount.ToString(Inv)).Append("(조합 ").Append(md.CompositeGuardCount.ToString(Inv))
              .Append(") · 회복 중 ").Append(md.RecoveringCount.ToString(Inv)).Append(" · 감쇠 소멸 ").Append(md.DecayedAwayPct.ToString("0.0", Inv)).Append("%\n");
        var rules = input.MistakeRules ?? Array.Empty<PlutoMistakeRuleSnapshot>();
        if (rules.Count == 0) sb.Append("- 활성 가드 없음\n");
        else
        {
            sb.Append("\n| 패턴 | 출처 | 차원 | 잔여 벌점 | 반감기(일) | 해제까지(일) |\n|---|---|---|---|---|---|\n");
            foreach (var rule in rules)
                sb.Append("| ").Append(Cell(rule.PatternKey)).Append(" | ").Append(rule.IsPostMortem ? "🩻 사후 분석" : "피드백").Append(" | ").Append(Cell(rule.FailureDimension)).Append(" | ")
                  .Append(rule.PenaltyWeight.ToString("0.00", Inv)).Append(" | ").Append(rule.HalfLifeDays.ToString("0", Inv)).Append(" | ").Append(rule.DaysUntilRelease.ToString("0.0", Inv)).Append(" |\n");
        }

        // 5) 📖 시리즈 용어집 요약(Step 38)
        sb.Append('\n').Append(GlossaryHeading).Append("\n\n");
        AppendError(sb, input, SectionGlossary);
        if (input.Glossary is { } g)
        {
            sb.Append("- 활성 시리즈 용어집 ").Append(g.ActiveSeriesCount.ToString(Inv)).Append("개 · 보호 용어 ").Append(g.TotalProtectedTerms.ToString(Inv))
              .Append("개(전역 ").Append(g.GlobalTermCount.ToString(Inv)).Append("개 포함)\n");
            if (g.TopScopes.Count > 0)
            {
                sb.Append("\n| 범위 | 보호 용어 |\n|---|---|\n");
                var shown = 0;
                foreach (var sc in g.TopScopes)
                {
                    if (sc is null) continue;
                    if (shown++ >= MaxGlossaryScopeRows) break;
                    var name = string.Equals(sc.Scope, PlutoSeriesArcMemoryService.GlobalGlossaryScope, StringComparison.Ordinal) ? "🌐 전역" : SafeCell(sc.Scope);
                    sb.Append("| ").Append(name).Append(" | ").Append(sc.TermCount.ToString(Inv)).Append(" |\n");
                }
            }
        }
        else if (!HasError(input, SectionGlossary)) sb.Append("- 미연결\n");

        // 6) 🛰️ 최근 자율 활동 피드(Step 38 · 블랙박스 비행 기록)
        sb.Append('\n').Append(ActivityHeading).Append("\n\n");
        AppendError(sb, input, SectionActivity);
        if (input.RecentActivity is { } acts)
        {
            if (acts.Count == 0) sb.Append("- 기록 없음\n");
            else
            {
                sb.Append("| 시각(UTC) | 종류 | 내용 |\n|---|---|---|\n");
                var shown = 0;
                foreach (var a in acts)
                {
                    if (a is null) continue;
                    if (shown++ >= MaxActivityRows) break;
                    // 🔒 재가림 — 피드 구현이 이미 가렸더라도 진단서는 공유 문서이므로 경로 · 비밀값 규칙을 한 번 더 적용
                    sb.Append("| ").Append(a.AtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", Inv)).Append(" | ").Append(a.Icon).Append(' ').Append(a.Kind.ToString())
                      .Append(" | ").Append(SafeCell(PlutoAutonomousActivityFeedService.Redact(a.MessageKorean))).Append(" |\n");
                }
            }
        }
        else if (!HasError(input, SectionActivity)) sb.Append("- 미연결\n");
        return sb.ToString();
    }

    /// <summary>🔒 Step 38 — 비밀값 가림 → 경로 가림 → 표 이스케이프(사용자 입력 · 예외 메시지용).</summary>
    public static string SafeCell(string? text) => Cell(PlutoAutonomousActivityFeedService.RedactSecrets(text));

    /// <summary>동반 동기화 소제목 헤더(테스트 · UI 공통).</summary>
    public const string CompanionHeading = "### 🔁 동반 동기화 파일(핵심 허브 9종 집계 밖)";
    /// <summary>🌐 Step 32 — 로캘별 성과 소절 제목.</summary>
    public const string LocaleHeading = "### 🌐 로캘별 성과(KO · EN · JA · ES)";

    /// <summary>🔁 Step 30 — 동반 파일 2종 소절(핵심 허브 표 · 집계와 분리 · 경로 없음 · 해시는 앞 12자).</summary>
    private static void AppendCompanions(StringBuilder sb, IReadOnlyList<PlutoCompanionSyncItem> companions)
    {
        sb.Append('\n').Append(CompanionHeading).Append("\n\n");
        if (companions.Count == 0) { sb.Append("- 점검 안 함\n"); return; }
        var inSync = 0;
        foreach (var c in companions) if (c.State == PlutoCompanionSyncState.InSync) inSync++;
        sb.Append("- 일치 ").Append(inSync.ToString(Inv)).Append('/').Append(companions.Count.ToString(Inv)).Append(" · 핵심 허브 ").Append(PlutoDriveHealthService.CorePlutoFileCount.ToString(Inv)).Append("종 불변\n\n");
        sb.Append("| 파일 | 상태 | 로컬 | Drive | 항목 | SHA-256(로컬/Drive) | 비고 |\n|---|---|---|---|---|---|---|\n");
        foreach (var c in companions)
            sb.Append("| ").Append(Cell(c.FileName)).Append(" | ").Append(Cell(c.StateKorean)).Append(" | ")
              .Append(c.LocalPresent ? c.LocalSizeBytes.ToString(Inv) + " B" : "—").Append(" | ")
              .Append(c.DrivePresent ? c.DriveSizeBytes.ToString(Inv) + " B" : "—").Append(" | ")
              .Append(c.EntryCount.ToString(Inv)).Append(" | ").Append(ShortSha(c.LocalSha256)).Append('/').Append(ShortSha(c.DriveSha256)).Append(" | ")
              .Append(Cell(c.ErrorDetail ?? "")).Append(" |\n");
    }

    private static string ShortSha(string? sha) => string.IsNullOrEmpty(sha) ? "—" : sha.Length <= 12 ? sha : sha.Substring(0, 12);

    private static bool HasError(PlutoDiagnosticInputs input, string section) => input.Errors is { } e && e.ContainsKey(section);

    private static void AppendError(StringBuilder sb, PlutoDiagnosticInputs input, string section)
    {
        if (input.Errors is { } e && e.TryGetValue(section, out var msg)) sb.Append("- ⚠️ 수집 실패: ").Append(SafeCell(msg)).Append('\n');   // 🔒 Step 38 — 예외 메시지의 토큰 · 키도 가림
    }

    private static string StatusText(PlutoDriveFileStatus s) => s switch
    {
        PlutoDriveFileStatus.Healthy => "✅ 정상",
        PlutoDriveFileStatus.Missing => "⚠️ 미생성",
        PlutoDriveFileStatus.CorruptOrEmpty => "❌ 손상",
        _ => "⛔ 드라이브 없음",
    };

    private static string KindText(PlutoPatternKind k) => k switch
    {
        PlutoPatternKind.Hook => "훅",
        PlutoPatternKind.Look => "룩",
        PlutoPatternKind.BgmMood => "BGM",
        _ => "태그",
    };

    // ── 💾 Step 29 — 진단서 파일 저장(.md) ──

    public const string ExportFilePrefix = "pluto_diagnostic_", ExportFileExtension = ".md";
    /// <summary>같은 초 충돌 시 접미 최대값(<c>_2</c> … <c>_99</c>).</summary>
    public const int MaxExportSuffix = 99;

    /// <summary>기본 저장 폴더 — 로컬 앱 데이터 <c>PromptFlowStudio/Pluto/diagnostics</c>.</summary>
    public static string DefaultExportDirectory()
        => Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", "Pluto", "diagnostics");

    /// <summary>파일명(InvariantCulture · 접미 1 → 없음).</summary>
    public static string ExportFileNameFor(DateTime stamp, int suffix = 1)
        => ExportFilePrefix + stamp.ToString("yyyyMMdd_HHmmss", Inv) + (suffix > 1 ? "_" + suffix.ToString(Inv) : string.Empty) + ExportFileExtension;

    public async Task<PlutoDiagnosticExportResult> ExportSnapshotMarkdownFileAsync(string? directory = null, CancellationToken ct = default)
    {
        var md = await BuildSnapshotMarkdownAsync(ct).ConfigureAwait(false);
        var stamp = _utcNow();
        stamp = (stamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(stamp, DateTimeKind.Utc) : stamp).ToLocalTime();
        return await Task.Run(() => WriteSnapshotFile(directory, md, stamp, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 💾 원자 저장(순수 BCL · 동기) — ① 저장 폴더 검증: 절대 경로만 · 잘못된 문자 거부 · 파일이 자리한 경로 · 재분석 지점(심볼릭 링크/정션) 폴더 거부
    /// ② 같은 폴더 임시 파일(CreateNew · BOM · 디스크 플러시) ③ <c>File.Move(overwrite: false)</c> — 대상이 파일 · 디렉터리 · 링크(끊긴 링크 포함)로
    /// 점유돼 있으면 다음 접미로(덮어쓰기 · 링크 따라가기 없음) ④ 실패 → 임시 파일 정리 + 실패 결과(예외 비전파 · 취소만 전파).
    /// </summary>
    public static PlutoDiagnosticExportResult WriteSnapshotFile(string? directory, string markdown, DateTime stamp, CancellationToken ct = default)
    {
        string dir;
        try
        {
            if (string.IsNullOrWhiteSpace(directory)) dir = DefaultExportDirectory();
            else
            {
                var d = directory.Trim();
                if (d.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(d)) return PlutoDiagnosticExportResult.Fail("⚠ 저장 폴더는 절대 경로여야 합니다");
                dir = Path.GetFullPath(d);
            }
            if (File.Exists(dir)) return PlutoDiagnosticExportResult.Fail("⚠ 저장 위치가 파일입니다 — 폴더를 지정하세요");
            var di = new DirectoryInfo(dir);
            if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0) return PlutoDiagnosticExportResult.Fail("⚠ 저장 폴더가 심볼릭 링크/정션이라 거부했습니다");
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return PlutoDiagnosticExportResult.Fail("⚠ 저장 폴더를 준비하지 못했습니다: " + RedactPaths(ex.Message));
        }

        var bom = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(markdown ?? string.Empty);
        var tmp = Path.Combine(dir, "." + ExportFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
            {
                fs.Write(bom, 0, bom.Length);
                fs.Write(body, 0, body.Length);
                fs.Flush(true);
            }
            for (var suffix = 1; suffix <= MaxExportSuffix; suffix++)
            {
                ct.ThrowIfCancellationRequested();
                var name = ExportFileNameFor(stamp, suffix);
                var target = Path.Combine(dir, name);
                if (IsPathOccupied(target)) continue;   // 🔒 파일 · 디렉터리 · 링크 → 다음 접미
                try { File.Move(tmp, target, overwrite: false); }
                catch (IOException) when (IsPathOccupied(target)) { continue; }   // 경합 — 그 사이 생김
                return new PlutoDiagnosticExportResult(true, target, name, bom.Length + body.LongLength, "💾 진단서를 저장했습니다: " + name);
            }
            return PlutoDiagnosticExportResult.Fail("⚠ 같은 시각 진단서가 너무 많습니다 — 잠시 후 다시 시도하세요");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return PlutoDiagnosticExportResult.Fail("⚠ 진단서 저장 실패: " + RedactPaths(ex.Message));
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[Pluto.Diagnostic] tmp cleanup: " + ex.Message); }
        }
    }

    /// <summary>경로가 무엇이든(파일 · 디렉터리 · 끊긴 링크 포함) 점유됐는지 — 링크 자체 속성으로 판정(대상 추적 없음).</summary>
    internal static bool IsPathOccupied(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }   // 판별 불가 → 점유로 간주(덮어쓰기 금지)
    }

    /// <summary>🔒 표 셀 안전화 — 절대 경로 가림 → <c>|</c> 이스케이프 → 개행 공백화.</summary>
    public static string Cell(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var redacted = RedactPaths(text);
        var sb = new StringBuilder(redacted.Length + 4);
        foreach (var ch in redacted)
        {
            if (ch == '|') sb.Append("\\|");
            else if (ch is '\r' or '\n' or '\t') sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>🔒 공백 구분 토큰 중 절대 경로(<c>X:\</c> · <c>\\server</c> · <c>/</c> 로 시작하는 다단 경로)를 <c>‹path›/파일명</c> 으로 치환.</summary>
    public static string RedactPaths(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.IndexOf(":\\", StringComparison.Ordinal) < 0 && text.IndexOf(":/", StringComparison.Ordinal) < 0 && text.IndexOf("\\\\", StringComparison.Ordinal) < 0
            && text.IndexOf('/') < 0) return text;
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { sb.Append(text[i]); i++; continue; }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            var token = text.Substring(start, i - start);
            if (!IsAbsolutePath(token)) { sb.Append(token); continue; }
            // 🔒 Step 29 — 공백 포함 경로(예: G:\내 드라이브\DATA) 이어 붙이기: 공백 1칸 뒤 같은 구분자를 가진 비절대 토큰은 같은 경로의 연속
            var sep = token.IndexOf('\\') >= 0 ? '\\' : '/';
            while (i + 1 < text.Length && text[i] == ' ' && !char.IsWhiteSpace(text[i + 1]))
            {
                var j = i + 1;
                var k = j;
                while (k < text.Length && !char.IsWhiteSpace(text[k])) k++;
                var next = text.Substring(j, k - j);
                if (next.IndexOf(sep) < 0 || IsAbsolutePath(next)) break;
                token = token + " " + next;
                i = k;
            }
            sb.Append(RedactedPathPrefix + LeafOf(token));
        }
        return sb.ToString();
    }

    private static bool IsAbsolutePath(string token)
    {
        var t = token.Trim('\'', '"', '(', ')', '[', ']', ',', ';', '.');
        if (t.Length >= 3 && char.IsLetter(t[0]) && t[1] == ':' && (t[2] == '\\' || t[2] == '/')) return true;
        if (t.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        return t.Length > 1 && t[0] == '/' && t.IndexOf('/', 1) > 0 && !t.StartsWith("//", StringComparison.Ordinal);
    }

    private static string LeafOf(string token)
    {
        var t = token.Trim('\'', '"', '(', ')', '[', ']', ',', ';');
        var trailing = t.EndsWith('.') ? "." : "";
        t = t.TrimEnd('.', '\\', '/');
        var leaf = Path.GetFileName(t.Replace('/', '\\').Replace('\\', Path.DirectorySeparatorChar));
        return (string.IsNullOrEmpty(leaf) ? "…" : leaf) + trailing;
    }
}
