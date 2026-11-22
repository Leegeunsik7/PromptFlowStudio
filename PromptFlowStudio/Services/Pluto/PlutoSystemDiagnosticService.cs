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

        return BuildMarkdown(new PlutoDiagnosticInputs(_utcNow())
        {
            Drive = drive, Conflicts = conflicts, PendingConflictResolutions = pending, Realized = realized, HitRate = hitRate,
            ParetoModalities = pareto, MistakeRules = rules, MistakeDecay = decay, Errors = errors,
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
        return sb.ToString();
    }

    private static bool HasError(PlutoDiagnosticInputs input, string section) => input.Errors is { } e && e.ContainsKey(section);

    private static void AppendError(StringBuilder sb, PlutoDiagnosticInputs input, string section)
    {
        if (input.Errors is { } e && e.TryGetValue(section, out var msg)) sb.Append("- ⚠️ 수집 실패: ").Append(Cell(msg)).Append('\n');
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
            sb.Append(IsAbsolutePath(token) ? RedactedPathPrefix + LeafOf(token) : token);
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
