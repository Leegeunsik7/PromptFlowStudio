using System;
using System.Threading;

namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [PromptFlowStudio] v15.2.1 👶 아기 스튜디오 울티메이트 매트릭스 — 테마(교체형) · 스마트 모디파이어(누적형) 카탈로그 +
///   Wan 2.1 아기 얼굴 보호 네거티브 가드. Workspace 우측 레일이 사용하며 모바일에도 링크된다(순수 BCL).
/// </summary>
public static class BabyStudioMatrix
{
    /// <summary>Wan 2.1 전용 Baby Facial Safety Negative Prompt — 아기 테마 클릭 시 네거티브에 원자적으로 결합된다.</summary>
    public const string FacialSafetyNegative =
        "distorted face, extra fingers, malformed limbs, blurry eyes, unnatural skin texture, dark heavy shadows";

    /// <summary>🍼 감성 성장 — 클릭 시 입력창 통째 교체.</summary>
    public static readonly PromptChip[] Growth =
    {
        new("천사 수면",     PromptPresets.Baby[0].Snippet, "💤"),
        new("비눗방울 미소", PromptPresets.Baby[1].Snippet, "🫧"),
        new("첫걸음",       PromptPresets.Baby[2].Snippet, "🧸"),
        new("이유식 장난",   PromptPresets.Baby[3].Snippet, "🥣"),
        new("배냇짓",       "포근한 속싸개에 감싸인 채 잠결에 배냇짓 미소를 짓는 신생아 아기, 하얀 리넨 침구, 창가의 부드러운 확산광, 85mm 인물 렌즈 클로즈업, 따뜻한 크림 톤", "👶"),
    };

    /// <summary>🧚 몽환 동화 — 클릭 시 입력창 통째 교체.</summary>
    public static readonly PromptChip[] Fantasy =
    {
        new("구름 침대",   "솜사탕 같은 뭉게구름 침대 위에서 곰인형을 안고 꿈꾸듯 잠든 아기, 파스텔 하늘빛 배경, 은은한 햇살 입자, 동화책 일러스트 감성", "☁️"),
        new("요정 숲",     PromptPresets.Baby[4].Snippet, "🌲"),
        new("별빛 밤하늘", "반짝이는 별빛과 초승달 아래 작은 담요를 덮고 하늘을 올려다보는 아기, 몽환적인 은하수 배경, 부드러운 푸른 달빛, 판타지 동화 분위기", "🌟"),
        new("풍선 여행",   "알록달록한 풍선 다발에 매달린 작은 바구니를 타고 하늘을 나는 해맑은 아기, 동화 속 마을 위 구름 사이, 따뜻한 오후 햇살, 경쾌한 모험 감성", "🎈"),
    };

    /// <summary>🎥 스마트 모디파이어 — 누를 때마다 기존 텍스트 뒤에 ", " 로 결합(이미 있으면 무시).</summary>
    public static readonly PromptChip[] Modifiers =
    {
        new("엔젤릭 조명",       "천상의 엔젤릭 소프트 림 라이트 조명",  "✨"),
        new("35mm 시네마틱",     "35mm 필름 시네마틱 룩",                "🎬"),
        new("8k 초고화질 렌즈",  "8k 초고화질 매크로 렌즈 디테일",       "🔍"),
        new("부드러운 슬로우",   "부드러운 슬로우 모션",                 "🕊️"),
    };

    /// <summary>
    /// 스마트 모디파이어 결합 — 빈 입력은 리터럴 참조, 이미 포함된 모디파이어는 현재 참조를 그대로 반환(0B).
    ///   결합이 필요할 때만 꼬리 공백/쉼표를 span 으로 잘라 <c>string.Create</c> 1회(결과 문자열 자체) — 중간 문자열 0.
    /// </summary>
    public static string AppendModifier(string? current, string modifier) => JoinOnce(current, modifier);

    /// <summary>인덱스 버전 — 범위 밖이면 현재 입력 유지.</summary>
    public static string AppendModifier(string? current, int index)
        => (uint)index < (uint)Modifiers.Length ? JoinOnce(current, Modifiers[index].Snippet) : current ?? "";

    /// <summary>
    /// 사용자 네거티브와 얼굴 보호 가드 병합 — 비었으면 가드 리터럴 참조, 이미 포함됐으면 사용자 참조 유지(중복 누적 0),
    ///   그 외엔 사용자 문구를 앞에 보존하고 가드를 뒤에 결합.
    /// </summary>
    public static string MergeNegative(string? userNegative) => JoinOnce(userNegative, FacialSafetyNegative);

    private static string JoinOnce(string? current, string tail)
    {
        if (string.IsNullOrEmpty(tail)) return current ?? "";
        if (current is null) return tail;
        var head = current.AsSpan().TrimEnd();
        while (head.Length > 0 && (head[^1] == ',' || char.IsWhiteSpace(head[^1]))) head = head[..^1];
        if (head.Length == 0) return tail;
        if (head.IndexOf(tail.AsSpan(), StringComparison.Ordinal) >= 0) return current;

        return string.Create(head.Length + 2 + tail.Length, (current, head.Length, tail), static (dst, st) =>
        {
            st.current.AsSpan(0, st.Length).CopyTo(dst);
            dst[st.Length] = ',';
            dst[st.Length + 1] = ' ';
            st.tail.AsSpan().CopyTo(dst[(st.Length + 2)..]);
        });
    }
}

/// <summary>
/// 아기 스튜디오 세션 설정 — Workspace 인스턴스당 1개. 네거티브 프롬프트는 CAS 로 원자 교체되어
///   사용자 편집(바인딩 setter)과 테마 주입이 겹쳐도 한쪽 갱신이 유실되지 않는다.
/// </summary>
public sealed class BabyStudioSession
{
    private string _negative = "";
    private int _guardHits;

    /// <summary>요청에 실릴 네거티브 프롬프트(사용자 입력 + 얼굴 보호 가드).</summary>
    public string NegativePrompt
    {
        get => Volatile.Read(ref _negative);
        set => Volatile.Write(ref _negative, value ?? "");
    }

    /// <summary>현재 네거티브에 얼굴 보호 가드가 포함되어 있는지.</summary>
    public bool GuardActive => NegativePrompt.Contains(BabyStudioMatrix.FacialSafetyNegative, StringComparison.Ordinal);

    /// <summary>가드가 실제로 새로 결합된 횟수(이미 포함된 상태의 재클릭은 세지 않음).</summary>
    public int GuardHits => Volatile.Read(ref _guardHits);

    /// <summary>
    /// 아기 테마 원클릭 — 프롬프트는 테마 리터럴 참조로 교체하고(범위 밖이면 현재 유지),
    ///   네거티브는 사용자 문구를 보존한 채 얼굴 보호 가드를 CAS 로 원자 결합한다.
    /// </summary>
    public string ApplyTheme(PromptChip[] themes, int index, string current)
    {
        if ((uint)index >= (uint)themes.Length) return current;
        while (true)
        {
            var seen = Volatile.Read(ref _negative);
            var merged = BabyStudioMatrix.MergeNegative(seen);
            if (ReferenceEquals(merged, seen)) break;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _negative, merged, seen), seen))
            {
                Interlocked.Increment(ref _guardHits);
                break;
            }
        }
        return themes[index].Snippet;
    }
}
