using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // EngineConfigService/ProcessRunner/EngineKeys
using PromptFlowStudio.Services.Media;     // FoleyCue/SfxResolver

namespace PromptFlowStudio.Services.Wan;

/// <summary>생성형 폴리 & 3D 공간 음향 계약.</summary>
public interface IGenAudioFoleyService
{
    /// <summary>Gemini 키가 구성돼 폴리 분석이 가능한지.</summary>
    bool Enabled { get; }

    /// <summary>
    /// 컷을 Gemini 로 분석해 오디오 프롬프트("heavy metallic footsteps" 등)를 만들고, 로컬 폴리 엔진(설정 시) 또는 내장 Sfx 라이브러리로
    ///   효과음을 해결해 <b>공간 팬(pan)</b>이 부여된 <see cref="FoleyCue"/> 목록을 반환한다. 실패/미해결은 빈 목록(Graceful).
    /// </summary>
    Task<IReadOnlyList<FoleyCue>> AnalyzeCutAsync(string? videoPath, string cutPrompt, int clipDurationMs, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] 생성형 폴리(Gen-Foley) & 3D 공간 음향 — 순수 BCL(<see cref="GeminiClient"/> 재사용, 외부 NuGet 0).
///   ▸ Gemini 가 컷을 분석해 오디오 프롬프트 + 피사체 위치/이동(pan -1 좌 ~ +1 우) + 시점(ms)을 산출.
///   ▸ 효과음 해결: 로컬 폴리 CLI(<see cref="EngineKeys.FoleyAudio"/> · AudioGen/Stable-Audio 등)가 설정돼 있으면 텍스트→wav 합성,
///     아니면 내장 <see cref="SfxResolver"/>(키워드 매칭)로 폴백 — 둘 다 없으면 해당 큐 생략(Graceful).
///   ▸ <see cref="VideoMergeService"/>가 <see cref="FoleyCue.Pan"/> 을 ffmpeg <c>pan</c> 필터로 좌/우 공간 배치해 믹싱(피사체 이동감).
///   ▸ 키 없음/실패는 빈 목록 → 기존 오디오 믹싱 100% 유지.
/// </summary>
public sealed class GenAudioFoleyService(GeminiClient gemini, EngineConfigService engines) : IGenAudioFoleyService
{
    private const int MaxCues = 6;

    /// <inheritdoc/>
    public bool Enabled => gemini.Enabled;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FoleyCue>> AnalyzeCutAsync(string? videoPath, string cutPrompt, int clipDurationMs, CancellationToken ct = default)
    {
        var empty = Array.Empty<FoleyCue>();
        if (!Enabled) return empty;
        var maxMs = clipDurationMs > 0 ? clipDurationMs : 5000;

        try
        {
            GeminiMedia[]? media = null;
            if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
            {
                var vf = new FileInfo(videoPath);
                if (vf.Length > 0 && vf.Length <= GeminiClient.MaxInlineBytes)
                    media = new[] { new GeminiMedia("video/mp4", await File.ReadAllBytesAsync(videoPath, ct).ConfigureAwait(false)) };
            }

            var res = await gemini.GenerateAsync(BuildInstruction(cutPrompt, maxMs), media, temperature: 0.4, ct: ct).ConfigureAwait(false);
            if (!res.Ok || res.Text is null) return empty;

            var specs = ParseSpecs(res.Text, maxMs);
            if (specs.Count == 0) return empty;

            var cues = new List<FoleyCue>();
            foreach (var s in specs)
            {
                ct.ThrowIfCancellationRequested();
                var path = await ResolveAudioAsync(s.Prompt, s.SfxType, ct).ConfigureAwait(false);
                if (path is null) continue;
                cues.Add(new FoleyCue(path, s.TimestampMs, s.Pan));
            }
            return cues;
        }
        catch (OperationCanceledException) { return empty; }
        catch (Exception ex) { Program.Log("GenAudioFoley.Analyze", ex); return empty; }
    }

    /// <summary>오디오 프롬프트 → 효과음 파일. ①폴리 CLI 합성(설정 시) → ②내장 Sfx 키워드 폴백 → ③null.</summary>
    private async Task<string?> ResolveAudioAsync(string audioPrompt, string sfxType, CancellationToken ct)
    {
        // ① 로컬 폴리 엔진(설정 시).
        var exe = engines.Resolve(EngineKeys.FoleyAudio);
        if (exe is not null)
        {
            try
            {
                var dir = Path.Combine(LicenseServiceDataArea(), "Media", "_foley");
                Directory.CreateDirectory(dir);
                var outWav = Path.Combine(dir, $"foley_{Hash(audioPrompt)}.wav");
                if (File.Exists(outWav)) return outWav;   // 캐시

                var args = ProcessRunner.Substitute(engines.Args(EngineKeys.FoleyAudio), new Dictionary<string, string>
                { ["prompt"] = audioPrompt, ["out"] = outWav, ["seconds"] = "2" });
                var r = await ProcessRunner.RunAsync(exe, args, null, null, ct).ConfigureAwait(false);
                if (r.Started && r.ExitCode == 0 && File.Exists(outWav)) return outWav;
            }
            catch (Exception ex) { Program.Log("GenAudioFoley.Synth", ex); }
        }

        // ② 내장 Sfx 라이브러리(키워드 매칭) 폴백.
        return SfxResolver.FindByType(sfxType);
    }

    private static string BuildInstruction(string cutPrompt, int maxMs)
    {
        return
            "당신은 영화 사운드 디자이너입니다. 아래 컷 장면에 어울리는 폴리(현장 효과음)를 설계하세요.\n" +
            $"장면: \"{cutPrompt}\"\n" +
            $"각 효과음의 시점은 이 컷 내부 기준(0~{maxMs}ms), 팬(pan)은 화면상 피사체 위치/이동(-1=왼쪽, 0=중앙, +1=오른쪽)입니다.\n" +
            "아래 Strict JSON 으로만 응답(다른 설명·마크다운 금지):\n" +
            "{\"foley\":[{\"prompt\":\"<영문 오디오 프롬프트 예: heavy metallic footsteps>\",\"sfxType\":\"<footsteps|whoosh|impact|wind|water|door|click 등 한 단어>\",\"timestampMs\":800,\"pan\":-0.6}]}\n" +
            "핵심 효과음 1~4개만. 피사체가 좌→우로 움직이면 pan 을 그 방향의 위치값으로 지정.";
    }

    private readonly record struct FoleySpec(string Prompt, string SfxType, int TimestampMs, double Pan);

    private static List<FoleySpec> ParseSpecs(string text, int maxMs)
    {
        var list = new List<FoleySpec>();
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(text));
            if (doc.RootElement.TryGetProperty("foley", out var fa) && fa.ValueKind == JsonValueKind.Array)
                foreach (var f in fa.EnumerateArray())
                {
                    if (list.Count >= MaxCues) break;
                    var prompt = StrOf(f, "prompt").Trim();
                    var type = StrOf(f, "sfxType").Trim();
                    if (prompt.Length == 0 && type.Length == 0) continue;
                    var ts = Math.Clamp(IntOf(f, "timestampMs"), 0, maxMs);
                    var pan = Math.Clamp(DblOf(f, "pan"), -1.0, 1.0);
                    list.Add(new FoleySpec(prompt.Length > 0 ? prompt : type, type.Length > 0 ? type : "impact", ts, pan));
                }
        }
        catch (Exception ex) { Program.Log("GenAudioFoley.Parse", ex); }
        return list;
    }

    private static string LicenseServiceDataArea() => PromptFlowStudio.Services.Account.LicenseService.DataAreaRoot();

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s ?? ""));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string StrOf(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";

    private static int IntOf(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (int)Math.Round(d);
        return int.TryParse(v.ToString(), out var p) ? p : 0;
    }

    private static double DblOf(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0;
    }
}
