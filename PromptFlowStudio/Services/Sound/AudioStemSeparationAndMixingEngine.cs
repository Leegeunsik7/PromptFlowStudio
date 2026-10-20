using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;   // IVideoMergeService

namespace PromptFlowStudio.Services.Sound;

public enum StemKind : byte { Vocals, Bgm, Drums, Sfx }

/// <summary>4-스템 세트(스템별 좌/우 · 길이 동일).</summary>
public sealed record StemSet(int Rate, int Length, float[][] Left, float[][] Right, string Method);

/// <summary>스템 채널 스트립 — 게인(dB) · 뮤트/솔로 · 3밴드 EQ(200Hz 셸프 · 1kHz 피킹 · 5kHz 셸프).</summary>
public readonly record struct StemChannel(float GainDb, bool Mute, bool Solo, float LowDb, float MidDb, float HighDb)
{
    public static readonly StemChannel Unity = new(0, false, false, 0, 0, 0);
    public bool HasEq => LowDb != 0 || MidDb != 0 || HighDb != 0;
}

public sealed record MixSettings(StemChannel[] Channels, bool Ducking, float DuckDepthDb, float DuckThresholdDb, float DuckAttackMs, float DuckReleaseMs, float TargetLufs, float CeilingDb)
{
    public static MixSettings Default => new(new[] { StemChannel.Unity, StemChannel.Unity, StemChannel.Unity, StemChannel.Unity }, true, 9, -40, 15, 250, -14, -1);

    public MixSettings With(StemKind k, StemChannel c)
    {
        var a = (StemChannel[])Channels.Clone();
        a[(int)k] = c;
        return this with { Channels = a };
    }
}

public sealed record MixReport(double PreLufs, double IntegratedLufs, float PeakDb, float AppliedGainDb, double DuckedSeconds);

/// <summary>
/// [PromptFlow Studio v16.33.0] AI 오디오 스템 분리 &amp; 다이내믹 믹서(순수 BCL STFT · Demucs 선택 결선).
///   ▸ BCL 분리: sqrt-Hann STFT(2048/1024 · COLA) → HPSS 메디안(시간/주파수 13탭) Wiener 마스크
///     · 보컬 = 하모닉 × 스테레오 중앙성² × 보컬 대역(180~5kHz) · 드럼 = 퍼커시브 × 저·중역 · SFX = 퍼커시브 잔여 · BGM = 입력 − 나머지(완전 재구성).
///   ▸ Demucs(htdemucs · PFS_DEMUCS 또는 PATH) 존재 시 우선 → 실패/부재 시 즉시 BCL 폴백.
///   ▸ 믹서: 스템별 게인/뮤트/솔로/3밴드 EQ · 보컬 사이드체인 BGM 오토 덕킹(어택/릴리즈) · BS.1770 라우드니스 정규화 · 피크 리미터.
///   ▸ 프레임/샘플 루프는 대여 버퍼 + 값형 바이쿼드(LINQ/박싱 0).
/// </summary>
public sealed class AudioStemSeparationAndMixingEngine
{
    public const int Rate = 48_000;
    public const int FftSize = 2048;
    public const int Hop = 1024;
    public const int MedianTaps = 13;
    public const double MaxSeconds = 300;

    private readonly Func<string?> _ffmpeg;
    private readonly Func<string?> _demucs;
    private int _busy;

    public AudioStemSeparationAndMixingEngine(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath, () => s_demucs.Value) { }

    public AudioStemSeparationAndMixingEngine(Func<string?> ffmpeg, Func<string?>? demucs)
    {
        _ffmpeg = ffmpeg;
        _demucs = demucs ?? (() => null);
    }

    private static readonly Lazy<string?> s_demucs = new(ResolveDemucs);

    public event Action? OnChange;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public bool DemucsAvailable => _demucs() is not null;
    public MixSettings Settings { get; set; } = MixSettings.Default;
    public StemSet? Stems { get; private set; }
    public string? SourcePath { get; private set; }
    public float[]? SourceLeft { get; private set; }
    public float[]? SourceRight { get; private set; }
    public MixReport? LastReport { get; private set; }
    public string? LastAction { get; private set; }

    public static string Label(StemKind k) => k switch { StemKind.Vocals => "보컬", StemKind.Bgm => "BGM", StemKind.Drums => "드럼", _ => "SFX" };

    // ══════════════════════ BCL 분리 ══════════════════════

    public static StemSet SeparateBcl(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int rate)
    {
        var len = Math.Min(left.Length, right.Length);
        var stemsL = new float[4][]; var stemsR = new float[4][];
        for (var s = 0; s < 4; s++) { stemsL[s] = new float[len]; stemsR[s] = new float[len]; }
        if (len == 0) return new(rate, 0, stemsL, stemsR, "BCL-HPSS");
        const int N = FftSize, H = Hop, Bins = N / 2 + 1;
        var mono = true;
        for (var i = 0; i < len && mono; i++) if (left[i] != right[i]) mono = false;
        var padded = len + 2 * N;
        var frames = (padded - N) / H + 1;
        var plan = new FftPlan(N);
        var win = new float[N];
        for (var i = 0; i < N; i++) win[i] = MathF.Sqrt(0.5f * (1 - MathF.Cos(2 * MathF.PI * i / N)));
        var mag = new float[(long)frames * Bins];
        var re = new float[N]; var im = new float[N];
        var rl = new float[N]; var il = new float[N]; var rr = new float[N]; var ir = new float[N];
        var tr = new float[N]; var ti = new float[N];

        // 1패스 — 미드 크기 스펙트로그램
        for (var f = 0; f < frames; f++)
        {
            Frame(left, right, len, f * H - N, win, re, mid: true, right2: null);
            Array.Clear(im);
            plan.Transform(re, im, false);
            var row = (long)f * Bins;
            for (var k = 0; k < Bins; k++) mag[row + k] = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
        }

        // 2패스 — 마스크 → 보컬/드럼/SFX 역변환 OLA
        Span<float> med = stackalloc float[MedianTaps];
        var halfTap = MedianTaps / 2;
        var hm = new float[Bins]; var vm = new float[Bins]; var dm = new float[Bins]; var sm = new float[Bins];
        var binHz = (float)rate / N;
        for (var f = 0; f < frames; f++)
        {
            var row = (long)f * Bins;
            for (var k = 0; k < Bins; k++)
            {
                for (var d = -halfTap; d <= halfTap; d++) med[d + halfTap] = mag[(long)Math.Clamp(f + d, 0, frames - 1) * Bins + k];
                var h = Median(med);
                for (var d = -halfTap; d <= halfTap; d++) med[d + halfTap] = mag[row + Math.Clamp(k + d, 0, Bins - 1)];
                var p = Median(med);
                var hh = h * h; var pp = p * p;
                hm[k] = hh + pp > 1e-20f ? hh / (hh + pp) : 0.5f;
            }
            Frame(left, right, len, f * H - N, win, rl, mid: false, right2: rr);
            Array.Clear(il); Array.Clear(ir);
            plan.Transform(rl, il, false);
            plan.Transform(rr, ir, false);
            for (var k = 0; k < Bins; k++)
            {
                var fk = k * binHz;
                float c = 1f;
                if (!mono)
                {
                    var al = MathF.Sqrt(rl[k] * rl[k] + il[k] * il[k]); var ar = MathF.Sqrt(rr[k] * rr[k] + ir[k] * ir[k]);
                    var dr = rl[k] - rr[k]; var di = il[k] - ir[k];
                    c = Math.Clamp(1f - MathF.Sqrt(dr * dr + di * di) / (al + ar + 1e-9f), 0f, 1f);
                }
                var vb = SoundDsp.Smoothstep(120, 180, fk) * (1 - SoundDsp.Smoothstep(5000, 7000, fk));
                var pm = 1f - hm[k];
                vm[k] = hm[k] * c * c * vb * (mono ? 0.8f : 1f);
                dm[k] = pm * (1 - SoundDsp.Smoothstep(5000, 9000, fk));
                sm[k] = pm - dm[k];
            }
            var start = f * H - N;
            ApplyAndAdd(plan, rl, il, vm, win, tr, ti, stemsL[(int)StemKind.Vocals], start);
            ApplyAndAdd(plan, rr, ir, vm, win, tr, ti, stemsR[(int)StemKind.Vocals], start);
            ApplyAndAdd(plan, rl, il, dm, win, tr, ti, stemsL[(int)StemKind.Drums], start);
            ApplyAndAdd(plan, rr, ir, dm, win, tr, ti, stemsR[(int)StemKind.Drums], start);
            ApplyAndAdd(plan, rl, il, sm, win, tr, ti, stemsL[(int)StemKind.Sfx], start);
            ApplyAndAdd(plan, rr, ir, sm, win, tr, ti, stemsR[(int)StemKind.Sfx], start);
        }
        // BGM = 입력 − (보컬 + 드럼 + SFX) → 스템 합 = 입력(정확)
        var bl = stemsL[(int)StemKind.Bgm]; var br = stemsR[(int)StemKind.Bgm];
        for (var i = 0; i < len; i++)
        {
            bl[i] = left[i] - stemsL[0][i] - stemsL[2][i] - stemsL[3][i];
            br[i] = right[i] - stemsR[0][i] - stemsR[2][i] - stemsR[3][i];
        }
        return new(rate, len, stemsL, stemsR, "BCL-HPSS");
    }

    private static void Frame(ReadOnlySpan<float> l, ReadOnlySpan<float> r, int len, int start, float[] win, float[] dst, bool mid, float[]? right2)
    {
        for (var i = 0; i < win.Length; i++)
        {
            var s = start + i;
            float a = 0, b = 0;
            if ((uint)s < (uint)len) { a = l[s]; b = r[s]; }
            if (mid) dst[i] = (a + b) * 0.5f * win[i];
            else { dst[i] = a * win[i]; right2![i] = b * win[i]; }
        }
    }

    private static void ApplyAndAdd(FftPlan plan, float[] re, float[] im, float[] mask, float[] win, float[] tr, float[] ti, float[] outBuf, int start)
    {
        var n = plan.N; var bins = n / 2 + 1;
        for (var k = 0; k < bins; k++) { tr[k] = re[k] * mask[k]; ti[k] = im[k] * mask[k]; }
        for (var k = bins; k < n; k++) { tr[k] = tr[n - k]; ti[k] = -ti[n - k]; }   // 에르미트 대칭
        plan.Transform(tr, ti, true);
        for (var i = 0; i < n; i++)
        {
            var s = start + i;
            if ((uint)s < (uint)outBuf.Length) outBuf[s] += tr[i] * win[i];
        }
    }

    private static float Median(Span<float> v)
    {
        for (var i = 1; i < v.Length; i++)
        {
            var x = v[i]; var j = i - 1;
            while (j >= 0 && v[j] > x) { v[j + 1] = v[j]; j--; }
            v[j + 1] = x;
        }
        return v[v.Length / 2];
    }

    // ══════════════════════ 믹서 ══════════════════════

    /// <summary>
    /// 스템 → 스테레오 마스터(덕킹 · EQ · 라우드니스 · 리미터). 선택 버스 출력: dialog(보컬 모노) · musicL/R(BGM+드럼+SFX) — 마스터 게인 반영, 리미터 전.
    /// </summary>
    public static MixReport Mix(StemSet stems, MixSettings s, Span<float> outL, Span<float> outR, Span<float> dialog = default, Span<float> musicL = default, Span<float> musicR = default)
    {
        var n = Math.Min(stems.Length, Math.Min(outL.Length, outR.Length));
        outL.Slice(0, n).Clear(); outR.Slice(0, n).Clear();
        var bus = dialog.Length >= n && musicL.Length >= n && musicR.Length >= n;
        if (bus) { dialog.Slice(0, n).Clear(); musicL.Slice(0, n).Clear(); musicR.Slice(0, n).Clear(); }
        var anySolo = false;
        foreach (var c in s.Channels) anySolo |= c.Solo;
        var rate = stems.Rate;
        var vl = ArrayPool<float>.Shared.Rent(Math.Max(1, n)); var vr = ArrayPool<float>.Shared.Rent(Math.Max(1, n));
        double ducked = 0;
        try
        {
            var vOn = Process(stems, StemKind.Vocals, s.Channels[0], anySolo, rate, n, vl, vr);
            if (!vOn) { Array.Clear(vl, 0, n); Array.Clear(vr, 0, n); }
            for (var i = 0; i < n; i++)
            {
                outL[i] = vl[i]; outR[i] = vr[i];
                if (bus) dialog[i] = (vl[i] + vr[i]) * 0.5f;
            }
            var tl = ArrayPool<float>.Shared.Rent(Math.Max(1, n)); var tq = ArrayPool<float>.Shared.Rent(Math.Max(1, n));
            try
            {
                for (var k = 1; k < 4; k++)
                {
                    var kind = (StemKind)k;
                    if (!Process(stems, kind, s.Channels[k], anySolo, rate, n, tl, tq)) continue;
                    if (kind == StemKind.Bgm && s.Ducking && vOn) ducked = Duck(vl, vr, tl, tq, n, rate, s);
                    for (var i = 0; i < n; i++)
                    {
                        outL[i] += tl[i]; outR[i] += tq[i];
                        if (bus) { musicL[i] += tl[i]; musicR[i] += tq[i]; }
                    }
                }
            }
            finally { ArrayPool<float>.Shared.Return(tl); ArrayPool<float>.Shared.Return(tq); }
        }
        finally { ArrayPool<float>.Shared.Return(vl); ArrayPool<float>.Shared.Return(vr); }

        var pre = SoundDsp.MeasureLufs(outL.Slice(0, n), outR.Slice(0, n), rate);
        var gainDb = pre <= -69 ? 0f : (float)Math.Clamp(s.TargetLufs - pre, -24, 24);
        var g = SoundDsp.DbToGain(gainDb);
        for (var i = 0; i < n; i++) { outL[i] *= g; outR[i] *= g; }
        if (bus) for (var i = 0; i < n; i++) { dialog[i] *= g; musicL[i] *= g; musicR[i] *= g; }
        var peak = SoundDsp.Limit(outL.Slice(0, n), outR.Slice(0, n), rate, s.CeilingDb);
        var post = SoundDsp.MeasureLufs(outL.Slice(0, n), outR.Slice(0, n), rate);
        return new(pre, post, peak, gainDb, ducked);
    }

    private static bool Process(StemSet stems, StemKind kind, in StemChannel c, bool anySolo, int rate, int n, float[] dl, float[] dr)
    {
        if (c.Mute || (anySolo && !c.Solo)) return false;
        var g = SoundDsp.DbToGain(c.GainDb);
        var sl = stems.Left[(int)kind]; var sr = stems.Right[(int)kind];
        if (!c.HasEq)
        {
            for (var i = 0; i < n; i++) { dl[i] = sl[i] * g; dr[i] = sr[i] * g; }
            return true;
        }
        var l1 = Biquad.LowShelf(200, 0.707, c.LowDb, rate); var l2 = Biquad.Peaking(1000, 0.9, c.MidDb, rate); var l3 = Biquad.HighShelf(5000, 0.707, c.HighDb, rate);
        var r1 = l1; var r2 = l2; var r3 = l3;
        for (var i = 0; i < n; i++)
        {
            dl[i] = l3.Process(l2.Process(l1.Process(sl[i]))) * g;
            dr[i] = r3.Process(r2.Process(r1.Process(sr[i]))) * g;
        }
        return true;
    }

    /// <summary>보컬 엔벨로프(어택/릴리즈) 임계 초과 시 BGM 감쇄(게인 스무딩) → 덕킹 초.</summary>
    private static double Duck(float[] vl, float[] vr, float[] bl, float[] br, int n, int rate, MixSettings s)
    {
        var att = MathF.Exp(-1f / (MathF.Max(1, s.DuckAttackMs) * 0.001f * rate));
        var rel = MathF.Exp(-1f / (MathF.Max(1, s.DuckReleaseMs) * 0.001f * rate));
        var thr = SoundDsp.DbToGain(s.DuckThresholdDb);
        var depth = Math.Clamp(s.DuckDepthDb, 0, 40);
        float env = 0, red = 0;
        long cnt = 0;
        for (var i = 0; i < n; i++)
        {
            var a = (MathF.Abs(vl[i]) + MathF.Abs(vr[i])) * 0.5f;
            env = a > env ? att * env + (1 - att) * a : rel * env + (1 - rel) * a;
            var target = env > thr ? depth : 0f;
            red = target > red ? att * red + (1 - att) * target : rel * red + (1 - rel) * target;
            var g = SoundDsp.DbToGain(-red);
            bl[i] *= g; br[i] *= g;
            if (red > depth * 0.5f) cnt++;
        }
        return (double)cnt / rate;
    }

    // ══════════════════════ 디코드 · Demucs · 인스턴스 흐름 ══════════════════════

    /// <summary>ffmpeg → 48kHz 스테레오 float(최대 300초 · 오디오 없음 null).</summary>
    public static async Task<(float[] L, float[] R)?> DecodeAudioAsync(string ffmpeg, string path, CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-v", "error", "-t", MaxSeconds.ToString(ci), "-i", path, "-vn", "-ac", "2", "-ar", Rate.ToString(ci), "-f", "f32le", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        if (!p.Start()) return null;
        _ = p.StandardError.ReadToEndAsync(ct);
        using var ms = new MemoryStream();
        try { await p.StandardOutput.BaseStream.CopyToAsync(ms, ct).ConfigureAwait(false); }
        finally { try { if (!p.HasExited) p.Kill(true); } catch { } }
        var frames = (int)(ms.Length / 8);
        if (frames < Rate / 10) return null;
        return Deinterleave(ms.GetBuffer(), frames);
    }

    private static (float[] L, float[] R) Deinterleave(byte[] buf, int frames)
    {
        var f = MemoryMarshal.Cast<byte, float>(buf.AsSpan(0, frames * 8));
        var l = new float[frames]; var r = new float[frames];
        for (var i = 0; i < frames; i++) { l[i] = f[2 * i]; r[i] = f[2 * i + 1]; }
        return (l, r);
    }

    public static string? ResolveDemucs()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("PFS_DEMUCS");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                foreach (var name in new[] { "demucs.exe", "demucs" })
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                }
        }
        catch (Exception ex) { Program.Log("Stem.ResolveDemucs", ex); }
        return null;
    }

    /// <summary>Demucs htdemucs(4스템: vocals/drums/bass/other) → BGM=bass+other · SFX=잔여. 실패 null.</summary>
    public static async Task<StemSet?> SeparateDemucsAsync(string demucs, float[] l, float[] r, int rate, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pfs_demucs_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var input = Path.Combine(dir, "mix.wav");
            SoundDsp.WriteStereoWav(input, l, r, rate);
            var code = await AiGenerativeFoleySoundscapeEngine.RunAsync(demucs, new() { "-n", "htdemucs", "--float32", "-o", dir, input }, ct).ConfigureAwait(false);
            if (code != 0) return null;
            var od = Path.Combine(dir, "htdemucs", "mix");
            var sl = new float[4][]; var sr = new float[4][];
            string[] names = { "vocals", "drums", "bass", "other" };
            var parts = new (float[] L, float[] R)[4];
            for (var i = 0; i < 4; i++)
            {
                if (!SoundDsp.TryReadWav(await File.ReadAllBytesAsync(Path.Combine(od, names[i] + ".wav"), ct).ConfigureAwait(false), out var pl, out var pr, out var pRate, out _)) return null;
                parts[i] = pRate == rate ? (pl, pr) : (SoundDsp.Resample(pl, pRate, rate), SoundDsp.Resample(pr, pRate, rate));
            }
            var n = l.Length;
            for (var s = 0; s < 4; s++) { sl[s] = new float[n]; sr[s] = new float[n]; }
            for (var i = 0; i < n; i++)
            {
                float G(int p, bool left) { var a = left ? parts[p].L : parts[p].R; return i < a.Length ? a[i] : 0f; }
                sl[0][i] = G(0, true); sr[0][i] = G(0, false);
                sl[2][i] = G(1, true); sr[2][i] = G(1, false);
                sl[1][i] = G(2, true) + G(3, true); sr[1][i] = G(2, false) + G(3, false);
                sl[3][i] = l[i] - sl[0][i] - sl[1][i] - sl[2][i]; sr[3][i] = r[i] - sr[0][i] - sr[1][i] - sr[2][i];
            }
            return new(rate, n, sl, sr, "Demucs-htdemucs");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Program.Log("Stem.Demucs", ex); return null; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>오디오 직접 주입 → 분리(Demucs 우선 · BCL 폴백).</summary>
    public async Task<StemSet> LoadAudioAsync(float[] l, float[] r, string? sourceTag, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _busy, 1);
        LastAction = "🎚 스템 분리 중";
        Raise();
        try
        {
            StemSet? set = null;
            if (_demucs() is { } dm) set = await SeparateDemucsAsync(dm, l, r, Rate, ct).ConfigureAwait(false);
            set ??= await Task.Run(() => SeparateBcl(l, r, Rate), ct).ConfigureAwait(false);
            Stems = set; SourceLeft = l; SourceRight = r; SourcePath = sourceTag;
            LastAction = "🎚 스템 분리 완료 · " + set.Method + " · " + (set.Length / (double)Rate).ToString("0.0", CultureInfo.InvariantCulture) + "초";
            return set;
        }
        finally { Volatile.Write(ref _busy, 0); Raise(); }
    }

    /// <summary>미디어 파일 → 디코드 → 분리. ffmpeg/오디오 부재 null.</summary>
    public async Task<StemSet?> LoadAsync(string path, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || !File.Exists(path)) { LastAction = "⚠ 오디오 로드 불가(ffmpeg/파일 확인)"; Raise(); return null; }
        try
        {
            var a = await DecodeAudioAsync(ffmpeg, path, ct).ConfigureAwait(false);
            if (a is null) { LastAction = "⚠ 오디오 트랙이 없습니다."; Raise(); return null; }
            return await LoadAudioAsync(a.Value.L, a.Value.R, path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { Program.Log("Stem.Load", ex); LastAction = "⚠ 로드 실패: " + ex.Message; Raise(); return null; }
    }

    /// <summary>현재 설정으로 믹스(스템 없으면 null).</summary>
    public (float[] L, float[] R, float[] Dialog, float[] MusicL, float[] MusicR, MixReport Report)? MixCurrent()
    {
        if (Stems is not { } st) return null;
        var n = st.Length;
        float[] l = new float[n], r = new float[n], d = new float[n], ml = new float[n], mr = new float[n];
        var rep = Mix(st, Settings, l, r, d, ml, mr);
        LastReport = rep;
        LastAction = "🎛 믹스 · " + rep.IntegratedLufs.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS · 피크 " + rep.PeakDb.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS";
        Raise();
        return (l, r, d, ml, mr, rep);
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Stem.OnChange", ex); }
        }
    }
}
