using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;     // IVideoMergeService
using PromptFlowStudio.Services.Spatial;   // MvHevcSpatialVideoTranscoder.Binauralize/SafeName

namespace PromptFlowStudio.Services.Sound;

public enum SpatialAudioLayout : byte { Binaural, Surround51, Surround714 }

/// <summary>스피커 정의(방위 도 · 좌 + (ADM 규약) · 고도 도).</summary>
public readonly record struct SpeakerDef(string Label, string AdmLabel, float Az, float El, bool Lfe);

/// <summary>오브젝트 위치 키(ADM 데카르트: X 좌-1~우+1 · Y 후-1~전+1 · Z 하-1~상+1).</summary>
public readonly record struct AtmosKeyframe(double T, float X, float Y, float Z);

public sealed class AtmosObject
{
    public string Name { get; init; } = "Object";
    public float[] Audio { get; init; } = Array.Empty<float>();
    public List<AtmosKeyframe> Path { get; init; } = new();
    public string Fingerprint { get; set; } = "";
}

public sealed record MasterOptions(bool Binaural = true, bool Surround51 = true, bool Atmos714 = true, bool AdmBwf = true, bool MuxVideo = true);

public sealed record MasterResult(bool Ok, string? Folder, IReadOnlyList<string> Files, double Lufs, float PeakDb, int FoleyEvents, int Objects, bool PartialRescue, string Message);

/// <summary>
/// [PromptFlow Studio v16.33.0] Dolby Atmos 3D 공간 음향 오브젝트 매퍼 &amp; 1-Click 오디오 마스터링.
///   ▸ 화면 추적(u,v,깊이 m) → ADM 데카르트 룸 좌표 · Foley 이벤트(팬/종류) → 종류별 오브젝트(비=상공 · 바람=상층) 자동 매핑.
///   ▸ 오디오 핑거프린트(64세그먼트 × 2밴드 에너지 차분 부호 → 128비트) — 오브젝트 식별/중복 검사.
///   ▸ 패닝: 방향 코사인^4 가중 + 거리(룸 중앙 → 확산) 블렌드 · 에너지 정규화(Σg²=1) · LFE 제외.
///   ▸ 렌더: 바이노럴(오브젝트 방위 트랙 → ITD/ILD) · 5.1(System B) · 7.1.4(System J) — 다이얼로그=C · 뮤직 베드=L/R(+서라운드 앰비언스) · LFE=120Hz LPF
///     · 블록(1024) 스트리밍 기록(메모리 O(블록)) · float32 WAVE_FORMAT_EXTENSIBLE 채널 마스크.
///   ▸ ADM BWF(ITU-R BS.2076-2): 7.1.4 DirectSpeakers 베드 + 오브젝트 트랙 · axml + chna 청크 → Dolby Atmos Renderer 임포트용.
///   ▸ 마스터: 스테레오(LUFS 정규화 · 리미터) · 바이노럴 · 5.1 · 7.1.4 · ADM · 메타 JSON · 영상 먹싱 · manifest(SHA-256) · 단계 실패 [부분 구제].
/// </summary>
public sealed class DolbyAtmosSpatialAudioMapper
{
    public const int Rate = 48_000;
    public const int Block = 1024;
    public const int MaxObjects = 16;

    public static readonly SpeakerDef[] Layout51 =
    {
        new("L", "M+030", 30, 0, false), new("R", "M-030", -30, 0, false), new("C", "M+000", 0, 0, false), new("LFE", "LFE1", 0, 0, true),
        new("Ls", "M+110", 110, 0, false), new("Rs", "M-110", -110, 0, false),
    };

    /// <summary>7.1.4 — WAV 채널 마스크 비트 순서(FL FR FC LFE BL BR SL SR TFL TFR TBL TBR).</summary>
    public static readonly SpeakerDef[] Layout714 =
    {
        new("L", "M+030", 30, 0, false), new("R", "M-030", -30, 0, false), new("C", "M+000", 0, 0, false), new("LFE", "LFE1", 0, 0, true),
        new("Lrs", "M+135", 135, 0, false), new("Rrs", "M-135", -135, 0, false), new("Lss", "M+090", 90, 0, false), new("Rss", "M-090", -90, 0, false),
        new("Ltf", "U+045", 45, 45, false), new("Rtf", "U-045", -45, 45, false), new("Ltr", "U+135", 135, 45, false), new("Rtr", "U-135", -135, 45, false),
    };

    public const uint Mask51 = 0x3F;
    public const uint Mask714 = 0x2D63F;

    private readonly Func<string?> _ffmpeg;
    private readonly AiGenerativeFoleySoundscapeEngine _foley;
    private readonly AudioStemSeparationAndMixingEngine _mixer;
    private readonly List<AtmosObject> _objects = new();
    private readonly object _gate = new();
    private int _busy;
    private long _masters;

    public DolbyAtmosSpatialAudioMapper(IVideoMergeService merge, AiGenerativeFoleySoundscapeEngine foley, AudioStemSeparationAndMixingEngine mixer)
        : this(() => merge.ResolvedFfmpegPath, foley, mixer) { }

    public DolbyAtmosSpatialAudioMapper(Func<string?> ffmpeg, AiGenerativeFoleySoundscapeEngine foley, AudioStemSeparationAndMixingEngine mixer)
    {
        _ffmpeg = ffmpeg;
        _foley = foley;
        _mixer = mixer;
    }

    public event Action? OnChange;

    public AiGenerativeFoleySoundscapeEngine Foley => _foley;
    public AudioStemSeparationAndMixingEngine Mixer => _mixer;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public long Masters => Interlocked.Read(ref _masters);
    public double Progress { get; private set; }
    public MasterResult? LastResult { get; private set; }
    public string? LastAction { get; private set; }
    public IReadOnlyList<AtmosObject> Objects { get { lock (_gate) return _objects.ToArray(); } }
    public int ObjectCount { get { lock (_gate) return _objects.Count; } }

    public static SpeakerDef[] LayoutOf(SpatialAudioLayout l) => l == SpatialAudioLayout.Surround51 ? Layout51 : Layout714;

    // ══════════════════════ 좌표 · 패닝 ══════════════════════

    /// <summary>화면(u 0~1 좌→우 · v 0~1 상→하 · 깊이 m) → 룸 좌표(근거리일수록 청취자 쪽으로 · 화면 상단 = 상층).</summary>
    public static AtmosKeyframe FromScreen(double t, float u, float v, float depthMeters)
        => new(t, Math.Clamp((u - 0.5f) * 2f, -1f, 1f), Math.Clamp(0.2f + Math.Clamp(depthMeters, 0f, 6f) / 6f * 0.8f, 0.2f, 1f), Math.Clamp((0.5f - v) * 1.2f, -1f, 1f));

    public static AtmosKeyframe PositionAt(IReadOnlyList<AtmosKeyframe> path, double t)
    {
        if (path.Count == 0) return new(t, 0, 1, 0);
        if (t <= path[0].T) return path[0] with { T = t };
        if (t >= path[^1].T) return path[^1] with { T = t };
        int lo = 0, hi = path.Count - 1;
        while (hi - lo > 1) { var mid = (lo + hi) >> 1; if (path[mid].T <= t) lo = mid; else hi = mid; }
        var a = path[lo]; var b = path[hi];
        var f = (float)((t - a.T) / Math.Max(1e-9, b.T - a.T));
        return new(t, a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);
    }

    /// <summary>오브젝트 → 스피커 게인(Σg²=1 · LFE 0 · 무할당).</summary>
    public static void PanGains(float x, float y, float z, ReadOnlySpan<SpeakerDef> layout, Span<float> g)
    {
        var len = MathF.Sqrt(x * x + y * y + z * z);
        float dx = 0, dy = 0, dz = 0;
        if (len > 1e-4f) { dx = x / len; dy = y / len; dz = z / len; }
        var dirW = Math.Clamp(len, 0f, 1f);
        var cnt = 0;
        for (var i = 0; i < layout.Length; i++) if (!layout[i].Lfe) cnt++;
        var uni = cnt > 0 ? 1f / MathF.Sqrt(cnt) : 0f;
        double sumD = 0;
        for (var i = 0; i < layout.Length; i++)
        {
            if (layout[i].Lfe) { g[i] = 0; continue; }
            var az = layout[i].Az * (MathF.PI / 180f); var el = layout[i].El * (MathF.PI / 180f);
            float sx = -MathF.Sin(az) * MathF.Cos(el), sy = MathF.Cos(az) * MathF.Cos(el), sz = MathF.Sin(el);   // 방위 + = 좌
            var d = MathF.Max(0f, dx * sx + dy * sy + dz * sz);
            var w = d * d * d * d;
            g[i] = w; sumD += w * w;
        }
        var norm = sumD > 1e-12 ? (float)(1 / Math.Sqrt(sumD)) : 0f;
        double sum = 0;
        for (var i = 0; i < layout.Length; i++)
        {
            if (layout[i].Lfe) continue;
            var dir = norm > 0 ? g[i] * norm : uni;
            g[i] = dirW * dir + (1 - dirW) * uni;
            sum += g[i] * g[i];
        }
        var k = sum > 1e-12 ? (float)(1 / Math.Sqrt(sum)) : 0f;
        for (var i = 0; i < layout.Length; i++) g[i] *= k;
    }

    // ══════════════════════ 핑거프린트 · 오브젝트 ══════════════════════

    /// <summary>64세그먼트 × (저역/고역) 에너지 차분 부호 → 32자리 hex(결정적).</summary>
    public static string Fingerprint(ReadOnlySpan<float> mono, int rate)
    {
        Span<double> lo = stackalloc double[64]; Span<double> hi = stackalloc double[64];
        lo.Clear(); hi.Clear();
        if (mono.Length >= 64)
        {
            var lp = Biquad.LowPass(1000, 0.707, rate);
            for (var i = 0; i < mono.Length; i++)
            {
                var b = (int)((long)i * 64 / mono.Length);
                var l = lp.Process(mono[i]); var h = mono[i] - l;
                lo[b] += l * l; hi[b] += h * h;
            }
        }
        ulong a = 0, c = 0;
        for (var i = 0; i < 64; i++)
        {
            var j = (i + 1) & 63;
            if (lo[j] > lo[i]) a |= 1UL << i;
            if (hi[j] - lo[j] > hi[i] - lo[i]) c |= 1UL << i;
        }
        return a.ToString("x16", CultureInfo.InvariantCulture) + c.ToString("x16", CultureInfo.InvariantCulture);
    }

    public void ClearObjects() { lock (_gate) _objects.Clear(); Raise(); }

    /// <summary>화면 추적 오브젝트 추가(상한 16 · 지문 자동).</summary>
    public bool AddTrackedObject(string name, float[] mono, IReadOnlyList<(double T, float U, float V, float DepthM)> track)
    {
        var o = new AtmosObject { Name = string.IsNullOrWhiteSpace(name) ? "Object" : name.Trim(), Audio = mono };
        foreach (var p in track) o.Path.Add(FromScreen(p.T, p.U, p.V, p.DepthM));
        o.Fingerprint = Fingerprint(mono, Rate);
        lock (_gate)
        {
            if (_objects.Count >= MaxObjects) return false;
            _objects.Add(o);
        }
        Raise();
        return true;
    }

    /// <summary>Foley 계획 → 종류별 오브젝트(이벤트 팬 → X · 앰비언스 상층). 반환 = 오브젝트 수.</summary>
    public int MapFromFoley(FoleyPlan plan)
    {
        var total = (int)(plan.Duration * plan.Rate);
        var list = new List<AtmosObject>();
        for (var k = 0; k < 8; k++)
        {
            var kind = (FoleyKind)k;
            AtmosObject? o = null;
            foreach (var e in plan.Events)
            {
                if (e.Kind != kind) continue;
                o ??= new AtmosObject { Name = "Foley " + AiGenerativeFoleySoundscapeEngine.Label(kind) };
                var z = kind switch { FoleyKind.Rain => 0.9f, FoleyKind.Wind => 0.5f, FoleyKind.Footstep => -0.3f, _ => 0f };
                var y = AiGenerativeFoleySoundscapeEngine.IsAmbience(kind) ? 0f : 0.8f;
                o.Path.Add(new(e.Start, AiGenerativeFoleySoundscapeEngine.IsAmbience(kind) ? 0f : e.Pan, y, z));
            }
            if (o is null) continue;
            var audio = AiGenerativeFoleySoundscapeEngine.RenderKindMono(plan.Events, kind, total, plan.Rate);
            list.Add(new AtmosObject { Name = o.Name, Audio = audio, Path = o.Path, Fingerprint = Fingerprint(audio, plan.Rate) });
        }
        lock (_gate)
        {
            _objects.RemoveAll(static x => x.Name.StartsWith("Foley ", StringComparison.Ordinal));
            foreach (var o in list) if (_objects.Count < MaxObjects) _objects.Add(o);
        }
        LastAction = "🛰 Atmos 오브젝트 " + list.Count.ToString(CultureInfo.InvariantCulture) + "개 매핑";
        Raise();
        return list.Count;
    }

    // ══════════════════════ 렌더 ══════════════════════

    /// <summary>
    /// 채널 기반 블록 렌더(interleaved · frames × 채널). dialog/music 베드 + (includeObjects) 오브젝트 패닝. LFE = 120Hz LPF(상태는 lfe 인자로 블록 간 유지).
    /// </summary>
    public static void RenderBlock(ReadOnlySpan<SpeakerDef> layout, ReadOnlySpan<float> dialog, ReadOnlySpan<float> musicL, ReadOnlySpan<float> musicR,
        IReadOnlyList<AtmosObject> objects, bool includeObjects, int start, int frames, int rate, Span<float> dst, ref Biquad lfe)
    {
        var ch = layout.Length;
        dst.Slice(0, frames * ch).Clear();
        int iL = -1, iR = -1, iC = -1, iLfe = -1, iSl = -1, iSr = -1;
        for (var i = 0; i < ch; i++)
        {
            switch (layout[i].Label)
            {
                case "L": iL = i; break; case "R": iR = i; break; case "C": iC = i; break; case "LFE": iLfe = i; break;
                case "Ls": case "Lss": iSl = i; break; case "Rs": case "Rss": iSr = i; break;
            }
        }
        for (var f = 0; f < frames; f++)
        {
            var s = start + f;
            float d = s < dialog.Length ? dialog[s] : 0f, ml = s < musicL.Length ? musicL[s] : 0f, mr = s < musicR.Length ? musicR[s] : 0f;
            var o = f * ch;
            if (iL >= 0) dst[o + iL] += ml;
            if (iR >= 0) dst[o + iR] += mr;
            if (iC >= 0) dst[o + iC] += d;
            if (iSl >= 0) dst[o + iSl] += ml * 0.35f;
            if (iSr >= 0) dst[o + iSr] += mr * 0.35f;
            if (iLfe >= 0) dst[o + iLfe] += lfe.Process((ml + mr) * 0.5f);
        }
        if (!includeObjects) return;
        Span<float> g = stackalloc float[16];
        for (var k = 0; k < objects.Count; k++)
        {
            var obj = objects[k];
            var p = PositionAt(obj.Path, (start + frames * 0.5) / rate);
            PanGains(p.X, p.Y, p.Z, layout, g);
            var a = obj.Audio;
            for (var f = 0; f < frames; f++)
            {
                var s = start + f;
                if (s >= a.Length) break;
                var x = a[s];
                if (x == 0f) continue;
                var o = f * ch;
                for (var c = 0; c < ch; c++) dst[o + c] += x * g[c];
            }
        }
    }

    /// <summary>바이노럴 — 베드(뮤직 L/R + 다이얼로그 중앙) + 오브젝트 방위 트랙 ITD/ILD.</summary>
    public static (float[] L, float[] R) RenderBinaural(ReadOnlySpan<float> dialog, ReadOnlySpan<float> musicL, ReadOnlySpan<float> musicR, IReadOnlyList<AtmosObject> objects, int frames, int rate)
    {
        var l = new float[frames]; var r = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var d = i < dialog.Length ? dialog[i] * 0.7071f : 0f;
            l[i] = (i < musicL.Length ? musicL[i] : 0f) + d;
            r[i] = (i < musicR.Length ? musicR[i] : 0f) + d;
        }
        const double TrackRate = 50;
        foreach (var o in objects)
        {
            var n = Math.Min(frames, o.Audio.Length);
            if (n == 0) continue;
            var steps = (int)(n / (double)rate * TrackRate) + 2;
            var az = new float[steps];
            for (var k = 0; k < steps; k++)
            {
                var p = PositionAt(o.Path, k / TrackRate);
                az[k] = MathF.Atan2(p.X, MathF.Max(1e-3f, MathF.Abs(p.Y)) * (p.Y < 0 ? -1 : 1)) * (180f / MathF.PI);
            }
            var st = new float[n * 2];
            MvHevcSpatialVideoTranscoder.Binauralize(o.Audio.AsSpan(0, n), st, rate, az, TrackRate);
            for (var i = 0; i < n; i++) { l[i] += st[2 * i]; r[i] += st[2 * i + 1]; }
        }
        return (l, r);
    }

    /// <summary>채널 레이아웃 WAV 스트리밍 기록(원자 교체).</summary>
    public static void WriteLayoutWav(string path, SpatialAudioLayout layout, float[] dialog, float[] musicL, float[] musicR, IReadOnlyList<AtmosObject> objects, int frames, int rate)
    {
        var spk = LayoutOf(layout);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            SoundDsp.WriteWavHeader(fs, spk.Length, rate, frames, layout == SpatialAudioLayout.Surround51 ? Mask51 : Mask714);
            var buf = new float[Block * spk.Length];
            var lfe = Biquad.LowPass(120, 0.707, rate);
            for (var s = 0; s < frames; s += Block)
            {
                var m = Math.Min(Block, frames - s);
                RenderBlock(spk, dialog, musicL, musicR, objects, true, s, m, rate, buf, ref lfe);
                SoundDsp.WriteFloats(fs, buf.AsSpan(0, m * spk.Length));
            }
        }
        File.Move(tmp, path, true);
    }

    // ══════════════════════ ADM BWF ══════════════════════

    private static string Tc(double sec)
    {
        sec = Math.Max(0, sec);
        var h = (int)(sec / 3600); var m = (int)(sec / 60 % 60); var s = sec % 60;
        return h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":" + s.ToString("00.00000", CultureInfo.InvariantCulture);
    }

    private static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    /// <summary>ITU-R BS.2076-2 ADM XML — 7.1.4 DirectSpeakers 베드(트랙 1~12) + 오브젝트(트랙 13~) 블록 위치(데카르트).</summary>
    public static string BuildAdmXml(IReadOnlyList<AtmosObject> objects, double duration, int rate, string title)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(4096 + objects.Count * 2048);
        var bed = Layout714;
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ebuCoreMain xmlns=\"urn:ebu:metadata-schema:ebucore\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n<coreMetadata><format><audioFormatExtended version=\"ITU-R_BS.2076-2\">\n");
        sb.Append("<audioProgramme audioProgrammeID=\"APR_1001\" audioProgrammeName=\"").Append(X(title)).Append("\" start=\"").Append(Tc(0)).Append("\" end=\"").Append(Tc(duration)).Append("\">");
        sb.Append("<audioContentIDRef>ACO_1001</audioContentIDRef>");
        if (objects.Count > 0) sb.Append("<audioContentIDRef>ACO_1002</audioContentIDRef>");
        sb.Append("</audioProgramme>\n");
        sb.Append("<audioContent audioContentID=\"ACO_1001\" audioContentName=\"Bed\"><audioObjectIDRef>AO_1001</audioObjectIDRef></audioContent>\n");
        if (objects.Count > 0)
        {
            sb.Append("<audioContent audioContentID=\"ACO_1002\" audioContentName=\"Objects\">");
            for (var i = 0; i < objects.Count; i++) sb.Append("<audioObjectIDRef>AO_").Append((0x1002 + i).ToString("X4", ci)).Append("</audioObjectIDRef>");
            sb.Append("</audioContent>\n");
        }
        sb.Append("<audioObject audioObjectID=\"AO_1001\" audioObjectName=\"Bed 7.1.4\" start=\"").Append(Tc(0)).Append("\" duration=\"").Append(Tc(duration)).Append("\"><audioPackFormatIDRef>AP_00011001</audioPackFormatIDRef>");
        for (var c = 0; c < bed.Length; c++) sb.Append("<audioTrackUIDRef>ATU_").Append((c + 1).ToString("X8", ci)).Append("</audioTrackUIDRef>");
        sb.Append("</audioObject>\n");
        for (var i = 0; i < objects.Count; i++)
        {
            var id = (0x1002 + i).ToString("X4", ci); var pk = (0x1001 + i).ToString("X4", ci);
            sb.Append("<audioObject audioObjectID=\"AO_").Append(id).Append("\" audioObjectName=\"").Append(X(objects[i].Name)).Append("\" start=\"").Append(Tc(0)).Append("\" duration=\"").Append(Tc(duration))
              .Append("\"><audioPackFormatIDRef>AP_0003").Append(pk).Append("</audioPackFormatIDRef><audioTrackUIDRef>ATU_").Append((bed.Length + 1 + i).ToString("X8", ci)).Append("</audioTrackUIDRef></audioObject>\n");
        }
        // 베드 팩/채널
        sb.Append("<audioPackFormat audioPackFormatID=\"AP_00011001\" audioPackFormatName=\"Bed_7.1.4\" typeLabel=\"0001\" typeDefinition=\"DirectSpeakers\">");
        for (var c = 0; c < bed.Length; c++) sb.Append("<audioChannelFormatIDRef>AC_0001").Append((0x1001 + c).ToString("X4", ci)).Append("</audioChannelFormatIDRef>");
        sb.Append("</audioPackFormat>\n");
        for (var c = 0; c < bed.Length; c++)
        {
            var cid = (0x1001 + c).ToString("X4", ci);
            sb.Append("<audioChannelFormat audioChannelFormatID=\"AC_0001").Append(cid).Append("\" audioChannelFormatName=\"").Append(bed[c].Label).Append("\" typeLabel=\"0001\" typeDefinition=\"DirectSpeakers\">");
            sb.Append("<audioBlockFormat audioBlockFormatID=\"AB_0001").Append(cid).Append("_00000001\"><speakerLabel>").Append(bed[c].AdmLabel).Append("</speakerLabel>");
            sb.Append("<position coordinate=\"azimuth\">").Append(bed[c].Az.ToString("0.##", ci)).Append("</position><position coordinate=\"elevation\">").Append(bed[c].El.ToString("0.##", ci))
              .Append("</position><position coordinate=\"distance\">1</position></audioBlockFormat></audioChannelFormat>\n");
        }
        // 오브젝트 팩/채널(블록 = 경로 키)
        for (var i = 0; i < objects.Count; i++)
        {
            var pk = (0x1001 + i).ToString("X4", ci);
            sb.Append("<audioPackFormat audioPackFormatID=\"AP_0003").Append(pk).Append("\" audioPackFormatName=\"").Append(X(objects[i].Name)).Append("\" typeLabel=\"0003\" typeDefinition=\"Objects\"><audioChannelFormatIDRef>AC_0003")
              .Append(pk).Append("</audioChannelFormatIDRef></audioPackFormat>\n");
            sb.Append("<audioChannelFormat audioChannelFormatID=\"AC_0003").Append(pk).Append("\" audioChannelFormatName=\"").Append(X(objects[i].Name)).Append("\" typeLabel=\"0003\" typeDefinition=\"Objects\">");
            var path = objects[i].Path;
            var keys = path.Count == 0 ? 1 : path.Count;
            for (var k = 0; k < keys; k++)
            {
                var p = path.Count == 0 ? new AtmosKeyframe(0, 0, 1, 0) : path[k];
                var t0 = k == 0 ? 0 : p.T;
                var t1 = k + 1 < keys ? path[k + 1].T : duration;
                sb.Append("<audioBlockFormat audioBlockFormatID=\"AB_0003").Append(pk).Append('_').Append((k + 1).ToString("X8", ci)).Append("\" rtime=\"").Append(Tc(t0)).Append("\" duration=\"").Append(Tc(Math.Max(0, t1 - t0))).Append("\">");
                sb.Append("<cartesian>1</cartesian><position coordinate=\"X\">").Append(p.X.ToString("0.####", ci)).Append("</position><position coordinate=\"Y\">").Append(p.Y.ToString("0.####", ci))
                  .Append("</position><position coordinate=\"Z\">").Append(p.Z.ToString("0.####", ci)).Append("</position></audioBlockFormat>");
            }
            sb.Append("</audioChannelFormat>\n");
        }
        // 스트림/트랙 포맷 + 트랙 UID
        var total = bed.Length + objects.Count;
        for (var t = 0; t < total; t++)
        {
            var isBed = t < bed.Length;
            var type = isBed ? "0001" : "0003";
            var idx = (0x1001 + (isBed ? t : t - bed.Length)).ToString("X4", ci);
            sb.Append("<audioStreamFormat audioStreamFormatID=\"AS_").Append(type).Append(idx).Append("\" audioStreamFormatName=\"PCM_").Append(t + 1).Append("\" formatLabel=\"0001\" formatDefinition=\"PCM\"><audioChannelFormatIDRef>AC_")
              .Append(type).Append(idx).Append("</audioChannelFormatIDRef><audioTrackFormatIDRef>AT_").Append(type).Append(idx).Append("_01</audioTrackFormatIDRef></audioStreamFormat>\n");
            sb.Append("<audioTrackFormat audioTrackFormatID=\"AT_").Append(type).Append(idx).Append("_01\" audioTrackFormatName=\"PCM_").Append(t + 1).Append("\" formatLabel=\"0001\" formatDefinition=\"PCM\"><audioStreamFormatIDRef>AS_")
              .Append(type).Append(idx).Append("</audioStreamFormatIDRef></audioTrackFormat>\n");
            sb.Append("<audioTrackUID UID=\"ATU_").Append((t + 1).ToString("X8", ci)).Append("\" sampleRate=\"").Append(rate).Append("\" bitDepth=\"32\"><audioTrackFormatIDRef>AT_").Append(type).Append(idx)
              .Append("_01</audioTrackFormatIDRef><audioPackFormatIDRef>AP_").Append(type).Append(isBed ? "1001" : idx).Append("</audioPackFormatIDRef></audioTrackUID>\n");
        }
        sb.Append("</audioFormatExtended></format></coreMetadata>\n</ebuCoreMain>\n");
        return sb.ToString();
    }

    /// <summary>chna 청크(트랙 인덱스 · UID · 트랙 포맷 · 팩 포맷 — 40바이트 엔트리).</summary>
    public static byte[] BuildChna(int bedTracks, int objectTracks)
    {
        var total = bedTracks + objectTracks;
        var b = new byte[4 + total * 40];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)total);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)total);
        var ci = CultureInfo.InvariantCulture;
        for (var t = 0; t < total; t++)
        {
            var o = 4 + t * 40;
            var isBed = t < bedTracks;
            var type = isBed ? "0001" : "0003";
            var idx = (0x1001 + (isBed ? t : t - bedTracks)).ToString("X4", ci);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)(t + 1));
            Encoding.ASCII.GetBytes("ATU_" + (t + 1).ToString("X8", ci), b.AsSpan(o + 2, 12));
            Encoding.ASCII.GetBytes("AT_" + type + idx + "_01", b.AsSpan(o + 14, 14));
            Encoding.ASCII.GetBytes("AP_" + type + (isBed ? "1001" : idx), b.AsSpan(o + 28, 11));
        }
        return b;
    }

    /// <summary>ADM BWF(.wav) — 7.1.4 베드(오브젝트 제외) 12트랙 + 오브젝트 모노 트랙 · chna + axml.</summary>
    public static void WriteAdmBwf(string path, float[] dialog, float[] musicL, float[] musicR, IReadOnlyList<AtmosObject> objects, int frames, int rate, string title)
    {
        var bed = Layout714;
        var ch = bed.Length + objects.Count;
        var xml = Encoding.UTF8.GetBytes(BuildAdmXml(objects, frames / (double)rate, rate, title));
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            SoundDsp.WriteWavHeader(fs, ch, rate, frames, 0, new[] { ("chna", BuildChna(bed.Length, objects.Count)), ("axml", xml) });
            var bedBuf = new float[Block * bed.Length];
            var outBuf = new float[Block * ch];
            var lfe = Biquad.LowPass(120, 0.707, rate);
            for (var s = 0; s < frames; s += Block)
            {
                var m = Math.Min(Block, frames - s);
                RenderBlock(bed, dialog, musicL, musicR, objects, false, s, m, rate, bedBuf, ref lfe);
                for (var f = 0; f < m; f++)
                {
                    var o = f * ch;
                    for (var c = 0; c < bed.Length; c++) outBuf[o + c] = bedBuf[f * bed.Length + c];
                    for (var k = 0; k < objects.Count; k++) { var a = objects[k].Audio; outBuf[o + bed.Length + k] = s + f < a.Length ? a[s + f] : 0f; }
                }
                SoundDsp.WriteFloats(fs, outBuf.AsSpan(0, m * ch));
            }
        }
        File.Move(tmp, path, true);
    }

    // ══════════════════════ 1-Click 마스터링 ══════════════════════

    /// <summary>오디오 버퍼 기반 마스터(테스트/외부 주입) — 스템 분리 → 믹스 → Foley/오브젝트 → 각 포맷 → manifest.</summary>
    public async Task<MasterResult> MasterFromAudioAsync(float[] l, float[] r, FoleyPlan? foley, string folder, string name, MasterOptions opt, string? videoForMux = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder)) return Finish(new(false, null, Array.Empty<string>(), -70, -120, 0, 0, false, "출력 폴더를 지정하세요."));
        if (Interlocked.Exchange(ref _busy, 1) == 1) return new(false, null, Array.Empty<string>(), -70, -120, 0, 0, false, "다른 마스터링이 진행 중입니다.");
        var dir = Path.Combine(folder, MvHevcSpatialVideoTranscoder.SafeName(name) + "_audio_master");
        var files = new List<string>();
        var notes = new StringBuilder();
        try
        {
            Directory.CreateDirectory(dir);
            Step(0.05, "🎚 스템 분리");
            if (_mixer.Stems is not { } stems || stems.Length != Math.Min(l.Length, r.Length) || !ReferenceEquals(_mixer.SourceLeft, l))
                stems = await _mixer.LoadAudioAsync(l, r, name, ct).ConfigureAwait(false);
            Step(0.35, "🎛 믹스");
            var mix = _mixer.MixCurrent()!.Value;
            var n = stems.Length;
            if (foley is not null)
            {
                var fl = foley.Left; var fr = foley.Right;
                for (var i = 0; i < n && i < fl.Length; i++) { mix.L[i] += fl[i]; mix.R[i] += fr[i]; }
                MapFromFoley(foley);
            }
            var peak = SoundDsp.Limit(mix.L, mix.R, Rate, _mixer.Settings.CeilingDb);
            var lufs = SoundDsp.MeasureLufs(mix.L, mix.R, Rate);
            var objects = Objects;
            var stereo = Path.Combine(dir, "master_stereo.wav");
            SoundDsp.WriteStereoWav(stereo, mix.L, mix.R, Rate);
            files.Add(stereo);
            await Task.Run(() =>
            {
                if (opt.Binaural)
                {
                    Step(0.5, "🎧 바이노럴");
                    var (bl, br) = RenderBinaural(mix.Dialog, mix.MusicL, mix.MusicR, objects, n, Rate);
                    SoundDsp.Limit(bl, br, Rate, _mixer.Settings.CeilingDb);
                    var p = Path.Combine(dir, "master_binaural.wav");
                    SoundDsp.WriteStereoWav(p, bl, br, Rate);
                    files.Add(p);
                }
                if (opt.Surround51) { Step(0.6, "🔈 5.1"); var p = Path.Combine(dir, "master_5.1.wav"); WriteLayoutWav(p, SpatialAudioLayout.Surround51, mix.Dialog, mix.MusicL, mix.MusicR, objects, n, Rate); files.Add(p); }
                if (opt.Atmos714) { Step(0.7, "🔊 7.1.4"); var p = Path.Combine(dir, "master_7.1.4.wav"); WriteLayoutWav(p, SpatialAudioLayout.Surround714, mix.Dialog, mix.MusicL, mix.MusicR, objects, n, Rate); files.Add(p); }
                if (opt.AdmBwf) { Step(0.8, "🛰 ADM BWF"); var p = Path.Combine(dir, "master_atmos_adm.wav"); WriteAdmBwf(p, mix.Dialog, mix.MusicL, mix.MusicR, objects, n, Rate, name); files.Add(p); }
            }, ct).ConfigureAwait(false);

            if (opt.MuxVideo && !string.IsNullOrWhiteSpace(videoForMux))
            {
                Step(0.88, "🎬 영상 먹싱");
                var outVid = Path.Combine(dir, MvHevcSpatialVideoTranscoder.SafeName(name) + "_mastered.mp4");
                var ffmpeg = _ffmpeg();
                if (ffmpeg is not null && File.Exists(videoForMux)
                    && await AiGenerativeFoleySoundscapeEngine.RunAsync(ffmpeg, AiGenerativeFoleySoundscapeEngine.BuildInjectArgs(videoForMux, stereo, outVid, false), ct).ConfigureAwait(false) == 0 && File.Exists(outVid))
                    files.Add(outVid);
                else notes.Append("영상 먹싱 생략/실패(ffmpeg·원본 확인). ");
            }

            var meta = new Dictionary<string, object>
            {
                ["generator"] = "PromptFlow Studio", ["version"] = "16.33.0", ["sampleRate"] = Rate, ["durationSec"] = Math.Round(n / (double)Rate, 3),
                ["stemMethod"] = stems.Method, ["integratedLufs"] = Math.Round(lufs, 2), ["truePeakApproxDb"] = Math.Round(peak, 2),
                ["foleyEvents"] = foley?.Events.Count ?? 0, ["objects"] = BuildObjectMeta(objects),
            };
            var metaPath = Path.Combine(dir, "atmos_metadata.json");
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct).ConfigureAwait(false);
            files.Add(metaPath);
            var entries = new List<Dictionary<string, object>>(files.Count);
            foreach (var f in files)
            {
                await using var fs = File.OpenRead(f);
                entries.Add(new() { ["name"] = Path.GetFileName(f), ["bytes"] = fs.Length, ["sha256"] = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant() });
            }
            await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new Dictionary<string, object> { ["files"] = entries, ["notes"] = notes.ToString().Trim() },
                new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct).ConfigureAwait(false);
            Interlocked.Increment(ref _masters);
            var partial = notes.Length > 0;
            var msg = "마스터 완료 · " + lufs.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS · 피크 " + peak.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS" + (partial ? " · [부분 구제] " + notes.ToString().Trim() : "");
            LastAction = "🏁 " + msg;
            return Finish(new(true, dir, files, lufs, peak, foley?.Events.Count ?? 0, objects.Count, partial, msg));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Finish(new(false, dir, files, -70, -120, 0, 0, files.Count > 0, "취소됨" + (files.Count > 0 ? " — [부분 구제] 생성된 파일 보존" : ""))); }
        catch (Exception ex)
        {
            Program.Log("Atmos.Master", ex);
            return Finish(new(false, dir, files, -70, -120, 0, 0, files.Count > 0, "마스터 실패: " + ex.Message + (files.Count > 0 ? " — [부분 구제] 생성된 파일 보존" : "")));
        }
        finally
        {
            Progress = 1;
            Volatile.Write(ref _busy, 0);
            Raise();
        }
    }

    /// <summary>영상/오디오 파일 → 디코드 → (선택) Foley 분석 → 마스터.</summary>
    public async Task<MasterResult> MasterAsync(string sourcePath, string folder, string? name, string? sceneText, bool foley, MasterOptions opt, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || !File.Exists(sourcePath)) return Finish(new(false, null, Array.Empty<string>(), -70, -120, 0, 0, false, "ffmpeg 또는 원본 파일이 없습니다."));
        var a = await AudioStemSeparationAndMixingEngine.DecodeAudioAsync(ffmpeg, sourcePath, ct).ConfigureAwait(false);
        if (a is null) return Finish(new(false, null, Array.Empty<string>(), -70, -120, 0, 0, false, "오디오 트랙이 없습니다."));
        var plan = foley ? await _foley.GenerateAsync(sourcePath, sceneText, ct).ConfigureAwait(false) : null;
        return await MasterFromAudioAsync(a.Value.L, a.Value.R, plan, folder, name ?? Path.GetFileNameWithoutExtension(sourcePath), opt, sourcePath, ct).ConfigureAwait(false);
    }

    private static List<Dictionary<string, object>> BuildObjectMeta(IReadOnlyList<AtmosObject> objects)
    {
        var list = new List<Dictionary<string, object>>(objects.Count);
        for (var i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            var path = new List<double[]>(o.Path.Count);
            foreach (var k in o.Path) path.Add(new[] { Math.Round(k.T, 3), Math.Round(k.X, 3), Math.Round(k.Y, 3), Math.Round(k.Z, 3) });
            list.Add(new() { ["id"] = i + 1, ["name"] = o.Name, ["fingerprint"] = o.Fingerprint, ["admTrack"] = Layout714.Length + 1 + i, ["path_t_x_y_z"] = path });
        }
        return list;
    }

    private void Step(double p, string action) { Progress = p; LastAction = action; Raise(); }

    private MasterResult Finish(MasterResult r)
    {
        LastResult = r;
        if (!r.Ok) LastAction = "⚠ " + r.Message;
        Raise();
        return r;
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Atmos.OnChange", ex); }
        }
    }
}
