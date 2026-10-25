using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [v18.0.4] ④ 인출 결과 스냅샷 — fMP4 · 썸네일 PNG 는 오케스트레이터 버퍼를 <see cref="ReadOnlyMemory{T}"/> 로 공유(복사 0).
///   <see cref="Generation"/> = 캡처 시점 렌더 번호 — 다음 렌더가 같은 버퍼를 덮어쓰면 <see cref="IsLive"/> 가 false.
/// </summary>
public readonly struct ShortsRenderResult
{
    public const int OutputWidth = 1080, OutputHeight = 1920;

    public readonly ReadOnlyMemory<byte> Video, ThumbnailPng;
    public readonly string? ThumbnailDataUrl, SourceName;
    public readonly long DurationUs, Generation;
    public readonly int ClipInMs, ClipOutMs, ThumbnailOffsetMs, Width, Height, VideoSamples, AudioSamples;
    public readonly ulong Hash;
    public readonly ShortsFactoryOneClickRenderOrchestrator? Owner;

    public ShortsRenderResult(ReadOnlyMemory<byte> video, ReadOnlyMemory<byte> thumbnailPng, string? thumbnailDataUrl, string? sourceName,
        long durationUs, int clipInMs, int clipOutMs, int thumbnailOffsetMs, int width, int height, int videoSamples, int audioSamples,
        ulong hash, long generation, ShortsFactoryOneClickRenderOrchestrator? owner)
    {
        Video = video; ThumbnailPng = thumbnailPng; ThumbnailDataUrl = thumbnailDataUrl; SourceName = sourceName;
        DurationUs = durationUs; ClipInMs = clipInMs; ClipOutMs = clipOutMs; ThumbnailOffsetMs = thumbnailOffsetMs;
        Width = width; Height = height; VideoSamples = videoSamples; AudioSamples = audioSamples;
        Hash = hash; Generation = generation; Owner = owner;
    }

    public int Bytes => Video.Length;
    public int DurationMs => (int)(DurationUs / 1000);
    /// <summary>9:16 세로 규격(1080×1920 표시 크기).</summary>
    public bool IsVertical => Width > 0 && (long)Width * 16 == (long)Height * 9;
    /// <summary>공유 버퍼가 아직 이 렌더의 바이트인지(재렌더 · 렌더 진행 중이면 false · 소유자 없는 외부 버퍼는 항상 true).</summary>
    public bool IsLive => !Video.IsEmpty && (Owner is null || (!Owner.Busy && Owner.Renders == Generation));

    /// <summary>오케스트레이터 최신 결과 → 스냅샷(무할당 · 썸네일 오프셋 = 클립 시작 기준 ms).</summary>
    public static ShortsRenderResult Capture(ShortsFactoryOneClickRenderOrchestrator o, ShortsFactoryWorkspaceContext ctx)
    {
        var ex = o.LastExport;
        var offset = Math.Clamp(ctx.ThumbnailAtMs - ctx.ClipInMs, 0, Math.Max(0, (int)(ex.DurationUs / 1000)));
        return new ShortsRenderResult(o.Output, o.ThumbnailPng, o.ThumbnailDataUrl, o.SourceName, ex.DurationUs, ctx.ClipInMs, ctx.ClipOutMs, offset,
            OutputWidth, OutputHeight, ex.VideoSamples, ex.AudioSamples, ex.Hash, o.Renders, o);
    }
}

/// <summary>[v18.0.4] 퍼블리셔 인계 페이로드 — 렌더 스냅샷 + 제목 · 해시태그(공백 구분 "#a #b") + 인계 순번.</summary>
public readonly struct SocialPublishPayload
{
    public readonly ShortsRenderResult Render;
    public readonly string Title, Hashtags;
    public readonly long Sequence;

    public SocialPublishPayload(in ShortsRenderResult render, string title, string hashtags, long sequence)
    {
        Render = render; Title = title; Hashtags = hashtags; Sequence = sequence;
    }

    public bool IsEmpty => Sequence == 0;
    public ReadOnlyMemory<byte> Video => Render.Video;
    public int Bytes => Render.Bytes;

    public SocialPublishPayload WithMetadata(string title, string hashtags) => new(Render, title, hashtags, Sequence);
}

/// <summary>
/// [PromptFlow Studio v18.0.4] 🚀 숏폼 팩토리 → 소셜 퍼블리셔 인메모리 핸드오프 브리지 — 수동 재임포트 제거.
///   ▸ <see cref="Push"/>: 고정 용량 링(<see cref="Capacity"/>)에 구조체 기록 — fMP4/PNG 는 슬라이스 공유(0 B 할당) · 초과 시 가장 오래된 것 폐기.
///   ▸ <see cref="Hydrate"/>: 퍼블리셔 모달 활성 시 대기분 소비 → 최신 1 건이 <see cref="Current"/>(탭 전환 · 재마운트에도 유지).
///   ▸ <see cref="OnHandoff"/>: 인계 통지(구독자 예외 격리) · <see cref="AssignVariantA"/>: ③ A/B 기본 슬롯(Variant A) 지정.
/// </summary>
public sealed class ShortsToPublisherHandoffBridge
{
    public const int Capacity = 4;
    /// <summary>소셜 퍼블리셔 탭 좌표 — ② 다채널 변형 배포(인계 자동 진입) · ③ A/B 레이스(Variant A 자동 지정).</summary>
    public const int PublisherDeployTab = 1, PublisherAbTab = 2;

    private readonly object _gate = new();
    private readonly SocialPublishPayload[] _ring = new SocialPublishPayload[Capacity];
    private int _head, _count;
    private long _seq, _handoffs, _consumed, _dropped, _rejected;
    private SocialPublishPayload _current;
    private long _variantA;

    public event Action? OnHandoff;

    public int Pending { get { lock (_gate) return _count; } }
    public long Handoffs => Interlocked.Read(ref _handoffs);
    public long Consumed => Interlocked.Read(ref _consumed);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Rejected => Interlocked.Read(ref _rejected);
    public SocialPublishPayload Current { get { lock (_gate) return _current; } }
    public bool HasCurrent { get { lock (_gate) return !_current.IsEmpty; } }
    /// <summary>③ A/B 레이스 Variant A 슬롯에 지정된 인계 순번(0 = 미지정).</summary>
    public long VariantASequence => Interlocked.Read(ref _variantA);
    public bool CurrentIsVariantA { get { lock (_gate) return !_current.IsEmpty && _current.Sequence == _variantA; } }

    /// <summary>렌더 스냅샷 인계 — 비어 있거나 낡은(재렌더된) 버퍼는 거부. 성공 시 통지.</summary>
    public bool Push(in ShortsRenderResult render, string title, string hashtags)
    {
        if (!render.IsLive) { Interlocked.Increment(ref _rejected); return false; }
        lock (_gate)
        {
            if (_count == Capacity)
            {
                _ring[_head] = default;
                _head = (_head + 1) % Capacity;
                _count--;
                _dropped++;
            }
            _ring[(_head + _count) % Capacity] = new SocialPublishPayload(render, title ?? "", hashtags ?? "", ++_seq);
            _count++;
            _handoffs++;
        }
        Notify();
        return true;
    }

    /// <summary>대기 중인 가장 오래된 페이로드 1 건 소비(FIFO) — <see cref="Current"/> 로 승격.</summary>
    public bool TryConsume(out SocialPublishPayload payload)
    {
        lock (_gate)
        {
            if (_count == 0) { payload = default; return false; }
            payload = _ring[_head];
            _ring[_head] = default;
            _head = (_head + 1) % Capacity;
            _count--;
            _consumed++;
            _current = payload;
            return true;
        }
    }

    /// <summary>대기분 전부 소비 → 최신 1 건을 현재 바인딩으로(새 인계가 있었으면 true).</summary>
    public bool Hydrate()
    {
        var any = false;
        while (TryConsume(out _)) any = true;
        return any;
    }

    /// <summary>퍼블리셔 폼에서 수정한 제목 · 해시태그를 현재 바인딩에 반영(버퍼 공유 유지).</summary>
    public bool UpdateMetadata(string title, string hashtags)
    {
        lock (_gate)
        {
            if (_current.IsEmpty) return false;
            _current = _current.WithMetadata(title ?? "", hashtags ?? "");
            return true;
        }
    }

    /// <summary>현재 인계 영상을 A/B 레이스 기본 슬롯(Variant A)으로 지정.</summary>
    public bool AssignVariantA()
    {
        lock (_gate)
        {
            if (_current.IsEmpty) return false;
            _variantA = _current.Sequence;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _head = _count = 0;
            _current = default;
            _variantA = 0;
        }
    }

    private void Notify()
    {
        try { OnHandoff?.Invoke(); }
        catch (Exception) { /* 구독자 예외 격리 — 인계는 이미 기록 */ }
    }

    // ══════════════════════ 메타데이터 생성(UI 1 회 · 비핫패스) ══════════════════════

    /// <summary>제목 — 절삭 대본 앞 6 단어 → 없으면 원본 파일명 → 기본값 · YouTube #Shorts 보강(100자).</summary>
    public static string ComposeTitle(string? script, string? sourceName)
    {
        var t = FirstWords(script, 6);
        if (t.Length == 0 && !string.IsNullOrWhiteSpace(sourceName)) t = Path.GetFileNameWithoutExtension(sourceName).Trim();
        if (t.Length == 0) t = "숏폼 하이라이트";
        return DirectSocialPublisherBridge.ShortsTitle(t);
    }

    /// <summary>해시태그 — #shorts #reels + 대본 핵심어(2자 이상 · 앞 3 개) + 더빙 언어 · 중복 제거.</summary>
    public static string ComposeHashtags(string? script, int dubLanguageMask)
    {
        var tags = new List<string>(8) { "shorts", "reels" };
        if (!string.IsNullOrWhiteSpace(script))
        {
            var picked = 0;
            foreach (var w in script.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var t = w.Trim('#', '.', ',', '!', '?', '"', '\'');
                if (t.Length < 2) continue;
                tags.Add(t);
                if (++picked == 3) break;
            }
        }
        if ((dubLanguageMask & ShortsFactoryWorkspaceContext.LangEn) != 0) tags.Add("english");
        if ((dubLanguageMask & ShortsFactoryWorkspaceContext.LangEs) != 0) tags.Add("español");
        if ((dubLanguageMask & ShortsFactoryWorkspaceContext.LangJa) != 0) tags.Add("日本語");
        return DirectSocialPublisherBridge.BuildCaption("", tags, 300);
    }

    private static string FirstWords(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var span = s.AsSpan().Trim();
        var end = 0;
        for (var n = 0; n < max && end < span.Length; n++)
        {
            var sp = span[end..].IndexOf(' ');
            end = sp < 0 ? span.Length : end + sp + 1;
        }
        return span[..end].Trim().ToString();
    }
}
