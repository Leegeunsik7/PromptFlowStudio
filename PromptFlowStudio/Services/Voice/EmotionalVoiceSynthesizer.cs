using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;      // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Media;        // DubLanguage / GlobalMultiDubbingEngine
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Voice;

/// <summary>감정 보이스 페르소나 — 키/이모지/이름/톤 + 속도배율/피치(반음) + 샘플 대사.</summary>
public sealed record VoicePersona(string Key, string Emoji, string Name, string Tone, double RateMul, double PitchSemitones, string Sample);

/// <summary>나레이션 세그먼트 — 텍스트 + 타임코드(ms).</summary>
public sealed record VoiceSegment(string Text, int StartMs, int EndMs);

/// <summary>
/// [PromptFlowStudio] v3.8.0 오프라인 감정 보이스 믹서(Task 2 · 순수 BCL + FFmpeg CLI · 외부 TTS SDK 0).
///   ▸ 페르소나 3종(자상한 엄마·쾌활한 아빠·B2B 전문 성우) × 5개국(ko/en/de/zh/ja)의 <b>감정 억양 나레이션</b>을 설계.
///   ▸ 실제 음성은 OS Web Speech / 기존 Edge·Kokoro TTS 로 합성하고, 이 엔진이 <b>FFmpeg 피치/속도 변조</b>
///     (<c>asetrate</c> 피치 시프트 + <c>aresample</c> + <c>atempo</c> 캐스케이드)로 감정 톤을 입힌다.
///   ▸ 컷 나레이션을 언어별로 오프라인 번역(<see cref="GlobalMultiDubbingEngine"/>)하고 <b>자막 타임코드와 실시간 동기화</b>.
/// </summary>
public sealed class EmotionalVoiceSynthesizer
{
    /// <summary>감정 페르소나 3종.</summary>
    public static readonly IReadOnlyList<VoicePersona> Personas = new[]
    {
        new VoicePersona("mom", "🤱", "자상한 엄마", "따뜻하고 다정한 속삭임", 0.94, 2.0, "우리 아기, 오늘도 정말 사랑해"),
        new VoicePersona("dad", "👨", "쾌활한 아빠", "밝고 경쾌한 놀이 톤", 1.08, -2.5, "자, 우리 신나게 놀아볼까?"),
        new VoicePersona("pro", "🎙️", "B2B 전문 성우", "신뢰감 있는 저음 내레이션", 0.98, -1.0, "정밀함은 검증에서 시작됩니다"),
    };

    /// <summary>지원 언어(더빙 엔진과 공유 · ko/en/de/zh/ja).</summary>
    public static IReadOnlyList<DubLanguage> Languages => GlobalMultiDubbingEngine.Languages;

    /// <summary>키로 페르소나 조회(없으면 null).</summary>
    public static VoicePersona? PersonaFor(string? key) => Personas.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    // ══════════════════════ 피치/속도 변조 체인 ══════════════════════

    /// <summary>atempo 는 0.5~2.0 만 허용 → 목표 배율을 다단 atempo 로 분해한다.</summary>
    public static string BuildAtempoCascade(double factor)
    {
        var f = Math.Clamp(factor, 0.25, 4.0);
        var stages = new List<double>();
        while (f > 2.0) { stages.Add(2.0); f /= 2.0; }
        while (f < 0.5) { stages.Add(0.5); f *= 2.0; }
        stages.Add(f);
        return string.Join(",", stages.Select(s => "atempo=" + F(s)));
    }

    /// <summary>
    /// 피치(반음) + 속도 변조 FFmpeg <c>-af</c> 체인.
    ///   <c>asetrate</c> 로 피치+속도를 동시 시프트한 뒤 <c>aresample</c> 로 샘플레이트 복원, <c>atempo</c> 로 속도를 원복/가감속.
    /// </summary>
    public static string BuildPitchTempoChain(double semitones, double tempoMul, int sampleRate = 44100)
    {
        var ratio = Math.Pow(2, semitones / 12.0);
        var restore = (1.0 / ratio) * Math.Clamp(tempoMul, 0.3, 3.0);   // asetrate 로 빨라진 속도 원복 + 페르소나 가감속
        return $"asetrate={sampleRate}*{F(ratio)},aresample={sampleRate},{BuildAtempoCascade(restore)}";
    }

    /// <summary>페르소나 키 → 감정 톤 변조 체인.</summary>
    public static string BuildPersonaChain(string? personaKey, int sampleRate = 44100)
    {
        var p = PersonaFor(personaKey) ?? Personas[0];
        return BuildPitchTempoChain(p.PitchSemitones, p.RateMul, sampleRate);
    }

    // ══════════════════════ 자막 타임코드 동기화 ══════════════════════

    /// <summary>텍스트 길이·언어·속도로 나레이션 길이(ms)를 추정한다(CJK 는 글자당 느림).</summary>
    public static int EstimateDurationMs(string? text, string? langCode, double rateMul)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return 0;
        var cps = (langCode ?? "").ToLowerInvariant() switch { "zh" or "ja" or "ko" => 4.5, _ => 13.0 };   // 초당 글자수
        var secs = s.Length / cps / Math.Max(0.3, rateMul);
        return (int)Math.Round(Math.Max(0.6, secs) * 1000);
    }

    /// <summary>프로젝트 컷 나레이션을 페르소나·언어에 맞춰 번역 + 타임코드 세그먼트로 만든다(자막 동기화).</summary>
    public IReadOnlyList<VoiceSegment> BuildSegments(StoryboardProject project, string? personaKey, string? langCode)
    {
        var p = PersonaFor(personaKey) ?? Personas[0];
        var code = string.IsNullOrWhiteSpace(langCode) ? "ko" : langCode!.Trim().ToLowerInvariant();
        var list = new List<VoiceSegment>();
        var cursor = 0;
        const int gap = 120;
        foreach (var scene in project.Scenes)
        {
            var ko = (scene.VoiceText ?? "").Trim();
            if (ko.Length == 0) continue;
            var text = code == "ko" ? ko : GlobalMultiDubbingEngine.Translate(ko, code);
            var dur = EstimateDurationMs(text, code, p.RateMul);
            list.Add(new VoiceSegment(text, cursor, cursor + dur));
            cursor += dur + gap;
        }
        return list;
    }

    /// <summary>세그먼트 → SRT 자막(HH:MM:SS,mmm).</summary>
    public static string BuildSrt(IReadOnlyList<VoiceSegment> segments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            sb.Append(i + 1).Append('\n');
            sb.Append(Ts(segments[i].StartMs)).Append(" --> ").Append(Ts(segments[i].EndMs)).Append('\n');
            sb.Append(segments[i].Text).Append('\n').Append('\n');
        }
        return sb.ToString();
    }

    private static string Ts(int ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
    }

    // ══════════════════════ 오케스트레이션(선택 · 입력 음성에 감정 톤 적용) ══════════════════════

    /// <summary>합성된 나레이션 WAV/MP3 에 페르소나 감정 톤을 입힌다. 입력/ffmpeg 부재·실패는 false(원음 유지 · Graceful).</summary>
    public async Task<bool> ApplyEmotionAsync(string audioIn, string audioOut, string? personaKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audioIn) || !File.Exists(audioIn)) return false;
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return false;
        try
        {
            var dir = Path.GetDirectoryName(audioOut);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var chain = BuildPersonaChain(personaKey);
            var args = new List<string> { "-y", "-i", audioIn, "-af", chain, "-c:a", "aac", "-b:a", "192k", audioOut };
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            return res.Ok && File.Exists(audioOut);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Program.Log("EmotionalVoice.Apply", ex); return false; }
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
