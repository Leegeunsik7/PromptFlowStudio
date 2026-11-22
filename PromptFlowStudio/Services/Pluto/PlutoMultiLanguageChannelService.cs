using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🌐 Step 31 — 지역 채널 훅 각색 스타일.</summary>
public static class PlutoHookAdaptationStyles
{
    /// <summary>ko-KR — 고밀도 직설(핵심부터 · 군더더기 제거).</summary>
    public const string HighDensityDirect = "high-density-direct";
    /// <summary>en-US — 가치 우선(시청 이득을 먼저 약속).</summary>
    public const string ValueFirst = "value-first";
    /// <summary>ja-JP — 고맥락 호기심(여운 · 이유를 남김).</summary>
    public const string HighContextCuriosity = "high-context-curiosity";
    /// <summary>es-ES — 감정 긴박(¡…! 강조).</summary>
    public const string EmotionalUrgency = "emotional-urgency";
}

/// <summary>🌐 로캘별 업로드 문구(설명 · 고정 댓글 CTA) — 사용자 정의 로캘은 영어 기본값.</summary>
public sealed record PlutoLocaleCopy(string GenericHook, string EndingPromptSuffix, string SeriesLineTemplate, string ContinuesFromTemplate, string NextEpisodeTemplate,
    string SeriesFinaleLine, string SubscribeLine, string PinnedNextTemplate, string PinnedSeriesCompleteLine, string PinnedStandaloneLine)
{
    /// <summary>영어 기본 문구.</summary>
    public static readonly PlutoLocaleCopy English = new("Wait for the ending", " — guess the ending?", "📺 Series part {0} of {1}", "", "▶ Next: part {0}",
        "🏁 Series finale", "👍 Like & 🔔 subscribe for the next one", "📌 Part {0} drops soon — subscribe + 🔔 so you don't miss it! Rate this part 👇",
        "📌 Series complete! Suggest the next series in the comments 👇", "📌 Made it to the end? Tell me your favorite moment 👇");
}

/// <summary>
/// 🌐 Step 31 — 지역 채널 로캘 프로필.
/// <paramref name="SeriesEpisodeTemplate"/> 는 <see cref="string.Format(IFormatProvider, string, object?, object?)"/> 형식(<c>{0}</c> = 편 · <c>{1}</c> = 총 편수).
/// </summary>
public sealed record PlutoChannelLocaleProfile(string LocaleCode, string DisplayName, string HookAdaptationStyle, string SeriesEpisodeTemplate,
    int MaxCharsPerLine, double TargetCharsPerSecond, string DefaultTtsVoiceId, double BaseSpeakingRate, IReadOnlyList<string> DefaultHashtags)
{
    /// <summary>문자 단위 줄바꿈(금칙 처리) — 띄어쓰기 없는 일본어. 그 밖 로캘은 단어(어절) 경계 우선.</summary>
    public bool CharacterLineBreaking { get; init; }

    /// <summary>칩 표기(<c>KO</c> · <c>EN</c> · <c>JA</c> · <c>ES</c>).</summary>
    public string ShortLabel => LocaleCode.Length >= 2 ? LocaleCode[..2].ToUpperInvariant() : LocaleCode.ToUpperInvariant();

    /// <summary>설명 · CTA 문구.</summary>
    public PlutoLocaleCopy Copy { get; init; } = PlutoLocaleCopy.English;

    /// <summary>훅 앞/뒤 장식(스타일별).</summary>
    public string HookPrefix { get; init; } = string.Empty;
    public string HookSuffix { get; init; } = string.Empty;
}

/// <summary>🌐 자막 줄바꿈 결과 — 안전 영역(<see cref="PlutoMultiLanguageChannelService.MaxSafeZoneLines"/>줄) 초과 시 세그먼트 분할 필요.</summary>
public sealed record PlutoSubtitleWrapResult(IReadOnlyList<string> Lines, int MaxCharsPerLine, bool ExceedsSafeZone);

/// <summary>🌐 TTS 길이 예산 — 세그먼트(장면) 안에 낭독이 끝나도록 말하기 속도 산정.</summary>
/// <param name="SpeakingRate">적용 속도(<see cref="PlutoMultiLanguageChannelService.MinSpeakingRate"/>~<see cref="PlutoMultiLanguageChannelService.MaxSpeakingRate"/>).</param>
/// <param name="RequiredRate">클램프 전 필요 속도.</param>
/// <param name="SpeechUnits">낭독 단위 수(문자 · 공백 · 구두점 제외).</param>
/// <param name="AvailableSeconds">장면 전환 가드 제외 가용 시간.</param>
/// <param name="EstimatedSeconds">적용 속도 기준 예상 낭독 시간.</param>
/// <param name="OverflowSeconds">가용 시간 초과분(0 = 장면 안에 끝남).</param>
/// <param name="MaxUnitsAtMaxRate">최대 속도로도 넣을 수 있는 최대 단위(요약 목표).</param>
public sealed record PlutoTtsBudget(double SpeakingRate, double RequiredRate, int SpeechUnits, double AvailableSeconds, double EstimatedSeconds, double OverflowSeconds, int MaxUnitsAtMaxRate)
{
    /// <summary>장면 전환을 넘지 않음.</summary>
    public bool FitsSegment => OverflowSeconds <= 0.0;
    /// <summary>최대 속도로도 넘침 → 번역 문장 축약 필요(더 빠르게 읽히지 않음 = 부자연 방지).</summary>
    public bool RequiresCondensation => OverflowSeconds > 0.0;
    /// <summary>속도가 안전 범위에 걸려 잘림.</summary>
    public bool Clamped { get; init; }
}

/// <summary>🌐 마스터 → 로캘 변형 ID 분해 결과.</summary>
public readonly record struct PlutoLocaleVariantId(string BaseClipId, string? Locale)
{
    public bool IsVariant => Locale is not null;
}

/// <summary>🌐 Step 31 — 다국어 지역 채널 레지스트리 · 훅/자막/TTS 각색기.</summary>
public interface IPlutoMultiLanguageChannelService
{
    /// <summary>등록 프로필(등록 순 · 내장 4종 먼저).</summary>
    IReadOnlyList<PlutoChannelLocaleProfile> Profiles { get; }

    /// <summary>로캘 정규화 후 조회(<c>ja</c> · <c>ja_jp</c> · <c>JA-jp</c> → <c>ja-JP</c>).</summary>
    bool TryGetProfile(string? locale, out PlutoChannelLocaleProfile profile);

    /// <summary>프로필 조회 — 미등록 → 기본 <c>ko-KR</c>.</summary>
    PlutoChannelLocaleProfile GetProfileOrDefault(string? locale);

    /// <summary>사용자 정의 로캘 등록(검증 실패 → false · 내장 로캘 교체 금지).</summary>
    bool RegisterProfile(PlutoChannelLocaleProfile profile);

    /// <summary>로캘 편 토큰(<c>1/3편</c> · <c>Ep.1/3</c> · <c>第1/3話</c> · <c>Ep. 1/3</c>).</summary>
    string FormatEpisodeToken(string? locale, int episode, int total);

    /// <summary>로캘 스타일로 훅 각색(일반 키 → 로캘 기본 훅 · 최대 안전 영역 2줄).</summary>
    string AdaptHook(string? locale, string? hookLine);

    /// <summary>로캘 인지 자막 줄바꿈(줄당 <see cref="PlutoChannelLocaleProfile.MaxCharsPerLine"/> 이하 · 문자소 단위).</summary>
    PlutoSubtitleWrapResult WrapSubtitle(string? locale, string? text);

    /// <summary>세그먼트 길이 안에 낭독이 끝나도록 말하기 속도 산정(0.85x~1.30x).</summary>
    PlutoTtsBudget ComputeTtsBudget(string? locale, string? narration, double segmentSeconds);
}

/// <summary>
/// 🌐 <see cref="IPlutoMultiLanguageChannelService"/> 순수 BCL 구현(결정론 · 스레드 안전 · 교체식 등록).
///   ▸ 자막 줄바꿈은 <see cref="StringInfo"/> 문자소 단위 — 결합 악센트(분해형 é) · 이모지 서로게이트가 줄 경계에서 갈라지지 않는다.
///   ▸ 일본어는 문자 단위 + 금칙(행두 금지 <c>、。」ー っ</c> · 행말 금지 <c>「（</c>), 그 밖은 단어/어절 경계 + 초과 단어만 강제 분할.
///   ▸ TTS: 장면 전환 가드(<see cref="SceneCutGuardSeconds"/>)를 뺀 시간 안에 끝나도록 속도를 올리되 <see cref="MaxSpeakingRate"/>에서 멈추고 축약 필요를 보고.
/// </summary>
public sealed class PlutoMultiLanguageChannelService : IPlutoMultiLanguageChannelService
{
    public const string DefaultLocale = "ko-KR";
    public const double MinSpeakingRate = 0.85;
    public const double MaxSpeakingRate = 1.30;
    /// <summary>장면 전환 직전 무음 가드(낭독이 컷을 넘어 다음 장면에 번지지 않게).</summary>
    public const double SceneCutGuardSeconds = 0.15;
    /// <summary>이보다 짧은 세그먼트는 낭독 불가(가용 0).</summary>
    public const double MinSpeakableSeconds = 0.4;
    /// <summary>Shorts 하단 안전 영역 줄 수.</summary>
    public const int MaxSafeZoneLines = 2;
    /// <summary>변형 ID 구분자(<c>{BaseClipId}::{Locale}</c>).</summary>
    public const string VariantSeparator = "::";
    public const int MaxCustomProfiles = 16;

    private const string LineStartForbidden = "、。，．,.・ー〜～」』）】〕〉》!?！？…‥ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ々ゝゞヽヾ";
    private const string LineEndForbidden = "「『（【〔〈《(";

    /// <summary>내장 4종 프로필.</summary>
    public static readonly IReadOnlyList<PlutoChannelLocaleProfile> BuiltInProfiles = new[]
    {
        new PlutoChannelLocaleProfile("ko-KR", "한국어", PlutoHookAdaptationStyles.HighDensityDirect, "{0}/{1}편", 15, 11.0, "ko-KR-SunHiNeural", 1.0, new[] { "#shorts", "#쇼츠" })
        {
            Copy = new PlutoLocaleCopy("결론부터: 끝까지 보면 반전", " — 결말 예상?", "📺 시리즈 {0}/{1}편", " — {0}편에서 이어집니다", "▶ 다음 {0}편: ",
                "🏁 시리즈 완결편", "👍 좋아요 · 🔔 구독으로 다음 영상을 받아보세요", "📌 {0}편이 곧 올라옵니다 — 구독 + 🔔 알림 켜고 놓치지 마세요! 이번 편 몇 점인가요? 👇",
                "📌 시리즈 완결! 다음 시리즈 주제를 댓글로 추천해 주세요 👇", "📌 끝까지 본 분들, 가장 좋았던 장면을 댓글로 알려주세요 👇"),
        },
        new PlutoChannelLocaleProfile("en-US", "English", PlutoHookAdaptationStyles.ValueFirst, "Ep.{0}/{1}", 34, 17.0, "en-US-JennyNeural", 1.0, new[] { "#shorts" })
        {
            Copy = PlutoLocaleCopy.English, HookPrefix = "Worth it: ",
        },
        new PlutoChannelLocaleProfile("ja-JP", "日本語", PlutoHookAdaptationStyles.HighContextCuriosity, "第{0}/{1}話", 15, 11.0, "ja-JP-NanamiNeural", 1.0, new[] { "#shorts", "#ショート" })
        {
            CharacterLineBreaking = true, HookSuffix = "…その理由は？",
            Copy = new PlutoLocaleCopy("最後まで見ると分かる", "—結末を予想？", "📺 シリーズ 第{0}/{1}話", " — 第{0}話の続き", "▶ 次回 第{0}話：",
                "🏁 シリーズ最終話", "👍 高評価 · 🔔 チャンネル登録で次回をお届けします", "📌 第{0}話もうすぐ公開 — チャンネル登録＋🔔で見逃さないで！今回は何点？👇",
                "📌 シリーズ完結！次のシリーズのテーマをコメントで教えてください👇", "📌 最後まで見てくれた方、一番好きな場面をコメントで👇"),
        },
        new PlutoChannelLocaleProfile("es-ES", "Español", PlutoHookAdaptationStyles.EmotionalUrgency, "Ep. {0}/{1}", 34, 17.0, "es-ES-ElviraNeural", 1.0, new[] { "#shorts", "#cortos" })
        {
            HookPrefix = "¡", HookSuffix = "!",
            Copy = new PlutoLocaleCopy("No te pierdas el final", " — ¿adivinas el final?", "📺 Serie, episodio {0} de {1}", " — continúa del episodio {0}", "▶ Siguiente: episodio {0} ",
                "🏁 Final de la serie", "👍 Dale like y 🔔 suscríbete para el próximo", "📌 ¡El episodio {0} llega pronto — suscríbete + 🔔 para no perdértelo! ¿Qué nota le das? 👇",
                "📌 ¡Serie completa! Sugiere la próxima serie en los comentarios 👇", "📌 ¿Llegaste al final? Cuéntame tu momento favorito 👇"),
        },
    };

    private PlutoChannelLocaleProfile[] _profiles = CopyBuiltIns();   // 교체식(락 없는 스냅샷 읽기)
    private readonly object _registerGate = new();

    private static PlutoChannelLocaleProfile[] CopyBuiltIns()
    {
        var a = new PlutoChannelLocaleProfile[BuiltInProfiles.Count];
        for (var i = 0; i < a.Length; i++) a[i] = BuiltInProfiles[i];
        return a;
    }

    public IReadOnlyList<PlutoChannelLocaleProfile> Profiles => Volatile.Read(ref _profiles);

    // ── 로캘 정규화 · 변형 ID ──

    /// <summary>
    /// 로캘 코드 정규화(<c>ja</c> → <c>ja-JP</c> · <c>es_es</c> → <c>es-ES</c> · <c>en</c> → <c>en-US</c> · <c>ko</c> → <c>ko-KR</c>).
    /// 형식 위반(문자/숫자/하이픈 외 · 경로 문자) → null — 사이드카 파일명 · 변형 ID 에 들어가므로 엄격히 거부.
    /// </summary>
    public static string? NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        var s = locale.Trim().Replace('_', '-');
        if (s.Length is < 2 or > 16) return null;
        foreach (var ch in s) if (!(ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')) return null;
        var dash = s.IndexOf('-');
        var lang = (dash < 0 ? s : s[..dash]).ToLowerInvariant();
        if (lang.Length is < 2 or > 3 || dash == s.Length - 1) return null;
        if (dash < 0)
            return lang switch { "ko" => "ko-KR", "en" => "en-US", "ja" or "jp" => "ja-JP", "es" => "es-ES", _ => null };
        var region = s[(dash + 1)..];
        if (region.IndexOf('-') >= 0 || region.Length is < 2 or > 4) return null;
        return lang + "-" + (region.Length == 2 ? region.ToUpperInvariant() : region);
    }

    /// <summary>변형 ID(<c>{BaseClipId}::{Locale}</c>) — 변형의 변형(<c>a::ja-JP::es-ES</c>)은 마스터 기준으로 평탄화.</summary>
    public static string BuildVariantClipId(string baseClipId, string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseClipId);
        var norm = NormalizeLocale(locale) ?? throw new ArgumentException("invalid locale", nameof(locale));
        return ParseVariantClipId(baseClipId).BaseClipId + VariantSeparator + norm;
    }

    /// <summary>변형 ID 분해(구분자 없음 · 로캘 형식 위반 → 마스터 ID 그대로).</summary>
    public static PlutoLocaleVariantId ParseVariantClipId(string? clipId)
    {
        if (string.IsNullOrWhiteSpace(clipId)) return new PlutoLocaleVariantId(string.Empty, null);
        var id = clipId.Trim();
        var first = id.IndexOf(VariantSeparator, StringComparison.Ordinal);
        if (first <= 0) return new PlutoLocaleVariantId(id, null);
        var last = id.LastIndexOf(VariantSeparator, StringComparison.Ordinal);
        var loc = NormalizeLocale(id[(last + VariantSeparator.Length)..]);
        return loc is null ? new PlutoLocaleVariantId(id, null) : new PlutoLocaleVariantId(id[..first], loc);
    }

    // ── 레지스트리 ──

    public bool TryGetProfile(string? locale, out PlutoChannelLocaleProfile profile)
    {
        var norm = NormalizeLocale(locale);
        if (norm is not null)
            foreach (var p in Volatile.Read(ref _profiles))
                if (string.Equals(p.LocaleCode, norm, StringComparison.OrdinalIgnoreCase)) { profile = p; return true; }
        profile = BuiltInProfiles[0];
        return false;
    }

    public PlutoChannelLocaleProfile GetProfileOrDefault(string? locale) => TryGetProfile(locale, out var p) ? p : BuiltInProfiles[0];

    public bool RegisterProfile(PlutoChannelLocaleProfile profile)
    {
        if (profile is null) return false;
        var norm = NormalizeLocale(profile.LocaleCode);
        if (norm is null || profile.MaxCharsPerLine is < 4 or > 80 || !double.IsFinite(profile.TargetCharsPerSecond) || profile.TargetCharsPerSecond is < 3.0 or > 40.0
            || !double.IsFinite(profile.BaseSpeakingRate) || profile.BaseSpeakingRate < MinSpeakingRate || profile.BaseSpeakingRate > MaxSpeakingRate
            || !IsValidEpisodeTemplate(profile.SeriesEpisodeTemplate)) return false;
        var clean = profile with { LocaleCode = norm, DefaultHashtags = profile.DefaultHashtags ?? Array.Empty<string>() };
        lock (_registerGate)
        {
            var cur = _profiles;
            foreach (var b in BuiltInProfiles) if (string.Equals(b.LocaleCode, norm, StringComparison.OrdinalIgnoreCase)) return false;   // 🛡️ 내장 교체 금지
            var list = new List<PlutoChannelLocaleProfile>(cur.Length + 1);
            var replaced = false;
            foreach (var p in cur)
            {
                if (string.Equals(p.LocaleCode, norm, StringComparison.OrdinalIgnoreCase)) { list.Add(clean); replaced = true; }
                else list.Add(p);
            }
            if (!replaced)
            {
                if (cur.Length - BuiltInProfiles.Count >= MaxCustomProfiles) return false;
                list.Add(clean);
            }
            Volatile.Write(ref _profiles, list.ToArray());
        }
        return true;
    }

    /// <summary>편 템플릿 검증 — <c>{0}</c> · <c>{1}</c> 둘 다 포함 · 형식 예외 없음.</summary>
    public static bool IsValidEpisodeTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 40 || template.IndexOf("{0}", StringComparison.Ordinal) < 0 || template.IndexOf("{1}", StringComparison.Ordinal) < 0) return false;
        try { _ = string.Format(CultureInfo.InvariantCulture, template, 1, 2); return true; }
        catch (FormatException) { return false; }
    }

    // ── 편 토큰 · 훅 ──

    public string FormatEpisodeToken(string? locale, int episode, int total)
    {
        if (episode < 1 || total < 2 || episode > total) return string.Empty;
        return string.Format(CultureInfo.InvariantCulture, GetProfileOrDefault(locale).SeriesEpisodeTemplate, episode, total);
    }

    public string AdaptHook(string? locale, string? hookLine)
    {
        var p = GetProfileOrDefault(locale);
        var h = CollapseWhitespace(hookLine);
        if (h.Length == 0 || IsGenericHookKey(h)) return p.Copy.GenericHook;
        // 장식 중복 방지(¡¡…!! · "Worth it: Worth it:")
        if (p.HookPrefix.Length > 0 && h.StartsWith(p.HookPrefix, StringComparison.Ordinal)) h = h[p.HookPrefix.Length..].TrimStart();
        if (p.HookSuffix.Length > 0 && h.EndsWith(p.HookSuffix, StringComparison.Ordinal)) h = h[..^p.HookSuffix.Length].TrimEnd();
        if (p.HookAdaptationStyle == PlutoHookAdaptationStyles.HighDensityDirect || p.HookAdaptationStyle == PlutoHookAdaptationStyles.EmotionalUrgency)
            h = h.TrimEnd('.', '。', '…', '!', '！', '?', '？').TrimStart('¡', '¿').Trim();
        if (h.Length == 0) return p.Copy.GenericHook;
        // 안전 영역 2줄 예산 — 장식을 뺀 본문만 자름(장식은 스타일 신호라 보존)
        var budget = Math.Max(4, p.MaxCharsPerLine * MaxSafeZoneLines - TextLength(p.HookPrefix) - TextLength(p.HookSuffix));
        if (TextLength(h) > budget) h = TakeTextElements(h, budget - 1).TrimEnd() + "…";
        return p.HookPrefix + h + p.HookSuffix;
    }

    private static bool IsGenericHookKey(string h)
        => h.Equals("montage-hook", StringComparison.OrdinalIgnoreCase) || h.Equals("hook", StringComparison.OrdinalIgnoreCase) || h.Equals("훅", StringComparison.Ordinal);

    // ── 자막 줄바꿈 ──

    public PlutoSubtitleWrapResult WrapSubtitle(string? locale, string? text)
    {
        var p = GetProfileOrDefault(locale);
        var lines = Wrap(CollapseWhitespace(text), p.MaxCharsPerLine, p.CharacterLineBreaking);
        return new PlutoSubtitleWrapResult(lines, p.MaxCharsPerLine, lines.Count > MaxSafeZoneLines);
    }

    /// <summary>순수 줄바꿈 — 모든 줄이 <paramref name="maxChars"/> 문자소 이하(빈 입력 → 0줄).</summary>
    public static IReadOnlyList<string> Wrap(string? text, int maxChars, bool characterBreaking)
    {
        var lines = new List<string>();
        var s = CollapseWhitespace(text);
        if (s.Length == 0) return lines;
        if (maxChars < 2) maxChars = 2;
        if (characterBreaking) WrapCharacters(SplitTextElements(s), maxChars, lines);
        else WrapWords(s, maxChars, lines);
        return lines;
    }

    private static void WrapWords(string s, int maxChars, List<string> lines)
    {
        var cur = new StringBuilder(maxChars * 2);
        var curLen = 0;
        foreach (var word in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var els = SplitTextElements(word);
            if (els.Count > maxChars)
            {
                // 초과 단어 — 현재 줄을 비우고 문자소 단위 강제 분할(줄 넘침 금지)
                if (curLen > 0) { lines.Add(cur.ToString()); cur.Clear(); curLen = 0; }
                for (var i = 0; i < els.Count; i += maxChars)
                {
                    var n = Math.Min(maxChars, els.Count - i);
                    var chunk = Join(els, i, n);
                    if (n == maxChars) lines.Add(chunk);
                    else { cur.Append(chunk); curLen = n; }
                }
                continue;
            }
            var need = curLen == 0 ? els.Count : curLen + 1 + els.Count;
            if (need > maxChars) { lines.Add(cur.ToString()); cur.Clear(); curLen = 0; need = els.Count; }
            if (curLen > 0) cur.Append(' ');
            cur.Append(word);
            curLen = need;
        }
        if (curLen > 0) lines.Add(cur.ToString());
    }

    private static void WrapCharacters(List<string> els, int maxChars, List<string> lines)
    {
        var start = 0;
        while (start < els.Count)
        {
            while (start < els.Count && els[start] == " ") start++;
            if (start >= els.Count) break;
            var end = Math.Min(els.Count, start + maxChars);   // [start, end)
            if (end < els.Count)
            {
                // 금칙: 다음 줄 첫 글자가 행두 금지 · 이번 줄 끝 글자가 행말 금지 → 줄을 앞당김(넘침 대신 당겨 내리기)
                var e = end;
                while (e > start + 1 && (IsLineStartForbidden(els[e]) || IsLineEndForbidden(els[e - 1]))) e--;
                if (e > start + 1) end = e;
            }
            var line = Join(els, start, end - start).Trim();
            if (line.Length > 0) lines.Add(line);
            start = end;
        }
    }

    private static bool IsLineStartForbidden(string el) => el.Length == 1 && LineStartForbidden.IndexOf(el[0]) >= 0;
    private static bool IsLineEndForbidden(string el) => el.Length == 1 && LineEndForbidden.IndexOf(el[0]) >= 0;

    // ── TTS 길이 예산 ──

    public PlutoTtsBudget ComputeTtsBudget(string? locale, string? narration, double segmentSeconds)
    {
        var p = GetProfileOrDefault(locale);
        var units = CountSpeechUnits(narration);
        var available = !double.IsFinite(segmentSeconds) || segmentSeconds < MinSpeakableSeconds ? 0.0 : segmentSeconds - SceneCutGuardSeconds;
        var baseRate = Math.Clamp(p.BaseSpeakingRate, MinSpeakingRate, MaxSpeakingRate);
        if (units == 0) return new PlutoTtsBudget(baseRate, baseRate, 0, available, 0.0, 0.0, MaxUnits(p, available));
        var natural = units / p.TargetCharsPerSecond;   // 1.0x 기준 낭독 시간
        if (available <= 0.0)
            return new PlutoTtsBudget(MaxSpeakingRate, double.PositiveInfinity, units, 0.0, natural / MaxSpeakingRate, natural / MaxSpeakingRate, 0) { Clamped = true };
        var required = natural / available;
        var desired = Math.Max(baseRate, required);   // 짧은 문장을 일부러 늘이지 않음(기본 속도 유지)
        var rate = Math.Clamp(desired, MinSpeakingRate, MaxSpeakingRate);
        var est = natural / rate;
        var overflow = est - available;
        return new PlutoTtsBudget(Math.Round(rate, 3, MidpointRounding.AwayFromZero), required, units, available, est, overflow > 1e-9 ? overflow : 0.0, MaxUnits(p, available))
        {
            Clamped = desired > MaxSpeakingRate,
        };
    }

    private static int MaxUnits(PlutoChannelLocaleProfile p, double available) => available <= 0 ? 0 : (int)Math.Floor(p.TargetCharsPerSecond * MaxSpeakingRate * available);

    /// <summary>낭독 단위 — 공백 · 구두점 · 기호(이모지) 제외 문자소 수.</summary>
    public static int CountSpeechUnits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var n = 0;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var el = (string)e.Current;
            if (el.Length == 0) continue;
            var cat = CharUnicodeInfo.GetUnicodeCategory(el, 0);
            if (cat is UnicodeCategory.SpaceSeparator or UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate
                || char.IsPunctuation(el, 0) || char.IsWhiteSpace(el, 0)) continue;
            n++;
        }
        return n;
    }

    // ── 문자소 유틸 ──

    /// <summary>공백 정리 + NFC 정규화(분해형 악센트를 합성형으로 — 길이 계산 일관).</summary>
    public static string CollapseWhitespace(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        string t;
        try { t = s.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { t = s; }   // 고아 서로게이트
        var sb = new StringBuilder(t.Length);
        var space = false;
        foreach (var ch in t.Trim())
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { if (!space && sb.Length > 0) sb.Append(' '); space = true; continue; }
            space = false;
            sb.Append(ch);
        }
        return sb.ToString().TrimEnd();
    }

    public static int TextLength(string? s) => string.IsNullOrEmpty(s) ? 0 : new StringInfo(s).LengthInTextElements;

    private static string TakeTextElements(string s, int count)
    {
        var si = new StringInfo(s);
        return count >= si.LengthInTextElements ? s : si.SubstringByTextElements(0, Math.Max(0, count));
    }

    private static List<string> SplitTextElements(string s)
    {
        var list = new List<string>(s.Length);
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }

    private static string Join(List<string> els, int start, int count)
    {
        var sb = new StringBuilder(count * 2);
        for (var i = start; i < start + count; i++) sb.Append(els[i]);
        return sb.ToString();
    }

    // ── 제목 로캘 추정(링커 · 결정론) ──

    /// <summary>
    /// 🔎 제목 문자열만으로 로캘 추정(강한 신호): 한글 → ko-KR · 가나/<c>話</c> → ja-JP · Pluto 편 토큰 형식 <c>Ep. n/N</c>(공백) → es-ES ·
    /// <c>Ep.n/N</c>(붙임) → en-US · 스페인어 고유 문자(ñ · ¿ · ¡ · 악센트 모음) → es-ES. 판정 불가 → null.
    /// </summary>
    public static string? InferTitleLocale(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        bool hangul = false, kana = false, spanish = false;
        foreach (var ch in title)
        {
            if (ch is >= '가' and <= '힣' or >= 'ㄱ' and <= 'ㆎ') hangul = true;
            else if (ch is >= '぀' and <= 'ヿ' || ch == '話') kana = true;
            else if (ch is 'ñ' or 'Ñ' or '¿' or '¡' or 'á' or 'é' or 'í' or 'ó' or 'ú' or 'Á' or 'É' or 'Í' or 'Ó' or 'Ú') spanish = true;
        }
        if (hangul && !kana) return "ko-KR";
        if (kana && !hangul) return "ja-JP";
        if (hangul || kana) return null;   // 혼합 → 판정 보류
        var ep = title.IndexOf("Ep.", StringComparison.Ordinal);
        if (ep >= 0 && ep + 3 < title.Length)
        {
            var c = title[ep + 3];
            if (c == ' ' && ep + 4 < title.Length && char.IsAsciiDigit(title[ep + 4])) return "es-ES";
            if (char.IsAsciiDigit(c)) return spanish ? "es-ES" : "en-US";
        }
        return spanish ? "es-ES" : null;
    }

    /// <summary>내보내기 헤더 언어(<c>ko</c>/<c>en</c>/<c>ja</c>/<c>es</c>) → 로캘(약한 신호 · Studio UI 언어 ≠ 채널 언어일 수 있음).</summary>
    public static string? LocaleOfExportLanguage(string? language) => NormalizeLocale(language);
}
