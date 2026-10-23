namespace PromptFlowStudio.Services.GoldenHourEngagement;

public enum ChannelTone : byte { Friendly, Informative, Playful }

/// <summary>
/// [PromptFlow Studio v17.95.0] 📌 골든타임 고정 댓글 &amp; 맞춤 답글 생성기.
///   ▸ 업로드 후 <see cref="PinWindowSec"/>(60 s) 이내 시청자 투표/의견을 묻는 앵커 댓글을 게이트웨이로 고정 — 실패 시 다음 틱 재시도, 창 초과 발행은 지연으로 기록.
///   ▸ 질문 · 긍정 댓글에 채널 톤(공감형 · 안내형 · 유쾌형) 정적 조각을 고정 char 버퍼(<see cref="ReplyCap"/>)에 이어 붙여 답글 조합 — 조합 경로 힙 할당 0.
/// </summary>
public sealed class GoldenHourSmartEngagementResponder
{
    public const int PinWindowSec = 60, ReplyCap = 280, AuthorCap = 20;
    public const string DefaultPinnedTemplate = "📌 여러분의 선택은? 🅰️ 제작 비하인드 vs 🅱️ 실전 따라하기 튜토리얼 — 댓글로 A/B 투표해 주세요! 가장 많은 공감을 받은 의견은 다음 영상에 그대로 반영합니다 🙌";

    // [톤][변형] — 긍정 호응
    private static readonly string[][] PositiveLines =
    {
        new[] { "봐주셔서 정말 감사해요! 🙏", "따뜻한 말씀 덕분에 힘이 나요 ❤️", "이런 댓글이 제일 행복해요 😊" },
        new[] { "시청해 주셔서 감사합니다. 다음 편도 준비 중이에요 📅", "응원 감사합니다! 고정 댓글 투표에도 참여해 주세요 📌", "피드백 감사드려요. 더 알찬 내용으로 찾아올게요 ✅" },
        new[] { "와 센스 만점 댓글 인정 🔥", "이 댓글 고정각인데요? 😎", "덕분에 오늘 하루 완성 🎉" },
    };

    // [톤][변형] — 질문 오프너 + [톤] 안내 본문
    private static readonly string[][] QuestionOpeners =
    {
        new[] { "좋은 질문 감사해요! ", "궁금하셨군요 😊 ", "물어봐 주셔서 반가워요! " },
        new[] { "질문 감사합니다. ", "문의 주신 부분 안내드려요. ", "정확한 답변 드릴게요. " },
        new[] { "오 날카로운 질문! ", "그 질문 기다렸습니다 😎 ", "역시 찐팬의 질문 🔥 " },
    };

    private static readonly string[] QuestionBodies =
    {
        "자세한 내용은 설명란 타임스탬프에 정리해 뒀어요 🙌",
        "설명란 링크와 고정 댓글에 단계별로 안내해 두었습니다 📎",
        "정답은 설명란에 숨겨 놨어요 🕵️ 못 찾으시면 다시 불러 주세요!",
    };

    private readonly char[] _buf = new char[ReplyCap];
    private int _len;
    private string _template = DefaultPinnedTemplate;

    public ChannelTone Tone { get; set; } = ChannelTone.Friendly;
    public double UploadUnix { get; private set; } = -1;
    public double PinnedAtUnix { get; private set; } = -1;
    public bool Uploaded => UploadUnix >= 0;
    public bool Pinned => PinnedAtUnix >= 0;
    public bool PinnedInWindow => Pinned && PinDelaySec <= PinWindowSec;
    public double PinDelaySec => Pinned ? PinnedAtUnix - UploadUnix : 0;
    public int PinAttempts { get; private set; }
    public int LastPinStatus { get; private set; }
    public long Composed { get; private set; }
    public ReadOnlySpan<char> LastReply => _buf.AsSpan(0, _len);

    /// <summary>고정 댓글 템플릿 — 공백 입력은 기본 템플릿으로 복원, 상한 <see cref="ReplyCap"/> 자.</summary>
    public string PinnedTemplate
    {
        get => _template;
        set => _template = string.IsNullOrWhiteSpace(value) ? DefaultPinnedTemplate : value.Length > ReplyCap ? value[..ReplyCap] : value;
    }

    public static string ToneName(ChannelTone t) => t switch
    {
        ChannelTone.Informative => "안내형",
        ChannelTone.Playful => "유쾌형",
        _ => "공감형",
    };

    public void MarkUploaded(double nowUnix)
    {
        UploadUnix = nowUnix;
        PinnedAtUnix = -1;
        PinAttempts = 0;
        LastPinStatus = 0;
    }

    /// <summary>업로드 후 미고정 상태면 고정 시도(창 초과여도 지연 발행). 반환 = 이번 호출로 고정 성공.</summary>
    public bool TryPublishPinned(double nowUnix, ICommentModerationGateway gateway)
    {
        if (!Uploaded || Pinned) return false;
        PinAttempts++;
        var st = 0;
        try { st = gateway.PinComment(_template.AsSpan()); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("GoldenHourEngagement.Pin", ex); st = 599; }
        LastPinStatus = st;
        if (st is < 200 or >= 300) return false;
        PinnedAtUnix = nowUnix;
        return true;
    }

    /// <summary>강제 재발행(템플릿 수정 반영) — 고정 시각 초기화 후 즉시 시도.</summary>
    public bool Republish(double nowUnix, ICommentModerationGateway gateway)
    {
        if (!Uploaded) return false;
        PinnedAtUnix = -1;
        return TryPublishPinned(nowUnix, gateway);
    }

    /// <summary>질문 · 긍정 댓글 답글을 내부 버퍼에 조합(무할당). 반환 = 답글 대상 여부.</summary>
    public bool TryCompose(CommentSentiment sentiment, long commentId, ReadOnlySpan<char> author)
    {
        _len = 0;
        if (sentiment is not (CommentSentiment.Question or CommentSentiment.Positive)) return false;
        var tone = (int)Tone;
        var variant = (int)((ulong)commentId % 3);
        Put("@");
        Put(author.Length > AuthorCap ? author[..AuthorCap] : author);
        Put(" ");
        if (sentiment == CommentSentiment.Question)
        {
            Put(QuestionOpeners[tone][variant]);
            Put(QuestionBodies[tone]);
        }
        else Put(PositiveLines[tone][variant]);
        Composed++;
        return true;
    }

    public void Reset()
    {
        UploadUnix = PinnedAtUnix = -1;
        PinAttempts = LastPinStatus = 0;
        Composed = 0;
        _len = 0;
    }

    private void Put(ReadOnlySpan<char> s)
    {
        var n = Math.Min(s.Length, ReplyCap - _len);
        s[..n].CopyTo(_buf.AsSpan(_len));
        _len += n;
    }
}
