using PromptFlowStudio.Blazor.Components.Common;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>📊 HTS 3대 테마 · 🔆 조도 판정 · ℹ️ 미디어 규격 파싱 · 🧹 렌더 캐시 정리 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class HtsDiagnosticsAndCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_hdc_" + Guid.NewGuid().ToString("N"));

    public HtsDiagnosticsAndCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void MediaProbeService_ExtractsVideoSpecsCorrectly()
    {
        const string h264 = """
        {"streams":[
          {"codec_name":"h264","codec_tag_string":"avc1","codec_type":"video","width":1920,"height":1080,"r_frame_rate":"30/1","avg_frame_rate":"30/1"}
        ],"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"15.000000","bit_rate":"2800000"}}
        """;
        var a = MediaProbeService.ParseFfprobeJson(h264, 5_662_310);
        Assert.NotNull(a);
        Assert.Equal("H.264 (avc1)", a!.CodecLabel);
        Assert.Equal("1920x1080 @ 30fps", a.ResolutionLabel);
        Assert.Equal("2.8 Mbps", a.BitrateLabel);
        Assert.Equal("5.4 MB", a.SizeLabel);
        Assert.False(a.HasAudio);
        Assert.Equal("Muted 🔇 (HTS 최적화)", a.AudioLabel);

        // VP9 WebM + 오디오 · bit_rate 부재 → 크기/길이 산출
        const string vp9 = """
        {"streams":[
          {"codec_name":"vp9","codec_tag_string":"[0][0][0][0]","codec_type":"video","width":1280,"height":720,"avg_frame_rate":"0/0","r_frame_rate":"24000/1001"},
          {"codec_name":"opus","codec_type":"audio"}
        ],"format":{"format_name":"matroska,webm","duration":"10.0"}}
        """;
        var b = MediaProbeService.ParseFfprobeJson(vp9, 2_500_000);
        Assert.NotNull(b);
        Assert.Equal("VP9", b!.CodecLabel);
        Assert.Equal("1280x720 @ 23.98fps", b.ResolutionLabel);
        Assert.Equal("2.0 Mbps", b.BitrateLabel);
        Assert.True(b.HasAudio);
        Assert.Equal("Audio Present 🔊", b.AudioLabel);

        // 비디오 스트림 없음/손상 → null
        Assert.Null(MediaProbeService.ParseFfprobeJson("""{"streams":[{"codec_type":"audio"}]}""", 1));
        Assert.Null(MediaProbeService.ParseFfprobeJson("{not json", 1));
        Assert.Null(MediaProbeService.ParseFfprobeJson(null, 1));

        // 경량 헤더(ffmpeg -i 배너) 폴백
        const string banner = """
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'C:\out\bg.mp4':
          Duration: 00:00:05.00, start: 0.000000, bitrate: 2850 kb/s
          Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 1920x1080 [SAR 1:1 DAR 16:9], 2846 kb/s, 30 fps, 30 tbr, 15360 tbn (default)
        """;
        var c = MediaProbeService.ParseFfmpegBanner(banner, 1_781_250);
        Assert.NotNull(c);
        Assert.Equal("H.264 (avc1)", c!.CodecLabel);
        Assert.Equal("1920x1080 @ 30fps", c.ResolutionLabel);
        Assert.Equal(2_850_000, c.BitrateBps);
        Assert.Equal(5.0, c.DurationSec, 3);
        Assert.False(c.HasAudio);
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", c.Container);
        Assert.True(MediaProbeService.ParseFfmpegBanner(banner + "\n  Stream #0:1: Audio: aac (LC), 48000 Hz, stereo", 1)!.HasAudio);

        // 조도 YAVG 파싱(0~1)
        var y = MediaProbeService.ParseSignalStatsYavg("frame:0\nlavfi.signalstats.YAVG=51.0\nframe:1\nlavfi.signalstats.YAVG=153.0\n");
        Assert.Equal(0.4, y!.Value, 3);
        Assert.Null(MediaProbeService.ParseSignalStatsYavg("no stats"));
    }

    [Fact]
    public async Task MediaProbeService_ProbeAsync_MissingFileReturnsNull_AndToolAbsentFallsBackToHeader()
    {
        var svc = new MediaProbeService(() => Path.Combine(_dir, "nope", "ffmpeg.exe"));
        Assert.Null(await svc.ProbeAsync(Path.Combine(_dir, "missing.mp4")));

        var f = Path.Combine(_dir, "clip.webm");
        File.WriteAllBytes(f, new byte[4096]);
        var spec = await svc.ProbeAsync(f);
        Assert.NotNull(spec);
        Assert.Equal(4096, spec!.FileSizeBytes);
        Assert.Equal("VP9", spec.CodecLabel);
    }

    [Fact]
    public void RenderCacheCleaner_RemovesOrphanTempFilesSuccessfully()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var old = now.AddHours(-2);
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "job1")).FullName;
        string Make(string dir, string name, int len, DateTime at)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllBytes(p, new byte[len]);
            File.SetLastWriteTimeUtc(p, at);
            File.SetLastAccessTimeUtc(p, at);
            return p;
        }

        var tempMp4 = Make(sub, "shot_temp_01.mp4", 1000, old);
        var tmp = Make(_dir, "render.tmp", 200, old);
        var concat = Make(sub, "concat_list.txt", 50, old);
        var synth = Make(sub, "synth_voice.wav", 300, old);
        var lead = Make(_dir, "_temp_part.mp4", 400, old);
        var fresh = Make(sub, "live_temp_02.mp4", 700, now.AddSeconds(-5));   // 진행 중 → 보존
        var final = Make(sub, "final.mp4", 5000, old);
        var notes = Make(sub, "notes.txt", 10, old);

        Assert.True(RenderCacheCleaner.IsTempArtifact("A_TEMP_x.MP4"));
        Assert.False(RenderCacheCleaner.IsTempArtifact("final.mp4"));
        Assert.False(RenderCacheCleaner.IsTempArtifact("template.png"));

        var cleaner = new RenderCacheCleaner(() => new[] { _dir, _dir }, () => 10, () => now);
        Assert.Equal(1000 + 200 + 50 + 300 + 400 + 700 + 5000 + 10, cleaner.MeasureBytes());

        var r = cleaner.Clean();
        Assert.Equal(5, r.TempDeleted);
        Assert.Equal(1950, r.TempBytes);
        Assert.Equal(0, r.PrunedDeleted);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(7660, r.TotalBefore);
        Assert.Equal(7660 - 1950, r.TotalAfter);
        foreach (var gone in new[] { tempMp4, tmp, concat, synth, lead }) Assert.False(File.Exists(gone));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(final));
        Assert.True(File.Exists(notes));
        Assert.Equal(r.TotalAfter, cleaner.MeasureBytes());
        Assert.Contains("회수", r.Summary);
    }

    [Fact]
    public void RenderCacheCleaner_LruPrune_RemovesOldestWithoutMetadataUntilUnderLimit()
    {
        var now = DateTime.UtcNow;
        const int Kb = 1024;
        var cleaner = new RenderCacheCleaner(() => new[] { _dir }, () => 1, () => now) { LimitBytesOverride = 1000L * Kb };
        string Big(string name, long len, DateTime at)
        {
            var p = Path.Combine(_dir, name);
            using (var fs = new FileStream(p, FileMode.Create)) fs.SetLength(len);
            File.SetLastWriteTimeUtc(p, at);
            File.SetLastAccessTimeUtc(p, at);
            return p;
        }
        var oldest = Big("a_old.mp4", 400L * Kb, now.AddDays(-3));
        var withMeta = Big("b_meta.mp4", 400L * Kb, now.AddDays(-4));   // 가장 오래됐지만 메타 보유 → 보호
        File.WriteAllText(Path.ChangeExtension(withMeta, null) + ".meta.json", "{}");
        var mid = Big("c_mid.mp4", 400L * Kb, now.AddDays(-2));
        var newest = Big("d_new.mp4", 400L * Kb, now.AddDays(-1));

        var r = cleaner.Clean();
        Assert.Equal(2, r.PrunedDeleted);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(mid));
        Assert.True(File.Exists(withMeta));
        Assert.True(File.Exists(newest));
        Assert.True(r.TotalAfter <= 1000L * Kb);
    }

    [Fact]
    public void HtsHudOverlay_SwitchesThemesCorrectly()
    {
        Assert.Equal(3, HtsHudThemes.All.Length);
        Assert.Equal("hts-hud hts-t-oled", HtsHudOverlay.RootClass(HtsHudTheme.OledBlack));
        Assert.Equal("hts-hud hts-t-navy", HtsHudOverlay.RootClass(HtsHudTheme.NavyQuant));
        Assert.Equal("hts-hud hts-t-mini", HtsHudOverlay.RootClass(HtsHudTheme.ModernMinimal));
        foreach (var t in HtsHudThemes.All)
            Assert.EndsWith(HtsHudThemes.CssClass(t), HtsHudOverlay.RootClass(t), StringComparison.Ordinal);

        Assert.Equal(10, HtsHudThemes.BookDepth(HtsHudTheme.NavyQuant));
        Assert.Equal(5, HtsHudThemes.BookDepth(HtsHudTheme.OledBlack));
        Assert.True(HtsHudThemes.UsesLineChart(HtsHudTheme.ModernMinimal));
        Assert.False(HtsHudThemes.UsesLineChart(HtsHudTheme.OledBlack));
        Assert.Equal("네이비 퀀트", HtsHudThemes.Label(HtsHudTheme.NavyQuant));

        Assert.Equal(HtsHudTheme.NavyQuant, HtsHudThemes.Parse("navy"));
        Assert.Equal(HtsHudTheme.ModernMinimal, HtsHudThemes.Parse("MODERNMINIMAL"));
        Assert.Equal(HtsHudTheme.ModernMinimal, HtsHudThemes.Parse("2"));
        Assert.Equal(HtsHudTheme.OledBlack, HtsHudThemes.Parse("99"));
        Assert.Equal(HtsHudTheme.OledBlack, HtsHudThemes.Parse(null));

        // 오버레이 속성 클램프 유지
        Assert.Equal(HtsHudOverlay.MaxOpacity, HtsHudOverlay.ClampOpacity(100));
        Assert.Equal(HtsHudOverlay.MinDimmer, HtsHudOverlay.ClampDimmer(-5));
    }

    [Fact]
    public void HtsHudThemes_LuminanceInspector_FlagsBrightBackgrounds()
    {
        var dark = HtsHudThemes.Evaluate(0.25, 20, HtsHudTheme.OledBlack);
        Assert.Equal(HtsReadability.Optimal, dark.Level);
        Assert.StartsWith("🟢 차트 가독성 최적", dark.Badge);
        Assert.Equal("ok", dark.CssClass);

        var bright = HtsHudThemes.Evaluate(0.6, 20, HtsHudTheme.OledBlack);
        Assert.Equal(HtsReadability.Caution, bright.Level);
        Assert.Equal(30, bright.RecommendedDimmer);
        Assert.Equal("🟡 주의: 배경이 다소 밝음 (Dimmer 30% 권장)", bright.Badge);
        Assert.True(bright.ContrastRatio < HtsHudThemes.MinContrast);

        // 권장 Dimmer 적용 시 최적 전환
        Assert.Equal(HtsReadability.Optimal, HtsHudThemes.Evaluate(0.6, bright.RecommendedDimmer, HtsHudTheme.OledBlack).Level);

        var na = HtsHudThemes.Evaluate(null, 20, HtsHudTheme.NavyQuant);
        Assert.Equal(HtsReadability.Unknown, na.Level);
        Assert.Equal("na", na.CssClass);
    }
}
