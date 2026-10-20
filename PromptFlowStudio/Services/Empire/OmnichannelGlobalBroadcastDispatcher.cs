using System.Globalization;
using System.Text;

namespace PromptFlowStudio.Services.Empire;

public enum PlatformKind : byte { Vod, ShortForm, Live, Social, FastTv, Ott, WebXr, Podcast }

public enum IngestProtocol : byte { ApiUpload, Rtmp, Srt, Hls, Rss }

public enum ChannelState : byte { Idle, Encoding, Queued, Sending, Live, Delivered, Backoff, Failed, Skipped }

/// <summary>플랫폼 프로파일(규격 · 메타데이터 한도 · 프로토콜 · 피크 시각/시간대).</summary>
public sealed record PlatformProfile(string Id, string Name, PlatformKind Kind, string Region, string Language, int Width, int Height, int Fps, int MaxKbps,
    int MaxDurationSec, int TitleMax, int TagMax, IngestProtocol Protocol, int PeakHourLocal, int UtcOffsetMin, double BaseEcpmUsd)
{
    public bool Vertical => Height > Width;
    public bool IsLiveIngest => Protocol is IngestProtocol.Rtmp or IngestProtocol.Srt;
    public string CredentialVar => "PFS_EMPIRE_KEY_" + Id.ToUpperInvariant().Replace('-', '_');
}

/// <summary>공유 인코딩 렌디션(해상도 · fps · 비트레이트 버킷) — 동일 규격 플랫폼끼리 1회 인코딩 공유.</summary>
public readonly record struct Rendition(int Width, int Height, int Fps, int Kbps, int Platforms);

/// <summary>분배 원본 패키지.</summary>
public sealed record DispatchPackage(string AssetId, string Title, string Description, IReadOnlyList<string> Tags, double DurationSec, string Language, bool Live);

/// <summary>플랫폼별 최적화 메타데이터 + 예약 시각.</summary>
public sealed record PlatformMetadata(string Title, string Description, IReadOnlyList<string> Tags, DateTimeOffset ScheduledUtc, double ClipSec, int RenditionIndex);

/// <summary>플랫폼 송출 결과(시도 · 페일오버 · 사유).</summary>
public readonly record struct DispatchResult(int Slot, bool Ok, int Attempts, bool UsedBackup, bool DryRun, string Reason);

/// <summary>
/// [PromptFlow Studio v16.50.0] 🌐 옴니채널 글로벌 멀티 플랫폼 자율 분배 마스터.
///   ▸ 52개 플랫폼 카탈로그(VOD · 숏폼 · 라이브 · 소셜 · FAST TV · OTT · WebXR · 팟캐스트) · 규격 기반 렌디션 래더 공유(동시 인코딩 횟수 최소화).
///   ▸ 메타데이터 자율 최적화: 제목 문자소 경계 절단 · 숏폼 훅 접두 · 해시태그 정규화/중복 제거/상한 · 길이 한도 클립 · 지역 피크 시각 예약(시간대 · 분 단위 분산).
///   ▸ 무중단 병렬 송출: SemaphoreSlim 동시성 상한 · 플랫폼별 데드라인 · 지수 백오프 재시도 · 백업 인게스트 페일오버 · 자격 증명 = 환경변수(없으면 드라이런) · 부분 실패 격리.
///   ▸ 라이브 헬스 Tick: 하트비트 정지 감지 → 백업 재연결 · 비트레이트 집계 — 무할당.
/// </summary>
public sealed class OmnichannelGlobalBroadcastDispatcher
{
    public const int MaxRetries = 3;

    private readonly PlatformProfile[] _platforms;
    private readonly ChannelState[] _state;
    private readonly bool[] _enabled, _backup;
    private readonly int[] _attempts;
    private readonly double[] _beat, _kbps, _deliveredAt;
    private readonly long[] _bytes;
    private readonly Func<int, DispatchPackage, PlatformMetadata, bool, CancellationToken, Task<bool>> _transport;
    private readonly Func<string, string?> _env;
    private long _dispatches, _delivered, _failed, _failovers, _reconnects, _encodes;

    public OmnichannelGlobalBroadcastDispatcher() : this(null, null) { }

    public OmnichannelGlobalBroadcastDispatcher(Func<int, DispatchPackage, PlatformMetadata, bool, CancellationToken, Task<bool>>? transport, Func<string, string?>? env)
    {
        _platforms = Catalog();
        var n = _platforms.Length;
        _state = new ChannelState[n]; _enabled = new bool[n]; _backup = new bool[n]; _attempts = new int[n];
        _beat = new double[n]; _kbps = new double[n]; _deliveredAt = new double[n]; _bytes = new long[n];
        Array.Fill(_enabled, true);
        _transport = transport ?? SimulatedTransport;
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    public event Action? OnChange;
    public event Action<DispatchPackage, DispatchResult>? OnDelivered;

    public int MaxParallel { get; set; } = 12;
    public int DeadlineMs { get; set; } = 8000;
    public int BackoffBaseMs { get; set; } = 400;
    public double StallMs { get; set; } = 3000;
    public double SimulatedFailRate { get; set; } = 0.06;
    public int PlatformCount => _platforms.Length;
    public PlatformProfile Platform(int i) => _platforms[i];
    public ChannelState State(int i) => _state[i];
    public bool Enabled(int i) => _enabled[i];
    public void SetEnabled(int i, bool on) { _enabled[i] = on; Raise(); }
    public bool OnBackup(int i) => _backup[i];
    public double Kbps(int i) => _kbps[i];
    public long BytesSent(int i) => Interlocked.Read(ref _bytes[i]);
    public long Dispatches => Interlocked.Read(ref _dispatches);
    public long Delivered => Interlocked.Read(ref _delivered);
    public long Failed => Interlocked.Read(ref _failed);
    public long Failovers => Interlocked.Read(ref _failovers);
    public long Reconnects => Interlocked.Read(ref _reconnects);
    public long Encodes => Interlocked.Read(ref _encodes);
    public string? LastAction { get; private set; }

    public int LiveCount { get { var n = 0; foreach (var s in _state) if (s == ChannelState.Live) n++; return n; } }
    public int EnabledCount { get { var n = 0; foreach (var e in _enabled) if (e) n++; return n; } }

    // ══════════════════════ 렌디션 래더 ══════════════════════

    public static int KbpsBucket(int kbps) => kbps <= 2500 ? 2500 : kbps <= 4500 ? 4500 : kbps <= 6000 ? 6000 : kbps <= 9000 ? 9000 : 16000;

    /// <summary>활성 플랫폼 규격을 (W,H,fps,버킷)으로 묶어 공유 렌디션 산출. renditionOf[i] = 플랫폼 i 렌디션 인덱스(비활성 -1).</summary>
    public List<Rendition> PlanRenditions(Span<int> renditionOf)
    {
        var list = new List<Rendition>(16);
        for (var i = 0; i < _platforms.Length; i++)
        {
            renditionOf[i] = -1;
            if (!_enabled[i]) continue;
            var p = _platforms[i];
            var kb = Math.Min(KbpsBucket(p.MaxKbps), p.MaxKbps);
            var idx = -1;
            for (var r = 0; r < list.Count; r++) { var x = list[r]; if (x.Width == p.Width && x.Height == p.Height && x.Fps == p.Fps && KbpsBucket(x.Kbps) == KbpsBucket(kb)) { idx = r; break; } }
            if (idx < 0) { list.Add(new Rendition(p.Width, p.Height, p.Fps, kb, 1)); idx = list.Count - 1; }
            else { var x = list[idx]; list[idx] = x with { Kbps = Math.Min(x.Kbps, kb), Platforms = x.Platforms + 1 }; }
            renditionOf[i] = idx;
        }
        return list;
    }

    // ══════════════════════ 메타데이터 최적화 ══════════════════════

    /// <summary>문자소(그래핌) 경계에서 최대 max 문자 이내로 절단, 잘리면 '…'.</summary>
    public static string TruncateGraphemes(string s, int max)
    {
        if (s.Length <= max) return s;
        if (max <= 1) return "…";
        var e = StringInfo.GetTextElementEnumerator(s);
        var end = 0;
        while (e.MoveNext())
        {
            var next = e.ElementIndex + ((string)e.Current).Length;
            if (next > max - 1) break;
            end = next;
        }
        return s[..end].TrimEnd() + "…";
    }

    /// <summary>해시태그 정규화(공백/기호 제거 · 소문자 키 중복 제거 · 상한).</summary>
    public static List<string> NormalizeTags(IReadOnlyList<string> tags, int max)
    {
        var res = new List<string>(Math.Min(tags.Count, Math.Max(0, max)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var t in tags)
        {
            if (res.Count >= max) break;
            sb.Clear();
            foreach (var c in t) if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            if (sb.Length == 0) continue;
            var tag = sb.ToString();
            if (seen.Add(tag)) res.Add(tag);
        }
        return res;
    }

    /// <summary>다음 지역 피크 시각(UTC) + 슬롯별 분 단위 분산(동시 몰림 방지).</summary>
    public static DateTimeOffset NextPeakUtc(DateTimeOffset nowUtc, PlatformProfile p, int slot)
    {
        var local = nowUtc.ToOffset(TimeSpan.FromMinutes(p.UtcOffsetMin));
        var peak = new DateTimeOffset(local.Year, local.Month, local.Day, p.PeakHourLocal, 0, 0, local.Offset).AddMinutes(slot % 6 * 5);
        if (peak <= local) peak = peak.AddDays(1);
        return peak.ToUniversalTime();
    }

    public PlatformMetadata OptimizeMetadata(int slot, DispatchPackage pkg, DateTimeOffset nowUtc, int rendition = -1)
    {
        var p = _platforms[slot];
        var title = pkg.Title.Trim();
        if (p.Kind == PlatformKind.ShortForm && !title.StartsWith("🔥", StringComparison.Ordinal)) title = "🔥 " + title;
        title = TruncateGraphemes(title, p.TitleMax);
        var tags = NormalizeTags(pkg.Tags, p.TagMax);
        var desc = pkg.Description.Trim();
        if (p.Kind is PlatformKind.ShortForm or PlatformKind.Social)
        {
            var sb = new StringBuilder(TruncateGraphemes(desc, 150));
            foreach (var t in tags) sb.Append(" #").Append(t);
            desc = sb.ToString();
        }
        else desc = TruncateGraphemes(desc, 4800);
        var clip = p.MaxDurationSec > 0 ? Math.Min(pkg.DurationSec, p.MaxDurationSec) : pkg.DurationSec;
        var when = pkg.Live || p.Kind is PlatformKind.Live or PlatformKind.FastTv ? nowUtc : NextPeakUtc(nowUtc, p, slot);
        return new PlatformMetadata(title, desc, tags, when, clip, rendition);
    }

    // ══════════════════════ 병렬 송출 ══════════════════════

    /// <summary>활성 플랫폼 전체로 동시 분배(렌디션 공유 인코딩 → 병렬 송출 · 재시도 · 백업 페일오버). 부분 실패는 격리.</summary>
    public async Task<DispatchResult[]> DispatchAsync(DispatchPackage pkg, DateTimeOffset nowUtc, double nowMs, CancellationToken ct = default)
    {
        var n = _platforms.Length;
        var rmap = new int[n];
        var ladder = PlanRenditions(rmap);
        Interlocked.Add(ref _encodes, ladder.Count);
        Interlocked.Increment(ref _dispatches);
        for (var i = 0; i < n; i++) _state[i] = _enabled[i] ? ChannelState.Encoding : ChannelState.Skipped;
        LastAction = "🎞 인코딩 " + ladder.Count.ToString(CultureInfo.InvariantCulture) + "종 → " + EnabledCount.ToString(CultureInfo.InvariantCulture) + "채널 · " + pkg.Title;
        Raise();
        using var gate = new SemaphoreSlim(Math.Max(1, MaxParallel));
        var tasks = new List<Task<DispatchResult>>(n);
        for (var i = 0; i < n; i++)
        {
            if (!_enabled[i]) continue;
            _state[i] = ChannelState.Queued;
            tasks.Add(SendOneAsync(i, pkg, OptimizeMetadata(i, pkg, nowUtc, rmap[i]), gate, nowMs, ct));
        }
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var ok = 0;
        foreach (var r in results) if (r.Ok) ok++;
        LastAction = "🌐 분배 완료 " + ok.ToString(CultureInfo.InvariantCulture) + "/" + results.Length.ToString(CultureInfo.InvariantCulture) + " · " + pkg.Title;
        Raise();
        return results;
    }

    private async Task<DispatchResult> SendOneAsync(int i, DispatchPackage pkg, PlatformMetadata meta, SemaphoreSlim gate, double nowMs, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var p = _platforms[i];
            var dry = string.IsNullOrEmpty(_env(p.CredentialVar));
            var useBackup = false;
            var reason = "";
            for (var attempt = 1; attempt <= MaxRetries + 1; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (attempt == MaxRetries + 1) { useBackup = true; Interlocked.Increment(ref _failovers); }
                _state[i] = ChannelState.Sending; _attempts[i] = attempt;
                bool ok;
                try
                {
                    using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    d.CancelAfter(DeadlineMs);
                    ok = await _transport(i, pkg, meta, useBackup, d.Token).ConfigureAwait(false);
                    reason = ok ? (useBackup ? "백업 인게스트" : "OK") : "거부";
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { ok = false; reason = "타임아웃"; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException) { ok = false; reason = ex.GetType().Name; }
                if (ok)
                {
                    _backup[i] = useBackup;
                    _state[i] = pkg.Live && (p.IsLiveIngest || p.Kind == PlatformKind.FastTv) ? ChannelState.Live : ChannelState.Delivered;
                    _beat[i] = nowMs; _deliveredAt[i] = nowMs;
                    Interlocked.Increment(ref _delivered);
                    var res = new DispatchResult(i, true, attempt, useBackup, dry, reason);
                    try { OnDelivered?.Invoke(pkg, res); } catch (Exception ex) { PromptFlowStudio.Program.Log("Omni.OnDelivered", ex); }
                    return res;
                }
                if (attempt <= MaxRetries)
                {
                    _state[i] = ChannelState.Backoff;
                    await Task.Delay(BackoffBaseMs << (attempt - 1), ct).ConfigureAwait(false);
                }
            }
            _state[i] = ChannelState.Failed;
            Interlocked.Increment(ref _failed);
            return new DispatchResult(i, false, MaxRetries + 1, true, dry, reason);
        }
        finally { gate.Release(); }
    }

    /// <summary>기본 전송(드라이런 시뮬레이션): 결정적 해시 기반 실패율 · 백업은 항상 성공.</summary>
    private Task<bool> SimulatedTransport(int slot, DispatchPackage pkg, PlatformMetadata meta, bool backup, CancellationToken ct)
    {
        if (backup) return Task.FromResult(true);
        var h = (uint)(slot * 2654435761u) ^ (uint)pkg.AssetId.GetHashCode(StringComparison.Ordinal) ^ (uint)(_attempts[slot] * 40503);
        return Task.FromResult((h % 1000) >= SimulatedFailRate * 1000);
    }

    // ══════════════════════ 라이브 헬스 ══════════════════════

    /// <summary>라이브 하트비트 수신(송출 kbps · 바이트). 무할당.</summary>
    public void Heartbeat(int slot, double nowMs, double kbps, long bytes)
    {
        if ((uint)slot >= (uint)_platforms.Length) return;
        _beat[slot] = nowMs; _kbps[slot] = kbps;
        Interlocked.Add(ref _bytes[slot], bytes);
    }

    /// <summary>라이브 정지 감지 → 백업 인게스트 재연결. 반환 = 재연결 수. 무할당.</summary>
    public int Tick(double nowMs)
    {
        var n = 0;
        for (var i = 0; i < _platforms.Length; i++)
        {
            if (_state[i] != ChannelState.Live || nowMs - _beat[i] <= StallMs) continue;
            _backup[i] = !_backup[i]; _beat[i] = nowMs; _kbps[i] = 0;
            Interlocked.Increment(ref _reconnects);
            n++;
        }
        if (n > 0) Raise();
        return n;
    }

    public void StopLive() { for (var i = 0; i < _state.Length; i++) if (_state[i] == ChannelState.Live) _state[i] = ChannelState.Delivered; Raise(); }

    public void ResetStates() { Array.Clear(_state); Array.Clear(_backup); Array.Clear(_kbps); Raise(); }

    public static string StateLabel(ChannelState s) => s switch
    {
        ChannelState.Encoding => "인코딩", ChannelState.Queued => "대기", ChannelState.Sending => "송출 중", ChannelState.Live => "LIVE", ChannelState.Delivered => "완료",
        ChannelState.Backoff => "재시도", ChannelState.Failed => "실패", ChannelState.Skipped => "제외", _ => "유휴",
    };

    public static string KindLabel(PlatformKind k) => k switch
    {
        PlatformKind.Vod => "VOD", PlatformKind.ShortForm => "숏폼", PlatformKind.Live => "라이브", PlatformKind.Social => "소셜", PlatformKind.FastTv => "FAST",
        PlatformKind.Ott => "OTT", PlatformKind.WebXr => "WebXR", _ => "팟캐스트",
    };

    /// <summary>53개 글로벌 플랫폼 카탈로그.</summary>
    public static PlatformProfile[] Catalog()
    {
        const int KST = 540, PST = -480, EST = -300, UTC = 0, CET = 60, JST = 540, IST = 330, BRT = -180, CST8 = 480, WIB = 420;
        static PlatformProfile V(string id, string n, string rg, string lang, int off, double cpm) => new(id, n, PlatformKind.Vod, rg, lang, 1920, 1080, 30, 8000, 0, 100, 15, IngestProtocol.ApiUpload, 19, off, cpm);
        static PlatformProfile S(string id, string n, string rg, string lang, int off, double cpm, int max = 60) => new(id, n, PlatformKind.ShortForm, rg, lang, 1080, 1920, 30, 6000, max, 80, 8, IngestProtocol.ApiUpload, 21, off, cpm);
        static PlatformProfile L(string id, string n, string rg, string lang, int off, double cpm, IngestProtocol pr = IngestProtocol.Rtmp, int kb = 6000) => new(id, n, PlatformKind.Live, rg, lang, 1920, 1080, 60, kb, 0, 140, 10, pr, 20, off, cpm);
        static PlatformProfile So(string id, string n, string rg, string lang, int off, double cpm, int w = 1080, int h = 1080) => new(id, n, PlatformKind.Social, rg, lang, w, h, 30, 4500, 140, 120, 5, IngestProtocol.ApiUpload, 12, off, cpm);
        static PlatformProfile F(string id, string n, string rg, int off, double cpm) => new(id, n, PlatformKind.FastTv, rg, "en", 1920, 1080, 30, 9000, 0, 60, 5, IngestProtocol.Hls, 20, off, cpm);
        static PlatformProfile O(string id, string n, string rg, string lang, int off, double cpm) => new(id, n, PlatformKind.Ott, rg, lang, 3840, 2160, 30, 16000, 0, 80, 10, IngestProtocol.ApiUpload, 21, off, cpm);
        static PlatformProfile X(string id, string n, int off, double cpm) => new(id, n, PlatformKind.WebXr, "GLOBAL", "en", 3840, 1920, 60, 16000, 0, 80, 10, IngestProtocol.Hls, 20, off, cpm);
        static PlatformProfile P(string id, string n, string rg, string lang, int off, double cpm) => new(id, n, PlatformKind.Podcast, rg, lang, 1280, 720, 30, 2500, 0, 120, 10, IngestProtocol.Rss, 7, off, cpm);
        return new[]
        {
            V("youtube", "YouTube", "GLOBAL", "en", PST, 7.5), V("naver-tv", "Naver TV", "KR", "ko", KST, 4.2), V("vimeo", "Vimeo", "GLOBAL", "en", EST, 3.0),
            V("dailymotion", "Dailymotion", "EU", "fr", CET, 2.8), V("bilibili", "Bilibili", "CN", "zh", CST8, 2.2), V("niconico", "Niconico", "JP", "ja", JST, 2.6),
            V("rumble", "Rumble", "US", "en", EST, 3.4), V("kakao-tv", "Kakao TV", "KR", "ko", KST, 3.8), V("youku", "Youku", "CN", "zh", CST8, 1.9), V("odysee", "Odysee", "GLOBAL", "en", UTC, 1.5),
            S("youtube-shorts", "YouTube Shorts", "GLOBAL", "en", PST, 0.6, 180), S("tiktok", "TikTok", "GLOBAL", "en", PST, 0.5, 600), S("ig-reels", "Instagram Reels", "GLOBAL", "en", PST, 0.9, 90),
            S("fb-reels", "Facebook Reels", "GLOBAL", "en", PST, 0.7, 90), S("snapchat", "Snapchat Spotlight", "US", "en", PST, 0.6), S("douyin", "Douyin", "CN", "zh", CST8, 0.4, 900),
            S("kuaishou", "Kuaishou", "CN", "zh", CST8, 0.3, 600), S("moj", "Moj", "IN", "hi", IST, 0.2), S("josh", "Josh", "IN", "hi", IST, 0.2), S("kwai", "Kwai", "BR", "pt", BRT, 0.3),
            S("naver-clip", "Naver Clip", "KR", "ko", KST, 0.5), S("pinterest-idea", "Pinterest Idea Pins", "GLOBAL", "en", PST, 0.8),
            L("youtube-live", "YouTube Live", "GLOBAL", "en", PST, 6.0), L("twitch", "Twitch", "GLOBAL", "en", PST, 5.5), L("chzzk", "CHZZK", "KR", "ko", KST, 4.0),
            L("soop", "SOOP(아프리카TV)", "KR", "ko", KST, 3.8), L("kick", "Kick", "GLOBAL", "en", EST, 3.0), L("tiktok-live", "TikTok LIVE", "GLOBAL", "en", PST, 2.0),
            L("fb-live", "Facebook Live", "GLOBAL", "en", PST, 3.2), L("bigo", "BIGO LIVE", "SEA", "en", WIB, 1.5), L("nimo", "Nimo TV", "SEA", "id", WIB, 1.2),
            L("trovo", "Trovo", "GLOBAL", "en", UTC, 1.4, IngestProtocol.Srt), L("linkedin-live", "LinkedIn Live", "GLOBAL", "en", EST, 8.0), L("x-live", "X Live", "GLOBAL", "en", EST, 2.4),
            So("instagram", "Instagram Feed", "GLOBAL", "en", PST, 1.1), So("facebook", "Facebook Feed", "GLOBAL", "en", PST, 1.0), So("x", "X", "GLOBAL", "en", EST, 0.9, 1920, 1080),
            So("linkedin", "LinkedIn", "GLOBAL", "en", EST, 3.5, 1920, 1080), So("threads", "Threads", "GLOBAL", "en", PST, 0.8),
            F("samsung-tvplus", "Samsung TV Plus", "GLOBAL", PST, 14.0), F("pluto", "Pluto TV", "US", PST, 13.0), F("roku", "The Roku Channel", "US", PST, 15.0),
            F("tubi", "Tubi", "US", PST, 12.5), F("lg-channels", "LG Channels", "GLOBAL", KST, 11.0), F("rakuten-tv", "Rakuten TV", "EU", CET, 10.0),
            O("netflix-partner", "Netflix 파트너 딜리버리", "GLOBAL", "en", PST, 20.0), O("tving", "TVING", "KR", "ko", KST, 9.0), O("wavve", "Wavve", "KR", "ko", KST, 8.5),
            O("prime-video-direct", "Prime Video Direct", "GLOBAL", "en", PST, 12.0),
            X("webxr-hub", "WebXR 홀로그램 허브", UTC, 6.0), X("vision-pro", "visionOS 공간 비디오", PST, 9.0),
            P("spotify-video", "Spotify Video Podcast", "GLOBAL", "en", UTC, 4.5), P("apple-podcasts", "Apple Podcasts", "GLOBAL", "en", PST, 4.0),
        };
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Omni.OnChange", ex); } }
}
