using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Services.AI;

/// <summary>오디오 트랙 병합 결과. <see cref="Applied"/>=false 면 원본 영상(<see cref="OutputPath"/>=입력)을 그대로 쓴다(Graceful).</summary>
public sealed record AudioMuxResult(bool Ok, bool Applied, string OutputPath, string Message);

/// <summary>영상 + 오디오 트랙 멀티플렉서 — 무음 Wan 2.1 클립에 효과음/앰비언스/BGM 을 입힌다.</summary>
public interface IAudioMuxer
{
    bool IsAvailable { get; }
    Task<AudioMuxResult> MuxAsync(string videoPath, string audioPath, string outputPath, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlow Studio v15.9.0] AI Auto-Foley — Wan 2.1 무음 한계 보완.
///   ▸ <see cref="GenerateAudioPrompt"/>: 비디오 프롬프트 키워드 → SFX + 앰비언스 오디오 프롬프트. 결과는 정적 초기화 시
///     미리 결합한 (SFX×앰비언스) 리터럴 캐시 참조를 돌려주므로 0B(span OrdinalIgnoreCase 스캔 · 결합/소문자화 없음).
///   ▸ <see cref="AutoFoleyAsync"/>: 분류된 SFX 키로 <see cref="SfxResolver"/> 라이브러리에서 오디오를 찾아 <see cref="IAudioMuxer"/> 로 병합.
///     효과음/ffmpeg 부재·실패는 예외 없이 Applied=false(원본 유지).
/// </summary>
public static class AudioSfxEngine
{
    private sealed record Profile(string Key, string[] Keywords, string Prompt);

    // 우선순위 순(앞선 프로필이 먼저 매칭). Key = SfxResolver 파일명 키워드.
    private static readonly Profile[] Sfx =
    {
        new("explosion", new[] { "explosion", "explode", "blast", "폭발", "불꽃놀이", "firework" }, "deep cinematic explosion boom, debris rumble"),
        new("thunder",   new[] { "thunder", "storm", "lightning", "천둥", "번개", "폭풍" },          "rolling thunder crack, heavy storm gusts"),
        new("rain",      new[] { "rain", "drizzle", "비가", "빗", "장마" },                           "steady rain patter on surfaces, soft droplets"),
        new("water",     new[] { "ocean", "wave", "sea", "river", "water", "바다", "파도", "강물", "물결", "호수" }, "gentle ocean waves washing ashore, water splashes"),
        new("fire",      new[] { "fire", "flame", "campfire", "불길", "모닥불", "촛불" },               "crackling fire, warm embers popping"),
        new("engine",    new[] { "car", "engine", "motorcycle", "drift", "자동차", "엔진", "오토바이" }, "revving engine, tires on asphalt"),
        new("footstep",  new[] { "walk", "run", "footstep", "걷", "달리", "뛰" },                     "rhythmic footsteps, light cloth rustle"),
        new("laugh",     new[] { "baby", "child", "laugh", "giggle", "아기", "아이", "웃" },            "soft baby giggles, gentle cooing"),
        new("bird",      new[] { "bird", "forest", "garden", "새소리", "숲", "정원" },                      "birdsong chirps, leaves rustling"),
        new("whoosh",    new[] { "fly", "flying", "zoom", "dolly", "drone", "비행", "날아", "드론", "줌" }, "airy cinematic whoosh, smooth swoosh pass"),
    };
    private const string SfxDefaultPrompt = "subtle cinematic foley, soft room tone";
    private const string SfxDefaultKey = "ambient";

    private static readonly Profile[] Ambience =
    {
        new("city",    new[] { "city", "street", "night", "neon", "도시", "거리", "야경", "네온" }, "distant city traffic hum"),
        new("nature",  new[] { "forest", "mountain", "field", "숲", "산속", "들판", "자연" },          "open nature air, faint wind"),
        new("indoor",  new[] { "room", "home", "nursery", "studio", "아기방", "집 안", "실내", "스튜디오" }, "quiet indoor room ambience"),
        new("space",   new[] { "space", "galaxy", "planet", "우주", "은하", "행성" },               "deep space drone, low cosmic pad"),
        new("fantasy", new[] { "magic", "fairy", "dream", "마법", "요정", "몽환", "동화" },          "shimmering magical chimes, dreamy pad"),
    };
    private const string AmbienceDefaultPrompt = "neutral soft ambience";

    // [sfx(0..Sfx.Length, 마지막=기본), amb(0..Ambience.Length, 마지막=기본)] 결합 캐시 — 런타임 결합 0.
    private static readonly string[] PromptCache = BuildCache();

    private static string[] BuildCache()
    {
        var cache = new string[(Sfx.Length + 1) * (Ambience.Length + 1)];
        for (var s = 0; s <= Sfx.Length; s++)
            for (var a = 0; a <= Ambience.Length; a++)
                cache[s * (Ambience.Length + 1) + a] = "SFX: " + (s < Sfx.Length ? Sfx[s].Prompt : SfxDefaultPrompt)
                    + " | Ambience: " + (a < Ambience.Length ? Ambience[a].Prompt : AmbienceDefaultPrompt)
                    + " | no music vocals, synced to on-screen motion";
        return cache;
    }

    /// <summary>비디오 프롬프트 → 오디오(SFX+앰비언스) 프롬프트. 캐시 리터럴 참조 반환 — 0B.</summary>
    public static string GenerateAudioPrompt(string? videoPrompt)
    {
        var p = videoPrompt.AsSpan();
        return PromptCache[Match(p, Sfx) * (Ambience.Length + 1) + Match(p, Ambience)];
    }

    /// <summary>SFX 라이브러리 키(파일명 키워드). 매칭 없으면 "ambient".</summary>
    public static string ClassifySfxKey(string? videoPrompt)
    {
        var i = Match(videoPrompt.AsSpan(), Sfx);
        return i < Sfx.Length ? Sfx[i].Key : SfxDefaultKey;
    }

    private static int Match(ReadOnlySpan<char> prompt, Profile[] table)
    {
        if (prompt.IsEmpty) return table.Length;
        for (var i = 0; i < table.Length; i++)
        {
            var kws = table[i].Keywords;
            for (var k = 0; k < kws.Length; k++)
                if (prompt.IndexOf(kws[k].AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0) return i;
        }
        return table.Length;
    }

    /// <summary>생성 완료 영상에 프롬프트 기반 효과음을 자동 병합 — <c>{name}.foley.mp4</c>. 실패/부재는 Applied=false(원본 유지).</summary>
    public static async Task<AudioMuxResult> AutoFoleyAsync(IAudioMuxer muxer, string videoPath, string? videoPrompt, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                return new(false, false, videoPath ?? "", "영상 파일을 찾을 수 없습니다.");
            if (!muxer.IsAvailable) return new(true, false, videoPath, "ffmpeg 미탐지 — 무음 원본 유지.");

            var key = ClassifySfxKey(videoPrompt);
            var audio = SfxResolver.FindByType(key);
            // FindByType 은 기본 전환음으로 폴백하므로 키워드 일치 파일만 전체 트랙으로 사용(전환음 루프 방지).
            if (audio is null || Path.GetFileNameWithoutExtension(audio).IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0)
                return new(true, false, videoPath, $"'{key}' 효과음 없음(Sfx 폴더) — 무음 원본 유지.");

            var dir = Path.GetDirectoryName(videoPath) ?? "";
            var output = Path.Combine(dir, Path.GetFileNameWithoutExtension(videoPath) + ".foley.mp4");
            return await muxer.MuxAsync(videoPath, audio, output, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(false, false, videoPath ?? "", "취소됨 — 원본 유지."); }
        catch (Exception ex) { Program.Log("AudioSfxEngine.AutoFoley", ex); return new(false, false, videoPath ?? "", "Auto-Foley 실패 — 원본 유지."); }
    }
}

/// <summary>ffmpeg 기반 <see cref="IAudioMuxer"/> — 영상 스트림 copy + 오디오 반복(-stream_loop)·aac 재인코딩·-shortest. 순수 BCL Process.</summary>
public sealed class FfmpegAudioMuxer : IAudioMuxer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public bool IsAvailable => ResolveFfmpeg() is not null;

    public async Task<AudioMuxResult> MuxAsync(string videoPath, string audioPath, string outputPath, CancellationToken ct = default)
    {
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return new(true, false, videoPath, "ffmpeg 미탐지 — 무음 원본 유지.");
        if (!File.Exists(videoPath) || !File.Exists(audioPath)) return new(false, false, videoPath, "입력 파일 없음 — 원본 유지.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        var args = new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", videoPath, "-stream_loop", "-1", "-i", audioPath,
            "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "128k",
            "-shortest", "-movflags", "+faststart", outputPath,
        };
        var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: deadline.Token).ConfigureAwait(false);
        if (res.Started && res.ExitCode == 0 && File.Exists(outputPath))
            return new(true, true, outputPath, "오디오 트랙 병합 완료.");

        try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* 부분 산출물 정리 실패 무시 */ }
        return new(false, false, videoPath, deadline.IsCancellationRequested ? "ffmpeg 시간 초과/취소 — 원본 유지." : "ffmpeg 병합 실패 — 원본 유지.");
    }

    private static string? ResolveFfmpeg()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            return CliPathResolver.GetExecutablePath("ffmpeg", "ffmpeg.exe");
        }
        catch { return null; }
    }
}
