using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.12.0 SFX 큐 — 해결된 오디오 경로 · 최종 타임라인 시각(초) · 볼륨(0~2).</summary>
public sealed record SfxCue(string Path, double AtSeconds, double Volume = 0.9);

/// <summary>
/// v16.12.0 오디오 믹스 계획 — 영상(무음/유음) + BGM(루핑·페이드아웃) + SFX 큐 → 최종 MP4 먹싱.
///   <see cref="KeepOriginalAudio"/>=true 면 입력 영상의 오디오 스트림도 믹스(없으면 false 권장 — 무음 중간 산출물).
///   v16.14.0: <see cref="Voice"/> 나레이션 트랙 + BGM 사이드체인 덕킹(<see cref="DuckRatio"/>) · <see cref="SubtitlePath"/>(ASS) 번인 시 영상 재인코딩.
/// </summary>
public sealed record AudioMixPlan(
    string VideoPath, string OutputPath, double TotalSeconds,
    string? BgmPath, IReadOnlyList<SfxCue> Sfx,
    double BgmVolume = 0.35, double BgmFadeInSeconds = 0.8, double BgmFadeOutSeconds = 2.0, bool KeepOriginalAudio = false,
    IReadOnlyList<VoiceCue>? Voice = null, double DuckRatio = 8, double VoiceVolume = 1.0,
    string? SubtitlePath = null, string? FontsDir = null);

/// <summary>
/// [PromptFlowStudio] v16.12.0 🎚️ Auto-Foley + BGM 멀티트랙 믹싱 파이프라인 — ffmpeg filter_complex 래퍼(순수 BCL).
///   ▸ BGM: <c>-stream_loop -1</c> 로 무한 루핑 → atrim 으로 영상 길이에 정확히 절단 → afade in/out(끝 2초) · 볼륨.
///   ▸ SFX: 고유 파일당 입력 1개(중복 사용은 asplit) → adelay(ms, 양 채널) · volume → amix(normalize=0, duration=longest) → atrim.
///   ▸ 먹싱: 영상은 -c:v copy(재인코딩 0) · 오디오 aac 192k · +faststart. 오디오 소스 전무면 영상 그대로 copy.
///   ▸ <see cref="BuildCues"/>: StoryboardProject 컷별 authored 폴리(PresetFoley · 컷-로컬 ms) 우선, 없으면 CinematicSfx 토큰을 컷 시작+0.25초에 순환 배치.
///     SFX 파일은 <see cref="SfxResolver.FindByType"/>(데이터폴더 → 앱번들) — 미해결 토큰은 건너뜀(Graceful). 큐 상한 <see cref="MaxCues"/>.
/// </summary>
public sealed class FfmpegAudioMixerService
{
    /// <summary>SFX 큐 상한(ffmpeg 입력/필터 그래프 폭주 방지).</summary>
    public const int MaxCues = 32;

    /// <summary>authored 폴리가 없을 때 CinematicSfx 를 놓는 컷-로컬 지연(초).</summary>
    public const double CinematicCueOffset = 0.25;

    /// <summary>v16.14.0 나레이션 큐 상한.</summary>
    public const int MaxVoiceCues = 48;

    /// <summary>v16.14.0 덕킹 사이드체인(나레이션 감지 시 BGM 압축 · 빠른 어택/완만한 릴리스).</summary>
    public const string DuckFilter = "sidechaincompress=threshold=0.03:attack=15:release=350:makeup=1:ratio=";

    private readonly Func<string?> _ffmpeg;

    public FfmpegAudioMixerService(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    /// <param name="ffmpegPath">ffmpeg 경로 공급자(테스트 주입용).</param>
    public FfmpegAudioMixerService(Func<string?> ffmpegPath) => _ffmpeg = ffmpegPath;

    public bool IsAvailable => _ffmpeg() is not null;

    /// <summary>
    /// 프로젝트 메타 → SFX 큐(최종 타임라인 시각). <paramref name="resolve"/> 는 토큰→파일 경로(null=미해결 · 테스트 주입).
    /// </summary>
    public static List<SfxCue> BuildCues(StoryboardProject project, IReadOnlyList<StoryboardScene> scenes, MixTimeline timeline,
        Func<string, string?>? resolve = null)
    {
        resolve ??= SfxResolver.FindByType;
        var cues = new List<SfxCue>(Math.Min(MaxCues, scenes.Count * 3));
        var cinematic = project.CinematicSfx;
        var rotate = 0;
        for (var i = 0; i < scenes.Count && i < timeline.CutStarts.Count && cues.Count < MaxCues; i++)
        {
            var start = timeline.CutStarts[i];
            var foley = scenes[i].PresetFoley;
            if (foley is { Count: > 0 })
            {
                for (var k = 0; k < foley.Count && cues.Count < MaxCues; k++)
                {
                    var f = foley[k];
                    var path = string.IsNullOrWhiteSpace(f.SfxType) ? null : resolve(f.SfxType);
                    var at = start + Math.Max(0, f.TimestampMs) / 1000.0;
                    if (path is not null && at < timeline.TotalSeconds) cues.Add(new SfxCue(path, Math.Round(at, 3)));
                }
            }
            else if (cinematic is { Count: > 0 })
            {
                var token = cinematic[rotate++ % cinematic.Count];
                var path = string.IsNullOrWhiteSpace(token) ? null : resolve(token);
                var at = start + CinematicCueOffset;
                if (path is not null && at < timeline.TotalSeconds) cues.Add(new SfxCue(path, Math.Round(at, 3), 0.8));
            }
        }
        return cues;
    }

    /// <summary>ffmpeg 인자 생성(순수 함수 · 파일 접근 0).</summary>
    public static List<string> BuildArgs(AudioMixPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var total = Math.Max(0.1, plan.TotalSeconds);
        var hasBgm = !string.IsNullOrWhiteSpace(plan.BgmPath);
        var args = new List<string>(32) { "-y", "-hide_banner", "-nostdin", "-i", plan.VideoPath };
        var next = 1;
        var bgmIdx = -1;
        if (hasBgm) { args.Add("-stream_loop"); args.Add("-1"); args.Add("-i"); args.Add(plan.BgmPath!); bgmIdx = next++; }

        // 고유 SFX 파일 → 입력 인덱스 · 사용 횟수(asplit 분기 수)
        var files = new List<string>(8);
        var uses = new List<int>(8);
        var cueFile = new int[Math.Min(plan.Sfx.Count, MaxCues)];
        for (var c = 0; c < cueFile.Length; c++)
        {
            var p = plan.Sfx[c].Path;
            var f = files.IndexOf(p);
            if (f < 0) { f = files.Count; files.Add(p); uses.Add(0); }
            uses[f]++;
            cueFile[c] = f;
        }
        var sfxBase = next;
        for (var f = 0; f < files.Count; f++) { args.Add("-i"); args.Add(files[f]); next++; }
        var voiceCount = plan.Voice is null ? 0 : Math.Min(plan.Voice.Count, MaxVoiceCues);
        var voiceBase = next;
        for (var v = 0; v < voiceCount; v++) { args.Add("-i"); args.Add(plan.Voice![v].Path); next++; }
        var duck = hasBgm && voiceCount > 0 && plan.DuckRatio > 1;

        var fc = new StringBuilder(512 + cueFile.Length * 120);
        var mixInputs = new List<string>(cueFile.Length + 2);
        if (plan.KeepOriginalAudio) { fc.Append("[0:a]aresample=48000,asetpts=PTS-STARTPTS[orig];"); mixInputs.Add("orig"); }
        if (hasBgm)
        {
            var fadeIn = Math.Min(plan.BgmFadeInSeconds, total / 4);
            var fadeOut = Math.Min(plan.BgmFadeOutSeconds, total / 3);
            fc.Append('[').Append(bgmIdx).Append(":a]aresample=48000,atrim=0:").Append(Inv(total)).Append(",asetpts=PTS-STARTPTS,volume=")
              .Append(Inv(Math.Clamp(plan.BgmVolume, 0, 2)));
            if (fadeIn > 0.01) fc.Append(",afade=t=in:st=0:d=").Append(Inv(fadeIn));
            if (fadeOut > 0.01) fc.Append(",afade=t=out:st=").Append(Inv(Math.Max(0, total - fadeOut))).Append(":d=").Append(Inv(fadeOut));
            fc.Append(duck ? "[bgmraw];" : "[bgm];");
            mixInputs.Add("bgm");
        }
        for (var f = 0; f < files.Count; f++)
        {
            fc.Append('[').Append(sfxBase + f).Append(":a]aresample=48000");
            if (uses[f] > 1)
            {
                fc.Append(",asplit=").Append(uses[f]);
                for (var u = 0; u < uses[f]; u++) fc.Append("[f").Append(f).Append('_').Append(u).Append(']');
                fc.Append(';');
            }
            else fc.Append("[f").Append(f).Append("_0];");
        }
        var taken = new int[files.Count];
        for (var c = 0; c < cueFile.Length; c++)
        {
            var f = cueFile[c];
            var ms = (long)Math.Round(Math.Max(0, plan.Sfx[c].AtSeconds) * 1000);
            fc.Append("[f").Append(f).Append('_').Append(taken[f]++).Append("]adelay=").Append(ms).Append('|').Append(ms)
              .Append(",volume=").Append(Inv(Math.Clamp(plan.Sfx[c].Volume, 0, 2))).Append("[s").Append(c).Append("];");
            mixInputs.Add("s" + c.ToString(CultureInfo.InvariantCulture));
        }
        if (voiceCount > 0)
        {
            // 나레이션: 큐별 adelay → 단일 [vo] 버스 → (덕킹 시) asplit 으로 사이드체인 키 분기
            for (var v = 0; v < voiceCount; v++)
            {
                var ms = (long)Math.Round(Math.Max(0, plan.Voice![v].AtSeconds) * 1000);
                fc.Append('[').Append(voiceBase + v).Append(":a]aresample=48000,aformat=channel_layouts=stereo,adelay=").Append(ms).Append('|').Append(ms)
                  .Append(",volume=").Append(Inv(Math.Clamp(plan.Voice[v].Volume * plan.VoiceVolume, 0, 2))).Append("[v").Append(v).Append("];");
            }
            for (var v = 0; v < voiceCount; v++) fc.Append("[v").Append(v).Append(']');
            if (voiceCount > 1) fc.Append("amix=inputs=").Append(voiceCount).Append(":duration=longest:dropout_transition=0:normalize=0");
            else fc.Append("anull");
            if (duck)
            {
                fc.Append(",asplit=2[vo][vkey];[bgmraw][vkey]").Append(DuckFilter).Append(Inv(Math.Clamp(plan.DuckRatio, 1, 20))).Append("[bgm];");
            }
            else fc.Append("[vo];");
            mixInputs.Add("vo");
        }
        var burn = !string.IsNullOrWhiteSpace(plan.SubtitlePath);
        if (burn) fc.Append("[0:v]").Append(AutoSubtitleGenerator.BuildBurnFilter(plan.SubtitlePath!, plan.FontsDir)).Append(",format=yuv420p[vout];");

        args.Add("-map"); args.Add(burn ? "[vout]" : "0:v");
        if (mixInputs.Count == 0 && burn)
        {
            fc.Length--;   // 마지막 ';' 제거(필터 그래프 = 번인 체인만)
            args.Add("-filter_complex"); args.Add(fc.ToString());
            if (plan.KeepOriginalAudio) { args.Add("-map"); args.Add("0:a?"); args.Add("-c:a"); args.Add("copy"); }
            AddVideoEncode(args);
        }
        else if (mixInputs.Count == 0)
        {
            if (plan.KeepOriginalAudio) { args.Add("-map"); args.Add("0:a?"); }
            args.Add("-c"); args.Add("copy");
        }
        else
        {
            if (mixInputs.Count == 1) fc.Append('[').Append(mixInputs[0]).Append(']').Append("anull");
            else
            {
                for (var m = 0; m < mixInputs.Count; m++) fc.Append('[').Append(mixInputs[m]).Append(']');
                fc.Append("amix=inputs=").Append(mixInputs.Count).Append(":duration=longest:dropout_transition=0:normalize=0");
            }
            fc.Append(",atrim=0:").Append(Inv(total)).Append(",alimiter=limit=0.95[aout]");
            args.Add("-filter_complex"); args.Add(fc.ToString());
            args.Add("-map"); args.Add("[aout]");
            if (burn) AddVideoEncode(args);
            else { args.Add("-c:v"); args.Add("copy"); }
            args.Add("-c:a"); args.Add("aac");
            args.Add("-b:a"); args.Add("192k");
            args.Add("-ar"); args.Add("48000");
        }
        args.Add("-t"); args.Add(Inv(total));
        args.Add("-movflags"); args.Add("+faststart");
        args.Add("-progress"); args.Add("pipe:1");
        args.Add(plan.OutputPath);
        return args;
    }

    // 자막 번인 시 영상 재인코딩(copy 불가) — 화질 우선 CRF 20 · 호환 yuv420p.
    private static void AddVideoEncode(List<string> args)
    {
        args.Add("-c:v"); args.Add("libx264");
        args.Add("-preset"); args.Add("veryfast");
        args.Add("-crf"); args.Add("20");
        args.Add("-pix_fmt"); args.Add("yuv420p");
    }

    /// <summary>믹싱/먹싱 실행 — 누락 SFX/BGM 파일은 계획에서 제외(Graceful).</summary>
    public async Task<MixResult> MixAsync(AudioMixPlan plan, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var exe = _ffmpeg();
        if (exe is null) return new(false, null, "ffmpeg.exe 를 찾을 수 없습니다.");
        if (!File.Exists(plan.VideoPath)) return new(false, null, "병합 영상이 없습니다.");
        var bgm = !string.IsNullOrWhiteSpace(plan.BgmPath) && File.Exists(plan.BgmPath) ? plan.BgmPath : null;
        var sfx = new List<SfxCue>(plan.Sfx.Count);
        foreach (var c in plan.Sfx) if (File.Exists(c.Path)) sfx.Add(c);
        List<VoiceCue>? voice = null;
        if (plan.Voice is { Count: > 0 })
        {
            voice = new List<VoiceCue>(plan.Voice.Count);
            foreach (var v in plan.Voice) if (File.Exists(v.Path)) voice.Add(v);
        }
        var sub = !string.IsNullOrWhiteSpace(plan.SubtitlePath) && File.Exists(plan.SubtitlePath) ? plan.SubtitlePath : null;
        var safe = plan with { BgmPath = bgm, Sfx = sfx, Voice = voice, SubtitlePath = sub };
        return await FfmpegVideoMixerService.RunAsync(exe, BuildArgs(safe), plan.OutputPath, plan.TotalSeconds, progress, ct).ConfigureAwait(false);
    }

    private static string Inv(double v) => FfmpegVideoMixerService.Inv(v);
}
