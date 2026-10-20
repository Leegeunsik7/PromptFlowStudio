using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Screenplay;

namespace PromptFlowStudio.Services.Cinema;

public enum PackageMode : byte { FeatureFilm, Series }

public readonly record struct ChapterMark(double StartSec, string Title);

public readonly record struct FilmSubtitleCue(double Start, double End, string Text);

public sealed record AudioTrackSpec(string Language, string Path, string Label);

public sealed record SubtitleTrackSpec(string Language, string Label, IReadOnlyList<FilmSubtitleCue> Cues);

/// <summary>에피소드 마스터 입력 — 클립 경로(순서) · 클립 길이(초 · ffmpeg 미가용 시 타임라인 기준) · 에피소드 내부 챕터.</summary>
public sealed record EpisodeMaster(string Title, IReadOnlyList<string> Clips, IReadOnlyList<double> ClipSeconds, IReadOnlyList<ChapterMark>? Chapters = null);

public sealed record MasteringOptions(PackageMode Mode, string Title, bool Mp4 = true, bool Hls = true, bool Dcp = true,
    IReadOnlyList<AudioTrackSpec>? Audio = null, IReadOnlyList<SubtitleTrackSpec>? Subtitles = null, IReadOnlyList<int>? HlsHeights = null, int HlsSegmentSec = 6, int Fps = 24);

public sealed record MasteringResult(bool Success, bool PartialRescue, string Folder, IReadOnlyList<string> Files, IReadOnlyList<string> Warnings, double DurationSec, int Chapters, string ManifestPath);

/// <summary>
/// [PromptFlow Studio v16.41.0] 🎞 장편 영화/시리즈 마스터링 패키저.
///   ▸ 에피소드별 클립 concat → 메자닌(work/epNN.mp4) → 장편(FeatureFilm: 단일 마스터 · 에피소드 = 챕터) 또는 시리즈(Series: 에피소드별 마스터).
///   ▸ 챕터 FFMETADATA · 다국어 오디오(-map · language 메타) · 다국어 자막(SRT/WebVTT · mov_text 임베드) · HLS(해상도 래더 · EXT-X-MEDIA 오디오/자막 그룹 · 마스터 플레이리스트) ·
///     DCP(에피소드 = 릴 · JPEG2000 MXF 영상 · 24bit/48k PCM MXF · ASSETMAP/VOLINDEX/PKL(SHA-1)/CPL XML).
///   ▸ 단계별 독립 실행 · ffmpeg 미가용/단계 실패 시 [부분 구제](챕터·자막·플레이리스트·매니페스트 보존) · CTS 취소 · package.json(SHA-256 · 집계 필드).
/// </summary>
public sealed class FeatureFilmMasteringPackager
{
    public const int DcpWidth = 1998, DcpHeight = 1080;
    private static readonly int[] DefaultLadder = { 1080, 720, 480 };

    private readonly Func<string?> _ffmpeg;
    private readonly string _root;
    private int _busy;

    public FeatureFilmMasteringPackager(IServiceProvider sp)
        : this(sp.GetService(typeof(IVideoMergeService)) is IVideoMergeService m ? () => m.ResolvedFfmpegPath : () => null) { }

    public FeatureFilmMasteringPackager(Func<string?> ffmpeg, string? root = null)
    {
        _ffmpeg = ffmpeg;
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "PromptFlowStudio", "Masters");
    }

    public event Action? OnChange;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public double Progress { get; private set; }
    public string Stage { get; private set; } = "대기";
    public MasteringResult? LastResult { get; private set; }
    public string? LastAction { get; private set; }

    // ══════════════════════ BCL 빌더(정적 · 테스트 가능) ══════════════════════

    public static string EscapeMeta(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            if (ch is '=' or ';' or '#' or '\\') sb.Append('\\');
            if (ch == '\n') { sb.Append("\\\n"); continue; }
            if (ch != '\r') sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>FFMETADATA1 챕터 파일(TIMEBASE 1/1000 · END = 다음 시작 또는 총 길이).</summary>
    public static string BuildFfMetadata(string title, IReadOnlyList<ChapterMark> chapters, double totalSec)
    {
        var sb = new StringBuilder(";FFMETADATA1\ntitle=").Append(EscapeMeta(title)).Append('\n');
        for (var i = 0; i < chapters.Count; i++)
        {
            var start = (long)Math.Round(chapters[i].StartSec * 1000);
            var end = (long)Math.Round((i + 1 < chapters.Count ? chapters[i + 1].StartSec : totalSec) * 1000);
            if (end <= start) continue;
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\nSTART=").Append(start.ToString(CultureInfo.InvariantCulture))
              .Append("\nEND=").Append(end.ToString(CultureInfo.InvariantCulture)).Append("\ntitle=").Append(EscapeMeta(chapters[i].Title)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Ts(double sec, char frac)
    {
        var ms = (long)Math.Round(Math.Max(0, sec) * 1000);
        return (ms / 3_600_000).ToString("00", CultureInfo.InvariantCulture) + ":" + (ms / 60_000 % 60).ToString("00", CultureInfo.InvariantCulture) + ":"
             + (ms / 1000 % 60).ToString("00", CultureInfo.InvariantCulture) + frac + (ms % 1000).ToString("000", CultureInfo.InvariantCulture);
    }

    public static string BuildSrt(IReadOnlyList<FilmSubtitleCue> cues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n').Append(Ts(cues[i].Start, ',')).Append(" --> ").Append(Ts(cues[i].End, ',')).Append('\n').Append(cues[i].Text).Append("\n\n");
        return sb.ToString();
    }

    public static string BuildVtt(IReadOnlyList<FilmSubtitleCue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues) sb.Append(Ts(c.Start, '.')).Append(" --> ").Append(Ts(c.End, '.')).Append('\n').Append(c.Text).Append("\n\n");
        return sb.ToString();
    }

    /// <summary>SRT 파싱(관용적 · 번호 생략 허용).</summary>
    public static List<FilmSubtitleCue> ParseSrt(string srt)
    {
        var list = new List<FilmSubtitleCue>();
        var lines = srt.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var arrow = lines[i].IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            if (!TryTs(lines[i].AsSpan(0, arrow).Trim(), out var a) || !TryTs(lines[i].AsSpan(arrow + 3).Trim(), out var b)) continue;
            var sb = new StringBuilder();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++) { if (sb.Length > 0) sb.Append('\n'); sb.Append(lines[i].Trim()); }
            list.Add(new FilmSubtitleCue(a, b, sb.ToString()));
        }
        return list;
    }

    private static bool TryTs(ReadOnlySpan<char> s, out double sec)
    {
        sec = 0;
        var sp = s.IndexOf(' '); if (sp > 0) s = s[..sp];
        if (s.Length < 8) return false;
        Span<int> parts = stackalloc int[4];
        var n = 0; var acc = 0; var digits = 0;
        foreach (var ch in s)
        {
            if (char.IsAsciiDigit(ch)) { acc = acc * 10 + (ch - '0'); digits++; continue; }
            if (n >= 3 || digits == 0) return false;
            parts[n++] = acc; acc = 0; digits = 0;
        }
        if (digits == 0 || n != 3) return false;
        parts[3] = acc;
        sec = parts[0] * 3600 + parts[1] * 60 + parts[2] + parts[3] / 1000.0;
        return true;
    }

    /// <summary>대본 대사 → 자막 큐(씬 길이 내 대사 길이 비례 배분 · offset 가산).</summary>
    public static List<FilmSubtitleCue> CuesFromScript(ScreenplayDoc doc, double offset = 0)
    {
        var cues = new List<FilmSubtitleCue>();
        var t = offset;
        foreach (var sc in doc.Scenes)
        {
            var sec = sc.Seconds > 0 ? sc.Seconds : 6;
            double weight = 0;
            foreach (var e in sc.Elements) if (e.Kind == ScriptElementKind.Dialogue) weight += Math.Max(4, CinematicText(e.Text).Length);
            if (weight > 0)
            {
                var cur = t;
                foreach (var e in sc.Elements)
                {
                    if (e.Kind != ScriptElementKind.Dialogue) continue;
                    var text = CinematicText(e.Text);
                    var d = sec * Math.Max(4, text.Length) / weight;
                    cues.Add(new FilmSubtitleCue(Math.Round(cur, 3), Math.Round(cur + d - 0.05, 3), (e.Character is null ? "" : e.Character + ": ") + text));
                    cur += d;
                }
            }
            t += sec;
        }
        return cues;
    }

    /// <summary>[[주석]] 제거 · 공백 정리.</summary>
    public static string CinematicText(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var p = s.IndexOf("[[", i, StringComparison.Ordinal);
            if (p < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, p - i);
            var e = s.IndexOf("]]", p, StringComparison.Ordinal);
            i = e < 0 ? s.Length : e + 2;
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string BuildConcatList(IEnumerable<string> files)
    {
        var sb = new StringBuilder("ffconcat version 1.0\n");
        foreach (var f in files) sb.Append("file '").Append(Path.GetFullPath(f).Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
        return sb.ToString();
    }

    public static string LangCode(string lang) => lang.ToLowerInvariant() switch
    {
        "ko" or "kor" => "kor", "en" or "eng" => "eng", "ja" or "jpn" => "jpn", "zh" or "chi" or "zho" => "zho", "es" or "spa" => "spa", "fr" or "fra" => "fra", "de" or "deu" => "deu",
        var x => x.Length == 3 ? x : "und",
    };

    /// <summary>마스터 MP4 ffmpeg 인자(concat 입력 · 추가 오디오 · 자막 · 챕터 메타 · 언어 태그).</summary>
    public static List<string> BuildMasterArgs(string concatPath, string metaPath, IReadOnlyList<AudioTrackSpec> audio, IReadOnlyList<string> srtPaths, IReadOnlyList<string> srtLangs, string output)
    {
        var a = new List<string> { "-y", "-hide_banner", "-f", "concat", "-safe", "0", "-i", concatPath };
        foreach (var t in audio) { a.Add("-i"); a.Add(t.Path); }
        foreach (var s in srtPaths) { a.Add("-i"); a.Add(s); }
        a.Add("-i"); a.Add(metaPath);
        var metaIdx = 1 + audio.Count + srtPaths.Count;
        a.AddRange(new[] { "-map", "0:v:0", "-map", "0:a?" });
        for (var i = 0; i < audio.Count; i++) { a.Add("-map"); a.Add((1 + i).ToString(CultureInfo.InvariantCulture) + ":a:0"); }
        for (var i = 0; i < srtPaths.Count; i++) { a.Add("-map"); a.Add((1 + audio.Count + i).ToString(CultureInfo.InvariantCulture) + ":s:0"); }
        a.AddRange(new[] { "-map_metadata", metaIdx.ToString(CultureInfo.InvariantCulture), "-map_chapters", metaIdx.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-c:s", "mov_text" });
        for (var i = 0; i < audio.Count; i++)
        {
            var idx = (1 + i).ToString(CultureInfo.InvariantCulture);
            a.Add("-metadata:s:a:" + idx); a.Add("language=" + LangCode(audio[i].Language));
            a.Add("-metadata:s:a:" + idx); a.Add("title=" + audio[i].Label);
        }
        for (var i = 0; i < srtPaths.Count; i++) { a.Add("-metadata:s:s:" + i.ToString(CultureInfo.InvariantCulture)); a.Add("language=" + LangCode(srtLangs[i])); }
        a.AddRange(new[] { "-movflags", "+faststart", output });
        return a;
    }

    public static List<string> BuildHlsVideoArgs(string input, int height, int segSec, string dir)
    {
        var kbps = height >= 1080 ? 5000 : height >= 720 ? 2800 : 1200;
        return new List<string> { "-y", "-hide_banner", "-i", input, "-map", "0:v:0", "-an", "-vf", "scale=-2:" + height.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "veryfast", "-b:v", kbps.ToString(CultureInfo.InvariantCulture) + "k", "-g", (segSec * 24).ToString(CultureInfo.InvariantCulture), "-sc_threshold", "0",
            "-hls_time", segSec.ToString(CultureInfo.InvariantCulture), "-hls_playlist_type", "vod", "-hls_segment_filename", Path.Combine(dir, "seg_%04d.ts"), Path.Combine(dir, "index.m3u8") };
    }

    public static List<string> BuildHlsAudioArgs(string input, int audioIndex, int segSec, string dir)
        => new() { "-y", "-hide_banner", "-i", input, "-map", "0:a:" + audioIndex.ToString(CultureInfo.InvariantCulture), "-vn", "-c:a", "aac", "-b:a", "160k",
            "-hls_time", segSec.ToString(CultureInfo.InvariantCulture), "-hls_playlist_type", "vod", "-hls_segment_filename", Path.Combine(dir, "seg_%04d.aac"), Path.Combine(dir, "index.m3u8") };

    /// <summary>WebVTT 단일 세그먼트 미디어 플레이리스트.</summary>
    public static string BuildSubtitlePlaylist(double durationSec, string vttName)
    {
        var td = Math.Max(1, (int)Math.Ceiling(durationSec));
        return "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:" + td.ToString(CultureInfo.InvariantCulture) + "\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:"
             + durationSec.ToString("0.000", CultureInfo.InvariantCulture) + ",\n" + vttName + "\n#EXT-X-ENDLIST\n";
    }

    /// <summary>HLS 마스터 플레이리스트(오디오/자막 렌디션 그룹 · 해상도 래더).</summary>
    public static string BuildMasterPlaylist(IReadOnlyList<int> heights, IReadOnlyList<(string Lang, string Label)> audio, IReadOnlyList<(string Lang, string Label)> subs)
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        for (var i = 0; i < audio.Count; i++)
            sb.Append("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",LANGUAGE=\"").Append(audio[i].Lang).Append("\",NAME=\"").Append(audio[i].Label).Append("\",DEFAULT=").Append(i == 0 ? "YES" : "NO")
              .Append(",AUTOSELECT=YES,URI=\"audio_").Append(audio[i].Lang).Append("/index.m3u8\"\n");
        for (var i = 0; i < subs.Count; i++)
            sb.Append("#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",LANGUAGE=\"").Append(subs[i].Lang).Append("\",NAME=\"").Append(subs[i].Label).Append("\",DEFAULT=").Append(i == 0 ? "YES" : "NO")
              .Append(",AUTOSELECT=YES,URI=\"subs_").Append(subs[i].Lang).Append("/index.m3u8\"\n");
        foreach (var h in heights)
        {
            var w = (int)Math.Round(h * 16 / 9.0 / 2) * 2;
            var bw = (h >= 1080 ? 5000 : h >= 720 ? 2800 : 1200) * 1000 + 160_000;
            sb.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(bw.ToString(CultureInfo.InvariantCulture)).Append(",RESOLUTION=").Append(w.ToString(CultureInfo.InvariantCulture)).Append('x').Append(h.ToString(CultureInfo.InvariantCulture))
              .Append(",CODECS=\"avc1.640028,mp4a.40.2\"");
            if (audio.Count > 0) sb.Append(",AUDIO=\"aud\"");
            if (subs.Count > 0) sb.Append(",SUBTITLES=\"subs\"");
            sb.Append('\n').Append("v").Append(h.ToString(CultureInfo.InvariantCulture)).Append("/index.m3u8\n");
        }
        return sb.ToString();
    }

    public static List<string> BuildDcpPictureArgs(string input, int fps, string output)
        => new() { "-y", "-hide_banner", "-i", input, "-an", "-r", fps.ToString(CultureInfo.InvariantCulture),
            "-vf", "scale=" + DcpWidth.ToString(CultureInfo.InvariantCulture) + ":" + DcpHeight.ToString(CultureInfo.InvariantCulture) + ":force_original_aspect_ratio=decrease,pad=" + DcpWidth.ToString(CultureInfo.InvariantCulture) + ":" + DcpHeight.ToString(CultureInfo.InvariantCulture) + ":(ow-iw)/2:(oh-ih)/2",
            "-c:v", "jpeg2000", "-pix_fmt", "rgb24", "-f", "mxf", output };

    public static List<string> BuildDcpSoundArgs(string input, string output)
        => new() { "-y", "-hide_banner", "-i", input, "-vn", "-c:a", "pcm_s24le", "-ar", "48000", "-ac", "2", "-f", "mxf", output };

    public sealed record DcpAsset(Guid Id, string FileName, long Size, string Sha1Base64, string Type, int Frames);

    /// <summary>파일 SHA-1(Base64 · PKL) — ArrayPool 스트리밍.</summary>
    public static string Sha1Base64(string path) => Convert.ToBase64String(HashFile(path, HashAlgorithmName.SHA1));

    public static byte[] HashFile(string path, HashAlgorithmName alg)
    {
        using var ih = IncrementalHash.CreateHash(alg);
        var buf = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var fs = File.OpenRead(path);
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0) ih.AppendData(buf, 0, n);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
        return ih.GetHashAndReset();
    }

    /// <summary>DCP Interop XML(CPL · PKL · ASSETMAP · VOLINDEX) 작성 — 릴 = (영상, 음향) 쌍. 반환 = 작성 파일.</summary>
    public static List<string> WriteDcpXml(string dir, string title, int fps, IReadOnlyList<(DcpAsset Picture, DcpAsset? Sound)> reels)
    {
        var inv = CultureInfo.InvariantCulture;
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", inv);
        var cplId = Guid.NewGuid(); var pklId = Guid.NewGuid(); var amId = Guid.NewGuid();
        var cpl = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<CompositionPlaylist xmlns=\"http://www.digicine.com/PROTO-ASDCP-CPL-20040511#\">\n")
            .Append("  <Id>urn:uuid:").Append(cplId).Append("</Id>\n  <AnnotationText>").Append(Xml(title)).Append("</AnnotationText>\n  <IssueDate>").Append(now)
            .Append("</IssueDate>\n  <Issuer>PromptFlow Studio</Issuer>\n  <Creator>PromptFlow Studio v16.41.0</Creator>\n  <ContentTitleText>").Append(Xml(title))
            .Append("</ContentTitleText>\n  <ContentKind>feature</ContentKind>\n  <ReelList>\n");
        foreach (var (pic, snd) in reels)
        {
            cpl.Append("    <Reel>\n      <Id>urn:uuid:").Append(Guid.NewGuid()).Append("</Id>\n      <AssetList>\n");
            AppendCplAsset(cpl, "MainPicture", pic, fps);
            if (snd is not null) AppendCplAsset(cpl, "MainSound", snd, fps);
            cpl.Append("      </AssetList>\n    </Reel>\n");
        }
        cpl.Append("  </ReelList>\n</CompositionPlaylist>\n");
        var cplName = "CPL_" + cplId.ToString("N")[..8] + ".xml";
        var cplPath = Path.Combine(dir, cplName);
        File.WriteAllText(cplPath, cpl.ToString(), new UTF8Encoding(false));
        var cplAsset = new DcpAsset(cplId, cplName, new FileInfo(cplPath).Length, Sha1Base64(cplPath), "text/xml;asdcpKind=CPL", 0);

        var all = new List<DcpAsset> { cplAsset };
        foreach (var (p, s) in reels) { all.Add(p); if (s is not null) all.Add(s); }
        var pkl = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<PackingList xmlns=\"http://www.digicine.com/PROTO-ASDCP-PKL-20040311#\">\n  <Id>urn:uuid:")
            .Append(pklId).Append("</Id>\n  <AnnotationText>").Append(Xml(title)).Append("</AnnotationText>\n  <IssueDate>").Append(now).Append("</IssueDate>\n  <Issuer>PromptFlow Studio</Issuer>\n  <Creator>PromptFlow Studio v16.41.0</Creator>\n  <AssetList>\n");
        foreach (var a in all)
            pkl.Append("    <Asset>\n      <Id>urn:uuid:").Append(a.Id).Append("</Id>\n      <Hash>").Append(a.Sha1Base64).Append("</Hash>\n      <Size>").Append(a.Size.ToString(inv))
               .Append("</Size>\n      <Type>").Append(a.Type).Append("</Type>\n    </Asset>\n");
        pkl.Append("  </AssetList>\n</PackingList>\n");
        var pklName = "PKL_" + pklId.ToString("N")[..8] + ".xml";
        File.WriteAllText(Path.Combine(dir, pklName), pkl.ToString(), new UTF8Encoding(false));

        var am = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<AssetMap xmlns=\"http://www.digicine.com/PROTO-ASDCP-AM-20040311#\">\n  <Id>urn:uuid:").Append(amId)
            .Append("</Id>\n  <Creator>PromptFlow Studio v16.41.0</Creator>\n  <VolumeCount>1</VolumeCount>\n  <IssueDate>").Append(now).Append("</IssueDate>\n  <Issuer>PromptFlow Studio</Issuer>\n  <AssetList>\n");
        AppendMap(am, pklId, pklName, true);
        foreach (var a in all) AppendMap(am, a.Id, a.FileName, false);
        am.Append("  </AssetList>\n</AssetMap>\n");
        File.WriteAllText(Path.Combine(dir, "ASSETMAP"), am.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "VOLINDEX"), "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<VolumeIndex xmlns=\"http://www.digicine.com/PROTO-ASDCP-AM-20040311#\">\n  <Index>1</Index>\n</VolumeIndex>\n", new UTF8Encoding(false));
        return new List<string> { cplPath, Path.Combine(dir, pklName), Path.Combine(dir, "ASSETMAP"), Path.Combine(dir, "VOLINDEX") };

        static void AppendCplAsset(StringBuilder sb, string tag, DcpAsset a, int fps)
            => sb.Append("        <").Append(tag).Append(">\n          <Id>urn:uuid:").Append(a.Id).Append("</Id>\n          <EditRate>").Append(fps.ToString(CultureInfo.InvariantCulture))
                 .Append(" 1</EditRate>\n          <IntrinsicDuration>").Append(a.Frames.ToString(CultureInfo.InvariantCulture)).Append("</IntrinsicDuration>\n          <EntryPoint>0</EntryPoint>\n          <Duration>")
                 .Append(a.Frames.ToString(CultureInfo.InvariantCulture)).Append("</Duration>\n          <Hash>").Append(a.Sha1Base64).Append("</Hash>\n        </").Append(tag).Append(">\n");

        static void AppendMap(StringBuilder sb, Guid id, string file, bool pkl)
        {
            sb.Append("    <Asset>\n      <Id>urn:uuid:").Append(id).Append("</Id>\n");
            if (pkl) sb.Append("      <PackingList>true</PackingList>\n");
            sb.Append("      <ChunkList>\n        <Chunk>\n          <Path>").Append(Xml(file)).Append("</Path>\n        </Chunk>\n      </ChunkList>\n    </Asset>\n");
        }
    }

    private static string Xml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>타이틀 → 파일 안전 슬러그.</summary>
    public static string Slug(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim()) sb.Append(char.IsLetterOrDigit(ch) ? ch : ch is ' ' or '-' or '_' ? '_' : '_');
        var r = sb.ToString().Trim('_');
        return r.Length == 0 ? "master" : r.Length > 48 ? r[..48] : r;
    }

    // ══════════════════════ 파이프라인 ══════════════════════

    /// <summary>1-Click 마스터링(단계별 독립 · 실패 시 부분 구제).</summary>
    public async Task<MasteringResult> PackageAsync(IReadOnlyList<EpisodeMaster> episodes, MasteringOptions o, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return new MasteringResult(false, false, "", Array.Empty<string>(), new[] { "이미 마스터링 중" }, 0, 0, "");
        var files = new List<string>();
        var warn = new List<string>();
        var folder = Path.Combine(_root, Slug(o.Title) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(folder);
            var work = Directory.CreateDirectory(Path.Combine(folder, "work")).FullName;
            var ffmpeg = _ffmpeg();
            if (ffmpeg is null) warn.Add("ffmpeg 미가용 — 영상 인코딩(MP4/HLS 세그먼트/DCP MXF) 생략 · 메타데이터/자막/플레이리스트 [부분 구제]");
            Report(0.02, "타임라인 집계");

            // 1) 타임라인 · 챕터
            var epStart = new double[episodes.Count];
            var epDur = new double[episodes.Count];
            double total = 0;
            for (var i = 0; i < episodes.Count; i++)
            {
                double d = 0;
                foreach (var s in episodes[i].ClipSeconds) d += Math.Max(0, s);
                epStart[i] = total; epDur[i] = d; total += d;
            }
            var chapters = new List<ChapterMark>();
            for (var i = 0; i < episodes.Count; i++)
            {
                if (o.Mode == PackageMode.FeatureFilm) chapters.Add(new ChapterMark(epStart[i], episodes[i].Title));
                if (episodes[i].Chapters is { } inner)
                    foreach (var c in inner)
                        if ((c.StartSec > 0 || o.Mode == PackageMode.Series) && c.StartSec < epDur[i]) chapters.Add(new ChapterMark((o.Mode == PackageMode.FeatureFilm ? epStart[i] : 0) + c.StartSec, (o.Mode == PackageMode.FeatureFilm ? episodes[i].Title + " · " : "") + c.Title));
            }
            if (o.Mode == PackageMode.FeatureFilm) chapters.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
            var meta = Path.Combine(folder, "chapters.ffmeta");
            await File.WriteAllTextAsync(meta, BuildFfMetadata(o.Title, chapters, total), new UTF8Encoding(false), ct).ConfigureAwait(false);
            files.Add(meta);

            // 2) 자막(SRT/VTT · 언어별)
            var subs = o.Subtitles ?? Array.Empty<SubtitleTrackSpec>();
            var srtPaths = new List<string>(); var srtLangs = new List<string>();
            var subDir = Directory.CreateDirectory(Path.Combine(folder, "subtitles")).FullName;
            foreach (var s in subs)
            {
                var lang = LangCode(s.Language);
                var srt = Path.Combine(subDir, Slug(o.Title) + "." + lang + ".srt");
                var vtt = Path.ChangeExtension(srt, ".vtt");
                await File.WriteAllTextAsync(srt, BuildSrt(s.Cues), new UTF8Encoding(false), ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(vtt, BuildVtt(s.Cues), new UTF8Encoding(false), ct).ConfigureAwait(false);
                files.Add(srt); files.Add(vtt); srtPaths.Add(srt); srtLangs.Add(lang);
            }
            Report(0.1, "에피소드 메자닌");

            // 3) 에피소드 메자닌(클립 concat)
            var mezz = new List<string?>(episodes.Count);
            for (var i = 0; i < episodes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var list = Path.Combine(work, "ep" + (i + 1).ToString("00", CultureInfo.InvariantCulture) + ".ffconcat");
                var existing = new List<string>();
                foreach (var c in episodes[i].Clips) if (File.Exists(c)) existing.Add(c); else warn.Add("클립 없음 · " + episodes[i].Title + " · " + Path.GetFileName(c));
                await File.WriteAllTextAsync(list, BuildConcatList(existing), new UTF8Encoding(false), ct).ConfigureAwait(false);
                string? outEp = null;
                if (ffmpeg is not null && existing.Count > 0)
                {
                    var target = Path.Combine(work, "ep" + (i + 1).ToString("00", CultureInfo.InvariantCulture) + ".mp4");
                    var r = await ProcessRunner.RunAsync(ffmpeg, new[] { "-y", "-hide_banner", "-f", "concat", "-safe", "0", "-i", list, "-c:v", "libx264", "-crf", "16", "-pix_fmt", "yuv420p", "-c:a", "aac", "-r", o.Fps.ToString(CultureInfo.InvariantCulture), target }, ct: ct).ConfigureAwait(false);
                    if (r.Started && r.ExitCode == 0 && File.Exists(target)) outEp = target; else warn.Add("메자닌 실패 · " + episodes[i].Title);
                }
                mezz.Add(outEp);
                Report(0.1 + 0.25 * (i + 1) / Math.Max(1, episodes.Count), "에피소드 메자닌 " + (i + 1).ToString(CultureInfo.InvariantCulture));
            }

            // 4) MP4 마스터(장편 1개 · 시리즈 에피소드별)
            var masters = new List<(string Name, string? Path, double Dur)>();
            var audio = o.Audio ?? Array.Empty<AudioTrackSpec>();
            if (o.Mp4 || o.Hls)
            {
                var units = new List<(string Name, List<string> Inputs, double Dur)>();
                if (o.Mode == PackageMode.FeatureFilm)
                {
                    var all = new List<string>();
                    foreach (var m in mezz) if (m is not null) all.Add(m);
                    units.Add((Slug(o.Title), all, total));
                }
                else
                    for (var i = 0; i < episodes.Count; i++)
                        units.Add(("EP" + (i + 1).ToString("00", CultureInfo.InvariantCulture) + "_" + Slug(episodes[i].Title), mezz[i] is { } m ? new List<string> { m } : new List<string>(), epDur[i]));
                foreach (var u in units)
                {
                    ct.ThrowIfCancellationRequested();
                    var list = Path.Combine(work, u.Name + ".ffconcat");
                    await File.WriteAllTextAsync(list, BuildConcatList(u.Inputs), new UTF8Encoding(false), ct).ConfigureAwait(false);
                    string? outPath = null;
                    if (ffmpeg is not null && u.Inputs.Count > 0)
                    {
                        var target = Path.Combine(folder, u.Name + ".mp4");
                        var r = await ProcessRunner.RunAsync(ffmpeg, BuildMasterArgs(list, meta, audio, srtPaths, srtLangs, target), ct: ct).ConfigureAwait(false);
                        if (r.Started && r.ExitCode == 0 && File.Exists(target)) { outPath = target; files.Add(target); } else warn.Add("MP4 마스터 실패 · " + u.Name);
                    }
                    masters.Add((u.Name, outPath, u.Dur));
                }
            }
            Report(0.5, "HLS 패키징");

            // 5) HLS(마스터별 폴더 · 래더 · 오디오/자막 렌디션)
            if (o.Hls)
            {
                var ladder = o.HlsHeights ?? DefaultLadder;
                foreach (var m in masters)
                {
                    var hls = Directory.CreateDirectory(Path.Combine(folder, "hls", m.Name)).FullName;
                    var audioGroups = new List<(string, string)>();
                    for (var i = 0; i < audio.Count; i++) audioGroups.Add((LangCode(audio[i].Language), audio[i].Label));
                    var subGroups = new List<(string, string)>();
                    for (var i = 0; i < subs.Count; i++)
                    {
                        var lang = LangCode(subs[i].Language);
                        var d = Directory.CreateDirectory(Path.Combine(hls, "subs_" + lang)).FullName;
                        var shift = 0.0;
                        if (o.Mode == PackageMode.Series)
                        {
                            var ei = masters.IndexOf(m);
                            shift = ei >= 0 && ei < epStart.Length ? epStart[ei] : 0;
                        }
                        var cues = new List<FilmSubtitleCue>();
                        foreach (var c in subs[i].Cues) if (c.End > shift && c.Start < shift + m.Dur) cues.Add(new FilmSubtitleCue(Math.Max(0, c.Start - shift), c.End - shift, c.Text));
                        await File.WriteAllTextAsync(Path.Combine(d, "subs.vtt"), BuildVtt(cues), new UTF8Encoding(false), ct).ConfigureAwait(false);
                        await File.WriteAllTextAsync(Path.Combine(d, "index.m3u8"), BuildSubtitlePlaylist(m.Dur, "subs.vtt"), new UTF8Encoding(false), ct).ConfigureAwait(false);
                        files.Add(Path.Combine(d, "index.m3u8")); files.Add(Path.Combine(d, "subs.vtt"));
                        subGroups.Add((lang, subs[i].Label));
                    }
                    var masterPl = Path.Combine(hls, "master.m3u8");
                    await File.WriteAllTextAsync(masterPl, BuildMasterPlaylist(ladder, audioGroups, subGroups), new UTF8Encoding(false), ct).ConfigureAwait(false);
                    files.Add(masterPl);
                    if (ffmpeg is null || m.Path is null) { warn.Add("HLS 세그먼트 생략 · " + m.Name); continue; }
                    foreach (var h in ladder)
                    {
                        var vd = Directory.CreateDirectory(Path.Combine(hls, "v" + h.ToString(CultureInfo.InvariantCulture))).FullName;
                        var r = await ProcessRunner.RunAsync(ffmpeg, BuildHlsVideoArgs(m.Path, h, o.HlsSegmentSec, vd), ct: ct).ConfigureAwait(false);
                        if (r.Started && r.ExitCode == 0) files.Add(Path.Combine(vd, "index.m3u8")); else warn.Add("HLS " + h.ToString(CultureInfo.InvariantCulture) + "p 실패 · " + m.Name);
                    }
                    for (var i = 0; i < audio.Count; i++)
                    {
                        var ad = Directory.CreateDirectory(Path.Combine(hls, "audio_" + LangCode(audio[i].Language))).FullName;
                        var r = await ProcessRunner.RunAsync(ffmpeg, BuildHlsAudioArgs(m.Path, 1 + i, o.HlsSegmentSec, ad), ct: ct).ConfigureAwait(false);
                        if (r.Started && r.ExitCode == 0) files.Add(Path.Combine(ad, "index.m3u8")); else warn.Add("HLS 오디오 실패 · " + audio[i].Language);
                    }
                }
            }
            Report(0.75, "DCP 패키징");

            // 6) DCP(릴 = 에피소드)
            if (o.Dcp)
            {
                var dcp = Directory.CreateDirectory(Path.Combine(folder, "DCP_" + Slug(o.Title))).FullName;
                var reels = new List<(DcpAsset, DcpAsset?)>();
                for (var i = 0; i < mezz.Count; i++)
                {
                    if (ffmpeg is null || mezz[i] is not { } src) continue;
                    ct.ThrowIfCancellationRequested();
                    var picName = "reel" + (i + 1).ToString("00", CultureInfo.InvariantCulture) + "_j2c.mxf";
                    var sndName = "reel" + (i + 1).ToString("00", CultureInfo.InvariantCulture) + "_pcm.mxf";
                    var rp = await ProcessRunner.RunAsync(ffmpeg, BuildDcpPictureArgs(src, o.Fps, Path.Combine(dcp, picName)), ct: ct).ConfigureAwait(false);
                    if (!(rp.Started && rp.ExitCode == 0)) { warn.Add("DCP 영상 MXF 실패 · 릴 " + (i + 1).ToString(CultureInfo.InvariantCulture)); continue; }
                    var frames = (int)Math.Round(epDur[i] * o.Fps);
                    var pic = new DcpAsset(Guid.NewGuid(), picName, new FileInfo(Path.Combine(dcp, picName)).Length, Sha1Base64(Path.Combine(dcp, picName)), "application/mxf", frames);
                    DcpAsset? snd = null;
                    var rs = await ProcessRunner.RunAsync(ffmpeg, BuildDcpSoundArgs(src, Path.Combine(dcp, sndName)), ct: ct).ConfigureAwait(false);
                    if (rs.Started && rs.ExitCode == 0) snd = new DcpAsset(Guid.NewGuid(), sndName, new FileInfo(Path.Combine(dcp, sndName)).Length, Sha1Base64(Path.Combine(dcp, sndName)), "application/mxf", frames);
                    else warn.Add("DCP 음향 MXF 실패 · 릴 " + (i + 1).ToString(CultureInfo.InvariantCulture));
                    reels.Add((pic, snd));
                    files.Add(Path.Combine(dcp, picName));
                    if (snd is not null) files.Add(Path.Combine(dcp, sndName));
                }
                if (reels.Count > 0) { files.AddRange(WriteDcpXml(dcp, o.Title, o.Fps, reels)); warn.Add("DCP: Interop 프리뷰(rgb24 J2K · XYZ 변환/KDM 미적용) — 상영관 인증 전 검수 필요"); }
                else warn.Add("DCP 생략 · 릴 MXF 없음");
            }
            Report(0.95, "매니페스트");

            // 7) 매니페스트(SHA-256 · 집계 필드)
            var manifest = Path.Combine(folder, "package.json");
            using (var fs = File.Create(manifest))
            using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("title", o.Title);
                w.WriteString("mode", o.Mode.ToString());
                w.WriteString("generator", "PromptFlow Studio v16.41.0");
                w.WriteString("createdUtc", DateTimeOffset.UtcNow);
                w.WriteNumber("episodes", episodes.Count);
                w.WriteNumber("durationSec", Math.Round(total, 3));
                w.WriteNumber("chapters", chapters.Count);
                w.WriteNumber("audioTracks", audio.Count);
                w.WriteNumber("subtitleTracks", subs.Count);
                w.WriteNumber("fileCount", files.Count);
                w.WriteBoolean("partialRescue", warn.Count > 0);
                w.WriteStartArray("files");
                long totalBytes = 0;
                foreach (var f in files)
                {
                    if (!File.Exists(f)) continue;
                    var len = new FileInfo(f).Length; totalBytes += len;
                    w.WriteStartObject();
                    w.WriteString("path", Path.GetRelativePath(folder, f).Replace('\\', '/'));
                    w.WriteNumber("size", len);
                    w.WriteString("sha256", Convert.ToHexString(HashFile(f, HashAlgorithmName.SHA256)).ToLowerInvariant());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("totalBytes", totalBytes);
                w.WriteStartArray("warnings");
                foreach (var x in warn) w.WriteStringValue(x);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            files.Add(manifest);
            var result = new MasteringResult(true, warn.Count > 0, folder, files, warn, total, chapters.Count, manifest);
            LastResult = result;
            LastAction = (warn.Count > 0 ? "🩹 [부분 구제] 마스터링 · " : "🎞 마스터링 완료 · ") + files.Count.ToString(CultureInfo.InvariantCulture) + "파일 · " + Path.GetFileName(folder);
            Report(1, "완료");
            return result;
        }
        catch (OperationCanceledException)
        {
            LastAction = "⏹ 마스터링 취소";
            Report(0, "취소");
            return LastResult = new MasteringResult(false, files.Count > 0, folder, files, warn, 0, 0, "");
        }
        catch (Exception ex)
        {
            Program.Log("Mastering.Package", ex);
            warn.Add("오류: " + ex.Message);
            LastAction = "⚠ 마스터링 실패 · [부분 구제] " + files.Count.ToString(CultureInfo.InvariantCulture) + "파일 보존";
            Report(0, "실패");
            return LastResult = new MasteringResult(false, files.Count > 0, folder, files, warn, 0, 0, "");
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private void Report(double p, string stage)
    {
        Progress = p; Stage = stage;
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Mastering.OnChange", ex); }
    }
}
