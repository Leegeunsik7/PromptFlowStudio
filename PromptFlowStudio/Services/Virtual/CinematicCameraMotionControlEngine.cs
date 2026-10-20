using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using PromptFlowStudio.Services.Autonomous;   // PerformanceFeedbackLoopEngine.CameraLexicon
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Virtual;

public enum CameraRigPreset : byte
{
    StaticLockOff, DollyIn, DollyOut, TruckLeft, TruckRight, PanLeft, PanRight, TiltUp, TiltDown,
    CraneUp, CraneDown, Orbit, GimbalFollow, Handheld, PanavisionReveal, DroneRise,
}

/// <summary>카메라 리그 키프레임(스테이지 좌표 m · x 우 / y 상 / z 전방 · 각도 도 · 초점거리 mm · 포커스 m).</summary>
public struct CameraKeyframe
{
    public float T, X, Y, Z, Pan, Tilt, Roll, FocalMm, FocusM;
}

public sealed record CameraRigPlan(CameraRigPreset Preset, double DurationSec, float Intensity, float LensMm, bool Anamorphic,
    CameraKeyframe[] Keyframes, string PromptPhrase, string Rationale);

/// <summary>
/// [PromptFlow Studio v16.32.0] 시네마틱 AI 카메라 궤적 생성기(순수 BCL).
///   ▸ 대본/무드 키워드(한/영) 가중 점수 → 리그 프리셋 추천(근거 문자열 동반).
///   ▸ 16종 프리셋(돌리·트럭·팬/틸트·크레인·오빗·짐벌 팔로우·핸드헬드·파나비전 아나모픽 리빌·드론 상승) → 이즈(smoothstep) 키프레임
///     · 핸드헬드는 시드 고정 값 노이즈(재현 가능) · 포커스 거리 = 피사체(전방 3m) 거리 자동 추종.
///   ▸ <see cref="Sample"/>: 이진 탐색 + 선형 보간(무할당 · UE 스트리밍 60Hz 핫패스).
///   ▸ 영상 생성 인젝션: 프롬프트에 카메라 구문이 없을 때만 리그 구문 + 렌즈 부착(멱등) · 프로젝트 일괄 적용.
///   ▸ UE5 내보내기: 스테이지 → UE(X 전방 · Y 우 · Z 상 · cm) 변환 JSON/CSV(Sequencer 임포트용).
/// </summary>
public sealed class CinematicCameraMotionControlEngine
{
    public const float SubjectDistance = 3f;
    public const int MaxKeys = 64;

    private static readonly (CameraRigPreset Preset, string[] Words)[] Moods =
    {
        (CameraRigPreset.DollyIn, new[] { "긴장", "몰입", "고백", "감정", "tension", "intimate", "suspense", "reveal emotion", "dolly in", "push in", "줌인" }),
        (CameraRigPreset.DollyOut, new[] { "고립", "외로", "이별", "lonely", "isolation", "farewell", "pull out", "풀아웃" }),
        (CameraRigPreset.TruckLeft, new[] { "나란히", "걷", "walk", "side by side", "truck" }),
        (CameraRigPreset.PanRight, new[] { "풍경", "둘러", "panorama", "scenery", "landscape", "pan shot", "패닝" }),
        (CameraRigPreset.TiltUp, new[] { "올려다", "거대", "건물", "tower", "giant", "look up", "tilt up" }),
        (CameraRigPreset.CraneUp, new[] { "웅장", "장엄", "클라이맥스", "epic", "grand", "majestic", "climax", "crane" }),
        (CameraRigPreset.Orbit, new[] { "회전", "주인공", "영웅", "hero", "orbit", "360", "오빗" }),
        (CameraRigPreset.GimbalFollow, new[] { "추격", "따라", "달리", "달린", "질주", "chase", "follow", "running", "tracking", "트래킹" }),
        (CameraRigPreset.Handheld, new[] { "혼란", "다큐", "격투", "불안", "chaos", "documentary", "fight", "panic", "handheld", "핸드헬드" }),
        (CameraRigPreset.PanavisionReveal, new[] { "영화", "시네마틱", "등장", "cinematic", "anamorphic", "panavision", "film", "blockbuster" }),
        (CameraRigPreset.DroneRise, new[] { "드론", "항공", "도시", "aerial", "drone", "skyline", "bird" }),
        (CameraRigPreset.StaticLockOff, new[] { "평화", "고요", "인터뷰", "calm", "peaceful", "interview", "still", "고정" }),
    };

    private long _plans, _injected;

    public event Action? OnChange;

    public long Plans => Interlocked.Read(ref _plans);
    public long Injected => Interlocked.Read(ref _injected);
    public CameraRigPlan? LastPlan { get; private set; }
    public string? LastAction { get; private set; }

    public static string Label(CameraRigPreset p) => p switch
    {
        CameraRigPreset.StaticLockOff => "고정(락오프)", CameraRigPreset.DollyIn => "돌리 인", CameraRigPreset.DollyOut => "돌리 아웃",
        CameraRigPreset.TruckLeft => "트럭 좌", CameraRigPreset.TruckRight => "트럭 우", CameraRigPreset.PanLeft => "팬 좌", CameraRigPreset.PanRight => "팬 우",
        CameraRigPreset.TiltUp => "틸트 업", CameraRigPreset.TiltDown => "틸트 다운", CameraRigPreset.CraneUp => "크레인 업", CameraRigPreset.CraneDown => "크레인 다운",
        CameraRigPreset.Orbit => "오빗", CameraRigPreset.GimbalFollow => "짐벌 팔로우", CameraRigPreset.Handheld => "핸드헬드",
        CameraRigPreset.PanavisionReveal => "파나비전 아나모픽 리빌", CameraRigPreset.DroneRise => "드론 상승", _ => p.ToString(),
    };

    /// <summary>프롬프트 구문(영문 · <see cref="PerformanceFeedbackLoopEngine.CameraLexicon"/> 정합).</summary>
    public static string Phrase(CameraRigPreset p) => p switch
    {
        CameraRigPreset.StaticLockOff => "static wide shot",
        CameraRigPreset.DollyIn => "slow dolly-in",
        CameraRigPreset.DollyOut => "slow dolly-out pull back",
        CameraRigPreset.TruckLeft => "lateral truck left",
        CameraRigPreset.TruckRight => "lateral truck right",
        CameraRigPreset.PanLeft => "slow pan left",
        CameraRigPreset.PanRight => "slow pan right",
        CameraRigPreset.TiltUp => "slow tilt up",
        CameraRigPreset.TiltDown => "slow tilt down",
        CameraRigPreset.CraneUp => "sweeping crane up shot",
        CameraRigPreset.CraneDown => "descending crane down shot",
        CameraRigPreset.Orbit => "orbit shot",
        CameraRigPreset.GimbalFollow => "smooth tracking shot on gimbal",
        CameraRigPreset.Handheld => "handheld close-up",
        CameraRigPreset.PanavisionReveal => "anamorphic 2.39:1 Panavision lens, slow cinematic push-in reveal",
        CameraRigPreset.DroneRise => "aerial drone shot rising",
        _ => "static wide shot",
    };

    public static float DefaultLens(CameraRigPreset p) => p switch
    {
        CameraRigPreset.DroneRise => 24, CameraRigPreset.CraneUp or CameraRigPreset.CraneDown => 28, CameraRigPreset.Handheld => 32,
        CameraRigPreset.PanavisionReveal => 50, CameraRigPreset.DollyIn or CameraRigPreset.DollyOut => 40, _ => 35,
    };

    // ══════════════════════ 추천 ══════════════════════

    /// <summary>대본 + 무드 키워드 → 프리셋(동점 시 선언 순 · 매칭 없으면 짐벌 팔로우 대신 고정).</summary>
    public static (CameraRigPreset Preset, string Rationale) Suggest(string? script, string? mood)
    {
        var text = ((script ?? "") + " " + (mood ?? "")).ToLowerInvariant();
        var best = -1; var bestScore = 0; string? hit = null;
        for (var i = 0; i < Moods.Length; i++)
        {
            var score = 0; string? first = null;
            foreach (var w in Moods[i].Words)
            {
                if (text.Contains(w, StringComparison.Ordinal)) { score += mood is not null && mood.Contains(w, StringComparison.OrdinalIgnoreCase) ? 2 : 1; first ??= w; }
            }
            if (score > bestScore) { bestScore = score; best = i; hit = first; }
        }
        return best < 0
            ? (CameraRigPreset.StaticLockOff, "키워드 없음 → 안정적인 고정 와이드")
            : (Moods[best].Preset, "‘" + hit + "’ 등 " + bestScore.ToString(CultureInfo.InvariantCulture) + "점 → " + Label(Moods[best].Preset));
    }

    // ══════════════════════ 생성 ══════════════════════

    public CameraRigPlan Generate(CameraRigPreset preset, double durationSec = 5, float intensity = 1f, int seed = 1, string? rationale = null)
    {
        var plan = Build(preset, durationSec, intensity, seed, rationale ?? Label(preset));
        Interlocked.Increment(ref _plans);
        LastPlan = plan;
        LastAction = "🎥 " + Label(preset) + " · " + plan.Keyframes.Length.ToString(CultureInfo.InvariantCulture) + "키 · " + plan.LensMm.ToString("0", CultureInfo.InvariantCulture) + "mm";
        Raise();
        return plan;
    }

    /// <summary>대본/무드 자동 추천 → 생성.</summary>
    public CameraRigPlan GenerateFor(string? script, string? mood, double durationSec = 5, float intensity = 1f)
    {
        var (p, why) = Suggest(script, mood);
        return Generate(p, durationSec, intensity, StableSeed(script), why);
    }

    public static CameraRigPlan Build(CameraRigPreset preset, double durationSec, float intensity, int seed, string rationale)
    {
        var dur = Math.Clamp(durationSec, 0.5, 120);
        var I = Math.Clamp(intensity, 0.1f, 2f);
        var n = preset == CameraRigPreset.Handheld ? 25 : 9;
        var keys = new CameraKeyframe[n];
        var lens = DefaultLens(preset);
        for (var k = 0; k < n; k++)
        {
            var u = (float)k / (n - 1);
            var e = u * u * (3 - 2 * u);
            ref var c = ref keys[k];
            c.T = (float)(u * dur);
            c.FocalMm = lens;
            switch (preset)
            {
                case CameraRigPreset.DollyIn: c.Z = (e - 1) * 1.5f * I; break;
                case CameraRigPreset.DollyOut: c.Z = -e * 1.5f * I; break;
                case CameraRigPreset.TruckLeft: c.X = -e * 1.5f * I; break;
                case CameraRigPreset.TruckRight: c.X = e * 1.5f * I; break;
                case CameraRigPreset.PanLeft: c.Pan = -e * 30f * I; break;
                case CameraRigPreset.PanRight: c.Pan = e * 30f * I; break;
                case CameraRigPreset.TiltUp: c.Tilt = e * 20f * I; break;
                case CameraRigPreset.TiltDown: c.Tilt = -e * 20f * I; break;
                case CameraRigPreset.CraneUp: c.Y = e * 2f * I; c.Tilt = -Deg(MathF.Atan2(c.Y, SubjectDistance)); break;
                case CameraRigPreset.CraneDown: c.Y = (1 - e) * 2f * I; c.Tilt = -Deg(MathF.Atan2(c.Y, SubjectDistance)); break;
                case CameraRigPreset.Orbit:
                {
                    var a = (e - 0.5f) * 90f * I * (MathF.PI / 180f);
                    c.X = SubjectDistance * MathF.Sin(a); c.Z = SubjectDistance * (1 - MathF.Cos(a)); c.Pan = -Deg(a);
                    break;
                }
                case CameraRigPreset.GimbalFollow:
                    c.Z = e * 2f * I; c.X = 0.3f * I * MathF.Sin(2 * MathF.PI * u); c.Roll = 2f * I * MathF.Sin(2 * MathF.PI * u);
                    break;
                case CameraRigPreset.Handheld:
                    c.X = 0.03f * I * Noise(seed, k, 0); c.Y = 0.03f * I * Noise(seed, k, 1);
                    c.Pan = 1.2f * I * Noise(seed, k, 2); c.Tilt = 1.2f * I * Noise(seed, k, 3); c.Roll = 0.8f * I * Noise(seed, k, 4);
                    break;
                case CameraRigPreset.PanavisionReveal: c.Z = (e - 1) * 0.8f * I; c.Y = 0.3f * I * e; c.Tilt = -Deg(MathF.Atan2(c.Y, SubjectDistance - c.Z)); break;
                case CameraRigPreset.DroneRise: c.Y = e * 15f * I; c.Z = e * 8f * I - 4f; c.Tilt = -Deg(MathF.Atan2(c.Y, SubjectDistance + 6f - c.Z)); break;
            }
            float dx = c.X, dy = c.Y, dz = SubjectDistance - c.Z;
            c.FocusM = MathF.Max(0.3f, MathF.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        var lensText = preset == CameraRigPreset.PanavisionReveal ? "" : ", " + lens.ToString("0", CultureInfo.InvariantCulture) + "mm lens";
        return new(preset, dur, I, lens, preset == CameraRigPreset.PanavisionReveal, keys, Phrase(preset) + lensText, rationale);
    }

    private static float Deg(float rad) => rad * (180f / MathF.PI);

    /// <summary>결정적 값 노이즈(-1~1 · 시드/키/채널 해시).</summary>
    public static float Noise(int seed, int k, int ch)
    {
        unchecked
        {
            var h = (uint)seed * 0x9E3779B1u ^ (uint)k * 0x85EBCA77u ^ (uint)ch * 0xC2B2AE3Du;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0x7FFFFF - 1f;
        }
    }

    public static int StableSeed(string? s)
    {
        unchecked
        {
            var h = 17;
            foreach (var ch in s ?? "") h = h * 31 + ch;
            return h;
        }
    }

    // ══════════════════════ 샘플링(핫패스) ══════════════════════

    /// <summary>t초 리그 상태(이진 탐색 + 선형 보간 · 무할당). 범위 밖은 끝 키 고정.</summary>
    public static CameraKeyframe Sample(ReadOnlySpan<CameraKeyframe> keys, double t)
    {
        if (keys.IsEmpty) return default;
        if (t <= keys[0].T) return keys[0];
        if (t >= keys[^1].T) return keys[^1];
        int lo = 0, hi = keys.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (keys[mid].T <= t) lo = mid; else hi = mid;
        }
        ref readonly var a = ref keys[lo];
        ref readonly var b = ref keys[hi];
        var span = b.T - a.T;
        var f = span > 1e-6f ? (float)((t - a.T) / span) : 0f;
        CameraKeyframe r;
        r.T = (float)t;
        r.X = a.X + (b.X - a.X) * f; r.Y = a.Y + (b.Y - a.Y) * f; r.Z = a.Z + (b.Z - a.Z) * f;
        r.Pan = a.Pan + (b.Pan - a.Pan) * f; r.Tilt = a.Tilt + (b.Tilt - a.Tilt) * f; r.Roll = a.Roll + (b.Roll - a.Roll) * f;
        r.FocalMm = a.FocalMm + (b.FocalMm - a.FocalMm) * f; r.FocusM = a.FocusM + (b.FocusM - a.FocusM) * f;
        return r;
    }

    /// <summary>스테이지(m · y 상 · z 전방) → UE5(cm · X 전방 · Y 우 · Z 상).</summary>
    public static (float X, float Y, float Z) StageToUnreal(float x, float y, float z) => (z * 100f, x * 100f, y * 100f);

    // ══════════════════════ 영상 생성 인젝션 ══════════════════════

    /// <summary>카메라 구문이 없을 때만 리그 구문 부착(멱등).</summary>
    public static string Inject(string? prompt, CameraRigPlan plan)
    {
        var p = (prompt ?? "").TrimEnd();
        if (p.Length == 0) return plan.PromptPhrase;
        if (p.Contains(plan.PromptPhrase, StringComparison.OrdinalIgnoreCase)) return p;
        if (PerformanceFeedbackLoopEngine.DetectFeature(p, PerformanceFeedbackLoopEngine.CameraLexicon) >= 0) return p;
        return p.TrimEnd(',', '.', ' ') + ", " + plan.PromptPhrase;
    }

    /// <summary>프로젝트 컷 일괄 — 컷별 프롬프트로 자동 추천(또는 고정 프리셋) 후 인젝션. 반환 = 변경된 컷 수.</summary>
    public int ApplyToProject(StoryboardProject? project, CameraRigPreset? fixedPreset = null, string? mood = null, float intensity = 1f)
    {
        if (project is null) return 0;
        var changed = 0;
        foreach (var s in project.Scenes)
        {
            var (preset, why) = fixedPreset is { } fp ? (fp, Label(fp)) : Suggest(s.Prompt, mood);
            var plan = Build(preset, 5, intensity, StableSeed(s.Prompt), why);
            var next = Inject(s.Prompt, plan);
            if (next == s.Prompt) continue;
            s.Prompt = next;
            changed++;
        }
        Interlocked.Add(ref _injected, changed);
        LastAction = "💉 카메라 리그 인젝션 · " + changed.ToString(CultureInfo.InvariantCulture) + "컷";
        Raise();
        return changed;
    }

    // ══════════════════════ UE5 내보내기 ══════════════════════

    /// <summary>fps 프레임 샘플 CSV(frame,time,x,y,z,pitch,yaw,roll,focal,focus — UE cm/도).</summary>
    public static string ToUnrealCsv(CameraRigPlan plan, double fps = 24)
    {
        fps = Math.Clamp(fps, 1, 120);
        var ci = CultureInfo.InvariantCulture;
        var frames = (int)Math.Ceiling(plan.DurationSec * fps) + 1;
        var sb = new StringBuilder(64 + frames * 72);
        sb.Append("frame,time,x_cm,y_cm,z_cm,pitch,yaw,roll,focal_mm,focus_cm\n");
        for (var f = 0; f < frames; f++)
        {
            var t = Math.Min(plan.DurationSec, f / fps);
            var k = Sample(plan.Keyframes, t);
            var (ux, uy, uz) = StageToUnreal(k.X, k.Y, k.Z);
            sb.Append(f.ToString(ci)).Append(',').Append(t.ToString("0.####", ci)).Append(',')
              .Append(ux.ToString("0.##", ci)).Append(',').Append(uy.ToString("0.##", ci)).Append(',').Append(uz.ToString("0.##", ci)).Append(',')
              .Append(k.Tilt.ToString("0.###", ci)).Append(',').Append(k.Pan.ToString("0.###", ci)).Append(',').Append(k.Roll.ToString("0.###", ci)).Append(',')
              .Append(k.FocalMm.ToString("0.#", ci)).Append(',').Append((k.FocusM * 100f).ToString("0.#", ci)).Append('\n');
        }
        return sb.ToString();
    }

    public static string ToUnrealJson(CameraRigPlan plan)
    {
        var keys = new List<Dictionary<string, object>>(plan.Keyframes.Length);
        foreach (var k in plan.Keyframes)
        {
            var (ux, uy, uz) = StageToUnreal(k.X, k.Y, k.Z);
            keys.Add(new()
            {
                ["t"] = Math.Round(k.T, 4), ["location"] = new[] { Math.Round(ux, 2), Math.Round(uy, 2), Math.Round(uz, 2) },
                ["rotation"] = new[] { Math.Round(k.Tilt, 3), Math.Round(k.Pan, 3), Math.Round(k.Roll, 3) },
                ["focalMm"] = Math.Round(k.FocalMm, 2), ["focusCm"] = Math.Round(k.FocusM * 100, 1),
            });
        }
        var doc = new Dictionary<string, object>
        {
            ["generator"] = "PromptFlow Studio", ["version"] = "16.32.0", ["units"] = "UE5 cm/deg (X fwd, Y right, Z up)",
            ["preset"] = plan.Preset.ToString(), ["duration"] = plan.DurationSec, ["lensMm"] = plan.LensMm, ["anamorphic"] = plan.Anamorphic,
            ["filmback"] = plan.Anamorphic ? "2.39:1" : "16:9", ["prompt"] = plan.PromptPhrase, ["keys"] = keys,
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("CameraMotion.OnChange", ex); }
        }
    }
}
