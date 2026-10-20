using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;   // AudioWaveSynthesizer

namespace PromptFlowStudio.Services.Timeline;

/// <summary>
/// [PromptFlow Studio v7.2.0] AI 멀티모달 오토 디렉터 엔진(순수 BCL · 외부 NuGet 0).
///   ▸ 스토리 텍스트를 문장/씬 단위로 파싱하고 감정 휴리스틱으로 컷 길이·자이로 모션·립싱크 강도·프롬프트 수식어를 배정(<see cref="DirectedScriptPlan"/>).
///   ▸ <see cref="ApplyToTimelineAsync"/> 가 결과를 v7.0.2 <see cref="MultiTrackTimelineState"/> 4트랙에 0.5초 스냅으로 자동 배치하고,
///     보컬 트랙에는 <see cref="AudioWaveSynthesizer"/> 가이드 파형(SVG)을 첨부한다(파형→보컬 트랙 자동 전달).
///   ▸ 100% 결정적·순수 함수 파싱(테스트 가능) → 어떤 입력에도 예외 없이 계획 반환(Graceful).
/// </summary>
public sealed class AiAutoDirectorEngine
{
    private const int MaxScenes = 8;                 // NLE concat 용량(v6.0.1 ConcatInputs)과 정합
    private const double LipSyncThreshold = 0.35;

    // 감정 키워드 사전(한/영 · 부분 포함 매칭).
    private static readonly (SceneEmotion Emo, string[] Kw)[] EmotionKw =
    {
        (SceneEmotion.Epic,    new[] { "폭발", "전투", "질주", "웅장", "거대", "epic", "battle", "explode", "storm" }),
        (SceneEmotion.Tension, new[] { "긴장", "위기", "추격", "위험", "급습", "tension", "danger", "chase", "attack" }),
        (SceneEmotion.Sad,     new[] { "슬픔", "눈물", "이별", "그리움", "외로", "sad", "tear", "lonely", "farewell" }),
        (SceneEmotion.Joy,     new[] { "기쁨", "행복", "웃음", "축하", "설렘", "joy", "happy", "smile", "celebrate" }),
    };

    /// <summary>스토리 텍스트 → 오토 디렉팅 계획(감정/타임코드/모션/립싱크/수식어). 빈 입력이면 Empty.</summary>
    public DirectedScriptPlan Direct(string? story)
    {
        var sentences = SplitScenes(story);
        if (sentences.Count == 0) return DirectedScriptPlan.Empty;

        var scenes = new List<DirectedScene>(sentences.Count);
        var cursor = 0.0;
        for (var i = 0; i < sentences.Count; i++)
        {
            var text = sentences[i];
            var emo = DetectEmotion(text);
            var dur = MultiTrackTimelineState.Snap(DurationFor(text, emo));
            var start = MultiTrackTimelineState.Snap(cursor);
            var lip = LipSyncFor(text);
            var scene = new DirectedScene(i + 1, text, start, dur, emo, GyroFor(emo), lip, ModifierFor(emo, text));
            scenes.Add(scene);
            cursor = start + dur;
        }
        return new DirectedScriptPlan(scenes, cursor);
    }

    /// <summary>
    /// 계획을 타임라인 4트랙에 배치한다(초기화 후 재배치). 보컬 씬이 있으면 가이드 파형을 합성해 보컬 클립 썸네일로 첨부.
    /// </summary>
    public async Task<string> ApplyToTimelineAsync(DirectedScriptPlan plan, MultiTrackTimelineState state, AudioWaveSynthesizer synth, CancellationToken ct = default)
    {
        if (plan is null || plan.Scenes.Count == 0) return "배치할 씬이 없습니다.";

        state.ClearAll(notify: false);
        state.SetTotalDuration(Math.Ceiling(Math.Max(5, plan.TotalSec)));

        // 보컬 씬 나레이션 → 가이드 파형 합성(BCL/네이티브) → 파형 SVG 를 보컬 트랙에 전달.
        var lipScenes = plan.Scenes.Where(s => s.LipSyncIntensity >= LipSyncThreshold).ToList();
        string? vocalThumb = null; var synthMsg = "";
        if (lipScenes.Count > 0)
        {
            var narration = string.Join(" ", lipScenes.Select(s => s.Text));
            var res = await synth.SynthesizeAsync(narration, plan.TotalSec, ct).ConfigureAwait(false);
            if (res.Ok) { vocalThumb = res.SvgDataUrl; synthMsg = " · " + res.Message; }
        }

        foreach (var s in plan.Scenes)
        {
            state.AddClip(TimelineTrackKind.Video, s.StartSec, s.DurationSec, $"컷 {s.Index} · {EmotionLabel(s.Emotion)}", "#22d3ee");
            state.AddClip(TimelineTrackKind.Prompt, s.StartSec, s.DurationSec, Trunc(s.PromptModifier, 26), "#f59e0b");
            if (s.LipSyncIntensity >= LipSyncThreshold)
                state.AddClip(TimelineTrackKind.Vocal, s.StartSec, s.DurationSec, $"보컬 {(int)(s.LipSyncIntensity * 100)}%", "#a855f7", vocalThumb);
            if (s.Emotion != SceneEmotion.Calm)
                state.AddClip(TimelineTrackKind.Gyro, s.StartSec, Math.Min(s.DurationSec, Math.Max(2, s.DurationSec * 0.7)), s.GyroMotion, "#34d399");
        }

        return $"🎬 AI 오토 디렉팅 — {plan.Scenes.Count}씬 · {plan.TotalSec:0.#}s 배치{synthMsg}";
    }

    // ── 파싱 헬퍼(순수) ──

    /// <summary>문장부호/개행으로 씬 분할 → 최대 8씬(초과 시 균등 병합). 빈 조각 제거.</summary>
    public static List<string> SplitScenes(string? story)
    {
        if (string.IsNullOrWhiteSpace(story)) return new List<string>();
        var raw = Regex.Split(story, @"(?<=[.!?。！？\n])")
                       .Select(s => s.Trim())
                       .Where(s => s.Length > 0)
                       .ToList();
        if (raw.Count == 0) raw.Add(story.Trim());
        if (raw.Count <= MaxScenes) return raw;

        // 초과 시 MaxScenes 개로 균등 병합.
        var merged = new List<string>(MaxScenes);
        var per = (double)raw.Count / MaxScenes;
        for (var i = 0; i < MaxScenes; i++)
        {
            var from = (int)(i * per);
            var to = (int)((i + 1) * per);
            if (to <= from) to = from + 1;
            merged.Add(string.Join(" ", raw.Skip(from).Take(to - from)));
        }
        return merged;
    }

    public static SceneEmotion DetectEmotion(string text)
    {
        var low = (text ?? "").ToLowerInvariant();
        foreach (var (emo, kw) in EmotionKw)
            if (kw.Any(k => low.Contains(k))) return emo;
        return SceneEmotion.Calm;
    }

    /// <summary>컷 길이 — 어절 수 기반(3~6초) + 감정 보정(고조=길게).</summary>
    private static double DurationFor(string text, SceneEmotion emo)
    {
        var words = string.IsNullOrWhiteSpace(text) ? 1 : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var baseSec = 2.5 + words * 0.35;
        var bonus = emo switch { SceneEmotion.Epic => 1.0, SceneEmotion.Tension => 0.5, _ => 0.0 };
        return Math.Clamp(baseSec + bonus, 3, 6);
    }

    /// <summary>립싱크 강도(0~1) — 대사/감탄(따옴표·!·?) + 길이 기반.</summary>
    private static double LipSyncFor(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var t = text;
        var score = 0.3;
        if (t.Contains('"') || t.Contains('\'') || t.Contains('"') || t.Contains('「')) score += 0.4;   // 대사
        if (t.Contains('!') || t.Contains('?') || t.Contains('！') || t.Contains('？')) score += 0.2;   // 감탄/질문
        if (t.Length >= 14) score += 0.15;
        return Math.Clamp(score, 0, 1);
    }

    private static string GyroFor(SceneEmotion emo) => emo switch
    {
        SceneEmotion.Epic => "오빗 스윕",
        SceneEmotion.Tension => "핸드헬드 푸시인",
        SceneEmotion.Joy => "팬업 부양",
        SceneEmotion.Sad => "슬로우 풀아웃",
        _ => "고정 홀드",
    };

    private static string ModifierFor(SceneEmotion emo, string text) => emo switch
    {
        SceneEmotion.Epic => "epic wide shot, volumetric light, dramatic",
        SceneEmotion.Tension => "high contrast, tight framing, tension",
        SceneEmotion.Joy => "warm golden hour, bright, uplifting",
        SceneEmotion.Sad => "desaturated, soft rain, melancholic",
        _ => "cinematic, natural light, steady",
    };

    private static string EmotionLabel(SceneEmotion emo) => emo switch
    {
        SceneEmotion.Epic => "웅장", SceneEmotion.Tension => "긴장", SceneEmotion.Joy => "기쁨", SceneEmotion.Sad => "슬픔", _ => "잔잔",
    };

    private static string Trunc(string s, int n) { var v = s ?? ""; return v.Length <= n ? v : v[..n] + "…"; }
}
