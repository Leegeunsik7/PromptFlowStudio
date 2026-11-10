using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>💌 15초 3막 감성 편지 자막(Act1 01~04초 · Act2 05~10초 · Act3 11~14초).</summary>
public sealed class PlutoSubtitleScript
{
    /// <summary>💌 3막 표시 구간(초) — 15초 쇼츠 기준.</summary>
    public static readonly (int Start, int End)[] ActWindows = { (1, 4), (5, 10), (11, 14) };

    public string SubjectName { get; init; } = "";
    public string Act1Text { get; init; } = "";
    public string Act2Text { get; init; } = "";
    public string Act3Text { get; init; } = "";
    public bool UsedLlm { get; init; }

    /// <summary>시간 동기 큐(<see cref="StockSuiteExporter.BurnSubtitles"/> 번인 입력).</summary>
    public SubtitleCue[] ToCues() => new[]
    {
        new SubtitleCue(ActWindows[0].Start, ActWindows[0].End, Act1Text),
        new SubtitleCue(ActWindows[1].Start, ActWindows[1].End, Act2Text),
        new SubtitleCue(ActWindows[2].Start, ActWindows[2].End, Act3Text),
    };

    /// <summary>표준 <c>.srt</c> 문자열(3블록 · <c>00:00:01,000 --> 00:00:04,000</c>).</summary>
    public string ToSrtFormat() => StockSuiteExporter.BuildSrt(ToCues());

    /// <summary>📋 복사용 편지 본문 — <c>To. 이름</c> · 3줄 · 서명.</summary>
    public string ToFullLetterText()
    {
        var sb = new StringBuilder(160);
        sb.Append("💌 To. ").Append(SubjectName).Append('\n').Append('\n')
          .Append(Act1Text).Append('\n')
          .Append(Act2Text).Append('\n')
          .Append(Act3Text).Append('\n').Append('\n')
          .Append("From. 언제나 곁에 있는 가족이");
        return sb.ToString();
    }
}

/// <summary>
/// 💌 Emotional Letter Subtitle Director — 부모/크리에이터 → 주인공에게 보내는 3막 시적 편지 자막.
/// 🧠 Gemini(<see cref="PlutoLlmBridge"/> 3.5초 예산) 우선 · 막별 스키마 검증 · 누락/실패 막은 테마 마일스톤 템플릿으로 결정론적 보강([부분 구제]).
/// </summary>
public sealed class PlutoSubtitleDirector
{
    public const int MaxActLength = 40;
    public const string DefaultSubjectName = "우리 아기";

    public const string LlmLetterInstruction =
        "You are Pluto, a warm Korean poetic letter writer for 15-second family short videos. " +
        "Write a tender 3-line letter subtitle FROM the parent/creator TO the subject, in natural Korean. " +
        "Line timing: act1 [00:01-00:04] gentle opening image, act2 [00:05-00:10] growing moment of today, act3 [00:11-00:14] loving promise that calls the subject by name. " +
        "Respond with JSON only: {\"act1\":string,\"act2\":string,\"act3\":string} - each max 40 characters, no emoji, no quotes, no hashtags.";

    private sealed record Theme(string Id, string[] Keywords, string[] Act1, string[] Act2, string[] Act3);

    /// <summary>🍼 테마 마일스톤 템플릿 사전(키워드 순서 = 우선순위 · 마지막 = 기본).</summary>
    private static readonly Theme[] Themes =
    {
        new("birthday", new[] { "birthday", "첫돌", "돌잔치", "생일", "케이크", "cake" },
            new[] { "촛불처럼 반짝이는 너의 첫 번째 생일", "작은 케이크 앞에서 동그래진 너의 눈" },
            new[] { "한 해 동안 쑥쑥 자라준 고마운 오늘", "모두의 박수 속에 환하게 웃는 너" },
            new[] { "{v} 태어나줘서 정말 고마워", "{v} 매년 이 날을 함께 축하하자" }),
        new("hundred", new[] { "100일", "백일", "hundred" },
            new[] { "백 번의 아침을 함께 맞이한 우리", "작은 숨결로 채워진 백 일의 시간" },
            new[] { "하루하루 눈부시게 자라준 너의 오늘", "처음보다 더 힘차게 웃는 너의 얼굴" },
            new[] { "{v} 앞으로의 모든 날도 함께할게", "{v} 너는 우리의 가장 큰 선물이야" }),
        new("walk", new[] { "first step", "걸음", "걷", "walk", "아장" },
            new[] { "너의 작은 발끝에 닿은 따스한 바닥", "조심스레 내민 너의 첫 발자국" },
            new[] { "한 걸음씩 세상을 향해 내딛는 눈부신 오늘", "넘어져도 다시 일어서는 용감한 너" },
            new[] { "{v} 어디든 함께 걸어갈게", "{v} 언제나 너의 뒤에서 응원할게" }),
        new("sleep", new[] { "sleep", "잠", "낮잠", "자장", "꿈", "nap" },
            new[] { "새근새근 고요한 너의 숨소리", "포근한 이불 속 작은 너의 얼굴" },
            new[] { "꿈속에서도 웃고 있는 너를 바라보는 밤", "세상에서 가장 평화로운 이 순간" },
            new[] { "{v} 좋은 꿈 꾸렴, 사랑해", "{v} 내일도 너의 곁에서 깨어날게" }),
        new("bath", new[] { "bath", "목욕", "물놀이", "거품", "bubble" },
            new[] { "몽글몽글 거품 사이로 반짝이는 미소", "따뜻한 물결에 동동 떠오른 웃음" },
            new[] { "첨벙첨벙 행복이 튀어 오르는 오늘", "작은 손으로 물방울을 잡는 너" },
            new[] { "{v} 오늘도 반짝반짝 빛나줘서 고마워", "{v} 너의 웃음이 우리의 행복이야" }),
        new("pet", new[] { "강아지", "고양이", "puppy", "dog", "cat", "kitten", "댕댕", "냥" },
            new[] { "꼬리 끝까지 반가움이 묻어나는 아침", "말랑한 발바닥에 닿은 햇살 한 조각" },
            new[] { "너와 함께라서 더 눈부신 오늘의 산책", "나를 바라보는 너의 동그란 눈동자" },
            new[] { "{v} 우리 오래오래 함께하자", "{v} 넌 언제나 우리의 가족이야" }),
        new("season", new[] { "snow", "눈사람", "바다", "beach", "벚꽃", "단풍", "봄", "여름", "가을", "겨울" },
            new[] { "계절이 건네준 첫 선물 같은 풍경", "바람에 실려 온 너의 맑은 웃음소리" },
            new[] { "처음 만난 세상 앞에 반짝이는 너의 눈", "오늘의 계절을 온몸으로 느끼는 너" },
            new[] { "{v} 모든 계절을 함께 걸어갈게", "{v} 이 풍경보다 네가 더 아름다워" }),
        new("default", Array.Empty<string>(),
            new[] { "너의 작은 손끝에 닿은 따스한 아침 햇살", "너의 웃음으로 시작되는 우리의 하루" },
            new[] { "한 걸음씩 세상을 향해 내딛는 눈부신 오늘", "어제보다 한 뼘 더 자란 너의 모습" },
            new[] { "{v} 사랑해, 언제나 너의 곁을 지켜줄게", "{v} 네가 있어 오늘도 참 행복해" }),
    };

    private readonly IPlutoLlmClient? _llm;
    private readonly PlutoIdentityVault? _identity;

    /// <summary>⏱ LLM 예산(기본 <see cref="PlutoLlmBridge.Budget"/> 3.5초 · 테스트 결정론용 주입).</summary>
    public TimeSpan LlmBudget { get; init; } = PlutoLlmBridge.Budget;

    public PlutoSubtitleDirector(IPlutoLlmClient? llm = null, PlutoIdentityVault? identity = null)
    {
        _llm = llm;
        _identity = identity;
    }

    /// <summary>💌 영상 프롬프트 → 3막 편지 자막(Gemini 우선 · 실패/3.5초 초과/빈 응답 → 로컬 템플릿). 호출자 취소만 전파.</summary>
    public async Task<PlutoSubtitleScript> GenerateLetterScriptAsync(string videoPrompt, string? subjectName = null, CancellationToken cancellationToken = default)
    {
        var name = await ResolveSubjectNameAsync(subjectName, cancellationToken).ConfigureAwait(false);
        var local = GenerateLocal(videoPrompt, name);
        var user = "Subject name: " + name + "\nVideo prompt: " + (videoPrompt ?? "").Trim()
                 + "\nTheme hint: " + DetectTheme(videoPrompt).Id;
        var text = await PlutoLlmBridge.TryCompleteAsync(_llm, LlmLetterInstruction, user, 0.85, cancellationToken, LlmBudget).ConfigureAwait(false);
        return Merge(local, text);
    }

    /// <summary>⚡ 로컬 결정론 편지(LLM 미사용) — 같은 프롬프트 + 이름 → 같은 결과.</summary>
    public static PlutoSubtitleScript GenerateLocal(string? videoPrompt, string? subjectName)
    {
        var name = string.IsNullOrWhiteSpace(subjectName) ? DefaultSubjectName : subjectName.Trim();
        var theme = DetectTheme(videoPrompt);
        var seed = StableHash((videoPrompt ?? "").Trim() + "|" + name);
        var vocative = Vocative(name);
        return new PlutoSubtitleScript
        {
            SubjectName = name,
            Act1Text = Pick(theme.Act1, seed),
            Act2Text = Pick(theme.Act2, seed >> 3),
            Act3Text = Pick(theme.Act3, seed >> 6).Replace("{v}", vocative, StringComparison.Ordinal),
            UsedLlm = false,
        };
    }

    /// <summary>호격 — 한글 받침 有 → <c>이름아,</c> · 無 → <c>이름야,</c> · 비한글/“우리 아기” → <c>이름,</c>.</summary>
    public static string Vocative(string name)
    {
        if (string.IsNullOrEmpty(name)) return DefaultSubjectName + ",";
        if (name.StartsWith("우리 ", StringComparison.Ordinal)) return name + ",";
        var last = name[^1];
        if (last < '가' || last > '힣') return name + ",";
        return name + ((last - '가') % 28 != 0 ? "아," : "야,");
    }

    private async Task<string> ResolveSubjectNameAsync(string? subjectName, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(subjectName)) return Clip(subjectName.Trim(), PlutoIdentityProfile.MaxNameLength);
        if (_identity is null) return DefaultSubjectName;
        try
        {
            var p = await _identity.GetActiveProfileAsync(ct).ConfigureAwait(false);
            return p is { SubjectName.Length: > 0 } ? Clip(p.SubjectName.Trim(), PlutoIdentityProfile.MaxNameLength) : DefaultSubjectName;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Subtitle] identity fallback: " + ex.Message);
            return DefaultSubjectName;
        }
    }

    /// <summary>LLM JSON 스키마 검증 — 유효 막만 채택, 나머지 로컬 보강. 하나도 없으면 로컬 그대로.</summary>
    internal static PlutoSubtitleScript Merge(PlutoSubtitleScript local, string? llmJson)
    {
        using var doc = PlutoLlmBridge.TryParse(llmJson);
        if (doc is null) return local;
        var root = doc.RootElement;
        var a1 = Sanitize(PlutoLlmBridge.Str(root, "act1", MaxActLength * 2));
        var a2 = Sanitize(PlutoLlmBridge.Str(root, "act2", MaxActLength * 2));
        var a3 = Sanitize(PlutoLlmBridge.Str(root, "act3", MaxActLength * 2));
        if (a1.Length == 0 && a2.Length == 0 && a3.Length == 0) return local;
        return new PlutoSubtitleScript
        {
            SubjectName = local.SubjectName,
            Act1Text = a1.Length > 0 ? a1 : local.Act1Text,
            Act2Text = a2.Length > 0 ? a2 : local.Act2Text,
            Act3Text = a3.Length > 0 ? a3 : local.Act3Text,
            UsedLlm = true,
        };
    }

    private static string Sanitize(string s)
    {
        if (s.Length == 0) return s;
        s = StockSuiteExporter.StripEmoji(s).Trim('"', '\'', ' ', '“', '”');
        return Clip(s, MaxActLength);
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max].TrimEnd() : s;

    private static Theme DetectTheme(string? prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            for (var i = 0; i < Themes.Length; i++)
            {
                var kws = Themes[i].Keywords;
                for (var j = 0; j < kws.Length; j++)
                    if (prompt.Contains(kws[j], StringComparison.OrdinalIgnoreCase)) return Themes[i];
            }
        }
        return Themes[^1];
    }

    private static string Pick(string[] pool, uint seed) => pool[(int)(seed % (uint)pool.Length)];

    /// <summary>프로세스 독립 FNV-1a(string.GetHashCode 랜덤화 회피 → 재시작 후에도 동일 템플릿).</summary>
    private static uint StableHash(string s)
    {
        var h = 2166136261u;
        for (var i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
        return h;
    }

    /// <summary>테마 ID(진단/테스트용).</summary>
    public static string ThemeOf(string? prompt) => DetectTheme(prompt).Id;
}
