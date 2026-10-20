using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using PromptFlowStudio.Services.DigitalTwin;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Music;

[JsonConverter(typeof(JsonStringEnumConverter<DanceStyle>))]
public enum DanceStyle { KPop, HipHop, House, Popping, Contemporary }

public enum DanceMove { Groove, ArmWave, KPopPoint, Clap, BodyRoll, StepTouch, HipHopRock, PowerHit, FreezePose }

/// <summary>안무 구간(박 단위) — 동작 · 에너지(0~1) · 좌우 반전.</summary>
public readonly record struct ChoreoSegment(double StartBeat, int Beats, DanceMove Move, float Energy, bool Mirror);

/// <summary>안무 옵션 — 스타일(null = 장르/무드 자동) · 진폭(안무 강도) · 복잡도(동작 전환 빈도) · 시드 · 클립 FPS.</summary>
public sealed record ChoreoOptions(DanceStyle? Style = null, float Amplitude = 0.7f, float Complexity = 0.5f, int Seed = 0, int Fps = 30);

/// <summary>안무 플랜 — 비트 그리드(첫 박 · 박 길이) + 정렬된 구간 배열(무할당 이진 탐색).</summary>
public sealed class ChoreographyPlan
{
    public DanceStyle Style { get; init; }
    public double Bpm { get; init; }
    public double FirstBeatSec { get; init; }
    public double BeatSec { get; init; }
    public double DurationSec { get; init; }
    public float Amplitude { get; init; }
    public float Complexity { get; init; }
    public ChoreoSegment[] Segments { get; init; } = Array.Empty<ChoreoSegment>();
    public int HookRepeats { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// [PromptFlow Studio v16.37.0] 💃 알고리즘 댄스 안무 &amp; 모션 제너레이터.
///   ▸ 비트 분석(BPM · 다운비트 · 마디 에너지 · 드랍) + 곡 구간(인트로/벌스/코러스/드랍) + 무드 → 스타일(K-Pop/Hip-hop/House/Popping/Contemporary) 동작 풀에서
///     8카운트(2마디) 프레이즈 단위 결정적 배치 · 코러스 = 훅 안무 반복 · 드랍 = 파워 히트 → 훅 · 복잡도 ↑ = 4카운트 전환 · 프레이즈 교대 좌우 반전.
///   ▸ 포즈 합성(무할당 · 시간 → 33 관절): 동작별 박 위상 함수(바운스 · 팔 궤적 · 스웨이/스텝 · 바디롤 · 히트/프리즈) → v16.36.0 PoseBuilder → 하체 스텝/바디 웨이브 후처리.
///   ▸ 리타겟: v16.36.0 SourceRig → Retarget(성인/아기/트윈 비율) → MocapClip(감정/손 제스처 채널) → BVH · 스토리보드 MV 컷 프롬프트(스타일 · 동작 · 트윈 · 표정).
/// </summary>
public sealed class AlgorithmicChoreographyController
{
    public const int PhraseBeats = 8;

    private static readonly int[] MirrorPairs = { 1, 4, 2, 5, 3, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 };

    public AlgorithmicChoreographyController() { }

    public AlgorithmicChoreographyController(IServiceProvider sp) : this() { }

    public event Action? OnChange;

    public ChoreographyPlan? Current { get; private set; }
    public MocapClip? LastClip { get; private set; }
    public string? LastAction { get; private set; }

    public static DanceStyle AutoStyle(MusicGenre? genre, string? mood)
    {
        var m = (mood ?? "").ToLowerInvariant();
        if (m.Contains("sad") || m.Contains("슬") || m.Contains("잔잔") || m.Contains("calm")) return DanceStyle.Contemporary;
        return genre switch
        {
            MusicGenre.HipHop => DanceStyle.HipHop, MusicGenre.Edm => DanceStyle.House, MusicGenre.Ballad => DanceStyle.Contemporary,
            MusicGenre.Lofi => DanceStyle.Popping, MusicGenre.Rock => DanceStyle.HipHop, _ => DanceStyle.KPop,
        };
    }

    public static string StyleLabel(DanceStyle s) => s switch
    {
        DanceStyle.KPop => "K-Pop", DanceStyle.HipHop => "힙합", DanceStyle.House => "하우스", DanceStyle.Popping => "팝핀", _ => "컨템퍼러리",
    };

    public static string MoveLabel(DanceMove m) => m switch
    {
        DanceMove.Groove => "그루브", DanceMove.ArmWave => "암 웨이브", DanceMove.KPopPoint => "포인트 안무", DanceMove.Clap => "클랩", DanceMove.BodyRoll => "바디 롤",
        DanceMove.StepTouch => "스텝 터치", DanceMove.HipHopRock => "락킹 바운스", DanceMove.PowerHit => "파워 히트", _ => "프리즈",
    };

    public static string MoveEnglish(DanceMove m) => m switch
    {
        DanceMove.Groove => "grooving with a relaxed bounce", DanceMove.ArmWave => "flowing arm waves", DanceMove.KPopPoint => "sharp K-pop point choreography",
        DanceMove.Clap => "clapping on the beat", DanceMove.BodyRoll => "smooth body roll", DanceMove.StepTouch => "side step-touch footwork",
        DanceMove.HipHopRock => "hip-hop rock bounce with attitude", DanceMove.PowerHit => "explosive power hit pose", _ => "popping freeze pose",
    };

    private static string StyleEnglish(DanceStyle s) => s switch
    {
        DanceStyle.KPop => "energetic K-pop idol choreography, sharp synchronized moves",
        DanceStyle.HipHop => "hip-hop street dance, heavy groove",
        DanceStyle.House => "house dance footwork, rhythmic jacking",
        DanceStyle.Popping => "popping dance with crisp hits",
        _ => "expressive contemporary dance, fluid lines",
    };

    private static DanceMove[] Pool(DanceStyle s) => s switch
    {
        DanceStyle.KPop => new[] { DanceMove.KPopPoint, DanceMove.Groove, DanceMove.ArmWave, DanceMove.Clap, DanceMove.StepTouch },
        DanceStyle.HipHop => new[] { DanceMove.HipHopRock, DanceMove.Groove, DanceMove.StepTouch, DanceMove.BodyRoll },
        DanceStyle.House => new[] { DanceMove.StepTouch, DanceMove.Groove, DanceMove.ArmWave, DanceMove.Clap },
        DanceStyle.Popping => new[] { DanceMove.FreezePose, DanceMove.BodyRoll, DanceMove.KPopPoint, DanceMove.HipHopRock },
        _ => new[] { DanceMove.ArmWave, DanceMove.BodyRoll, DanceMove.FreezePose, DanceMove.Groove },
    };

    // ── 플랜 생성(결정적) ───────────────────────────────────────────────────

    /// <summary>시각 → 곡 구간(트랙 구간 우선 · 없으면 마디 에너지로 추정).</summary>
    private static SectionKind KindAt(double t, MusicTrack? track, BeatAnalysis a)
    {
        if (track is { Sections.Count: > 0, Bpm: > 0 })
        {
            var barSec = 240.0 / track.Bpm;
            var bar = (int)(t / barSec + 1e-6);
            foreach (var s in track.Sections) if (bar >= s.StartBar && bar < s.StartBar + s.Bars) return s.Kind;
            return SectionKind.Outro;
        }
        foreach (var d in a.Drops) if (t >= d - 0.05 && t < d + 4 * 4 * a.BeatSec) return SectionKind.Drop;
        var e = EnergyAt(t, a);
        return e > 0.8 ? SectionKind.Chorus : e < 0.35 ? SectionKind.Intro : SectionKind.Verse;
    }

    private static float EnergyAt(double t, BeatAnalysis a)
    {
        if (a.BarEnergy.Length == 0) return 0.6f;
        var i = AudioBeatVisualSyncEngine.Nearest(a.Downbeats, (float)t);
        if (i >= 0 && a.Downbeats[i] > t + 0.05) i--;
        return a.BarEnergy[Math.Clamp(i, 0, a.BarEnergy.Length - 1)];
    }

    public ChoreographyPlan Generate(BeatAnalysis a, MusicTrack? track, ChoreoOptions? options = null)
    {
        var o = options ?? new ChoreoOptions();
        var style = o.Style ?? AutoStyle(track?.Genre, track?.Mood);
        var bpm = a.Bpm > 0 ? a.Bpm : track?.Bpm ?? 120;
        var beatSec = 60.0 / bpm;
        double first = a.Downbeats.Length > 0 ? a.Downbeats[0] : a.Beats.Length > 0 ? a.Beats[0] : 0;
        while (first - beatSec * 4 >= -0.02) first -= beatSec * 4;
        var duration = Math.Max(a.Duration, track?.Seconds ?? 0);
        var totalBeats = (int)Math.Ceiling((duration - first) / beatSec);
        var pool = Pool(style);
        var hook = pool[0];
        var rng = new Sound.XorShift32(o.Seed != 0 ? o.Seed : (int)(bpm * 100) + (int)style * 7919);
        var complexity = Math.Clamp(o.Complexity, 0, 1);
        var segs = new List<ChoreoSegment>(totalBeats / 4 + 2);
        var hookRepeats = 0;
        var phrase = 0;
        // 드랍 박(분석 우선 · 없으면 곡 구간) — 프레이즈 경계를 드랍에 재정렬
        var dropBeats = new List<int>();
        foreach (var d in a.Drops) dropBeats.Add((int)Math.Round((d - first) / beatSec));
        if (dropBeats.Count == 0 && track is { Sections.Count: > 0, Bpm: > 0 })
            foreach (var s in track.Sections) if (s.Kind == SectionKind.Drop) dropBeats.Add((int)Math.Round((s.StartBar * 240.0 / track.Bpm - first) / beatSec));
        for (var b = 0; b < totalBeats; phrase++)
        {
            var t = first + b * beatSec;
            var len = Math.Min(PhraseBeats, totalBeats - b);
            foreach (var db in dropBeats) if (db > b && db < b + len) len = db - b;
            var dropStart = dropBeats.Contains(b);
            var kind = dropStart ? SectionKind.Drop : KindAt(t + 0.01, track, a);
            var energy = EnergyAt(t + 0.01, a);
            var mirror = (phrase & 1) == 1;
            var bStart = b;
            b += len;
            switch (kind)
            {
                case SectionKind.Intro or SectionKind.Outro:
                    segs.Add(new ChoreoSegment(bStart, len, DanceMove.Groove, Math.Min(energy, 0.5f), mirror));
                    break;
                case SectionKind.Drop when dropStart:
                    segs.Add(new ChoreoSegment(bStart, Math.Min(2, len), DanceMove.PowerHit, 1f, false));
                    if (len > 2) segs.Add(new ChoreoSegment(bStart + 2, len - 2, hook, 1f, mirror));
                    hookRepeats++;
                    break;
                case SectionKind.Chorus or SectionKind.Drop:
                    // 훅 반복: 코러스 프레이즈 = [훅 4 · 서브 4](복잡도 낮으면 훅 8)
                    if (complexity < 0.35 || len <= 4) segs.Add(new ChoreoSegment(bStart, len, hook, Math.Max(0.8f, energy), mirror));
                    else { segs.Add(new ChoreoSegment(bStart, 4, hook, Math.Max(0.8f, energy), mirror)); segs.Add(new ChoreoSegment(bStart + 4, len - 4, pool[1 + phrase % (pool.Length - 1)], Math.Max(0.8f, energy), !mirror)); }
                    hookRepeats++;
                    break;
                default:
                    if (complexity >= 0.6 && len > 4)
                    {
                        segs.Add(new ChoreoSegment(bStart, 4, Pick(pool, ref rng), energy, mirror));
                        segs.Add(new ChoreoSegment(bStart + 4, len - 4, Pick(pool, ref rng), energy, !mirror));
                    }
                    else segs.Add(new ChoreoSegment(bStart, len, Pick(pool, ref rng), energy, mirror));
                    break;
            }
        }
        var plan = new ChoreographyPlan
        {
            Style = style, Bpm = bpm, FirstBeatSec = first, BeatSec = beatSec, DurationSec = duration, Amplitude = Math.Clamp(o.Amplitude, 0, 1), Complexity = complexity,
            Segments = segs.ToArray(), HookRepeats = hookRepeats,
            Summary = StyleLabel(style) + " · " + segs.Count.ToString(CultureInfo.InvariantCulture) + "구간 · 훅 반복 " + hookRepeats.ToString(CultureInfo.InvariantCulture) + " · " + bpm.ToString("0.#", CultureInfo.InvariantCulture) + " BPM",
        };
        Current = plan;
        LastAction = "💃 안무 생성 · " + plan.Summary;
        Raise();
        return plan;
    }

    private static DanceMove Pick(DanceMove[] pool, ref Sound.XorShift32 rng) => pool[Math.Min(pool.Length - 1, (int)(rng.Next01() * pool.Length))];

    /// <summary>시각 → 구간 인덱스(이진 탐색 · 무할당).</summary>
    public static int SegmentAt(ChoreographyPlan p, double t)
    {
        var segs = p.Segments;
        if (segs.Length == 0) return -1;
        var b = (t - p.FirstBeatSec) / p.BeatSec;
        int lo = 0, hi = segs.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (segs[mid].StartBeat <= b) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    // ── 포즈 합성(무할당) ───────────────────────────────────────────────────

    private static float Ease(float x) { x = Math.Clamp(x, 0, 1); return 1 - (1 - x) * (1 - x); }

    /// <summary>시각 t(초) → 33 관절 포즈(x,y,z,vis · 길이 ≥ 132 · 무할당). 반환: 동작.</summary>
    public static DanceMove EvaluatePose(ChoreographyPlan p, double t, Span<float> pose)
    {
        var si = SegmentAt(p, t);
        var seg = si >= 0 ? p.Segments[si] : new ChoreoSegment(0, PhraseBeats, DanceMove.Groove, 0.5f, false);
        var gb = Math.Max(0, (t - p.FirstBeatSec) / p.BeatSec);
        var lb = (float)Math.Max(0, gb - seg.StartBeat);
        var ph = lb - MathF.Floor(lb);
        var count = (int)lb;
        var A = p.Amplitude * (0.4f + 0.6f * seg.Energy);
        const float hx = 0.5f, hh = 0.1f;
        var hy = 0.16f;
        float lwx = hx + 0.16f, lwy = hy + 0.38f, rwx = hx - 0.16f, rwy = hy + 0.38f;
        float bounce = 0, sway = 0, lean = 0, stepL = 0, stepR = 0, roll = 0;
        var pi = MathF.PI;
        switch (seg.Move)
        {
            case DanceMove.Groove:
                bounce = A * 0.025f * (0.5f + 0.5f * MathF.Cos(2 * pi * lb));
                lwy += A * 0.05f * MathF.Sin(2 * pi * lb); rwy -= A * 0.05f * MathF.Sin(2 * pi * lb);
                sway = A * 0.015f * MathF.Sin(pi * lb);
                break;
            case DanceMove.ArmWave:
                lwx = hx + 0.2f + A * 0.06f * MathF.Sin(pi * lb); lwy = hy - 0.02f + A * 0.09f * MathF.Cos(pi * lb);
                rwx = hx - 0.2f + A * 0.06f * MathF.Sin(pi * lb + pi / 2); rwy = hy - 0.02f + A * 0.09f * MathF.Cos(pi * lb + pi / 2);
                bounce = A * 0.01f * (0.5f + 0.5f * MathF.Cos(2 * pi * lb));
                roll = A * 0.012f;
                break;
            case DanceMove.KPopPoint:
            {
                var s = Ease(ph * 4);
                float ax, ay, bx, by, cx, cy, dx, dy;   // (오른손 이전→목표, 왼손 이전→목표)
                if ((count & 1) == 0) { ax = hx - 0.05f; ay = hy + 0.2f; bx = hx - 0.24f; by = hy - 0.14f; cx = hx + 0.25f; cy = hy + 0.15f; dx = hx + 0.12f; dy = hy + 0.36f; }
                else { ax = hx - 0.24f; ay = hy - 0.14f; bx = hx - 0.05f; by = hy + 0.2f; cx = hx + 0.12f; cy = hy + 0.36f; dx = hx + 0.25f; dy = hy + 0.15f; }
                rwx = ax + (bx - ax) * s; rwy = ay + (by - ay) * s;
                lwx = cx + (dx - cx) * s; lwy = cy + (dy - cy) * s;
                bounce = A * 0.022f * (1 - s);
                break;
            }
            case DanceMove.Clap:
            {
                var d = MathF.Abs(MathF.Sin(pi * lb));
                lwx = hx + 0.03f + 0.15f * d * A; rwx = hx - 0.03f - 0.15f * d * A;
                lwy = rwy = hy + 0.24f - 0.04f * A * (1 - d);
                bounce = A * 0.02f * (1 - d);
                break;
            }
            case DanceMove.BodyRoll:
                roll = A * 0.03f;
                lwx = hx + 0.12f; lwy = hy + 0.32f; rwx = hx - 0.12f; rwy = hy + 0.32f;
                bounce = A * 0.015f * (0.5f - 0.5f * MathF.Cos(pi * lb));
                break;
            case DanceMove.StepTouch:
            {
                var side = MathF.Sin(pi * lb);
                sway = A * 0.05f * side;
                if (side > 0) stepL = A * 0.06f * side; else stepR = A * 0.06f * side;
                lwy += A * 0.06f * side; rwy -= A * 0.06f * side;
                bounce = A * 0.02f * MathF.Abs(MathF.Cos(pi * lb));
                break;
            }
            case DanceMove.HipHopRock:
                bounce = A * 0.03f * MathF.Max(0, MathF.Sin(2 * pi * (lb + 0.25f)));
                lean = A * 0.03f * MathF.Sin(2 * pi * lb);
                lwx = hx + 0.1f; lwy = hy + 0.28f + 0.03f * MathF.Sin(2 * pi * lb); rwx = hx - 0.1f; rwy = lwy;
                break;
            case DanceMove.PowerHit:
            {
                var s = Ease(lb * 6);
                lwx = hx + 0.16f + 0.1f * s; lwy = hy + 0.38f - 0.56f * s * Math.Max(0.5f, A);
                rwx = hx - 0.16f - 0.1f * s; rwy = lwy;
                bounce = A * 0.05f * MathF.Exp(-lb * 3);
                break;
            }
            default:   // FreezePose(팝핀 틱)
            {
                var tick = MathF.Sin(4 * pi * lb) >= 0 ? 1 : -1;
                rwx = hx - 0.05f; rwy = hy - 0.25f + A * 0.01f * tick;
                lwx = hx + 0.12f; lwy = hy + 0.34f;
                break;
            }
        }
        if (seg.Move != DanceMove.FreezePose) hy += A * 0.01f * MathF.Cos(2 * pi * lb);
        PoseBuilder.Write(pose, hx + lean, hy, hh, lwx, lwy, rwx, rwy, bounce, 0.95f);
        // 후처리: 스웨이(상체) · 바디 롤(높이 위상) · 스텝(하체)
        for (var j = 0; j < MocapFrame.PoseJoints; j++)
        {
            var i = j * MocapFrame.PoseStride;
            var y = pose[i + 1];
            if (j < (int)PoseJoint.LeftHip) pose[i] += sway;
            else if (j < (int)PoseJoint.LeftKnee) pose[i] += sway * 0.6f;
            if (roll > 0) pose[i] += roll * MathF.Sin(pi * lb - y * 10);
            if (stepL != 0 && j >= (int)PoseJoint.LeftKnee && (j & 1) == 1) pose[i] += j == (int)PoseJoint.LeftKnee ? stepL * 0.5f : stepL;
            if (stepR != 0 && j >= (int)PoseJoint.LeftKnee && (j & 1) == 0) pose[i] += j == (int)PoseJoint.RightKnee ? stepR * 0.5f : stepR;
        }
        if (seg.Mirror)
        {
            for (var j = 0; j < MocapFrame.PoseJoints; j++) pose[j * MocapFrame.PoseStride] = 2 * hx - pose[j * MocapFrame.PoseStride];
            for (var k = 0; k < MirrorPairs.Length; k += 2)
            {
                int a = MirrorPairs[k] * MocapFrame.PoseStride, b = MirrorPairs[k + 1] * MocapFrame.PoseStride;
                for (var c = 0; c < MocapFrame.PoseStride; c++) (pose[a + c], pose[b + c]) = (pose[b + c], pose[a + c]);
            }
        }
        return seg.Move;
    }

    private static HandGesture GestureOf(DanceMove m, bool right) => m switch
    {
        DanceMove.KPopPoint => right ? HandGesture.Point : HandGesture.OpenPalm,
        DanceMove.Clap or DanceMove.PowerHit or DanceMove.ArmWave => HandGesture.OpenPalm,
        DanceMove.FreezePose => right ? HandGesture.Peace : HandGesture.Fist,
        DanceMove.HipHopRock => HandGesture.Fist,
        _ => HandGesture.None,
    };

    // ── 클립(리타겟) · BVH ──────────────────────────────────────────────────

    /// <summary>플랜 → 리타겟 모캡 클립(v16.36.0 리그 · 표정/제스처 채널).</summary>
    public MocapClip BuildClip(ChoreographyPlan p, RigProportions? target = null, int fps = 30, FacialEmotion emotion = FacialEmotion.HappySmile)
    {
        var tgt = target ?? RigProportions.Adult;
        fps = Math.Clamp(fps, 10, 120);
        var n = Math.Max(1, (int)(p.DurationSec * fps));
        var data = new float[n * MocapClip.Stride];
        Span<float> pose = stackalloc float[MocapFrame.PoseLength];
        Span<float> p3 = stackalloc float[MocapFrame.PoseJoints * 3];
        Span<Vector3> src = stackalloc Vector3[Rig.Joints];
        Span<Vector3> dst = stackalloc Vector3[Rig.Joints];
        var guide = FacialEmotionPuppeteerEngine.GuideOf(emotion, 0.7, false);
        for (var f = 0; f < n; f++)
        {
            var t = f / (double)fps;
            var move = EvaluatePose(p, t, pose);
            for (var j = 0; j < MocapFrame.PoseJoints; j++) { p3[j * 3] = pose[j * 4]; p3[j * 3 + 1] = pose[j * 4 + 1]; p3[j * 3 + 2] = pose[j * 4 + 2]; }
            FacialAndHandGestureMappingController.SourceRig(p3, src);
            FacialAndHandGestureMappingController.Retarget(src, tgt, dst);
            var o = f * MocapClip.Stride;
            for (var j = 0; j < Rig.Joints; j++) { data[o + j * 3] = dst[j].X; data[o + j * 3 + 1] = dst[j].Y; data[o + j * 3 + 2] = dst[j].Z; }
            data[o + MocapClip.GuideAt] = (float)guide.MouthCornerLift; data[o + MocapClip.GuideAt + 1] = (float)guide.MouthOpen; data[o + MocapClip.GuideAt + 2] = (float)guide.BrowRaise;
            data[o + MocapClip.GuideAt + 3] = (float)guide.EyeOpen; data[o + MocapClip.GuideAt + 4] = (float)guide.CheekRaise; data[o + MocapClip.GuideAt + 5] = (float)guide.LipPucker;
            data[o + MocapClip.EmotionAt] = (float)emotion;
            data[o + MocapClip.IntensityAt] = emotion == FacialEmotion.Neutral ? 0 : 0.7f;
            data[o + MocapClip.LeftAt] = (float)GestureOf(move, false);
            data[o + MocapClip.RightAt] = (float)GestureOf(move, true);
            data[o + MocapClip.TimeAt] = (float)(t * 1000);
        }
        var clip = FacialAndHandGestureMappingController.BuildClip(data, n, tgt);
        LastClip = clip;
        LastAction = "🦴 안무 클립 " + n.ToString(CultureInfo.InvariantCulture) + "프레임 · " + tgt.Label + " · " + clip.ActionLabel;
        Raise();
        return clip;
    }

    public string ExportBvh(MocapClip clip, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, FacialAndHandGestureMappingController.ToBvh(clip));
        LastAction = "💾 안무 BVH " + Path.GetFileName(path);
        Raise();
        return path;
    }

    /// <summary>
    /// 1-Click MV 구성 — 비트 플랜 적용(전환/VFX/스피드/폴리/BGM) → 컷별 안무 동작 · 스타일 · 표정 · 트윈 · 가사 구간 주입. 반환: 컷 수.
    /// </summary>
    public int ComposeMusicVideo(StoryboardProject project, AudioBeatVisualSyncEngine sync, BeatSyncPlan plan, ChoreographyPlan choreo, MusicTrack? track, DigitalTwinAsset? twin, bool replace = true, string? basePrompt = null)
    {
        if (replace) project.Scenes.Clear();
        var start = project.Scenes.Count;
        var subject = twin is null ? "a charismatic dancer" : "the digital twin performer";
        for (var i = 0; i < plan.Cuts.Count; i++)
            project.Scenes.Add(new StoryboardScene { Prompt = (string.IsNullOrWhiteSpace(basePrompt) ? subject + " performing in a stylish music video set" : basePrompt.Trim()) });
        // 새로 추가한 컷만 비트 플랜과 1:1 정렬
        var tail = new StoryboardProject();
        for (var i = start; i < project.Scenes.Count; i++) tail.Scenes.Add(project.Scenes[i]);
        sync.ApplyToProject(tail, plan, track?.Path, createMissing: false);
        if (!string.IsNullOrWhiteSpace(track?.Path)) project.BgmPath = track!.Path;
        Span<float> pose = stackalloc float[MocapFrame.PoseLength];
        var sad = (track?.Mood ?? "").Contains("슬", StringComparison.Ordinal) || (track?.Mood ?? "").Contains("sad", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < plan.Cuts.Count; i++)
        {
            var c = plan.Cuts[i];
            var sc = tail.Scenes[i];
            var move = EvaluatePose(choreo, c.StartSec + Math.Min(c.DurationSec, choreo.BeatSec) * 0.5, pose);
            var tokens = new List<string>(4) { StyleEnglish(choreo.Style), MoveEnglish(move), "full-body dance shot, " + choreo.Bpm.ToString("0", CultureInfo.InvariantCulture) + " BPM rhythm" };
            if (track is not null)
                foreach (var cue in track.Lyrics)
                    if (cue.StartSec <= c.StartSec + c.DurationSec && cue.EndSec >= c.StartSec) { tokens.Add("singing \"" + Trim(cue.Text, 40) + "\""); break; }
            sc.Prompt = CharacterIdentityLockService.MergeTokens(sc.Prompt, tokens);
            var emo = sad ? FacialEmotion.Neutral : c.OnDrop || move == DanceMove.PowerHit ? FacialEmotion.Surprised : FacialEmotion.HappySmile;
            if (emo != FacialEmotion.Neutral)
            {
                var inj = FacialEmotionPuppeteerEngine.Apply(sc.Prompt, sc.NegativePrompt, emo, 0.5 + 0.4 * c.Tension / 100.0);
                sc.Prompt = inj.Prompt; sc.NegativePrompt = inj.Negative; sc.Emotion = emo; sc.EmotionIntensity = inj.Intensity;
            }
            sc.NegativePrompt = CharacterIdentityLockService.MergeTokens(sc.NegativePrompt, new[] { "off-beat motion", "broken limbs", "extra arms", "frozen body" });
            if (twin is not null) AiDigitalTwinGeneratorEngine.ApplyToScene(sc, twin);
        }
        LastAction = "🎬 MV 구성 " + plan.Cuts.Count.ToString(CultureInfo.InvariantCulture) + "컷 · " + StyleLabel(choreo.Style) + (twin is null ? "" : " · 트윈 " + twin.Name);
        Raise();
        return plan.Cuts.Count;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s.Replace("\"", "'") : s[..max].Replace("\"", "'") + "…";

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Choreo.OnChange", ex); }
    }
}
