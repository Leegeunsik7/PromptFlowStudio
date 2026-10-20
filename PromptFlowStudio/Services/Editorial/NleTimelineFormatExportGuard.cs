using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Editorial;

public enum NleFormat : byte { Edl = 0, FcpXml, Aaf }

public enum NleIssue : byte { None = 0, Empty, Overlap, Gap, NegativeSource, TooManyEvents, BadFps, Overflow, UnknownSource }

/// <summary>스팬 UTF-8 출력기(넘침 시 Ok = false · 이후 쓰기 무시 · 무할당).</summary>
public ref struct Utf8SpanWriter
{
    private readonly Span<byte> _buf;
    public int Pos;
    public bool Ok;

    public Utf8SpanWriter(Span<byte> buf) { _buf = buf; Pos = 0; Ok = true; }

    public ReadOnlySpan<byte> Written => _buf[..Pos];

    public void Raw(ReadOnlySpan<byte> s)
    {
        if (!Ok) return;
        if (Pos + s.Length > _buf.Length) { Ok = false; return; }
        s.CopyTo(_buf[Pos..]); Pos += s.Length;
    }

    public void Text(ReadOnlySpan<char> s)
    {
        if (!Ok) return;
        if (Pos + Encoding.UTF8.GetByteCount(s) > _buf.Length) { Ok = false; return; }
        Pos += Encoding.UTF8.GetBytes(s, _buf[Pos..]);
    }

    /// <summary>XML 이스케이프 텍스트(&amp; &lt; &gt; &quot;).</summary>
    public void Xml(ReadOnlySpan<char> s)
    {
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var esc = s[i] switch { '&' => "&amp;"u8, '<' => "&lt;"u8, '>' => "&gt;"u8, '"' => "&quot;"u8, _ => default };
            if (esc.IsEmpty) continue;
            Text(s[start..i]); Raw(esc); start = i + 1;
        }
        Text(s[start..]);
    }

    public void Int(long v, int minDigits = 0)
    {
        if (!Ok) return;
        var fmt = minDigits > 0 ? new System.Buffers.StandardFormat('D', (byte)minDigits) : default;
        if (!Utf8Formatter.TryFormat(v, _buf[Pos..], out var n, fmt)) { Ok = false; return; }
        Pos += n;
    }

    public void Pad(byte c, int count)
    {
        if (!Ok) return;
        if (Pos + count > _buf.Length) { Ok = false; return; }
        _buf.Slice(Pos, count).Fill(c); Pos += count;
    }

    /// <summary>SMPTE 타임코드 HH:MM:SS:FF(NDF · 정수 fps).</summary>
    public void Timecode(long frame, int fps)
    {
        var f = frame % fps; var s = frame / fps;
        Int(s / 3600 % 24, 2); Raw(":"u8); Int(s / 60 % 60, 2); Raw(":"u8); Int(s % 60, 2); Raw(":"u8); Int(f, 2);
    }
}

/// <summary>
/// [PromptFlow Studio v16.79.0] 📤 NLE 타임라인 표준 데이터 익스포트 가드.
/// 사전 검증(빈 타임라인 · 트랙 내 겹침/틈 · 음수 소스 · CMX 999 이벤트 · fps · 미등록 소스) → 사전 할당 버퍼(기본 4 MB)에 UTF-8/바이너리 직접 스트리밍(무할당):
/// ▸ EDL = CMX3600(TITLE · FCM NON-DROP · 이벤트 V/A · 릴 8자 · 소스/레코드 TC · FROM CLIP NAME) + 역파싱 왕복 검증.
/// ▸ FCPXML 1.9 = format/asset 리소스 · sequence spine asset-clip + BGM 연결 클립(lane −1) · 몽타주 연결 클립(lane 1) · rational time.
/// ▸ AAF = Microsoft CFB v3(512 B 섹터 · FAT · 디렉터리) 구조화 저장소 — Header/Content/CompositionMob/SourceMobs 스트림(PFA1 레코드). 공식 AAF SDK 객체 모델 인코딩 아님.
/// </summary>
public sealed class NleTimelineFormatExportGuard
{
    public const int DefaultCapacity = 4 << 20, CfbSector = 512, CfbMinStream = 4096;
    private const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF, FatSect = 0xFFFFFFFD, NoStream = 0xFFFFFFFF;

    private readonly byte[] _buf;
    private readonly byte[] _scratch;
    private readonly int[] _order = new int[EditTimeline.MaxClips];

    public NleTimelineFormatExportGuard(int capacity = DefaultCapacity)
    {
        _buf = new byte[capacity];
        _scratch = new byte[capacity / 2];
    }

    public NleIssue LastIssue { get; private set; }
    public int LastIssueClip { get; private set; } = -1;
    public int LastBytes { get; private set; }
    public NleFormat LastFormat { get; private set; }
    public float LastMs { get; private set; }
    public long Exports { get; private set; }
    public ReadOnlySpan<byte> Output => _buf.AsSpan(0, LastBytes);

    // ══════════════════════ 검증 ══════════════════════

    /// <summary>타임라인 검증(트랙별 레코드 순 정렬 후 겹침 검사 · 비디오 트랙 0 의 틈 검사). 무할당.</summary>
    public NleIssue Validate(EditTimeline tl, bool requireContiguousV1 = true)
    {
        LastIssueClip = -1;
        if (tl.Fps <= 0 || tl.Fps > 120) return LastIssue = NleIssue.BadFps;
        if (tl.Count == 0) return LastIssue = NleIssue.Empty;
        for (var t = 0; t < tl.TrackCount; t++)
        {
            var n = SortTrack(tl, t);
            var end = 0;
            for (var k = 0; k < n; k++)
            {
                ref readonly var c = ref tl.Clip(_order[k]);
                if (c.Source < 0 || c.Source >= tl.SourceCount) { LastIssueClip = _order[k]; return LastIssue = NleIssue.UnknownSource; }
                if (c.SrcIn < 0) { LastIssueClip = _order[k]; return LastIssue = NleIssue.NegativeSource; }
                if (k > 0 && c.RecIn < end) { LastIssueClip = _order[k]; return LastIssue = NleIssue.Overlap; }
                if (t == 0 && requireContiguousV1 && k > 0 && c.RecIn > end) { LastIssueClip = _order[k]; return LastIssue = NleIssue.Gap; }
                end = c.RecOut;
            }
        }
        if (tl.Count > 999) return LastIssue = NleIssue.TooManyEvents;
        return LastIssue = NleIssue.None;
    }

    /// <summary>트랙 t 클립 인덱스를 RecIn 오름차순으로 _order 에 정렬(삽입 정렬 · 무할당). 반환 = 개수.</summary>
    private int SortTrack(EditTimeline tl, int t)
    {
        var n = 0;
        for (var i = 0; i < tl.Count; i++)
        {
            if (tl.Clip(i).Track != t) continue;
            var j = n++;
            var r = tl.Clip(i).RecIn;
            while (j > 0 && tl.Clip(_order[j - 1]).RecIn > r) { _order[j] = _order[j - 1]; j--; }
            _order[j] = i;
        }
        return n;
    }

    /// <summary>포맷 내보내기(검증 → 버퍼 기록). 반환 = 성공. 결과 = <see cref="Output"/>.</summary>
    public bool Export(EditTimeline tl, NleFormat fmt)
    {
        using var guard = ZeroGcRegionScope.Enter("Nle.Export");
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastFormat = fmt; LastBytes = 0;
        var issue = Validate(tl);
        if (issue != NleIssue.None) return false;
        var n = fmt switch { NleFormat.Edl => WriteEdl(tl, _buf), NleFormat.FcpXml => WriteFcpXml(tl, _buf), _ => WriteAaf(tl, _buf) };
        if (n < 0) { LastIssue = NleIssue.Overflow; return false; }
        LastBytes = n;
        Exports++;
        LastMs = (float)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return true;
    }

    // ══════════════════════ EDL(CMX3600) ══════════════════════

    /// <summary>CMX3600 EDL 기록(소스/레코드 TC 는 01:00:00:00 기점). 반환 = 바이트 수(-1 = 넘침).</summary>
    public int WriteEdl(EditTimeline tl, Span<byte> dst)
    {
        var w = new Utf8SpanWriter(dst);
        var hour = 3600L * tl.Fps;
        w.Raw("TITLE: "u8); w.Text(tl.Title); w.Raw("\r\nFCM: NON-DROP FRAME\r\n\r\n"u8);
        var ev = 1;
        for (var t = 0; t < tl.TrackCount; t++)
        {
            var n = SortTrack(tl, t);
            var audio = tl.TrackKind(t) == EditTrackKind.Audio;
            for (var k = 0; k < n; k++)
            {
                ref readonly var c = ref tl.Clip(_order[k]);
                w.Int(ev++, 3); w.Raw("  "u8);
                var reel = tl.SourceReel(c.Source);
                w.Text(reel); w.Pad((byte)' ', Math.Max(1, 9 - reel.Length));
                w.Raw(audio ? "A     "u8 : t == 0 ? "V     "u8 : "V2    "u8);
                w.Raw("C        "u8);
                w.Timecode(hour + c.SrcIn, tl.Fps); w.Raw(" "u8); w.Timecode(hour + c.SrcIn + c.Length, tl.Fps); w.Raw(" "u8);
                w.Timecode(hour + c.RecIn, tl.Fps); w.Raw(" "u8); w.Timecode(hour + c.RecOut, tl.Fps); w.Raw("\r\n"u8);
                w.Raw("* FROM CLIP NAME: "u8); w.Text(tl.SourceName(c.Source)); w.Raw("\r\n"u8);
                w.Raw("* PFS CUT REASON: "u8); w.Int((int)c.Reason); w.Raw("\r\n\r\n"u8);
            }
        }
        return w.Ok ? w.Pos : -1;
    }

    /// <summary>EDL 역파싱(왕복 검증): 이벤트 수 · 레코드 총 길이(V 트랙) · 릴/TC 형식 오류 수. 무할당.</summary>
    public static (int Events, long VideoFrames, int Errors) ParseEdl(ReadOnlySpan<byte> edl, int fps)
    {
        int events = 0, errors = 0; long frames = 0;
        Span<long> tc = stackalloc long[4];
        while (!edl.IsEmpty)
        {
            var nl = edl.IndexOf((byte)'\n');
            var line = nl < 0 ? edl : edl[..nl];
            edl = nl < 0 ? default : edl[(nl + 1)..];
            if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
            if (line.Length < 3 || !IsDigit(line[0]) || !IsDigit(line[1]) || !IsDigit(line[2])) continue;
            events++;
            // 마지막 4개 토큰 = srcIn srcOut recIn recOut
            var rest = line; var got = 0;
            for (var k = 3; k >= 0; k--)
            {
                var sp = rest.LastIndexOf((byte)' ');
                if (sp < 0 || !TryTc(rest[(sp + 1)..], fps, out tc[k])) { errors++; break; }
                rest = rest[..sp].TrimEnd((byte)' ');
                got++;
            }
            if (got != 4) continue;
            if (tc[1] - tc[0] != tc[3] - tc[2] || tc[3] <= tc[2]) errors++;
            if (line.IndexOf(" V "u8) > 0) frames += tc[3] - tc[2];
        }
        return (events, frames, errors);
    }

    private static bool IsDigit(byte b) => b >= '0' && b <= '9';

    private static bool TryTc(ReadOnlySpan<byte> s, int fps, out long frame)
    {
        frame = 0;
        if (s.Length != 11 || s[2] != ':' || s[5] != ':' || s[8] != ':') return false;
        for (var i = 0; i < 11; i++) if (i % 3 != 2 && !IsDigit(s[i])) return false;
        int h = (s[0] - '0') * 10 + s[1] - '0', m = (s[3] - '0') * 10 + s[4] - '0', sec = (s[6] - '0') * 10 + s[7] - '0', f = (s[9] - '0') * 10 + s[10] - '0';
        if (m > 59 || sec > 59 || f >= fps) return false;
        frame = ((h * 60L + m) * 60 + sec) * fps + f;
        return true;
    }

    // ══════════════════════ FCPXML 1.9 ══════════════════════

    public int WriteFcpXml(EditTimeline tl, Span<byte> dst)
    {
        var w = new Utf8SpanWriter(dst);
        var fps = tl.Fps;
        w.Raw("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE fcpxml>\n<fcpxml version=\"1.9\">\n  <resources>\n"u8);
        w.Raw("    <format id=\"r0\" name=\"FFVideoFormat1080p"u8); w.Int(fps); w.Raw("\" frameDuration=\"1/"u8); w.Int(fps); w.Raw("s\" width=\"1920\" height=\"1080\"/>\n"u8);
        for (var s = 0; s < tl.SourceCount; s++)
        {
            w.Raw("    <asset id=\"a"u8); w.Int(s); w.Raw("\" name=\""u8); w.Xml(tl.SourceName(s)); w.Raw("\" start=\"0s\" duration=\""u8); Rational(ref w, tl.TrackEnd(0) + 3600L * fps, fps);
            w.Raw("\" hasVideo=\"1\" hasAudio=\"1\" format=\"r0\">\n      <media-rep kind=\"original-media\" src=\"file:///PromptFlow/Editorial/"u8);
            w.Xml(tl.SourceReel(s)); w.Raw(".mov\"/>\n    </asset>\n"u8);
        }
        w.Raw("  </resources>\n  <library>\n    <event name=\"PromptFlow Editorial\">\n      <project name=\""u8); w.Xml(tl.Title); w.Raw("\">\n        <sequence format=\"r0\" duration=\""u8);
        var total = tl.TrackEnd(0);
        Rational(ref w, total, fps); w.Raw("\" tcStart=\"0s\" tcFormat=\"NDF\">\n          <spine>\n"u8);
        var n = SortTrack(tl, 0);
        for (var k = 0; k < n; k++)
        {
            ref readonly var c = ref tl.Clip(_order[k]);
            w.Raw("            <asset-clip ref=\"a"u8); w.Int(c.Source); w.Raw("\" name=\""u8); w.Xml(tl.SourceName(c.Source));
            w.Raw("\" offset=\""u8); Rational(ref w, c.RecIn, fps); w.Raw("\" start=\""u8); Rational(ref w, c.SrcIn, fps); w.Raw("\" duration=\""u8); Rational(ref w, c.Length, fps); w.Raw("\">\n"u8);
            // 연결 클립(다른 트랙): 이 스파인 클립 구간에서 시작하는 클립
            for (var i = 0; i < tl.Count; i++)
            {
                ref readonly var o = ref tl.Clip(i);
                if (o.Track == 0 || o.RecIn < c.RecIn || o.RecIn >= c.RecOut) continue;
                var lane = tl.TrackKind(o.Track) == EditTrackKind.Audio ? -o.Track : o.Track;
                w.Raw("              <asset-clip ref=\"a"u8); w.Int(o.Source); w.Raw("\" lane=\""u8); w.Int(lane); w.Raw("\" name=\""u8); w.Xml(tl.SourceName(o.Source));
                // 연결 클립 offset = 부모 로컬 시간(start + (rec − parentRecIn))
                w.Raw("\" offset=\""u8); Rational(ref w, c.SrcIn + (o.RecIn - c.RecIn), fps); w.Raw("\" start=\""u8); Rational(ref w, o.SrcIn, fps);
                w.Raw("\" duration=\""u8); Rational(ref w, o.Length, fps); w.Raw(tl.TrackKind(o.Track) == EditTrackKind.Audio ? "\" audioRole=\"music\"/>\n"u8 : "\"/>\n"u8);
            }
            w.Raw("            </asset-clip>\n"u8);
        }
        w.Raw("          </spine>\n        </sequence>\n      </project>\n    </event>\n  </library>\n</fcpxml>\n"u8);
        return w.Ok ? w.Pos : -1;
    }

    private static void Rational(ref Utf8SpanWriter w, long frames, int fps)
    {
        if (frames == 0) { w.Raw("0s"u8); return; }
        w.Int(frames); w.Raw("/"u8); w.Int(fps); w.Raw("s"u8);
    }

    // ══════════════════════ AAF(CFB 구조화 저장소) ══════════════════════

    /// <summary>
    /// CFB v3 컨테이너 기록: Root(AAF) → Header 스트림 · Content 저장소 → CompositionMob 스트림(클립 레코드 24 B) · SourceMobs 스트림(소스 레코드).
    /// 각 스트림은 4096 B 이상으로 패딩(미니 스트림 회피) · 페이로드 앞 'PFA1' + 길이. 반환 = 바이트 수(-1 = 넘침).
    /// </summary>
    public int WriteAaf(EditTimeline tl, Span<byte> dst)
    {
        // 1) 스트림 페이로드를 스크래치에 직렬화
        var scr = _scratch.AsSpan();
        var hdrLen = BuildHeader(tl, scr);
        var compOff = Align(hdrLen);
        var compLen = BuildComposition(tl, scr[compOff..]);
        var srcOff = compOff + Align(compLen);
        var srcLen = BuildSources(tl, scr[srcOff..]);
        if (hdrLen < 0 || compLen < 0 || srcLen < 0 || srcOff + Align(srcLen) > scr.Length) return -1;
        Span<int> off = stackalloc int[] { 0, compOff, srcOff };
        Span<int> len = stackalloc int[] { Align(hdrLen), Align(compLen), Align(srcLen) };

        // 2) 레이아웃: [FAT × f][DIR × 2][Header][Comp][Src]
        var dataSecs = 0;
        for (var i = 0; i < 3; i++) dataSecs += len[i] / CfbSector;
        const int dirSecs = 2;
        var f = 1;
        while (f * (CfbSector / 4) < f + dirSecs + dataSecs) f++;
        if (f > 109) return -1;
        var total = CfbSector * (1 + f + dirSecs + dataSecs);
        if (total > dst.Length) return -1;
        var d = dst[..total];
        d.Clear();

        // 헤더
        ReadOnlySpan<byte> sig = stackalloc byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        sig.CopyTo(d);
        BinaryPrimitives.WriteUInt16LittleEndian(d[24..], 0x003E); BinaryPrimitives.WriteUInt16LittleEndian(d[26..], 0x0003);
        BinaryPrimitives.WriteUInt16LittleEndian(d[28..], 0xFFFE); BinaryPrimitives.WriteUInt16LittleEndian(d[30..], 9); BinaryPrimitives.WriteUInt16LittleEndian(d[32..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(d[44..], (uint)f);
        BinaryPrimitives.WriteUInt32LittleEndian(d[48..], (uint)f); // 첫 디렉터리 섹터
        BinaryPrimitives.WriteUInt32LittleEndian(d[56..], CfbMinStream);
        BinaryPrimitives.WriteUInt32LittleEndian(d[60..], EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(d[68..], EndOfChain);
        for (var i = 0; i < 109; i++) BinaryPrimitives.WriteUInt32LittleEndian(d[(76 + i * 4)..], i < f ? (uint)i : FreeSect);

        // FAT
        var fat = d.Slice(CfbSector, f * CfbSector);
        for (var i = 0; i < f * CfbSector / 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(fat[(i * 4)..], FreeSect);
        for (var i = 0; i < f; i++) SetFat(fat, i, FatSect);
        SetFat(fat, f, (uint)(f + 1)); SetFat(fat, f + 1, EndOfChain);
        var sec = f + dirSecs;
        Span<int> start = stackalloc int[3];
        for (var s = 0; s < 3; s++)
        {
            start[s] = sec;
            var n = len[s] / CfbSector;
            for (var k = 0; k < n; k++) SetFat(fat, sec + k, k == n - 1 ? EndOfChain : (uint)(sec + k + 1));
            scr.Slice(off[s], len[s]).CopyTo(d[(CfbSector * (1 + sec))..]);
            sec += n;
        }

        // 디렉터리(0 Root · 1 Content · 2 Header · 3 CompositionMob · 4 SourceMobs) — 형제는 이름 정렬 우측 체인
        var dir = d.Slice(CfbSector * (1 + f), dirSecs * CfbSector);
        for (var i = 0; i < 8; i++) { var e = dir.Slice(i * 128, 128); BinaryPrimitives.WriteUInt32LittleEndian(e[68..], NoStream); BinaryPrimitives.WriteUInt32LittleEndian(e[72..], NoStream); BinaryPrimitives.WriteUInt32LittleEndian(e[76..], NoStream); }
        DirEntry(dir, 0, "Root Entry", 5, NoStream, 2, EndOfChain, 0);
        // AAF 루트 CLSID 표식
        ReadOnlySpan<byte> clsid = stackalloc byte[] { 0x41, 0x41, 0x46, 0x42, 0x0D, 0x00, 0x4F, 0x4D, 0x06, 0x0E, 0x2B, 0x34, 0x01, 0x01, 0x01, 0xFF };
        clsid.CopyTo(dir[80..]);
        DirEntry(dir, 2, "Header", 2, 1, NoStream, (uint)start[0], len[0]);            // 루트 자식: Header → Content(우측)
        DirEntry(dir, 1, "Content", 1, NoStream, 3, EndOfChain, 0);
        DirEntry(dir, 3, "SourceMobs", 2, 4, NoStream, (uint)start[2], len[2]);       // CFB 형제 순서 = 이름 길이 → 대문자: SourceMobs(10) < CompositionMob(14)
        DirEntry(dir, 4, "CompositionMob", 2, NoStream, NoStream, (uint)start[1], len[1]);
        return total;
    }

    private static int Align(int n) => n < 0 ? n : Math.Max(CfbMinStream, (n + CfbSector - 1) / CfbSector * CfbSector);

    private static void SetFat(Span<byte> fat, int i, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(fat[(i * 4)..], v);

    private static void DirEntry(Span<byte> dir, int id, string name, byte type, uint right, uint child, uint startSec, int size)
    {
        var e = dir.Slice(id * 128, 128);
        for (var i = 0; i < name.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(e[(i * 2)..], name[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(e[64..], (ushort)((name.Length + 1) * 2));
        e[66] = type; e[67] = 1; // 검정
        BinaryPrimitives.WriteUInt32LittleEndian(e[68..], NoStream);
        BinaryPrimitives.WriteUInt32LittleEndian(e[72..], right);
        BinaryPrimitives.WriteUInt32LittleEndian(e[76..], child);
        BinaryPrimitives.WriteUInt32LittleEndian(e[116..], startSec);
        BinaryPrimitives.WriteUInt32LittleEndian(e[120..], (uint)size);
    }

    private static int BuildHeader(EditTimeline tl, Span<byte> s)
    {
        var w = new Utf8SpanWriter(s);
        w.Raw("PFA1"u8); w.Raw("\0\0\0\0"u8);
        w.Raw("{\"fps\":"u8); w.Int(tl.Fps); w.Raw(",\"tracks\":"u8); w.Int(tl.TrackCount); w.Raw(",\"clips\":"u8); w.Int(tl.Count);
        w.Raw(",\"sources\":"u8); w.Int(tl.SourceCount); w.Raw(",\"title\":\""u8); w.Xml(tl.Title); w.Raw("\"}"u8);
        if (!w.Ok) return -1;
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], w.Pos - 8);
        return w.Pos;
    }

    private static int BuildComposition(EditTimeline tl, Span<byte> s)
    {
        var need = 8 + tl.Count * 24 + tl.TrackCount * 16;
        if (need > s.Length) return -1;
        "PFA1"u8.CopyTo(s); BinaryPrimitives.WriteInt32LittleEndian(s[4..], need - 8);
        var o = 8;
        for (var t = 0; t < tl.TrackCount; t++)
        {
            s[o] = (byte)tl.TrackKind(t); s[o + 1] = (byte)t;
            var nm = tl.TrackName(t);
            for (var i = 0; i < Math.Min(14, nm.Length); i++) s[o + 2 + i] = nm[i] < 128 ? (byte)nm[i] : (byte)'?';
            o += 16;
        }
        foreach (ref readonly var c in tl.Clips)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s[o..], c.Track); BinaryPrimitives.WriteInt32LittleEndian(s[(o + 4)..], c.Source);
            BinaryPrimitives.WriteInt32LittleEndian(s[(o + 8)..], c.SrcIn); BinaryPrimitives.WriteInt32LittleEndian(s[(o + 12)..], c.RecIn);
            BinaryPrimitives.WriteInt32LittleEndian(s[(o + 16)..], c.Length); BinaryPrimitives.WriteInt32LittleEndian(s[(o + 20)..], (int)c.Reason);
            o += 24;
        }
        return o;
    }

    private static int BuildSources(EditTimeline tl, Span<byte> s)
    {
        var w = new Utf8SpanWriter(s);
        w.Raw("PFA1"u8); w.Raw("\0\0\0\0"u8);
        for (var i = 0; i < tl.SourceCount; i++) { w.Text(tl.SourceReel(i)); w.Raw("\t"u8); w.Text(tl.SourceName(i)); w.Raw("\n"u8); }
        if (!w.Ok) return -1;
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], w.Pos - 8);
        return w.Pos;
    }

    private static uint CfbNext(ReadOnlySpan<byte> file, int fatSecs, uint sct)
    {
        var idx = (int)sct; var fs = idx / (CfbSector / 4);
        if (fs >= fatSecs) return EndOfChain;
        var fatSec = BinaryPrimitives.ReadUInt32LittleEndian(file[(76 + fs * 4)..]);
        return BinaryPrimitives.ReadUInt32LittleEndian(file[(int)(CfbSector * (1 + fatSec) + (idx % (CfbSector / 4)) * 4)..]);
    }

    /// <summary>CFB 판독(검증용): 이름으로 스트림 찾기 → FAT 체인 추적 → 'PFA1' 페이로드 반환. 실패 = 빈 스팬.</summary>
    public static ReadOnlySpan<byte> ReadCfbStream(ReadOnlySpan<byte> file, string name, Span<byte> dst)
    {
        if (file.Length < CfbSector * 2 || file[0] != 0xD0 || file[1] != 0xCF) return default;
        var fatSecs = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[44..]);
        var dirStart = BinaryPrimitives.ReadUInt32LittleEndian(file[48..]);
        // 디렉터리 체인 순회
        for (uint ds = dirStart, guardN = 0; ds != EndOfChain && guardN < 64; ds = CfbNext(file, fatSecs, ds), guardN++)
        {
            var sec = file.Slice((int)(CfbSector * (1 + ds)), CfbSector);
            for (var k = 0; k < CfbSector / 128; k++)
            {
                var e = sec.Slice(k * 128, 128);
                if (e[66] != 2) continue;
                var nl = BinaryPrimitives.ReadUInt16LittleEndian(e[64..]) / 2 - 1;
                if (nl != name.Length) continue;
                var match = true;
                for (var i = 0; i < nl && match; i++) match = BinaryPrimitives.ReadUInt16LittleEndian(e[(i * 2)..]) == name[i];
                if (!match) continue;
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[120..]);
                if (size > dst.Length) return default;
                var o = 0;
                for (var s = BinaryPrimitives.ReadUInt32LittleEndian(e[116..]); s != EndOfChain && o < size; s = CfbNext(file, fatSecs, s))
                {
                    var n = Math.Min(CfbSector, size - o);
                    file.Slice((int)(CfbSector * (1 + s)), n).CopyTo(dst[o..]);
                    o += n;
                }
                if (o < 8 || !dst[..4].SequenceEqual("PFA1"u8)) return default;
                var plen = BinaryPrimitives.ReadInt32LittleEndian(dst[4..]);
                return plen >= 0 && plen + 8 <= o ? dst.Slice(8, plen) : default;
            }
        }
        return default;
    }
}
