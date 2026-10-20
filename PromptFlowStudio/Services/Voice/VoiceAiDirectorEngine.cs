using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PromptFlowStudio.Services.Media;       // SafePlatform
using PromptFlowStudio.Services.Storyboard;  // StoryboardProject

namespace PromptFlowStudio.Services.Voice;

/// <summary>음성 명령 의도.</summary>
public enum VoiceIntent
{
    None, ZoomIn, ZoomOut, CameraOrbit, PromptBoost, BgmDuckDown, BgmUp,
    StartGeneration, StopGeneration, Merge, SafeZoneOn, SafeZoneOff, SpeedUp, SpeedDown, EmbossOn, NextScene, Unknown
}

/// <summary>파싱된 음성 명령 — 의도 + 대상 컷 + 값/인자 + 원문 + 신뢰도.</summary>
public sealed record VoiceCommand(VoiceIntent Intent, int? TargetCut, double? Value, string? Arg, string Raw, double Confidence);

/// <summary>적용 후 호출부가 수행할 부수효과 종류.</summary>
public enum VoiceActionKind { None, Mutated, StartGeneration, StopGeneration, Merge }

/// <summary>음성 명령 적용 결과 — 성공·부수효과·라벨(이력용)·메시지.</summary>
public sealed record VoiceActionResult(bool Ok, VoiceActionKind Action, string Label, string Message);

/// <summary>
/// [PromptFlowStudio] 실시간 음성 명령 기반 AI 감독 믹서(v2.9.0) — 순수 C# BCL 오프라인 키워드 스포팅/NLU(외부 Speech/ML 라이브러리 0).
///   ▸ 음성→텍스트는 브라우저 내장 Web Speech API(JS Interop)로 얻고, 이 엔진이 <b>구어체 한국어 명령</b>을 오프라인 파싱한다.
///   ▸ "3번 컷 카메라 오르빗", "BGM 볼륨 줄여", "즉시 생성 시작", "유튜브 세이프존 켜기" 등 → <see cref="VoiceCommand"/> 로 정밀 파싱(<see cref="ParseMultiple"/> 복합문 분해).
///   ▸ <see cref="Apply"/> 가 스토리보드/주조종실 상태를 실제 변경하고, 생성/중지/병합 같은 부수효과는 <see cref="VoiceActionKind"/> 로 호출부에 위임.
/// </summary>
public sealed class VoiceAiDirectorEngine
{
    // 의도별 키워드(우선순위 순 · 먼저 매칭되는 의도 채택). 구체적 문구를 앞에 둔다.
    private static readonly (VoiceIntent Intent, string[] Any)[] Rules =
    {
        (VoiceIntent.StopGeneration, new[] { "정지", "멈춰", "멈춤", "중지", "스톱" }),
        (VoiceIntent.StartGeneration, new[] { "즉시 생성", "생성 시작", "제작 시작", "생성해", "만들어", "렌더 시작", "렌더링 시작", "고고" }),
        (VoiceIntent.Merge, new[] { "병합", "합쳐", "합치", "머지" }),
        (VoiceIntent.SafeZoneOn, new[] { "세이프존 켜", "세이프존 온", "안전선 켜", "세이프존 활성" }),
        (VoiceIntent.SafeZoneOff, new[] { "세이프존 꺼", "세이프존 오프", "안전선 꺼", "세이프존 해제" }),
        (VoiceIntent.CameraOrbit, new[] { "오르빗", "궤도", "카메라 회전", "회전 전환", "360" }),
        (VoiceIntent.ZoomIn, new[] { "줌인", "줌 인", "확대", "당겨", "당기", "가까이", "클로즈업" }),
        (VoiceIntent.ZoomOut, new[] { "줌아웃", "줌 아웃", "축소", "빼", "멀리", "와이드" }),
        (VoiceIntent.PromptBoost, new[] { "프롬프트 부스팅", "부스팅", "부스트", "보강", "화질 올려", "퀄리티 올려", "디테일 살려" }),
        (VoiceIntent.BgmDuckDown, new[] { "볼륨 줄", "음악 줄", "음악 낮", "비지엠 줄", "bgm 줄", "덕킹", "소리 줄" }),
        (VoiceIntent.BgmUp, new[] { "볼륨 올려", "음악 키", "음악 올려", "소리 키", "크게" }),
        (VoiceIntent.SpeedUp, new[] { "빠르게", "배속 올려", "속도 올려", "스피드업" }),
        (VoiceIntent.SpeedDown, new[] { "느리게", "배속 내려", "속도 내려", "슬로우" }),
        (VoiceIntent.EmbossOn, new[] { "워터마크", "엠보스", "각인" }),
        (VoiceIntent.NextScene, new[] { "다음 장면", "다음 컷", "넘겨", "다음으로" }),
    };

    private static readonly (string[] Words, SafePlatform Platform)[] PlatformWords =
    {
        (new[] { "유튜브", "youtube", "쇼츠", "shorts" }, SafePlatform.YouTubeShorts),
        (new[] { "틱톡", "tiktok" }, SafePlatform.TikTok),
        (new[] { "인스타", "릴스", "instagram", "reels" }, SafePlatform.InstagramReels),
        (new[] { "네이버", "클립" }, SafePlatform.NaverClip),
        (new[] { "당근" }, SafePlatform.KarrotStory),
        (new[] { "샤오홍슈", "샤홍" }, SafePlatform.Xiaohongshu),
    };

    // ══════════════════════ 파싱 ══════════════════════

    /// <summary>복합 문장을 접속어로 분해해 여러 명령으로 파싱한다("A 하고 B" → [A, B]).</summary>
    public IReadOnlyList<VoiceCommand> ParseMultiple(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return Array.Empty<VoiceCommand>();
        var norm = raw;
        foreach (var c in new[] { "그리고", "그다음", "그 다음", "이고 ", "하고 ", "키고 ", "켜고 ", "끄고 ", "," })
            norm = norm.Replace(c, "|");
        var parts = norm.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = parts.Select(Parse).Where(c => c.Intent != VoiceIntent.None).ToList();
        return list.Count > 0 ? list : new List<VoiceCommand> { Parse(raw) };
    }

    /// <summary>단일 구어체 명령을 파싱한다(의도·대상컷·플랫폼·신뢰도).</summary>
    public VoiceCommand Parse(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return new(VoiceIntent.None, null, null, null, "", 0);
        var low = raw.ToLowerInvariant();

        var intent = VoiceIntent.Unknown;
        foreach (var (it, any) in Rules)
            if (any.Any(k => low.Contains(k.ToLowerInvariant()))) { intent = it; break; }

        var cut = ExtractCutNumber(low);
        var plat = ExtractPlatform(low);
        var conf = intent == VoiceIntent.Unknown ? 0.2 : (cut is not null || plat is not null ? 1.0 : 0.85);
        return new(intent, cut, null, plat?.ToString(), raw, conf);
    }

    // 대상 컷 번호(1-based). "3번"/"3번째"/"세번째"/"첫번째" 등.
    private static int? ExtractCutNumber(string low)
    {
        var m = Regex.Match(low, @"(\d+)\s*(번|컷|번째|째)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;
        foreach (var (word, val) in KoreanNums)
            if (low.Contains(word + "번") || low.Contains(word + "번째") || low.Contains(word + "째") || low.Contains(word + " 컷") || low.Contains(word + "컷")) return val;
        return null;
    }

    private static readonly (string Word, int Val)[] KoreanNums =
    {
        ("첫", 1), ("하나", 1), ("한", 1), ("일", 1), ("둘", 2), ("두", 2), ("이", 2), ("셋", 3), ("세", 3), ("삼", 3),
        ("넷", 4), ("네", 4), ("사", 4), ("다섯", 5), ("오", 5), ("여섯", 6), ("육", 6), ("일곱", 7), ("칠", 7),
        ("여덟", 8), ("팔", 8), ("아홉", 9), ("구", 9), ("열", 10), ("십", 10),
    };

    private static SafePlatform? ExtractPlatform(string low)
    {
        foreach (var (words, p) in PlatformWords)
            if (words.Any(w => low.Contains(w))) return p;
        return null;
    }

    // ══════════════════════ 적용 ══════════════════════

    /// <summary>명령을 프로젝트에 적용한다(상태 변경 + 부수효과 종류 반환). 대상 컷 미지정 시 합리적 기본(전체/첫 컷).</summary>
    public VoiceActionResult Apply(VoiceCommand cmd, StoryboardProject project)
    {
        if (project is null) return new(false, VoiceActionKind.None, "무효", "프로젝트 없음");
        var scenes = project.Scenes;
        StoryboardScene? Target() => (cmd.TargetCut is int n && n >= 1 && n <= scenes.Count) ? scenes[n - 1] : scenes.FirstOrDefault();

        switch (cmd.Intent)
        {
            case VoiceIntent.ZoomIn: return AppendShot(Target(), "slow cinematic zoom-in, push-in", cmd, "카메라 줌인");
            case VoiceIntent.ZoomOut: return AppendShot(Target(), "cinematic zoom-out, pull-back reveal", cmd, "카메라 줌아웃");
            case VoiceIntent.CameraOrbit: return AppendShot(Target(), "sweeping 360-degree orbit camera move", cmd, "카메라 오르빗");
            case VoiceIntent.PromptBoost: return AppendShot(Target(), "ultra-detailed, cinematic lighting, 4K masterpiece", cmd, "프롬프트 부스팅");

            case VoiceIntent.BgmDuckDown:
                project.EnableAudioDucking = true;
                project.BgmVolume = Math.Clamp(project.BgmVolume - 0.15, 0.05, 1.0);
                return new(true, VoiceActionKind.Mutated, "BGM 덕킹", $"BGM 볼륨 {project.BgmVolume:0.00} · 덕킹 ON");
            case VoiceIntent.BgmUp:
                project.BgmVolume = Math.Clamp(project.BgmVolume + 0.15, 0.05, 1.0);
                return new(true, VoiceActionKind.Mutated, "BGM 업", $"BGM 볼륨 {project.BgmVolume:0.00}");

            case VoiceIntent.SpeedUp:
                project.PlaybackSpeed = Math.Clamp(project.PlaybackSpeed + 0.15, 0.5, 2.0); project.UseTempoSpeedup = project.PlaybackSpeed > 1.001;
                return new(true, VoiceActionKind.Mutated, "배속 업", $"배속 {project.PlaybackSpeed:0.00}x");
            case VoiceIntent.SpeedDown:
                project.PlaybackSpeed = Math.Clamp(project.PlaybackSpeed - 0.15, 0.5, 2.0); project.UseTempoSpeedup = project.PlaybackSpeed > 1.001;
                return new(true, VoiceActionKind.Mutated, "배속 다운", $"배속 {project.PlaybackSpeed:0.00}x");

            case VoiceIntent.SafeZoneOn:
                var p = Enum.TryParse<SafePlatform>(cmd.Arg, out var pp) && pp != SafePlatform.None ? pp : SafePlatform.YouTubeShorts;
                project.TargetPlatform = p;
                return new(true, VoiceActionKind.Mutated, "세이프존 ON", $"{p} 세이프존 활성");
            case VoiceIntent.SafeZoneOff:
                project.TargetPlatform = SafePlatform.None;
                return new(true, VoiceActionKind.Mutated, "세이프존 OFF", "세이프존 해제");

            case VoiceIntent.EmbossOn:
                project.EmbossWatermark = true;
                return new(true, VoiceActionKind.Mutated, "워터마크 ON", "3D 엠보싱 워터마크 활성");

            case VoiceIntent.StartGeneration: return new(true, VoiceActionKind.StartGeneration, "생성 시작", "AI 영상 생성 시작");
            case VoiceIntent.StopGeneration: return new(true, VoiceActionKind.StopGeneration, "생성 중지", "생성 중지");
            case VoiceIntent.Merge: return new(true, VoiceActionKind.Merge, "병합", "최종 병합 시작");
            case VoiceIntent.NextScene: return new(true, VoiceActionKind.Mutated, "다음 장면", "다음 컷으로 이동");

            default: return new(false, VoiceActionKind.None, "미인식", $"명령을 이해하지 못했습니다: \"{cmd.Raw}\"");
        }
    }

    private static VoiceActionResult AppendShot(StoryboardScene? scene, string phrase, VoiceCommand cmd, string label)
    {
        if (scene is null) return new(false, VoiceActionKind.None, label, "대상 컷이 없습니다.");
        var prompt = (scene.Prompt ?? "").TrimEnd();
        if (!prompt.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            scene.Prompt = prompt.Length == 0 ? phrase : prompt + ", " + phrase;
        var where = cmd.TargetCut is int n ? $"{n}번 컷" : "현재 컷";
        return new(true, VoiceActionKind.Mutated, label, $"{where}: {label} 적용");
    }
}
