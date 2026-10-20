using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PromptFlowStudio.Services.Cinema;
using PromptFlowStudio.Services.Screenplay;

namespace PromptFlowStudio.Services.Mcr;

/// <summary>콘텐츠 구간(값형) — 긴장도 0~100 · 정서가(-1 슬픔 ~ 1 따뜻함).</summary>
public readonly record struct ContentBeat(double Start, double Duration, int Tension, float Valence);

/// <summary>광고 브레이크 후보(값형).</summary>
public readonly record struct AdBreakCandidate(double TimeSec, float Score, int TensionBefore, int TensionAfter, double DialogueGapSec);

public sealed record AdPolicy(double MinSpacingSec = 480, int MaxBreaks = 6, double BreakSeconds = 30, double GuardStartSec = 60, double GuardEndSec = 30,
    int ClimaxTension = 85, double MinDialogueGapSec = 1.0, float SpeechRms = 0.02f, double LivePauseSec = 1.5, int LiveMaxTension = 60);

public sealed record AdBreak(int Index, uint EventId, double TimeSec, double DurationSec, float Score, string Reason);

/// <summary>SCTE-35 splice_insert 디코드 결과(값형).</summary>
public readonly record struct SpliceInsertInfo(uint EventId, bool OutOfNetwork, bool HasTime, long PtsTime, bool HasDuration, long Duration, bool AutoReturn, ushort ProgramId, byte AvailNum, byte AvailsExpected);

/// <summary>SCTE-35(ANSI/SCTE 35) splice_info_section 인코더/디코더 — MPEG-2 CRC32 · 무할당.</summary>
public static class Scte35
{
    public const int MaxSectionBytes = 64;
    private static readonly uint[] CrcTable = BuildCrc();

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i << 24;
            for (var k = 0; k < 8; k++) c = (c & 0x80000000u) != 0 ? (c << 1) ^ 0x04C11DB7u : c << 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>CRC-32/MPEG-2(poly 0x04C11DB7 · init 0xFFFFFFFF · 무반사 · 무XOR).</summary>
    public static uint Crc32Mpeg2(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ b) & 0xFF];
        return crc;
    }

    /// <summary>splice_insert 섹션 인코딩(pts/duration 90kHz · null = 생략). 반환 = 바이트 수.</summary>
    public static int EncodeSpliceInsert(Span<byte> d, uint eventId, bool outOfNetwork, long? ptsTime, long? duration, ushort programId = 1, byte availNum = 0, byte availsExpected = 0, bool autoReturn = true)
    {
        var cmdLen = 4 + 1 + 1 + (ptsTime.HasValue ? 5 : 0) + (duration.HasValue ? 5 : 0) + 4;
        var total = 14 + cmdLen + 2 + 4;
        if (d.Length < total) return 0;
        var sectionLength = total - 3;
        d[0] = 0xFC;
        d[1] = (byte)(0x30 | ((sectionLength >> 8) & 0x0F));   // syntax 0 · private 0 · sap_type 11
        d[2] = (byte)sectionLength;
        d[3] = 0;                                              // protocol_version
        d[4] = 0; d[5] = 0; d[6] = 0; d[7] = 0; d[8] = 0;     // encrypted 0 · alg 0 · pts_adjustment 0
        d[9] = 0;                                              // cw_index
        d[10] = 0xFF;                                          // tier 0xFFF(12) + cmd length(12)
        d[11] = (byte)(0xF0 | ((cmdLen >> 8) & 0x0F));
        d[12] = (byte)cmdLen;
        d[13] = 0x05;                                          // splice_insert
        var p = 14;
        BinaryPrimitives.WriteUInt32BigEndian(d[p..], eventId); p += 4;
        d[p++] = 0x7F;                                         // cancel 0 · reserved
        d[p++] = (byte)((outOfNetwork ? 0x80 : 0) | 0x40 | (duration.HasValue ? 0x20 : 0) | (ptsTime.HasValue ? 0 : 0x10) | 0x0F);
        if (ptsTime is { } pts) { WriteTime(d[p..], 0xFE, pts); p += 5; }   // null = splice_immediate(splice_time 생략)
        if (duration is { } du) { WriteTime(d[p..], (byte)((autoReturn ? 0x80 : 0) | 0x7E), du); p += 5; }
        BinaryPrimitives.WriteUInt16BigEndian(d[p..], programId); p += 2;
        d[p++] = availNum; d[p++] = availsExpected;
        d[p++] = 0; d[p++] = 0;                                // descriptor_loop_length
        BinaryPrimitives.WriteUInt32BigEndian(d[p..], Crc32Mpeg2(d[..p])); p += 4;
        return p;

        static void WriteTime(Span<byte> t, byte flagsHigh, long v)
        {
            v &= 0x1FFFFFFFFL;
            t[0] = (byte)((flagsHigh & 0xFE) | (int)((v >> 32) & 1));
            BinaryPrimitives.WriteUInt32BigEndian(t[1..], (uint)v);
        }
    }

    /// <summary>splice_insert 섹션 디코딩(CRC 검증 · 무할당).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> s, out SpliceInsertInfo info)
    {
        info = default;
        if (s.Length < 18 || s[0] != 0xFC) return false;
        var sectionLength = ((s[1] & 0x0F) << 8) | s[2];
        var total = sectionLength + 3;
        if (total > s.Length || total < 18) return false;
        if (Crc32Mpeg2(s[..(total - 4)]) != BinaryPrimitives.ReadUInt32BigEndian(s[(total - 4)..])) return false;
        if ((s[4] & 0x80) != 0 || s[13] != 0x05) return false;
        var p = 14;
        var id = BinaryPrimitives.ReadUInt32BigEndian(s[p..]); p += 4;
        if ((s[p++] & 0x80) != 0) { info = new SpliceInsertInfo(id, false, false, 0, false, 0, false, 0, 0, 0); return true; }
        var f = s[p++];
        bool oon = (f & 0x80) != 0, prog = (f & 0x40) != 0, dur = (f & 0x20) != 0, imm = (f & 0x10) != 0;
        if (!prog) return false;
        long pts = 0; var hasTime = false;
        if (!imm)
        {
            if ((s[p] & 0x80) != 0) { pts = ((long)(s[p] & 1) << 32) | BinaryPrimitives.ReadUInt32BigEndian(s[(p + 1)..]); hasTime = true; p += 5; }
            else p += 1;
        }
        long duration = 0; var auto = false;
        if (dur) { auto = (s[p] & 0x80) != 0; duration = ((long)(s[p] & 1) << 32) | BinaryPrimitives.ReadUInt32BigEndian(s[(p + 1)..]); p += 5; }
        var pid = BinaryPrimitives.ReadUInt16BigEndian(s[p..]); p += 2;
        info = new SpliceInsertInfo(id, oon, hasTime, pts, dur, duration, auto, pid, s[p], s[p + 1]);
        return true;
    }

    public static string ToHex(ReadOnlySpan<byte> b) => "0x" + Convert.ToHexString(b);
    public static string ToBase64(ReadOnlySpan<byte> b) => Convert.ToBase64String(b);
}

/// <summary>
/// [PromptFlow Studio v16.43.0] 📢 비디오 맥락 연동 SSAI 동적 광고 인젝터.
///   ▸ 맥락 분석: 대본 씬(긴장도 · 분위기 정서가) + 대사 구간 → 씬 경계 후보 점수(클라이맥스 양측 차단 · 대사 간격 · 긴장 해소 가산 · 정서 전환 감점) → 간격/개수 제약 탐욕 선택(무할당 코어).
///   ▸ SCTE-35 splice_insert(OUT: 90kHz PTS + break_duration · IN: 복귀) 바이너리 → HLS(#EXT-X-DATERANGE SCTE35-OUT/IN · #EXT-X-CUE-OUT/IN · DISCONTINUITY · 광고 세그먼트 스티칭 · 세그먼트 경계 스냅) / DASH(EventStream urn:scte:scte35:2014:xml+bin) 인젝션.
///   ▸ 라이브 감지(FeedLive · 무할당): PGM 오디오 무음 휴지 ≥ 1.5s · 긴장도 ≤ 상한 · 최소 간격 → 즉시 큐(MCR 결선).
/// </summary>
public sealed class ContextualServerSideAdInsertionEngine
{
    private uint _nextEventId = 0x5046_0001;
    private double _liveLastBreak = double.NegativeInfinity, _liveSilentSince = double.NaN;
    private long _liveCues, _injected;

    public event Action? OnChange;
    public AdPolicy Policy { get; set; } = new();
    public IReadOnlyList<AdBreak> Plan { get; private set; } = Array.Empty<AdBreak>();
    public long LiveCues => Interlocked.Read(ref _liveCues);
    public long Injected => Interlocked.Read(ref _injected);
    public string? LastAction { get; private set; }

    // ══════════════════════ 맥락 분석 ══════════════════════

    public static float ValenceOf(SceneMood m) => m switch
    {
        SceneMood.Warm => 0.8f, SceneMood.Melancholy => -0.7f, SceneMood.Tense => -0.3f, SceneMood.Action => 0.1f, SceneMood.Mystery => -0.2f, _ => 0f,
    };

    /// <summary>대본 → 구간 · 대사 구간(씬 길이 내 대사 길이 비례).</summary>
    public static (ContentBeat[] Beats, (double Start, double End)[] Dialogue) Timeline(ScreenplayDoc doc)
    {
        var beats = new List<ContentBeat>(doc.Scenes.Count);
        var dlg = new List<(double, double)>();
        double t = 0;
        foreach (var sc in doc.Scenes)
        {
            var sec = sc.Seconds > 0 ? sc.Seconds : 6;
            beats.Add(new ContentBeat(t, sec, sc.Tension, ValenceOf(AutonomousSceneBlockingEngine.MoodOf(sc))));
            double weight = 0;
            foreach (var e in sc.Elements) weight += e.Kind == ScriptElementKind.Dialogue ? Math.Max(4, e.Text.Length) : e.Kind == ScriptElementKind.Action ? Math.Max(4, e.Text.Length) * 0.5 : 0;
            var cur = t;
            if (weight > 0)
                foreach (var e in sc.Elements)
                {
                    if (e.Kind is not (ScriptElementKind.Dialogue or ScriptElementKind.Action)) continue;
                    var d = sec * (e.Kind == ScriptElementKind.Dialogue ? Math.Max(4, e.Text.Length) : Math.Max(4, e.Text.Length) * 0.5) / weight;
                    if (e.Kind == ScriptElementKind.Dialogue) dlg.Add((cur, cur + d * 0.9));
                    cur += d;
                }
            t += sec;
        }
        return (beats.ToArray(), dlg.ToArray());
    }

    /// <summary>후보 점수화 + 선택(무할당 코어). 반환 = 선택 수(시간순).</summary>
    public static int SelectBreaks(ReadOnlySpan<ContentBeat> beats, ReadOnlySpan<(double Start, double End)> dialogue, AdPolicy p, Span<AdBreakCandidate> scratch, Span<AdBreakCandidate> chosen)
    {
        if (beats.Length < 2) return 0;
        var end = beats[^1].Start + beats[^1].Duration;
        var n = 0;
        for (var i = 1; i < beats.Length && n < scratch.Length; i++)
        {
            var t = beats[i].Start;
            if (t < p.GuardStartSec || t > end - p.GuardEndSec) continue;
            int before = beats[i - 1].Tension, after = beats[i].Tension;
            if (Math.Max(before, after) >= p.ClimaxTension) continue;
            var gap = double.MaxValue;
            foreach (var (s, e) in dialogue)
            {
                if (t >= s && t <= e) { gap = 0; break; }
                gap = Math.Min(gap, Math.Min(Math.Abs(t - s), Math.Abs(t - e)));
            }
            if (gap < p.MinDialogueGapSec) continue;
            var score = 1f - 0.6f * Math.Max(before, after) / 100f + 0.3f * Math.Max(0, before - after) / 100f + 0.1f * (float)Math.Min(gap, 5) / 5f
                        - 0.15f * MathF.Abs(beats[i - 1].Valence - beats[i].Valence) * (beats[i - 1].Valence < 0 ? 1f : 0.3f);
            scratch[n++] = new AdBreakCandidate(t, score, before, after, gap == double.MaxValue ? 99 : gap);
        }
        // 점수 내림차순 삽입 정렬
        for (var i = 1; i < n; i++) { var x = scratch[i]; var j = i - 1; while (j >= 0 && scratch[j].Score < x.Score) { scratch[j + 1] = scratch[j]; j--; } scratch[j + 1] = x; }
        var c = 0;
        for (var i = 0; i < n && c < Math.Min(p.MaxBreaks, chosen.Length); i++)
        {
            var ok = true;
            for (var k = 0; k < c; k++) if (Math.Abs(chosen[k].TimeSec - scratch[i].TimeSec) < p.MinSpacingSec) { ok = false; break; }
            if (ok) chosen[c++] = scratch[i];
        }
        for (var i = 1; i < c; i++) { var x = chosen[i]; var j = i - 1; while (j >= 0 && chosen[j].TimeSec > x.TimeSec) { chosen[j + 1] = chosen[j]; j--; } chosen[j + 1] = x; }
        return c;
    }

    public IReadOnlyList<AdBreak> PlanBreaks(ScreenplayDoc doc)
    {
        var (beats, dlg) = Timeline(doc);
        return PlanBreaks(beats, dlg);
    }

    public IReadOnlyList<AdBreak> PlanBreaks(ContentBeat[] beats, (double Start, double End)[] dialogue)
    {
        var scratch = new AdBreakCandidate[beats.Length];
        var chosen = new AdBreakCandidate[Math.Max(1, Policy.MaxBreaks)];
        var n = SelectBreaks(beats, dialogue, Policy, scratch, chosen);
        var list = new List<AdBreak>(n);
        for (var i = 0; i < n; i++)
        {
            var c = chosen[i];
            var reason = "긴장 " + c.TensionBefore.ToString(CultureInfo.InvariantCulture) + "→" + c.TensionAfter.ToString(CultureInfo.InvariantCulture) + " · 대사 간격 " + Math.Min(c.DialogueGapSec, 99).ToString("0.0", CultureInfo.InvariantCulture) + "s";
            list.Add(new AdBreak(i + 1, _nextEventId++, Math.Round(c.TimeSec, 3), Policy.BreakSeconds, MathF.Round(c.Score, 3), reason));
        }
        Plan = list;
        LastAction = "🧭 광고 브레이크 " + n.ToString(CultureInfo.InvariantCulture) + "개 배치";
        Raise();
        return list;
    }

    // ══════════════════════ 매니페스트 인젝션 ══════════════════════

    public static byte[] CueOut(AdBreak b, long ptsOffset = 0)
    {
        Span<byte> buf = stackalloc byte[Scte35.MaxSectionBytes];
        var n = Scte35.EncodeSpliceInsert(buf, b.EventId, true, ptsOffset + (long)Math.Round(b.TimeSec * 90000), (long)Math.Round(b.DurationSec * 90000), 1, (byte)b.Index, 0);
        return buf[..n].ToArray();
    }

    public static byte[] CueIn(AdBreak b, long ptsOffset = 0)
    {
        Span<byte> buf = stackalloc byte[Scte35.MaxSectionBytes];
        var n = Scte35.EncodeSpliceInsert(buf, b.EventId, false, ptsOffset + (long)Math.Round((b.TimeSec + b.DurationSec) * 90000), null, 1, (byte)b.Index, 0);
        return buf[..n].ToArray();
    }

    /// <summary>
    /// HLS 미디어 플레이리스트 인젝션 — 브레이크 시각을 가장 가까운 세그먼트 경계로 스냅 → DATERANGE(SCTE35-OUT) · CUE-OUT · DISCONTINUITY · 광고 세그먼트 · DISCONTINUITY · CUE-IN · DATERANGE(SCTE35-IN).
    /// adSegments(브레이크) = 광고 세그먼트(URI, 길이) · null 이면 placeholder(6초 단위).
    /// </summary>
    public string InjectHls(string mediaPlaylist, IReadOnlyList<AdBreak> breaks, DateTimeOffset programStart, Func<AdBreak, IReadOnlyList<(string Uri, double Duration)>>? adSegments = null)
    {
        var lines = mediaPlaylist.Replace("\r", "").Split('\n');
        var sb = new StringBuilder(mediaPlaylist.Length + breaks.Count * 600);
        double t = 0;
        var bi = 0;
        var inj = 0;
        foreach (var raw in lines)
        {
            var line = raw;
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal)) { sb.Append(line).Append('\n'); continue; }
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                // 세그먼트 시작 경계 t — 다음 브레이크와 가장 가까운 경계에서 삽입
                while (bi < breaks.Count && breaks[bi].TimeSec <= t + SegmentLen(line) / 2)
                {
                    if (breaks[bi].TimeSec >= t - SegmentLen(line) / 2) { AppendBreak(sb, breaks[bi], t, programStart, adSegments); inj++; }
                    bi++;
                }
                sb.Append(line).Append('\n');
                t += SegmentLen(line);
                continue;
            }
            sb.Append(line).Append('\n');
        }
        Interlocked.Add(ref _injected, inj);
        LastAction = "💉 HLS SCTE-35 인젝션 · " + inj.ToString(CultureInfo.InvariantCulture) + "개";
        Raise();
        return sb.ToString();

        static double SegmentLen(string inf)
        {
            var s = inf.AsSpan(8);
            var c = s.IndexOf(',');
            return double.TryParse(c >= 0 ? s[..c] : s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
    }

    private static void AppendBreak(StringBuilder sb, AdBreak b, double boundary, DateTimeOffset start, Func<AdBreak, IReadOnlyList<(string Uri, double Duration)>>? ads)
    {
        var snapped = b with { TimeSec = boundary };
        var id = "pfs-ad-" + b.EventId.ToString("x8", CultureInfo.InvariantCulture);
        var startDate = start.AddSeconds(boundary).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var dur = b.DurationSec.ToString("0.000", CultureInfo.InvariantCulture);
        sb.Append("#EXT-X-DATERANGE:ID=\"").Append(id).Append("\",START-DATE=\"").Append(startDate).Append("\",PLANNED-DURATION=").Append(dur)
          .Append(",SCTE35-OUT=").Append(Scte35.ToHex(CueOut(snapped))).Append('\n');
        sb.Append("#EXT-X-CUE-OUT:DURATION=").Append(dur).Append('\n');
        sb.Append("#EXT-X-DISCONTINUITY\n");
        var segs = ads?.Invoke(b);
        if (segs is null || segs.Count == 0)
        {
            var left = b.DurationSec; var k = 0;
            while (left > 0.001) { var d = Math.Min(6, left); sb.Append("#EXTINF:").Append(d.ToString("0.000", CultureInfo.InvariantCulture)).Append(",\nads/").Append(id).Append("/seg_").Append(k++.ToString("000", CultureInfo.InvariantCulture)).Append(".ts\n"); left -= d; }
        }
        else foreach (var (u, d) in segs) sb.Append("#EXTINF:").Append(d.ToString("0.000", CultureInfo.InvariantCulture)).Append(",\n").Append(u).Append('\n');
        sb.Append("#EXT-X-DISCONTINUITY\n#EXT-X-CUE-IN\n");
        sb.Append("#EXT-X-DATERANGE:ID=\"").Append(id).Append("\",START-DATE=\"").Append(startDate).Append("\",SCTE35-IN=").Append(Scte35.ToHex(CueIn(snapped))).Append('\n');
    }

    /// <summary>DASH MPD 인젝션 — 첫 Period 에 EventStream(SCTE-35 바이너리) 삽입.</summary>
    public string InjectDash(string mpd, IReadOnlyList<AdBreak> breaks)
    {
        var at = mpd.IndexOf("<Period", StringComparison.Ordinal);
        if (at < 0) return mpd;
        var close = mpd.IndexOf('>', at);
        if (close < 0) return mpd;
        var sb = new StringBuilder("\n    <EventStream schemeIdUri=\"urn:scte:scte35:2014:xml+bin\" timescale=\"90000\">\n");
        foreach (var b in breaks)
            sb.Append("      <Event presentationTime=\"").Append(((long)Math.Round(b.TimeSec * 90000)).ToString(CultureInfo.InvariantCulture)).Append("\" duration=\"")
              .Append(((long)Math.Round(b.DurationSec * 90000)).ToString(CultureInfo.InvariantCulture)).Append("\" id=\"").Append(b.EventId.ToString(CultureInfo.InvariantCulture))
              .Append("\">\n        <Signal xmlns=\"http://www.scte.org/schemas/35/2016\"><Binary>").Append(Scte35.ToBase64(CueOut(b))).Append("</Binary></Signal>\n      </Event>\n");
        sb.Append("    </EventStream>");
        Interlocked.Add(ref _injected, breaks.Count);
        return mpd[..(close + 1)] + sb + mpd[(close + 1)..];
    }

    // ══════════════════════ 라이브(무할당) ══════════════════════

    /// <summary>라이브 PGM 신호 → 자연 휴지 시 광고 큐(true). 무할당.</summary>
    public bool FeedLive(double tSec, float speechRms, int tension)
    {
        var p = Policy;
        if (speechRms >= p.SpeechRms || tension > p.LiveMaxTension) { _liveSilentSince = double.NaN; return false; }
        if (double.IsNaN(_liveSilentSince)) _liveSilentSince = tSec;
        if (tSec - _liveSilentSince < p.LivePauseSec || tSec - _liveLastBreak < p.MinSpacingSec) return false;
        _liveLastBreak = tSec;
        _liveSilentSince = double.NaN;
        Interlocked.Increment(ref _liveCues);
        return true;
    }

    public void ResetLive() { _liveLastBreak = double.NegativeInfinity; _liveSilentSince = double.NaN; }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Ssai.OnChange", ex); } }
}
