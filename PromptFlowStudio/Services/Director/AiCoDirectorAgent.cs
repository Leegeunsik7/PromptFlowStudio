using System;
using System.Collections.Generic;
using System.Linq;
using PromptFlowStudio.Services.Media;       // FoleySpec / VideoTransition
using PromptFlowStudio.Services.Storyboard;  // StoryboardProject / StoryboardScene

namespace PromptFlowStudio.Services.Director;

/// <summary>연출 노트 심각도.</summary>
public enum DirectorSeverity { Info, Suggest, Warn }

/// <summary>원클릭 수정 종류.</summary>
public enum DirectorFixKind { None, AppendShot, SetTension, AddSfx, ChangeTransition }

/// <summary>원클릭 수정 패치(대상 컷 1-based · 인자).</summary>
public sealed record DirectorFix(DirectorFixKind Kind, int CutIndex, string Arg, int IntArg);

/// <summary>연출 노트 — 대상 컷·심각도·분류·메시지 + (선택)원클릭 수정.</summary>
public sealed record DirectorNote(int CutIndex, DirectorSeverity Severity, string Category, string Message, string? FixLabel, DirectorFix? Fix);

/// <summary>연출 리포트 — 종합 점수(0~100)·노트 목록·요약.</summary>
public sealed record DirectorReport(int Score, IReadOnlyList<DirectorNote> Notes, string Summary);

/// <summary>
/// [PromptFlowStudio] 멀티모달 AI 자율 가이드 연출 감독(v3.0.0) — 순수 C# BCL 휴리스틱(외부 ML 라이브러리 0).
///   ▸ 완성 컷 시퀀스의 <b>프롬프트·텐션 곡선·카메라 워크·폴리</b>를 종합 분석해 연출 개선안을 실시간 제안한다.
///   ▸ "2번 컷 템포가 늘어집니다", "4번 컷을 매크로 렌즈로 바꾸면 훅이 강화됩니다" 등 노트 + <b>원클릭 수정 패치</b>(<see cref="ApplyFix"/>)를 함께 제공.
///   ▸ 텐션 처짐/피크 부재/훅 약함/카메라 단조/폴리 누락/짧은 프롬프트를 규칙 기반으로 검출(결정론적 · Gemini 불필요).
/// </summary>
public sealed class AiCoDirectorAgent
{
    private static readonly string[] CameraWords = { "zoom", "orbit", "dolly", "pan", "macro", "close-up", "closeup", "wide", "push-in", "pull", "aerial", "drone", "handheld", "whip", "tilt" };
    private static readonly string[] HookWords = { "반전", "충격", "마법", "스파이크", "reveal", "shock", "magic", "spike", "burst", "explode", "twist", "충돌", "pop", "surprise" };

    /// <summary>컷 시퀀스를 분석해 연출 리포트를 만든다.</summary>
    public DirectorReport Analyze(StoryboardProject project)
    {
        var notes = new List<DirectorNote>();
        var scenes = project?.Scenes ?? new List<StoryboardScene>();
        if (scenes.Count == 0) return new(0, notes, "분석할 컷이 없습니다. 먼저 스토리보드를 구성하세요.");

        var tension = scenes.Select(s => s.PresetTension ?? 0).ToArray();

        // 1) 텐션 처짐(가운데 컷이 양 이웃보다 크게 낮음) → 템포 늘어짐.
        for (var i = 1; i < scenes.Count - 1; i++)
            if (tension[i] < tension[i - 1] - 15 && tension[i] < tension[i + 1] - 15)
            {
                var target = (tension[i - 1] + tension[i + 1]) / 2;
                notes.Add(new(i + 1, DirectorSeverity.Warn, "템포", $"{i + 1}번 컷의 템포가 다소 늘어집니다(긴장도 {tension[i]}). 앞뒤 흐름에 맞춰 끌어올리길 권장합니다.",
                    $"긴장도 {target}로 올리기", new DirectorFix(DirectorFixKind.SetTension, i + 1, "", target)));
            }

        // 2) 클라이맥스 피크 부재.
        if (tension.Length > 0 && tension.Max() < 70)
        {
            var peakIdx = Array.IndexOf(tension, tension.Max());
            notes.Add(new(peakIdx + 1, DirectorSeverity.Suggest, "클라이맥스", "전체적으로 클라이맥스가 약합니다. 가장 강한 컷의 긴장도를 높여 몰입 피크를 만드세요.",
                "피크 컷 긴장도 88", new DirectorFix(DirectorFixKind.SetTension, peakIdx + 1, "", 88)));
        }

        // 3) 훅(1번 컷) 약함 — 훅 키워드/폴리 모두 없음.
        var c1 = scenes[0];
        if (!HasHook(c1))
            notes.Add(new(1, DirectorSeverity.Warn, "훅", "1번 컷의 첫 3초 훅이 약합니다. 매크로 임팩트 샷 + 강한 효과음으로 이탈률을 낮추세요.",
                "매크로 임팩트 샷 적용", new DirectorFix(DirectorFixKind.AppendShot, 1, "extreme macro impact shot, dramatic reveal, punchy dynamic framing", 0)));

        // 4) 카메라 워크 단조로움(구별되는 카메라 키워드 2종 미만).
        var camVariety = scenes.SelectMany(s => CameraWords.Where(w => (s.Prompt ?? "").ToLowerInvariant().Contains(w))).Distinct().Count();
        if (camVariety < 2 && scenes.Count >= 3)
        {
            var mid = scenes.Count / 2;
            notes.Add(new(mid + 1, DirectorSeverity.Suggest, "카메라", $"{mid + 1}번 컷을 매크로 렌즈 샷으로 변경하면 리듬과 바이럴 훅이 강화됩니다.",
                "매크로 렌즈 샷으로 변경", new DirectorFix(DirectorFixKind.AppendShot, mid + 1, "macro lens close-up, shallow depth of field", 0)));
        }

        // 5) 폴리 누락 컷.
        for (var i = 0; i < scenes.Count; i++)
            if ((scenes[i].PresetFoley?.Count ?? 0) == 0)
            {
                notes.Add(new(i + 1, DirectorSeverity.Suggest, "사운드", $"{i + 1}번 컷에 효과음(폴리)이 없습니다. 포인트 사운드로 임팩트를 더하세요.",
                    "효과음 추가(sparkle)", new DirectorFix(DirectorFixKind.AddSfx, i + 1, "sparkle", 0)));
                break;   // 대표 1건만(과잉 알림 방지)
            }

        // 6) 짧은 프롬프트.
        for (var i = 0; i < scenes.Count; i++)
            if ((scenes[i].Prompt ?? "").Trim().Length < 20)
            {
                notes.Add(new(i + 1, DirectorSeverity.Suggest, "프롬프트", $"{i + 1}번 컷 프롬프트가 짧습니다. 렌즈·조명·질감 묘사를 보강하세요.",
                    "시네마틱 디테일 보강", new DirectorFix(DirectorFixKind.AppendShot, i + 1, "cinematic lighting, hyper-detailed, 4K film grain", 0)));
                break;
            }

        var score = Math.Clamp(100 - notes.Sum(n => n.Severity == DirectorSeverity.Warn ? 12 : n.Severity == DirectorSeverity.Suggest ? 5 : 0), 0, 100);
        var summary = notes.Count == 0
            ? $"완성도 우수({score}점) — 연출 흐름이 매끄럽습니다."
            : $"연출 완성도 {score}점 · 개선 제안 {notes.Count}건(경고 {notes.Count(n => n.Severity == DirectorSeverity.Warn)}건).";
        return new(score, notes, summary);
    }

    /// <summary>원클릭 수정을 프로젝트에 적용한다(대상 컷 유효성 검사).</summary>
    public bool ApplyFix(StoryboardProject project, DirectorFix? fix)
    {
        if (fix is null || project is null) return false;
        var idx = fix.CutIndex - 1;
        if (idx < 0 || idx >= project.Scenes.Count) return false;
        var scene = project.Scenes[idx];
        switch (fix.Kind)
        {
            case DirectorFixKind.AppendShot:
                var prompt = (scene.Prompt ?? "").TrimEnd();
                if (!prompt.Contains(fix.Arg, StringComparison.OrdinalIgnoreCase))
                    scene.Prompt = prompt.Length == 0 ? fix.Arg : prompt + ", " + fix.Arg;
                return true;
            case DirectorFixKind.SetTension:
                scene.PresetTension = Math.Clamp(fix.IntArg, 0, 100);
                return true;
            case DirectorFixKind.AddSfx:
                scene.PresetFoley ??= new List<FoleySpec>();
                scene.PresetFoley.Add(new FoleySpec(fix.Arg, 1200, 0));
                return true;
            case DirectorFixKind.ChangeTransition:
                scene.EnterTransition = new VideoTransition(fix.Arg, 400);
                return true;
            default: return false;
        }
    }

    /// <summary>리포트의 모든 수정을 일괄 적용한다(적용 건수 반환).</summary>
    public int ApplyAll(StoryboardProject project, DirectorReport report)
        => report?.Notes.Count(n => n.Fix is not null && ApplyFix(project, n.Fix)) ?? 0;

    private static bool HasHook(StoryboardScene s)
    {
        var low = (s.Prompt ?? "").ToLowerInvariant();
        var hasKeyword = HookWords.Any(w => low.Contains(w.ToLowerInvariant()));
        var hasFoley = (s.PresetFoley?.Count ?? 0) > 0;
        return hasKeyword || hasFoley;
    }
}
