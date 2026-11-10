using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>💌 단일 막 KO/EN 이중 자막(표시 구간 초 + 한국어 주 자막 + 영어 보조 자막).</summary>
public sealed class PlutoSubtitleAct
{
    public int Act { get; init; }
    public int StartSec { get; init; }
    public int EndSec { get; init; }
    public string KoreanText { get; set; } = "";
    public string EnglishText { get; set; } = "";
    /// <summary>하위 호환 — 주 자막(<see cref="KoreanText"/>).</summary>
    public string Text => KoreanText;
}

/// <summary>💌 15초 3막 KO/EN 이중 감성 편지 자막(Act1 01~04초 · Act2 05~10초 · Act3 11~14초).</summary>
public sealed class PlutoSubtitleScript
{
    /// <summary>💌 3막 표시 구간(초) — 15초 쇼츠 기준.</summary>
    public static readonly (int Start, int End)[] ActWindows = { (1, 4), (5, 10), (11, 14) };

    public const string LetterSignatureKo = "From. 언제나 곁에 있는 가족이";
    public const string LetterSignatureEn = "From. Your family, always by your side";

    public string SubjectName { get; init; } = "";
    public string Act1Text { get; init; } = "";
    public string Act2Text { get; init; } = "";
    public string Act3Text { get; init; } = "";
    public string Act1En { get; init; } = "";
    public string Act2En { get; init; } = "";
    public string Act3En { get; init; } = "";
    public bool UsedLlm { get; init; }

    /// <summary>3막 KO/EN 쌍(미리보기·직렬화용 스냅샷).</summary>
    public PlutoSubtitleAct[] Acts => new[]
    {
        new PlutoSubtitleAct { Act = 1, StartSec = ActWindows[0].Start, EndSec = ActWindows[0].End, KoreanText = Act1Text, EnglishText = Act1En },
        new PlutoSubtitleAct { Act = 2, StartSec = ActWindows[1].Start, EndSec = ActWindows[1].End, KoreanText = Act2Text, EnglishText = Act2En },
        new PlutoSubtitleAct { Act = 3, StartSec = ActWindows[2].Start, EndSec = ActWindows[2].End, KoreanText = Act3Text, EnglishText = Act3En },
    };

    /// <summary>시간 동기 큐(<see cref="StockSuiteExporter.BurnSubtitles"/> 번인 입력) — KO 주 자막 + EN 보조 자막.</summary>
    public SubtitleCue[] ToCues() => new[]
    {
        new SubtitleCue(ActWindows[0].Start, ActWindows[0].End, Act1Text, Act1En),
        new SubtitleCue(ActWindows[1].Start, ActWindows[1].End, Act2Text, Act2En),
        new SubtitleCue(ActWindows[2].Start, ActWindows[2].End, Act3Text, Act3En),
    };

    /// <summary>표준 이중 언어 <c>.srt</c> 문자열(3블록 · <c>00:00:01,000 --> 00:00:04,000</c> · KO 줄 + EN 줄).</summary>
    public string ToSrtFormat() => StockSuiteExporter.BuildSrt(ToCues());

    /// <summary>📋 복사용 편지 본문(KO 전용) — <c>To. 이름</c> · 3줄 · 서명.</summary>
    public string ToFullLetterText()
    {
        var sb = new StringBuilder(160);
        sb.Append("💌 To. ").Append(SubjectName).Append('\n').Append('\n')
          .Append(Act1Text).Append('\n')
          .Append(Act2Text).Append('\n')
          .Append(Act3Text).Append('\n').Append('\n')
          .Append(LetterSignatureKo);
        return sb.ToString();
    }

    /// <summary>📋 이중 언어 복사 본문 — <c>To. 이름</c> · 막별 KO/EN 2줄 블록 · KO/EN 서명. EN 빈 막은 KO 줄만.</summary>
    public string ToBilingualClipboardText()
    {
        var sb = new StringBuilder(320);
        sb.Append("💌 To. ").Append(SubjectName).Append('\n');
        AppendPair(sb, Act1Text, Act1En);
        AppendPair(sb, Act2Text, Act2En);
        AppendPair(sb, Act3Text, Act3En);
        sb.Append('\n').Append(LetterSignatureKo).Append('\n').Append(LetterSignatureEn);
        return sb.ToString();
    }

    private static void AppendPair(StringBuilder sb, string ko, string en)
    {
        sb.Append('\n').Append(ko).Append('\n');
        if (!string.IsNullOrWhiteSpace(en)) sb.Append(en).Append('\n');
    }
}

/// <summary>
/// 💌 Emotional Letter Subtitle Director — 부모/크리에이터 → 주인공에게 보내는 3막 KO/EN 이중 시적 편지 자막.
/// 🧠 Gemini(<see cref="PlutoLlmBridge"/> 3.5초 예산) 우선 · 막별 스키마 검증(KO+EN 쌍 모두 유효해야 채택) · 누락/실패 막은 테마 마일스톤 템플릿 쌍으로 결정론적 보강([부분 구제]).
/// </summary>
public sealed class PlutoSubtitleDirector
{
    public const int MaxActLength = 40;
    public const int MaxEnglishLength = 72;
    public const string DefaultSubjectName = "우리 아기";
    public const string DefaultEnglishName = "My little one";

    public const string LlmLetterInstruction =
        "You are Pluto, a warm poetic letter writer for 15-second family short videos shared with a global audience. " +
        "Write a tender 3-act bilingual letter subtitle FROM the parent/creator TO the subject: each act has a natural Korean line (ko) and a poetic, natural English rendering of the same line (en). " +
        "Line timing: act1 [00:01-00:04] gentle opening image, act2 [00:05-00:10] growing moment of today, act3 [00:11-00:14] loving promise that calls the subject by name. " +
        "Respond with a JSON array only: [{\"act\":1,\"startSec\":1,\"endSec\":4,\"ko\":string,\"en\":string},{\"act\":2,\"startSec\":5,\"endSec\":10,\"ko\":string,\"en\":string},{\"act\":3,\"startSec\":11,\"endSec\":14,\"ko\":string,\"en\":string}] " +
        "- ko max 40 characters, en max 70 characters, no emoji, no quotes, no hashtags.";

    /// <summary>🍼 KO/EN 템플릿 쌍(같은 의미 · <c>{v}</c>=KO 호격, <c>{n}</c>=EN 호칭).</summary>
    private readonly record struct Line(string Ko, string En);

    private sealed record Theme(string Id, string[] Keywords, Line[] Act1, Line[] Act2, Line[] Act3);

    /// <summary>🍼 테마 마일스톤 템플릿 사전(키워드 순서 = 우선순위 · 마지막 = 기본).</summary>
    private static readonly Theme[] Themes =
    {
        new("birthday", new[] { "birthday", "첫돌", "돌잔치", "생일", "케이크", "cake" },
            new Line[] { new("촛불처럼 반짝이는 너의 첫 번째 생일", "Your very first birthday, glowing like a candle"), new("작은 케이크 앞에서 동그래진 너의 눈", "Your eyes grew wide before that tiny cake") },
            new Line[] { new("한 해 동안 쑥쑥 자라준 고마운 오늘", "A grateful day for a whole year of growing"), new("모두의 박수 속에 환하게 웃는 너", "You beam so bright amid everyone's applause") },
            new Line[] { new("{v} 태어나줘서 정말 고마워", "{n}, thank you for being born to us"), new("{v} 매년 이 날을 함께 축하하자", "{n}, let's celebrate this day every year") }),
        new("hundred", new[] { "100일", "백일", "hundred" },
            new Line[] { new("백 번의 아침을 함께 맞이한 우리", "A hundred mornings we have greeted together"), new("작은 숨결로 채워진 백 일의 시간", "A hundred days filled with your tiny breaths") },
            new Line[] { new("하루하루 눈부시게 자라준 너의 오늘", "Growing more dazzling with every single day"), new("처음보다 더 힘차게 웃는 너의 얼굴", "Your smile, stronger than on the very first day") },
            new Line[] { new("{v} 앞으로의 모든 날도 함께할게", "{n}, I'll be with you for all the days ahead"), new("{v} 너는 우리의 가장 큰 선물이야", "{n}, you are our greatest gift") }),
        new("walk", new[] { "first step", "걸음", "걷", "walk", "아장" },
            new Line[] { new("너의 작은 발끝에 닿은 따스한 바닥", "The warm floor beneath your tiny toes"), new("조심스레 내민 너의 첫 발자국", "Your very first step, taken so carefully") },
            new Line[] { new("한 걸음씩 세상을 향해 내딛는 눈부신 오늘", "A dazzling day, stepping toward the world"), new("넘어져도 다시 일어서는 용감한 너", "So brave, rising again each time you fall") },
            new Line[] { new("{v} 어디든 함께 걸어갈게", "{n}, wherever you go, I'll walk with you"), new("{v} 언제나 너의 뒤에서 응원할게", "{n}, I'll always be cheering right behind you") }),
        new("sleep", new[] { "sleep", "잠", "낮잠", "자장", "꿈", "nap" },
            new Line[] { new("새근새근 고요한 너의 숨소리", "The soft, quiet rhythm of your breathing"), new("포근한 이불 속 작은 너의 얼굴", "Your little face tucked in a cozy blanket") },
            new Line[] { new("꿈속에서도 웃고 있는 너를 바라보는 밤", "A night watching you smile even in your dreams"), new("세상에서 가장 평화로운 이 순간", "The most peaceful moment in the whole world") },
            new Line[] { new("{v} 좋은 꿈 꾸렴, 사랑해", "{n}, sweet dreams. I love you"), new("{v} 내일도 너의 곁에서 깨어날게", "{n}, I'll wake up beside you tomorrow too") }),
        new("bath", new[] { "bath", "목욕", "물놀이", "거품", "bubble" },
            new Line[] { new("몽글몽글 거품 사이로 반짝이는 미소", "A sparkling smile peeking through the bubbles"), new("따뜻한 물결에 동동 떠오른 웃음", "Laughter floating on warm little waves") },
            new Line[] { new("첨벙첨벙 행복이 튀어 오르는 오늘", "A splashy day where happiness leaps up"), new("작은 손으로 물방울을 잡는 너", "Your tiny hands reaching for water drops") },
            new Line[] { new("{v} 오늘도 반짝반짝 빛나줘서 고마워", "{n}, thank you for shining so bright today"), new("{v} 너의 웃음이 우리의 행복이야", "{n}, your laughter is our happiness") }),
        new("pet", new[] { "강아지", "고양이", "puppy", "dog", "cat", "kitten", "댕댕", "냥" },
            new Line[] { new("꼬리 끝까지 반가움이 묻어나는 아침", "A morning of joy, right to the tip of your tail"), new("말랑한 발바닥에 닿은 햇살 한 조각", "A ray of sunlight on your soft little paws") },
            new Line[] { new("너와 함께라서 더 눈부신 오늘의 산책", "Today's walk shines brighter because of you"), new("나를 바라보는 너의 동그란 눈동자", "Your round eyes looking up at me") },
            new Line[] { new("{v} 우리 오래오래 함께하자", "{n}, let's stay together for a long, long time"), new("{v} 넌 언제나 우리의 가족이야", "{n}, you'll always be our family") }),
        new("season", new[] { "snow", "눈사람", "바다", "beach", "벚꽃", "단풍", "봄", "여름", "가을", "겨울" },
            new Line[] { new("계절이 건네준 첫 선물 같은 풍경", "A view like the season's very first gift"), new("바람에 실려 온 너의 맑은 웃음소리", "Your clear laughter carried on the breeze") },
            new Line[] { new("처음 만난 세상 앞에 반짝이는 너의 눈", "Your eyes sparkling at a brand-new world"), new("오늘의 계절을 온몸으로 느끼는 너", "Feeling today's season with your whole heart") },
            new Line[] { new("{v} 모든 계절을 함께 걸어갈게", "{n}, I'll walk through every season with you"), new("{v} 이 풍경보다 네가 더 아름다워", "{n}, you're more beautiful than this view") }),
        new("default", Array.Empty<string>(),
            new Line[] { new("너의 작은 손끝에 닿은 따스한 아침 햇살", "Warm morning sunlight on your tiny fingertips"), new("너의 웃음으로 시작되는 우리의 하루", "Our day begins with your laughter") },
            new Line[] { new("한 걸음씩 세상을 향해 내딛는 눈부신 오늘", "A dazzling day, stepping toward the world"), new("어제보다 한 뼘 더 자란 너의 모습", "A little taller than you were yesterday") },
            new Line[] { new("{v} 사랑해, 언제나 너의 곁을 지켜줄게", "{n}, I love you. I'll always be by your side"), new("{v} 네가 있어 오늘도 참 행복해", "{n}, because of you, today is so happy") }),
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
        var enName = EnglishName(name);
        var l1 = Pick(theme.Act1, seed);
        var l2 = Pick(theme.Act2, seed >> 3);
        var l3 = Pick(theme.Act3, seed >> 6);
        return new PlutoSubtitleScript
        {
            SubjectName = name,
            Act1Text = l1.Ko,
            Act2Text = l2.Ko,
            Act3Text = l3.Ko.Replace("{v}", vocative, StringComparison.Ordinal),
            Act1En = l1.En,
            Act2En = l2.En,
            Act3En = l3.En.Replace("{n}", enName, StringComparison.Ordinal),
            UsedLlm = false,
        };
    }

    /// <summary>EN 호칭 — 기본/“우리 …” → <c>My little one</c> · 그 외 이름 그대로.</summary>
    public static string EnglishName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DefaultEnglishName;
        var n = name.Trim();
        return n == DefaultSubjectName || n.StartsWith("우리 ", StringComparison.Ordinal) ? DefaultEnglishName : n;
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

    /// <summary>
    /// 🌊 스트리밍 요소 파서(<see cref="PlutoJsonElementParser{T}"/>) — <c>{"act","ko","en"}</c> 1개 → <see cref="PlutoSubtitleAct"/>.
    /// <see cref="Merge"/>와 동일 검증(막 1~3 · KO 필수 · EN 라틴 문자 1자 이상) · 구간은 <see cref="PlutoSubtitleScript.ActWindows"/>.
    /// </summary>
    public static bool TryParseStreamedAct(ReadOnlyMemory<byte> elementUtf8, int ordinal, out PlutoSubtitleAct act)
    {
        act = null!;
        using var doc = JsonDocument.Parse(elementUtf8);
        var el = doc.RootElement;
        if (el.ValueKind != JsonValueKind.Object) return false;
        var idx = ordinal;
        if (el.TryGetProperty("act", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var n)) idx = n - 1;
        if ((uint)idx >= 3u) return false;
        var k = Sanitize(PlutoLlmBridge.Str(el, "ko", MaxActLength * 2), MaxActLength);
        var e = Sanitize(PlutoLlmBridge.Str(el, "en", MaxEnglishLength * 2), MaxEnglishLength);
        if (k.Length == 0 || !HasLatinLetter(e)) return false;
        var w = PlutoSubtitleScript.ActWindows[idx];
        act = new PlutoSubtitleAct { Act = idx + 1, StartSec = w.Start, EndSec = w.End, KoreanText = k, EnglishText = e };
        return true;
    }

    /// <summary>
    /// LLM JSON 스키마 검증 — 루트 배열 <c>[{"act","ko","en"}]</c> 또는 <c>{"acts":[…]}</c>. 막 번호(<c>act</c> 1~3, 없으면 순서) 기준,
    /// KO·EN 모두 유효한 막만 채택(EN은 라틴 문자 1자 이상) · 나머지 막은 로컬 KO/EN 쌍 보강. 하나도 없으면 로컬 그대로.
    /// </summary>
    internal static PlutoSubtitleScript Merge(PlutoSubtitleScript local, string? llmJson)
    {
        using var doc = PlutoLlmBridge.TryParse(llmJson);
        if (doc is null) return local;
        var arr = doc.RootElement;
        if (arr.ValueKind == JsonValueKind.Object && arr.TryGetProperty("acts", out var inner)) arr = inner;
        if (arr.ValueKind != JsonValueKind.Array) return local;
        var ko = new string?[3];
        var en = new string?[3];
        var pos = 0;
        foreach (var el in arr.EnumerateArray())
        {
            var idx = pos++;
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (el.TryGetProperty("act", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var n)) idx = n - 1;
            if ((uint)idx >= 3u || ko[idx] is not null) continue;
            var k = Sanitize(PlutoLlmBridge.Str(el, "ko", MaxActLength * 2), MaxActLength);
            var e = Sanitize(PlutoLlmBridge.Str(el, "en", MaxEnglishLength * 2), MaxEnglishLength);
            if (k.Length == 0 || !HasLatinLetter(e)) continue;
            ko[idx] = k;
            en[idx] = e;
        }
        if (ko[0] is null && ko[1] is null && ko[2] is null) return local;
        return new PlutoSubtitleScript
        {
            SubjectName = local.SubjectName,
            Act1Text = ko[0] ?? local.Act1Text,
            Act2Text = ko[1] ?? local.Act2Text,
            Act3Text = ko[2] ?? local.Act3Text,
            Act1En = en[0] ?? local.Act1En,
            Act2En = en[1] ?? local.Act2En,
            Act3En = en[2] ?? local.Act3En,
            UsedLlm = true,
        };
    }

    private static bool HasLatinLetter(string s)
    {
        foreach (var c in s)
            if (char.IsAsciiLetter(c)) return true;
        return false;
    }

    private static string Sanitize(string s, int max)
    {
        if (s.Length == 0) return s;
        s = StockSuiteExporter.StripEmoji(s).Trim('"', '\'', ' ', '“', '”');
        return Clip(s, max);
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

    private static Line Pick(Line[] pool, uint seed) => pool[(int)(seed % (uint)pool.Length)];

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
