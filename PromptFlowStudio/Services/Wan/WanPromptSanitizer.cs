using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// 프롬프트 위생화 결과. <see cref="Prompt"/> 는 보정된 최종 프롬프트, <see cref="FixedDeficits"/> 는
///   어떤 무료 모델 결함을 어떻게 막았는지(툴팁/로그용 한국어 목록), <see cref="Changed"/> 는 실제 변경 여부.
/// </summary>
public sealed record PromptSanitizeResult(string Prompt, IReadOnlyList<string> FixedDeficits, bool Changed);

/// <summary>자동 보정 항목 카탈로그 1건 — UI 툴팁("무슨 결함을 어떻게 고쳤는가")과 테스트가 공유.</summary>
public sealed record AutoFixInfo(string Key, string Icon, string Deficit, string Fix, string Detail);

/// <summary>
/// [PromptFlowStudio] Wan 2.1 무료 요금제 결함 자동 보정 — 프롬프트 위생화 & 네거티브 자동 주입기(순수 BCL · 외부 NuGet 0 · 정적 순수 함수).
///   ▸ 무료/기본 모델에서 잦은 <b>신체 왜곡·텍스트 깨짐·배경 뭉개짐·프레임 깜빡임</b>을 억제하도록 프롬프트를 정제하고,
///     안면/모션 안정화 <b>양성 토큰</b> + 결함 억제 <b>네거티브 키워드</b>(deformed/blurry/overlapping limbs/flickering/artifacts/low resolution 등)를 자동 결합.
///   ▸ 세로(9:16)면 모바일 최적 구도 토큰을 추가 주입.
///   ▸ <b>멱등</b>(<see cref="Marker"/> 있으면 재주입 안 함) · enabled=false 면 원본 유지 · 사용자 원문(한/영)은 보존.
///   ▸ API/스키마 무변경: 네거티브는 별도 필드가 아니라 프롬프트에 자연어("avoiding: …")로 임베드 → 100% 역호환·Graceful.
///     (모델이 네거티브를 무시해도 양성 안정화 토큰이 품질을 끌어올리는 belt-and-suspenders 설계.)
/// </summary>
public static class WanPromptSanitizer
{
    /// <summary>멱등 판별 마커(주입 완료 표식). 프롬프트 말미에 대괄호로 남긴다.</summary>
    public const string Marker = "free-tier deficit guard";

    /// <summary>무료 모델 결함 억제용 네거티브 키워드(안면/모션/화질 안정화).</summary>
    public static readonly string[] NegativeKeywords =
    {
        "deformed", "blurry", "overlapping limbs", "flickering", "artifacts", "low resolution",
        "distorted face", "extra fingers", "mutated hands", "text glitch", "warping background", "jitter",
    };

    /// <summary>안면/모션/프레임 안정화 양성 토큰(무료 모델 품질 상향).</summary>
    public const string StabilizerTokens =
        "clean stable footage, consistent anatomy, sharp focus, coherent smooth motion, temporally stable frames, natural proportions";

    /// <summary>세로(9:16) 모바일 최적 구도 토큰.</summary>
    public const string PortraitTokens =
        "vertical 9:16 composition, centered subject, mobile-first framing, full body kept in frame";

    /// <summary>화면 내 텍스트/워터마크 환각을 유발하는(무료 모델에서 자주 깨지는) 위험 토큰 — 감지 시 억제 네거티브를 강화.</summary>
    private static readonly string[] TextHallucinationTriggers =
    {
        "text", "letters", "words", "caption", "subtitle", "typography", "watermark", "logo", "sign", "writing",
    };

    /// <summary>
    /// 자동 보정 항목 카탈로그 — 프롬프트/디플리커/샤픈/모션보간/속도제한 5종.
    ///   AutoPilot 스위치 UI 와 SmartTooltip 이 "무슨 결함을 어떻게 고쳤는가"를 렌더할 때 사용.
    /// </summary>
    public static readonly IReadOnlyList<AutoFixInfo> Catalog = new[]
    {
        new AutoFixInfo("prompt", "🧬", "신체 왜곡 · 텍스트 깨짐 · 배경 뭉개짐",
            "프롬프트 위생화 + 안정화 네거티브 자동 주입",
            "deformed·overlapping limbs·artifacts 등 결함 키워드를 억제하고 안면/모션 안정화 토큰과 9:16 구도를 결합합니다."),
        new AutoFixInfo("deflicker", "🩹", "프레임 깜빡임(Flicker) · 밝기 튐",
            "FFmpeg deflicker + 시간축 디노이즈",
            "인접 프레임의 휘도 변동을 평균화(deflicker=mode=pm)하고 hqdn3d 로 잔여 노이즈를 정리해 깜빡임을 제거합니다."),
        new AutoFixInfo("sharpen", "🔎", "선명도 저하 · 디테일 손실",
            "Unsharp Mask + Lanczos 리샘플",
            "언샤프 마스크로 윤곽 선명도를 복원하고 Lanczos 고품질 리샘플로 화질 손실을 최소화합니다."),
        new AutoFixInfo("temporal", "🌊", "시간축 노이즈 · 프레임 지글거림",
            "시간적 슈퍼 레졸루션(tmix 프레임 평균)",
            "인접 3프레임을 중앙 가중(1:2:1) 평균해 시간축 노이즈를 저감하고, 잔상 없이 유료(Pro) 모델급 매끄러움을 얻습니다."),
        new AutoFixInfo("deband", "🎨", "컬러 밴딩 · 계단식 그라데이션",
            "디밴딩 색상 안정화(deband)",
            "하늘·배경 그라데이션의 계단식 색 경계(밴딩)를 감지해 부드럽게 디더링, 프로덕션급 계조를 복원합니다."),
        new AutoFixInfo("interpolate", "🎞️", "모션 뭉개짐 · 끊김(저 FPS)",
            "GPU 가속(NVENC/CUDA) 60fps 모션 보간",
            "움직임 벡터 기반(mci·aobmc)으로 중간 프레임을 생성해 60fps 로 부드럽게 만듭니다. NVIDIA GPU 자동 탐지 → CUDA 가속, 미지원 시 CPU 폴백. 연산이 무거워 기본은 수동입니다."),
        new AutoFixInfo("rate", "⚠️", "API 속도 제한(429) · 서버 지연",
            "지수 백오프 자동 재시도 + 쿨다운 가드",
            "429/쓰로틀을 감지하면 4→8→16초… 지수적으로 대기하며 자동 재시도하고, 남은 시간을 상태 카드로 표시합니다."),
    };

    /// <summary>카탈로그에서 키로 항목 조회(없으면 null).</summary>
    public static AutoFixInfo? Get(string key) =>
        Catalog.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>이미 위생화(멱등 마커 포함)된 프롬프트인지.</summary>
    public static bool IsSanitized(string? prompt) =>
        !string.IsNullOrEmpty(prompt) && prompt.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 프롬프트를 위생화하고 안정화 토큰/네거티브/구도를 자동 주입한다(멱등).
    /// </summary>
    /// <param name="prompt">원본 프롬프트(한/영 무관 · 원문 보존).</param>
    /// <param name="portrait">세로(9:16) 컷이면 true — 모바일 구도 토큰 추가.</param>
    /// <param name="enabled">Auto-Pilot(프롬프트 보정) 활성 여부. false 면 원본 그대로.</param>
    public static PromptSanitizeResult Sanitize(string? prompt, bool portrait, bool enabled)
    {
        var raw = (prompt ?? "").Trim();
        if (!enabled) return new(raw, Array.Empty<string>(), false);
        if (IsSanitized(raw)) return new(raw, Array.Empty<string>(), false);   // 멱등 — 재주입 방지

        var fixes = new List<string>();

        // ① 위생화 — 중복 쉼표/공백 정리 + 프롬프트 확장 폭주를 막는 길이 캡(서버 800자보다 여유).
        var body = Hygiene(raw, out var hygieneApplied);
        if (hygieneApplied) fixes.Add("프롬프트 표기 정리(중복 구분자·공백)");

        var sb = new StringBuilder(body);

        // ② 안면/모션/프레임 안정화 양성 토큰.
        AppendClause(sb, StabilizerTokens);
        fixes.Add("안면·모션·프레임 안정화 토큰 주입");

        // ③ 세로 구도(9:16) 최적화.
        if (portrait)
        {
            AppendClause(sb, PortraitTokens);
            fixes.Add("9:16 세로 모바일 구도 최적화");
        }

        // ④ 결함 억제 네거티브 자동 주입(+텍스트 환각 감지 시 화면 텍스트 억제 보강).
        var negatives = new List<string>(NegativeKeywords);
        if (MentionsScreenText(raw))
        {
            negatives.Add("unwanted on-screen text");
            negatives.Add("gibberish letters");
            fixes.Add("화면 텍스트 환각 억제 보강");
        }
        AppendClause(sb, "avoiding: " + string.Join(", ", negatives));
        fixes.Add("무료 모델 결함 억제 네거티브 주입");

        // ⑤ 멱등 마커.
        AppendClause(sb, "[" + Marker + "]");

        return new(sb.ToString(), fixes, true);
    }

    /// <summary>중복 쉼표/공백을 정리하고 과길이를 자른다(양성 토큰 추가 여유를 두고 640자로 캡). 실제 정리가 있었으면 applied=true.</summary>
    private static string Hygiene(string raw, out bool applied)
    {
        var before = raw;
        var s = raw;

        // 연속 공백 → 1개.
        var sb = new StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            var isSpace = ch is ' ' or '\t' or '\r' or '\n';
            if (isSpace)
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else { sb.Append(ch); prevSpace = false; }
        }
        s = sb.ToString().Trim();

        // 중복 쉼표(", , ,") → 1개.
        while (s.Contains(", ,")) s = s.Replace(", ,", ",");
        while (s.Contains(",,")) s = s.Replace(",,", ",");
        s = s.Trim().TrimEnd(',', ' ', '\t');

        // 과길이 캡(양성 토큰/네거티브 추가분 여유 확보).
        if (s.Length > 640) s = s[..640].TrimEnd(',', ' ');

        applied = !string.Equals(s, before.Trim(), StringComparison.Ordinal);
        return s;
    }

    /// <summary>프롬프트가 화면 텍스트/워터마크를 언급하는지(환각 위험) — 소문자 부분일치.</summary>
    private static bool MentionsScreenText(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        return TextHallucinationTriggers.Any(t => lower.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>절을 쉼표로 이어 붙인다(빈 본문이면 절만).</summary>
    private static void AppendClause(StringBuilder sb, string clause)
    {
        if (sb.Length == 0) { sb.Append(clause); return; }
        // 말미 쉼표/공백 정리 후 결합.
        while (sb.Length > 0 && (sb[^1] == ',' || sb[^1] == ' ')) sb.Length--;
        sb.Append(", ").Append(clause);
    }
}
