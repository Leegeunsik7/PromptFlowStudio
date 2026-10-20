using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.12.0 후처리 진행 스냅숏(0~100 · 단계 문구).</summary>
public readonly record struct PostProductionProgress(int Percent, string Stage);

/// <summary>
/// v16.14.0 나레이션·자막 옵션 — 컷 VoiceText → TTS(톤·배속·공급자) · BGM 덕킹 비율 · 세이프존 자막(SRT/ASS 사이드카 + 번인).
/// </summary>
public sealed record VoiceoverMixOptions(
    bool Narration = true, NarrationTone Tone = NarrationTone.FairyTale, double Speed = 1.0, TtsProvider Provider = TtsProvider.Auto,
    double DuckRatio = 8, bool Subtitles = true, bool BurnIn = true, SafePlatform Platform = SafePlatform.YouTubeShorts,
    SubtitleStyle? Style = null);

/// <summary>
/// [PromptFlowStudio] v16.12.0 🎬 Cinematic Post-Production 엔진 — 완료 컷 → [영상 병합(트랜지션)] → [BGM+SFX 믹싱·먹싱] → 최종 MP4.
///   ▸ 1단계(0~70%): <see cref="FfmpegVideoMixerService"/> 로 정규화+Crossfade 병합(무음 중간 파일).
///   ▸ 2단계(70~100%): <see cref="FfmpegAudioMixerService"/> 로 BGM 루핑/페이드 + 컷 타이밍 SFX → 영상 copy 먹싱.
///   ▸ 출력: 첫 컷 폴더의 <c>Final\final_yyyyMMdd_HHmmss.mp4</c> · 중간 파일은 finally 에서 정리. 성공 시 project.FinalVideoPath 갱신.
///   ▸ 동시 실행 1건(재진입 차단) · 실패/취소는 결과 record 로 반환(예외 0).
///   ▸ v16.14.0 4중 먹싱(<see cref="VoiceoverMixOptions"/> 지정 시): 영상 + BGM(나레이션 사이드체인 덕킹) + SFX + Voiceover + 자막 번인.
///     단계: 병합(0~55) → TTS(55~75) → 자막 SRT/ASS(75) → 믹싱·번인(75~100). TTS 전량 실패여도 BGM/SFX 내보내기는 계속([부분 구제]).
///     SRT/ASS 는 최종 MP4 옆에 사이드카로 보존(플랫폼 업로드용).
/// </summary>
public sealed class PostProductionEngine(FfmpegVideoMixerService video, FfmpegAudioMixerService audio,
    AiVoiceoverTtsService? voiceover = null, AutoSubtitleGenerator? subtitles = null)
{
    public bool CanNarrate => voiceover is { IsAvailable: true };
    public bool HasSubtitleEngine => subtitles is not null;

    private int _busy;

    public bool IsAvailable => video.IsAvailable;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>완료(로컬 파일 보유) 컷 목록.</summary>
    public static List<StoryboardScene> ReadyScenes(StoryboardProject project)
    {
        var list = new List<StoryboardScene>(project.Scenes.Count);
        foreach (var s in project.Scenes)
            if (s.Status == SceneStatus.Completed && !string.IsNullOrWhiteSpace(s.LocalPath)) list.Add(s);
        return list;
    }

    public async Task<MixResult> ExportAsync(StoryboardProject project, IProgress<PostProductionProgress>? progress = null,
        PostTransition transition = PostTransition.Crossfade, CancellationToken ct = default, VoiceoverMixOptions? voice = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (Interlocked.Exchange(ref _busy, 1) != 0) return new(false, null, "이미 최종 내보내기가 진행 중입니다.");
        string? temp = null;
        try
        {
            if (!video.IsAvailable) return new(false, null, "ffmpeg.exe 를 찾을 수 없어 최종 내보내기를 할 수 없습니다.");
            var scenes = ReadyScenes(project);
            if (scenes.Count == 0) return new(false, null, "완료된 컷이 없습니다 — 먼저 스토리보드를 생성하세요.");

            var (w, h) = StoryboardProject.ParseResolution(project.ExportResolution);
            var inputs = new List<string>(scenes.Count);
            foreach (var s in scenes) inputs.Add(s.LocalPath!);
            var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputs[0]))!, "Final");
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var output = Path.Combine(dir, "final_" + stamp + ".mp4");
            temp = Path.Combine(dir, "video_" + stamp + ".tmp.mp4");

            var vplan = new VideoMixPlan(inputs, temp, w, h, 30, transition);
            var videoEnd = voice is null ? 70 : 55;
            progress?.Report(new(0, "🎞️ 컷 병합·트랜지션 렌더 중…"));
            var vr = await video.MixAsync(vplan, Map(progress, 0, videoEnd, "🎞️ 컷 병합·트랜지션 렌더 중…"), ct).ConfigureAwait(false);
            if (!vr.Ok) return vr;

            var timeline = FfmpegVideoMixerService.Timeline(vplan);
            var cues = FfmpegAudioMixerService.BuildCues(project, scenes, timeline);
            var aplan = new AudioMixPlan(temp, output, timeline.TotalSeconds, project.BgmPath, cues);
            List<VoiceCue>? narration = null;
            var subNote = "";
            if (voice is not null)
            {
                narration = await NarrateAsync(scenes, timeline, voice, progress, ct).ConfigureAwait(false);
                var sub = await WriteSubtitlesAsync(narration, scenes, timeline, voice, w, h, output, ct).ConfigureAwait(false);
                subNote = sub is null ? "" : " · 자막 " + sub.Value.Count.ToString(CultureInfo.InvariantCulture) + "블록" + (voice.BurnIn ? "(번인)" : "(SRT/ASS)");
                aplan = aplan with
                {
                    Voice = narration, DuckRatio = voice.DuckRatio,
                    SubtitlePath = sub is not null && voice.BurnIn ? sub.Value.AssPath : null,
                    FontsDir = FontsDirOf(FontResolver.FindKoreanFont(voice.Style?.Bold ?? true)),
                };
            }
            var mixFrom = voice is null ? 70 : 75;
            var mixStage = voice is null ? "🎚️ BGM·SFX 믹싱 중…" : "🎚️ BGM·SFX·나레이션 덕킹 믹싱 · 자막 번인 중…";
            progress?.Report(new(mixFrom, mixStage));
            var ar = await audio.MixAsync(aplan, Map(progress, mixFrom, 100, mixStage), ct).ConfigureAwait(false);
            if (!ar.Ok && aplan.SubtitlePath is not null && !ct.IsCancellationRequested)
            {
                // [부분 구제] libass 미포함 ffmpeg 등 번인 실패 → 번인 없이 재먹싱(SRT/ASS 사이드카는 유지)
                ar = await audio.MixAsync(aplan with { SubtitlePath = null }, Map(progress, mixFrom, 100, mixStage), ct).ConfigureAwait(false);
                if (ar.Ok) subNote += " · 번인 실패→사이드카만";
            }
            if (!ar.Ok) return ar;

            project.FinalVideoPath = output;
            progress?.Report(new(100, "✅ 최종 영상 완성"));
            var sfxNote = cues.Count > 0 ? "SFX " + cues.Count.ToString(CultureInfo.InvariantCulture) + "개" : "SFX 없음";
            var bgmNote = string.IsNullOrWhiteSpace(project.BgmPath) ? "BGM 없음" : "BGM 루핑·페이드";
            var voNote = voice is null ? "" : narration is { Count: > 0 }
                ? " · 나레이션 " + narration.Count.ToString(CultureInfo.InvariantCulture) + "컷" + (string.IsNullOrWhiteSpace(project.BgmPath) ? "" : "(BGM 덕킹)")
                : " · 나레이션 없음";
            return new(true, output, scenes.Count.ToString(CultureInfo.InvariantCulture) + "컷 · " + bgmNote + " · " + sfxNote + voNote + subNote);
        }
        catch (Exception ex) { return new(false, null, "최종 내보내기 실패: " + ex.Message); }
        finally
        {
            if (temp is not null) { try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 정리 실패 무시 */ } }
            Volatile.Write(ref _busy, 0);
        }
    }

    // TTS 단계(55~75%) — 엔진 부재/전량 실패는 빈 목록(나레이션 없이 계속).
    private async Task<List<VoiceCue>> NarrateAsync(List<StoryboardScene> scenes, MixTimeline timeline, VoiceoverMixOptions o,
        IProgress<PostProductionProgress>? progress, CancellationToken ct)
    {
        if (!o.Narration || voiceover is null || !voiceover.IsAvailable) return new List<VoiceCue>(0);
        const string stage = "🎙️ 동화 나레이션 합성 중…";
        progress?.Report(new(55, stage));
        return await voiceover.SynthesizeStoryAsync(scenes, timeline, o.Tone, o.Speed, o.Provider, Map(progress, 55, 75, stage), ct).ConfigureAwait(false);
    }

    // 자막: 합성된 나레이션 타이밍 우선 · 없으면 VoiceText 추정 길이로 컷 시작에 배치. 출력 MP4 옆 SRT/ASS 사이드카.
    private static async Task<(string AssPath, int Count)?> WriteSubtitlesAsync(List<VoiceCue> narration, List<StoryboardScene> scenes,
        MixTimeline timeline, VoiceoverMixOptions o, int w, int h, string output, CancellationToken ct)
    {
        if (!o.Subtitles) return null;
        var timing = narration;
        if (timing.Count == 0)
        {
            timing = new List<VoiceCue>(scenes.Count);
            for (var i = 0; i < scenes.Count && i < timeline.CutStarts.Count; i++)
            {
                var text = scenes[i].VoiceText;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var at = timeline.CutStarts[i] + AiVoiceoverTtsService.LeadInSeconds;
                if (at >= timeline.TotalSeconds) continue;
                var dur = Math.Min(AiVoiceoverTtsService.EstimateSeconds(text, o.Tone, o.Speed), timeline.TotalSeconds - at);
                timing.Add(new VoiceCue("", at, dur, text.Trim()));
            }
        }
        var cues = AutoSubtitleGenerator.BuildCues(timing, o.Style);
        if (cues.Count == 0) return null;
        var layout = AutoSubtitleGenerator.ComputeLayout(o.Platform, w, h, o.Style);
        var ass = await AutoSubtitleGenerator.WriteAsync(output, cues, layout, o.Style, ct).ConfigureAwait(false);
        return (ass, cues.Count);
    }

    private static string? FontsDirOf(string? fontFile)
    {
        if (string.IsNullOrWhiteSpace(fontFile)) return null;
        try { return Path.GetDirectoryName(Path.GetFullPath(fontFile)); } catch { return null; }
    }

    private static IProgress<double>? Map(IProgress<PostProductionProgress>? p, int from, int to, string stage)
        => p is null ? null : new Progress01(p, from, to, stage);

    // 0~1 → 구간 % (동일 값 반복 보고 생략).
    private sealed class Progress01(IProgress<PostProductionProgress> inner, int from, int to, string stage) : IProgress<double>
    {
        private int _last = -1;
        public void Report(double value)
        {
            var pct = from + (int)Math.Round(Math.Clamp(value, 0, 1) * (to - from));
            if (pct == Interlocked.Exchange(ref _last, pct)) return;
            inner.Report(new(pct, stage));
        }
    }
}
