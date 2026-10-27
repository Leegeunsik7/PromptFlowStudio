using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PromptFlowStudio.Services.Shorts;

public enum ShortsHookType { QuestionHook, ShockingFact, VisualSpectacle, RelatableDaily }

public enum BgmBeatPreset { HyperBeat, LofiChill, TrendingPop, DramaticDrop }

public enum CaptionStyle { BoldYellow, NeonCyber, MinimalWhite, ImpactDrop }

/// <summary>
/// 숏폼 1컷 규격 — 3컷(훅 3s · 본문 9s · CTA 3s) 중 하나.
///   ▸ 컷 전환 메타: <see cref="BgmBpm"/> 기준 <see cref="TransitionBeatIndex"/> 박에서 <see cref="TransitionFx"/> 로 다음 컷 진입.
/// </summary>
public sealed record ShortsCutSpec
{
    public int CutIndex { get; init; }
    public string Phase { get; init; } = "";
    public float StartSec { get; init; }
    public float DurationSec { get; init; }
    public string CameraMotion { get; init; } = "";
    public string VisualPrompt { get; init; } = "";
    public string Resolution { get; init; } = AiShortsService.DefaultResolution;
    public int BgmBpm { get; init; }
    /// <summary>컷 종료 지점의 누적 박 번호(0 기준) — 프리셋 BPM 은 3초 경계가 정확히 박에 떨어지도록 선정.</summary>
    public int TransitionBeatIndex { get; init; }
    public string TransitionFx { get; init; } = "";
    public long Seed { get; init; }
}

public sealed record ShortsRequest(
    string Topic,
    ShortsHookType HookType,
    BgmBeatPreset BgmPreset,
    CaptionStyle Caption,
    string TargetResolution = AiShortsService.DefaultResolution);

/// <summary>
/// [PromptFlowStudio] 6번 워크플로우 【AI 15초 숏폼 스튜디오】 3컷 시퀀서.
///   ▸ 훅 [0-3s] · 본문 [3-12s] · 클라이맥스/CTA [12-15s] = 항상 15.0초.
///   ▸ 모든 컷에 9:16 세로 구도 토큰 + 하단 자막/우측 아이콘 세이프존 회피 토큰 주입.
///   ▸ BGM 프리셋 BPM 으로 컷 전환 박 번호·전환 효과 메타 부착.
/// </summary>
public sealed class AiShortsService
{
    public const string DefaultResolution = "720x1280";
    public const float TotalDurationSec = 15.0f;
    public const string VerticalToken = "9:16 vertical composition";

    private static readonly float[] CutDurations = { 3.0f, 9.0f, 3.0f };
    private static readonly string[] Phases = { "Hook [0-3s]", "Main Narrative [3-12s]", "Climax/CTA [12-15s]" };

    /// <summary>프리셋 BPM — 3·12·15초 경계가 모두 정수 박(140→7박/3s, 80→4박, 120→6박, 100→5박).</summary>
    public static int BpmFor(BgmBeatPreset preset) => preset switch
    {
        BgmBeatPreset.HyperBeat => 140,
        BgmBeatPreset.LofiChill => 80,
        BgmBeatPreset.TrendingPop => 120,
        BgmBeatPreset.DramaticDrop => 100,
        _ => 120,
    };

    /// <summary>"WxH" 정규화 — 형식 불일치 시 기본값, 가로형 입력은 세로형으로 뒤집음.</summary>
    public static string NormalizeVerticalResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution)) return DefaultResolution;
        var s = resolution.Trim();
        var x = s.IndexOf('x');
        if (x <= 0 || x == s.Length - 1) return DefaultResolution;
        if (!int.TryParse(s.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out var w) ||
            !int.TryParse(s.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            w <= 0 || h <= 0) return DefaultResolution;
        if (w > h) (w, h) = (h, w);
        return w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<ShortsCutSpec> GenerateShortsSequence(ShortsRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var topic = string.IsNullOrWhiteSpace(req.Topic) ? "오늘의 이야기" : req.Topic.Trim();
        var resolution = NormalizeVerticalResolution(req.TargetResolution);
        var bpm = BpmFor(req.BgmPreset);
        var baseSeed = StableSeed(topic, req.HookType);

        var cuts = new List<ShortsCutSpec>(3);
        var start = 0f;
        for (var i = 0; i < 3; i++)
        {
            var duration = CutDurations[i];
            var end = start + duration;
            var camera = CameraFor(i, req.HookType, req.BgmPreset);
            cuts.Add(new ShortsCutSpec
            {
                CutIndex = i + 1,
                Phase = Phases[i],
                StartSec = start,
                DurationSec = duration,
                CameraMotion = camera,
                VisualPrompt = BuildPrompt(i, topic, camera, req, bpm),
                Resolution = resolution,
                BgmBpm = bpm,
                TransitionBeatIndex = (int)MathF.Round(end * bpm / 60f),
                TransitionFx = i == 2 ? "Hold on Final Beat" : TransitionFor(req.BgmPreset),
                Seed = baseSeed + i,
            });
            start = end;
        }
        return cuts;
    }

    private static string BuildPrompt(int cut, string topic, string camera, ShortsRequest req, int bpm)
    {
        var sb = new StringBuilder(384);
        sb.Append("Wan2.1, ").Append(VerticalToken)
          .Append(", portrait framing, center-weighted subject, tall headroom");
        switch (cut)
        {
            case 0: sb.Append(", ").Append(HookLook(req.HookType)).Append(" — ").Append(topic); break;
            case 1: sb.Append(", main narrative beat-synced montage — ").Append(topic).Append(", clear storytelling progression"); break;
            default: sb.Append(", climax payoff — ").Append(topic).Append(", end card call-to-action, follow & like prompt"); break;
        }
        sb.Append(", camera ").Append(camera)
          .Append(", ").Append(CaptionLook(req.Caption))
          .Append(", keep bottom caption safe zone and right-side icon rail clear")
          .Append(", cut on beat ").Append(bpm.ToString(CultureInfo.InvariantCulture)).Append(" BPM")
          .Append(", ").Append(CutDurations[cut].ToString("0.#", CultureInfo.InvariantCulture)).Append('s');
        return sb.ToString();
    }

    private static string CameraFor(int cut, ShortsHookType hook, BgmBeatPreset bgm) => cut switch
    {
        0 => hook switch
        {
            ShortsHookType.QuestionHook => "Fast Push-In",
            ShortsHookType.ShockingFact => "Crash Zoom In",
            ShortsHookType.VisualSpectacle => "Vertical Crane Rise",
            _ => "Handheld POV Push",
        },
        1 => bgm == BgmBeatPreset.LofiChill ? "Slow Vertical Tilt Reveal" : "Dynamic Orbit",
        _ => "Snap Zoom Out",
    };

    private static string HookLook(ShortsHookType hook) => hook switch
    {
        ShortsHookType.QuestionHook => "on-screen question title hook, curious direct-to-camera gaze",
        ShortsHookType.ShockingFact => "bold shocking fact headline hook, sudden dramatic reveal",
        ShortsHookType.VisualSpectacle => "jaw-dropping visual spectacle opener, hyper-detailed macro",
        _ => "relatable everyday POV moment, smartphone handheld feel",
    };

    private static string CaptionLook(CaptionStyle caption) => caption switch
    {
        CaptionStyle.BoldYellow => "bold yellow outlined captions",
        CaptionStyle.NeonCyber => "neon cyan glow captions",
        CaptionStyle.MinimalWhite => "minimal white sans-serif captions",
        _ => "impact drop-in kinetic captions",
    };

    private static string TransitionFor(BgmBeatPreset bgm) => bgm switch
    {
        BgmBeatPreset.HyperBeat => "Hard Cut + Flash",
        BgmBeatPreset.LofiChill => "Soft Crossfade",
        BgmBeatPreset.TrendingPop => "Whip Pan",
        _ => "Bass Drop Smash Cut",
    };

    /// <summary>프로세스 간 결정적 시드(FNV-1a) — string.GetHashCode 는 실행마다 달라짐.</summary>
    private static long StableSeed(string topic, ShortsHookType hook)
    {
        var h = 2166136261u;
        foreach (var c in topic) h = unchecked((h ^ c) * 16777619u);
        h = unchecked((h ^ (uint)hook) * 16777619u);
        return h & int.MaxValue;
    }
}
