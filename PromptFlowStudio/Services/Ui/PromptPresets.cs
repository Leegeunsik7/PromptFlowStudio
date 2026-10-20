using System;

namespace PromptFlowStudio.Services.Ui;

/// <summary>프롬프트 추천 칩 1개 — 표시 라벨 + 실제 삽입 스니펫(영문 프롬프트) + 아이콘.</summary>
public sealed record PromptChip(string Label, string Snippet, string Icon);

/// <summary>
/// [PromptFlowStudio] 카메라·스타일·모션 프롬프트 프리셋 카탈로그(정적).
///   Workspace 퀵칩과 MotionPresets(/motion) 페이지가 공유한다.
/// </summary>
public static class PromptPresets
{
    /// <summary>카메라 워크(팬/줌/드론 등).</summary>
    public static readonly PromptChip[] Camera =
    {
        new("팬 오른쪽",   "cinematic pan right",                     "➡️"),
        new("팬 왼쪽",     "cinematic pan left",                      "⬅️"),
        new("돌리 줌",     "dolly zoom, vertigo effect",              "🔍"),
        new("크레인 업",   "crane shot moving upward",                "⬆️"),
        new("트래킹 샷",   "tracking shot following the subject",     "🎯"),
        new("드론 부감",   "aerial drone shot, top-down view",        "🚁"),
        new("핸드헬드",    "handheld camera with subtle shake",       "🤳"),
        new("오빗",        "camera orbiting around the subject",      "🔄"),
    };

    /// <summary>비주얼 스타일/무드.</summary>
    public static readonly PromptChip[] Style =
    {
        new("시네마틱",    "cinematic, film grain, shallow depth of field", "🎞️"),
        new("사이버펑크",  "cyberpunk neon lighting, rain, reflections",    "🌆"),
        new("골든아워",    "golden hour warm sunlight",                     "🌅"),
        new("느와르",      "black and white film noir, high contrast",      "🕶️"),
        new("몽환적",      "dreamy soft focus, bloom, ethereal",            "✨"),
        new("애니메이션",  "anime style, vibrant colors",                   "🎨"),
    };

    /// <summary>모션/시간 표현.</summary>
    public static readonly PromptChip[] Motion =
    {
        new("슬로우 모션", "slow motion",              "🐢"),
        new("타임랩스",    "time-lapse",               "⏩"),
        new("부드러운 움직임", "smooth gentle motion", "🌊"),
    };

    /// <summary>v15.2.0: 👶 아기 스튜디오 마스터피스(Wan 2.1) — 클릭 시 입력창을 통째로 교체하는 완성형 프롬프트.</summary>
    public static readonly PromptChip[] Baby =
    {
        new("천사 같은 수면",   "부드러운 아침 햇살이 스며드는 아기 침대, 새근새근 평화롭게 잠든 천사 같은 신생아, 8k 초고화질 클로즈업, 시네마틱 감성 조명, 부드러운 슬로우 카메라 팬", "💤"),
        new("비눗방울 미소",    "맑은 눈망울로 까르르 웃으며 날아다니는 비눗방울을 잡으려는 귀여운 아기, 따뜻한 파스텔 톤 배경, 50mm 단렌즈 아웃포커싱, 밝고 행복한 분위기", "🫧"),
        new("인형과 첫걸음",    "자신의 몸집만한 곰인형을 껴안고 거실을 아장아장 걷는 1살 아기, 감성적인 역광, 8k UHD 해상도, 따뜻하고 포근한 가족 영화 느낌", "🧸"),
        new("이유식 장난꾸러기", "얼굴에 이유식을 잔뜩 묻히고 해맑게 장난치는 통통한 볼의 아기, 부엌 창문으로 들어오는 자연광, 캐논 시네마 렌즈, 귀여운 슬로우 모션", "🥣"),
        new("요정 숲 스튜디오", "숲속 요정 의상을 입고 꽃밭에 앉아 호기심 가득한 표정으로 나비를 바라보는 아기, 몽환적인 동화 속 숲속 배경, 부드러운 입자 효과, 스튜디오 컨셉 촬영", "🧚"),
    };

    /// <summary>
    /// 마스터피스 프리셋 원클릭 주입 — 기존 입력을 버리고 정적 리터럴 참조를 그대로 반환(복사·연결 없음 → 0B).
    ///   범위 밖 인덱스는 현재 입력을 유지한다.
    /// </summary>
    public static string InjectMasterpiece(PromptChip[] presets, int index, string current)
        => (uint)index < (uint)presets.Length ? presets[index].Snippet : current;
}

/// <summary>
/// [PromptFlowStudio] 페이지 간 프롬프트 전달(싱글턴) — MotionPresets → Workspace 로 스니펫을 넘긴다.
///   MotionPresets 가 <see cref="Send"/> 후 /workspace 로 이동하면, Workspace 가 진입 시 <see cref="Consume"/> 로 소비한다.
/// </summary>
public sealed class PromptDraftService
{
    private string? _pending;

    /// <summary>워크스페이스로 보낼 스니펫 적재(기존 것에 이어붙임).</summary>
    public void Send(string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet)) return;
        _pending = string.IsNullOrWhiteSpace(_pending) ? snippet : _pending + ", " + snippet;
    }

    /// <summary>적재된 스니펫을 한 번 꺼내 비운다(없으면 null).</summary>
    public string? Consume()
    {
        var v = _pending;
        _pending = null;
        return v;
    }
}
