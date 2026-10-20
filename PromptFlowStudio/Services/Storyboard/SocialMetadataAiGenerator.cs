using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>v16.15.0 AI 소셜 메타데이터 대상 채널.</summary>
public enum SocialChannel { YouTube, Instagram, TikTok, NaverClip, Pinterest }

/// <summary>v16.15.0 메타데이터 생성 입력 — 주제 · 대본 · 테마 카테고리.</summary>
public sealed record SocialMetadataRequest(string Topic, string Script, string? ThemeCategory = null);

/// <summary>v16.15.0 생성 결과 — 채널별 <see cref="PlatformMeta"/>(입력 순서) · AI 사용 채널 수 · 상태 문구.</summary>
public sealed record SocialMetadataResult(IReadOnlyDictionary<SocialChannel, PlatformMeta> Posts, int AiChannels, string Message)
{
    public bool AiUsed => AiChannels > 0;
}

/// <summary>
/// [PromptFlowStudio] v16.15.0 🏷️ AI 소셜 메타데이터 생성기 — 스토리보드 주제·대본·테마 카테고리 → Gemini Strict JSON → 채널별 제목/본문/해시태그.
///   ▸ 채널 규칙: YouTube(검색형 제목 ≤100자 · 태그 ≤15) · Instagram(감성 캡션 ≤2200 · 태그 ≤30) · TikTok(훅 ≤150 · 태그 ≤8)
///     · Naver Clip(검색 키워드 제목 ≤40 · 태그 ≤10) · Pinterest(에버그린 핀 제목 ≤100 · 태그 ≤10).
///   ▸ Gemini 출력은 비신뢰 → 스키마 즉시 검증(채널 객체 · 문자열 필드 · 배열) + 해시태그 정규화(# 제거 · 공백/기호 제거 · 중복 제거 · 상한).
///   ▸ 키 없음/실패/채널 누락은 <see cref="PlatformSeoAgent"/> 오프라인 결과 → 그것도 없으면 로컬 템플릿으로 채널 단위 폴백([부분 구제]).
/// </summary>
public sealed class SocialMetadataAiGenerator
{
    /// <summary>대본 프롬프트 상한(문자) — 토큰 폭주 방지.</summary>
    public const int MaxScriptChars = 1500;

    private readonly Func<string, CancellationToken, Task<GeminiResult>>? _generate;
    private readonly Func<bool> _enabled;
    private readonly PlatformSeoAgent? _offline;

    public SocialMetadataAiGenerator(GeminiClient? gemini = null, PlatformSeoAgent? offline = null)
    {
        _generate = gemini is null ? null : (p, ct) => gemini.GenerateAsync(p, null, 0.7, ct);
        _enabled = () => gemini is { Enabled: true };
        _offline = offline;
    }

    /// <param name="generate">지시문 → Gemini 결과(테스트 주입).</param>
    public SocialMetadataAiGenerator(Func<string, CancellationToken, Task<GeminiResult>> generate, PlatformSeoAgent? offline)
    {
        _generate = generate;
        _enabled = static () => true;
        _offline = offline;
    }

    public bool AiEnabled => _generate is not null && _enabled();

    public static string KeyOf(SocialChannel c) => c switch
    {
        SocialChannel.YouTube => "youtube",
        SocialChannel.Instagram => "instagram",
        SocialChannel.TikTok => "tiktok",
        SocialChannel.NaverClip => "naverclip",
        _ => "pinterest",
    };

    /// <summary>기존 오프라인 SEO(<see cref="PlatformMeta.Platform"/>) 표시명 — 폴백 매칭 키.</summary>
    public static string DisplayOf(SocialChannel c) => c switch
    {
        SocialChannel.YouTube => "YouTube Shorts",
        SocialChannel.Instagram => "Instagram Reels",
        SocialChannel.TikTok => "TikTok",
        SocialChannel.NaverClip => "Naver Clip",
        _ => "Pinterest",
    };

    public static string IconOf(SocialChannel c) => c switch
    {
        SocialChannel.YouTube => "▶️",
        SocialChannel.Instagram => "📸",
        SocialChannel.TikTok => "🎵",
        SocialChannel.NaverClip => "🟢",
        _ => "📌",
    };

    public static (int Title, int Body, int Tags) LimitsOf(SocialChannel c) => c switch
    {
        SocialChannel.YouTube => (100, 5000, 15),
        SocialChannel.Instagram => (60, 2200, 30),
        SocialChannel.TikTok => (150, 2200, 8),
        SocialChannel.NaverClip => (40, 1000, 10),
        _ => (100, 500, 10),
    };

    /// <summary>스토리보드 → 요청(대본 = 컷 VoiceText 우선, 없으면 컷 프롬프트 · 상한 절단).</summary>
    public static SocialMetadataRequest FromProject(StoryboardProject project, string? themeCategory = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var sb = new StringBuilder(512);
        foreach (var s in project.Scenes)
        {
            var line = string.IsNullOrWhiteSpace(s.VoiceText) ? s.Prompt : s.VoiceText;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.Trim());
            if (sb.Length >= MaxScriptChars) break;
        }
        if (sb.Length > MaxScriptChars) sb.Length = MaxScriptChars;
        var topic = string.IsNullOrWhiteSpace(project.CoverTitle) ? project.Title : project.CoverTitle;
        return new SocialMetadataRequest(topic ?? "", sb.ToString(), themeCategory);
    }

    /// <summary>Gemini 지시문(Strict JSON 스키마 명시 · 순수 함수).</summary>
    public static string BuildPrompt(SocialMetadataRequest req, IReadOnlyList<SocialChannel> channels)
    {
        var script = req.Script ?? "";
        if (script.Length > MaxScriptChars) script = script[..MaxScriptChars];
        var sb = new StringBuilder(1024 + script.Length);
        sb.Append("당신은 아동·육아 숏폼 채널의 소셜 미디어 마케팅 카피라이터입니다. 아래 영상 정보로 플랫폼별 업로드 메타데이터를 한국어로 작성하세요.\n")
          .Append("주제: ").Append(Quote(req.Topic)).Append('\n')
          .Append("테마 카테고리: ").Append(Quote(string.IsNullOrWhiteSpace(req.ThemeCategory) ? "일반" : req.ThemeCategory)).Append('\n')
          .Append("대본:\n").Append(Quote(script)).Append("\n\n")
          .Append("플랫폼 규칙:\n");
        foreach (var c in channels)
        {
            var (t, b, n) = LimitsOf(c);
            sb.Append("- ").Append(KeyOf(c)).Append(": 제목 ").Append(t).Append("자 이하, 본문 ").Append(b).Append("자 이하, 해시태그 ").Append(n).Append("개 이하 · ")
              .Append(c switch
              {
                  SocialChannel.YouTube => "검색 키워드를 앞에 둔 정보형 제목, 설명란 첫 줄 요약",
                  SocialChannel.Instagram => "감성·공감 캡션, 댓글 유도 질문, 이모지 적당히",
                  SocialChannel.TikTok => "첫 1초 훅 문장, 짧고 트렌디하게, fyp 계열 태그 포함",
                  SocialChannel.NaverClip => "네이버 검색 키워드 중심 짧은 제목",
                  _ => "에버그린 검색 키워드 핀 제목과 설명",
              }).Append('\n');
        }
        sb.Append("아동 안전: 과장·공포·선정 표현 금지, 실존 인물/브랜드 사칭 금지.\n")
          .Append("아래 Strict JSON 으로만 응답(마크다운·설명 금지). 해시태그는 # 없이 단어만:\n{");
        for (var i = 0; i < channels.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(KeyOf(channels[i])).Append("\":{\"title\":\"\",\"description\":\"\",\"hashtags\":[\"\"]}");
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 채널별 메타데이터 생성(예외 0). AI 결과가 유효한 채널만 AI 로 채우고 나머지는 오프라인 폴백.
    /// </summary>
    public async Task<SocialMetadataResult> GenerateAsync(SocialMetadataRequest req, IReadOnlyList<SocialChannel> channels,
        StoryboardProject? project = null, bool useAi = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        var posts = new Dictionary<SocialChannel, PlatformMeta>(channels.Count);
        var note = "오프라인 SEO";
        if (useAi && AiEnabled && channels.Count > 0)
        {
            try
            {
                var res = await _generate!(BuildPrompt(req, channels), ct).ConfigureAwait(false);
                if (res.Ok && res.Text is not null)
                {
                    var parsed = Parse(res.Text, channels);
                    foreach (var kv in parsed) posts[kv.Key] = kv.Value;
                    note = parsed.Count == channels.Count ? "Gemini AI" : parsed.Count > 0 ? "Gemini AI 일부 + 오프라인 폴백" : "AI 응답 검증 실패 → 오프라인 폴백";
                }
                else note = "AI 실패(" + res.Message + ") → 오프라인 폴백";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Program.Log("SocialMetadataAi.Generate", ex); note = "AI 예외 → 오프라인 폴백"; }
        }
        var ai = posts.Count;
        IReadOnlyList<PlatformMeta>? offline = null;
        foreach (var c in channels)
        {
            if (posts.ContainsKey(c)) continue;
            if (offline is null && project is not null && _offline is not null)
            {
                try { offline = _offline.PackageAll(project); } catch (Exception ex) { Program.Log("SocialMetadataAi.Offline", ex); offline = Array.Empty<PlatformMeta>(); }
            }
            posts[c] = FromOffline(c, offline) ?? Template(c, req);
        }
        return new SocialMetadataResult(posts, ai, note);
    }

    /// <summary>Gemini JSON 파싱 + 스키마 검증 — 유효 채널만 반환(순수 함수).</summary>
    public static Dictionary<SocialChannel, PlatformMeta> Parse(string text, IReadOnlyList<SocialChannel> channels)
    {
        var map = new Dictionary<SocialChannel, PlatformMeta>(channels.Count);
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(text));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var c in channels)
            {
                if (!doc.RootElement.TryGetProperty(KeyOf(c), out var o) || o.ValueKind != JsonValueKind.Object) continue;
                if (!o.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String) continue;
                if (!o.TryGetProperty("description", out var d) || d.ValueKind != JsonValueKind.String) continue;
                if (!o.TryGetProperty("hashtags", out var h) || h.ValueKind != JsonValueKind.Array) continue;
                var (tl, bl, nl) = LimitsOf(c);
                var title = Cap(t.GetString(), tl);
                var body = Cap(d.GetString(), bl);
                if (title.Length == 0 && body.Length == 0) continue;
                var tags = new List<string>(nl);
                foreach (var e in h.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) AddTag(tags, e.GetString(), nl);
                map[c] = new PlatformMeta(DisplayOf(c), IconOf(c), title, body, tags);
            }
        }
        catch (JsonException) { /* 비-JSON → 전 채널 폴백 */ }
        return map;
    }

    /// <summary>해시태그 정규화 추가 — '#' · 공백 · 기호 제거(문자/숫자/_ 만) · 대소문자 무시 중복 제거 · 30자 · 상한.</summary>
    public static bool AddTag(List<string> tags, string? raw, int max)
    {
        if (tags.Count >= max || string.IsNullOrWhiteSpace(raw)) return false;
        Span<char> buf = stackalloc char[30];
        var n = 0;
        foreach (var ch in raw)
            if ((char.IsLetterOrDigit(ch) || ch == '_') && n < buf.Length) buf[n++] = ch;
        if (n == 0) return false;
        var tag = new string(buf[..n]);
        foreach (var t in tags) if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) return false;
        tags.Add(tag);
        return true;
    }

    /// <summary>업로드용 메타데이터 텍스트(.txt) — 플랫폼/규격 헤더 + 제목 + 본문 + 해시태그.</summary>
    public static string ToText(PlatformMeta meta, string? header = null)
    {
        var sb = new StringBuilder(256 + meta.Body.Length);
        if (!string.IsNullOrWhiteSpace(header)) sb.Append(header).Append("\r\n\r\n");
        sb.Append("[제목]\r\n").Append(meta.Title).Append("\r\n\r\n")
          .Append("[본문]\r\n").Append(meta.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n\r\n")
          .Append("[해시태그]\r\n");
        for (var i = 0; i < meta.Hashtags.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append('#').Append(meta.Hashtags[i]);
        }
        return sb.Append("\r\n").ToString();
    }

    private static PlatformMeta? FromOffline(SocialChannel c, IReadOnlyList<PlatformMeta>? offline)
    {
        if (offline is null) return null;
        var name = DisplayOf(c);
        foreach (var m in offline)
        {
            if (!string.Equals(m.Platform, name, StringComparison.Ordinal)) continue;
            var (tl, bl, nl) = LimitsOf(c);
            var tags = new List<string>(nl);
            foreach (var t in m.Hashtags) AddTag(tags, t, nl);
            return new PlatformMeta(name, IconOf(c), Cap(m.Title, tl), Cap(m.Body, bl), tags);
        }
        return null;
    }

    // 최후 폴백 — 주제/카테고리 기반 로컬 템플릿(항상 성공).
    private static PlatformMeta Template(SocialChannel c, SocialMetadataRequest req)
    {
        var (tl, bl, nl) = LimitsOf(c);
        var topic = string.IsNullOrWhiteSpace(req.Topic) ? "우리 아기 동화" : req.Topic.Trim();
        var tags = new List<string>(nl);
        AddTag(tags, topic, nl);
        if (!string.IsNullOrWhiteSpace(req.ThemeCategory)) AddTag(tags, req.ThemeCategory, nl);
        foreach (var t in c switch
        {
            SocialChannel.YouTube => new[] { "shorts", "동화", "아기", "육아" },
            SocialChannel.Instagram => new[] { "육아스타그램", "아기스타그램", "동화", "reels" },
            SocialChannel.TikTok => new[] { "fyp", "foryou", "동화", "아기" },
            SocialChannel.NaverClip => new[] { "네이버클립", "동화", "육아" },
            _ => new[] { "baby", "storytime", "parenting" },
        }) AddTag(tags, t, nl);
        var script = (req.Script ?? "").Trim();
        var firstLine = script.Length == 0 ? "" : script.Split('\n')[0];
        var body = c == SocialChannel.Instagram ? firstLine + "\n\n여러분 아이는 어떤 장면을 제일 좋아했나요? 댓글로 알려주세요 💬" : firstLine;
        return new PlatformMeta(DisplayOf(c), IconOf(c), Cap(c == SocialChannel.TikTok ? topic + " ✨" : topic, tl), Cap(body.Trim(), bl), tags);
    }

    private static string Cap(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..max].TrimEnd();
    }

    // 지시문 인용 — 사용자 입력의 따옴표/개행 정리(프롬프트 구조 보존).
    private static string Quote(string? s) => "\"" + (s ?? "").Replace("\"", "'", StringComparison.Ordinal).Trim() + "\"";
}
