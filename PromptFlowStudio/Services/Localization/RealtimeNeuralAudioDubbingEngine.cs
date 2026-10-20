using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.Localization;

public enum DubLanguage : byte { Ko, En, Ja, Zh, Es, Fr, De, Ar }

/// <summary>립싱크 매칭 리포트.</summary>
/// <summary>Before/After = 더빙 비짐 입모양 대비 상관(원 영상 단순 리샘플 → 매칭·워프 후) · AvBefore/AvAfter = 더빙 오디오 포락 대비 상관.</summary>
public readonly record struct LipSyncReport(int Frames, float BeforeCorrelation, float AfterCorrelation, float AvBefore, float AvAfter, float MeanAbsOffsetMs, float MaxAbsOffsetMs,
    float IsochronyFactor, float SourceMs, float DubMs, double WarpMicrosPerFrame, float MeanAbsOpennessError);

/// <summary>
/// [PromptFlow Studio v16.69.0] 👄 실시간 신경망 오디오 더빙 &amp; 립싱크 매처 — 25fps.
/// G2V: 한글 자모 분해(초/중/종성) · 가나 모음 행 · 라틴/아랍 문자 규칙 · 한자(근사) → 비짐 음소 · 언어별 발화 속도.
/// 등시성: 더빙 길이를 원 대사 길이에 ±25% 범위 정합. 입모양 트랙(동시조음 평활) · 더빙 오디오 RMS 포락(SIMD).
/// 매칭: 밴드 제한 DTW(±8프레임)로 원 영상 프레임 재선택 → 잔차는 입 ROI 수직 리샘플(SIMD 행 보간 · Vector&lt;ushort&gt;)로 보정 →
/// 보정 오프셋 스펙트럼(ms) · 전/후 상관. 영상 프레임 그리기/워프/측정 · DTW · 포락 무할당.
/// </summary>
public sealed class RealtimeNeuralAudioDubbingEngine
{
    public const int Fps = 25, MaxFrames = 750, Band = 8, FrameW = 160, FrameH = 120;
    public const int MouthCx = 80, MouthCy = 82, MouthHalfW = 22, RoiX = 52, RoiY = 52, RoiW = 56, RoiH = 60;
    public const uint Skin = 0xFF8CB4E6, Lip = 0xFF2A1E5A; // ABGR(LE 바이트 RGBA)

    // 히라가나 0x3041..0x3096 모음 행(a/i/u/e/o · N=ん · q=っ)
    private const string KanaVowel = "aaiiuueeooaaiiuueeooaaiiuueeooaaiiquueeooaiueoaaaiiiuuueeeoooaiueoaauuooaiueoaaieoNuae";
    private const string KanaBilabial = "ばぱびぴぶぷべぺぼぽまみむめも";

    private readonly object _gate = new();
    private readonly float[] _cost = new float[MaxFrames * (2 * Band + 1)];
    private readonly byte[] _back = new byte[MaxFrames * (2 * Band + 1)];
    private readonly float[] _resampled = new float[MaxFrames];
    private readonly byte[] _frame = new byte[FrameW * FrameH * 4];
    private readonly byte[] _roiCopy = new byte[RoiW * RoiH * 4];
    private long _frames, _warps, _phonemes;
    private double _lastWarpMicros;

    public event Action? OnChange;

    public long FramesProcessed { get { lock (_gate) return _frames; } }
    public long Warps { get { lock (_gate) return _warps; } }
    public long PhonemesGenerated { get { lock (_gate) return _phonemes; } }
    public double LastWarpMicrosPerFrame { get { lock (_gate) return _lastWarpMicros; } }
    public string? LastAction { get; private set; }

    /// <summary>언어별 초당 음절 수(발화 속도).</summary>
    public static float SyllableRate(DubLanguage l) => l switch
    {
        DubLanguage.Ja or DubLanguage.Es => 7.8f, DubLanguage.Ko or DubLanguage.Fr => 7f, DubLanguage.En => 6.2f, DubLanguage.De => 6f, DubLanguage.Zh => 5.2f, _ => 6.5f,
    };

    public static string LanguageLabel(DubLanguage l) => l switch
    {
        DubLanguage.Ko => "한국어", DubLanguage.En => "English", DubLanguage.Ja => "日本語", DubLanguage.Zh => "中文", DubLanguage.Es => "Español",
        DubLanguage.Fr => "Français", DubLanguage.De => "Deutsch", _ => "العربية",
    };

    // ══════════ G2V ══════════

    /// <summary>텍스트 → 비짐 음소(무할당). 기록 수 반환.</summary>
    public int ToPhonemes(ReadOnlySpan<char> text, DubLanguage lang, Span<DubPhoneme> dst)
    {
        var scale = 7f / SyllableRate(lang);
        float vMs = 115 * scale, cMs = 55 * scale, pause = 160;
        var n = 0;
        var wordStart = true;
        for (var i = 0; i < text.Length && n < dst.Length - 3; i++)
        {
            var c = text[i];
            if (c is >= '가' and <= '힣')
            {
                var code = c - 0xAC00;
                int ini = code / 588, med = code % 588 / 28, fin = code % 28;
                if (ini != 11) dst[n++] = new DubPhoneme(ini is 6 or 7 or 8 or 17 ? Viseme.MBP : Viseme.C, cMs, ini is 2 or 5 or 6 or 9, false);  // ㅇ(11) 무음 · ㄴㄹㅁ 유성
                dst[n++] = new DubPhoneme(HangulVowel(med), vMs, true, wordStart);
                if (fin != 0) dst[n++] = new DubPhoneme(fin is 16 or 17 or 18 or 26 ? Viseme.MBP : Viseme.C, cMs * 0.8f, fin is 4 or 8 or 16 or 21, false);
                wordStart = false;
            }
            else if (c is >= 'ぁ' and <= 'ヶ')
            {
                var h = c >= 'ァ' ? (char)(c - 0x60) : c;
                var idx = h - 0x3041;
                if ((uint)idx >= (uint)KanaVowel.Length) continue;
                var v = KanaVowel[idx];
                if (v == 'q') { dst[n++] = new DubPhoneme(Viseme.C, cMs, false, false); continue; }
                if (v == 'N') { dst[n++] = new DubPhoneme(Viseme.C, vMs * 0.7f, true, false); continue; }
                var hasCons = idx > 9;
                if (hasCons) dst[n++] = new DubPhoneme(KanaBilabial.Contains(h) ? Viseme.MBP : h is 'ふ' or 'ぶ' ? Viseme.FV : Viseme.C, cMs * 0.8f, true, false);
                dst[n++] = new DubPhoneme(VowelOf(v), vMs * 0.85f, true, wordStart);
                wordStart = false;
            }
            else if (c is >= '一' and <= '鿿')
            {
                // 한자: 발음 사전 없음 — 코드포인트 해시로 결정적 성모/운모 근사
                var hsh = (uint)c * 2654435761u;
                dst[n++] = new DubPhoneme((hsh >> 7) % 7 == 0 ? Viseme.MBP : Viseme.C, cMs, (hsh & 1) == 0, false);
                dst[n++] = new DubPhoneme(VowelOf("aeiou"[(int)(hsh >> 11) % 5]), vMs * 1.1f, true, true);
            }
            else if (c is >= 'ء' and <= 'ي')
            {
                var v = c switch { 'ا' or 'آ' or 'أ' => Viseme.A, 'و' => Viseme.U, 'ي' or 'ى' => Viseme.I, 'م' or 'ب' => Viseme.MBP, 'ف' => Viseme.FV, _ => Viseme.C };
                dst[n++] = new DubPhoneme(v, v == Viseme.C || v == Viseme.MBP || v == Viseme.FV ? cMs : vMs * 0.8f, v != Viseme.FV, wordStart);
                if (v is Viseme.C or Viseme.MBP) dst[n++] = new DubPhoneme(Viseme.A, vMs * 0.5f, true, false);   // 단모음 보충(무표기 모음)
                wordStart = false;
            }
            else if (char.IsLetter(c))
            {
                var l = char.ToLowerInvariant(c);
                var v = l switch
                {
                    'a' or 'á' or 'à' or 'â' or 'ä' => Viseme.A,
                    'e' or 'é' or 'è' or 'ê' => Viseme.E,
                    'i' or 'í' or 'y' or 'î' => Viseme.I,
                    'o' or 'ó' or 'ô' or 'ö' => Viseme.O,
                    'u' or 'ú' or 'ü' or 'w' or 'û' => Viseme.U,
                    'm' or 'b' or 'p' => Viseme.MBP,
                    'f' or 'v' => Viseme.FV,
                    _ => Viseme.C,
                };
                var vowel = v is Viseme.A or Viseme.E or Viseme.I or Viseme.O or Viseme.U;
                // 연속 같은 비짐 병합(이중자 'ee', 'ss')
                if (n > 0 && dst[n - 1].Viseme == v) { dst[n - 1] = dst[n - 1] with { DurationMs = dst[n - 1].DurationMs + (vowel ? vMs * 0.4f : cMs * 0.3f) }; continue; }
                dst[n++] = new DubPhoneme(v, vowel ? vMs * 0.8f : cMs, vowel || l is 'm' or 'n' or 'l' or 'r' or 'b' or 'd' or 'g' or 'v' or 'z', vowel && wordStart);
                if (vowel) wordStart = false;
            }
            else if (c is ' ' or '　')
            {
                wordStart = true;
                if (n > 0 && dst[n - 1].Viseme != Viseme.Rest) dst[n++] = new DubPhoneme(Viseme.Rest, 30, false, false);
            }
            else if (c is ',' or '.' or '!' or '?' or '，' or '。' or '！' or '？' or '、' or '،' or '؟' or ';' or ':')
            {
                wordStart = true;
                dst[n++] = new DubPhoneme(Viseme.Rest, c is ',' or '，' or '、' or '،' ? pause : pause * 1.6f, false, false);
            }
        }
        lock (_gate) _phonemes += n;
        return n;
    }

    private static Viseme HangulVowel(int med) => med switch
    {
        0 or 1 or 2 or 3 => Viseme.A,          // ㅏㅐㅑㅒ
        4 or 5 or 6 or 7 => Viseme.E,          // ㅓㅔㅕㅖ
        8 or 9 or 10 or 11 or 12 => Viseme.O,  // ㅗㅘㅙㅚㅛ
        13 or 14 or 15 or 16 or 17 => Viseme.U, // ㅜㅝㅞㅟㅠ
        _ => Viseme.I,                         // ㅡㅢㅣ
    };

    private static Viseme VowelOf(char v) => v switch { 'a' => Viseme.A, 'i' => Viseme.I, 'u' => Viseme.U, 'e' => Viseme.E, _ => Viseme.O };

    public static float DurationMs(ReadOnlySpan<DubPhoneme> p)
    {
        float t = 0;
        for (var i = 0; i < p.Length; i++) t += p[i].DurationMs;
        return t;
    }

    /// <summary>등시성 정합 — 원 대사 길이에 맞춰 음소 길이 배율(0.75~1.33) 적용. 배율 반환.</summary>
    public static float FitIsochrony(Span<DubPhoneme> p, float targetMs)
    {
        var cur = DurationMs(p);
        if (cur <= 0) return 1;
        var k = Math.Clamp(targetMs / cur, 0.75f, 1.33f);
        for (var i = 0; i < p.Length; i++) p[i] = p[i] with { DurationMs = p[i].DurationMs * k };
        return k;
    }

    /// <summary>입모양 트랙(프레임 중심 시각 비짐 → 개방도 · 30ms 동시조음 평활) · 프레임 수 반환(무할당).</summary>
    public static int MouthTrack(ReadOnlySpan<DubPhoneme> p, Span<float> open)
    {
        var total = DurationMs(p);
        var frames = Math.Min(open.Length, (int)MathF.Ceiling(total * Fps / 1000f));
        var idx = 0; float start = 0, cur = 0.05f;
        var k = 1 - MathF.Exp(-(1000f / Fps) / 30f);
        for (var f = 0; f < frames; f++)
        {
            var t = (f + 0.5f) * 1000f / Fps;
            while (idx < p.Length - 1 && t >= start + p[idx].DurationMs) { start += p[idx].DurationMs; idx++; }
            var target = idx < p.Length ? DubPhoneme.Openness(p[idx].Viseme) : 0.05f;
            cur += (target - cur) * k;
            open[f] = cur;
        }
        return frames;
    }

    /// <summary>오디오 프레임 RMS 포락(Vector&lt;float&gt; 제곱합 · √ 압축 · 0..1 정규화) — 프레임 수(무할당).</summary>
    public static int AudioEnvelope(ReadOnlySpan<float> pcm, int sampleRate, Span<float> env)
    {
        var hop = sampleRate / Fps;
        var frames = Math.Min(env.Length, pcm.Length / hop);
        var max = 1e-9f;
        for (var f = 0; f < frames; f++)
        {
            var w = pcm.Slice(f * hop, hop);
            float s = 0; var i = 0;
            if (Vector.IsHardwareAccelerated && w.Length >= Vector<float>.Count)
            {
                var acc = Vector<float>.Zero;
                var vs = MemoryMarshal.Cast<float, Vector<float>>(w);
                for (var k = 0; k < vs.Length; k++) acc += vs[k] * vs[k];
                s = Vector.Sum(acc); i = vs.Length * Vector<float>.Count;
            }
            for (; i < w.Length; i++) s += w[i] * w[i];
            var r = MathF.Sqrt(MathF.Sqrt(s / hop));
            env[f] = r;
            if (r > max) max = r;
        }
        for (var f = 0; f < frames; f++) env[f] /= max;
        return frames;
    }

    public static float Pearson(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 2) return 0;
        double ma = 0, mb = 0;
        for (var i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;
        double sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < n; i++) { var x = a[i] - ma; var y = b[i] - mb; sab += x * y; saa += x * x; sbb += y * y; }
        return saa > 0 && sbb > 0 ? (float)(sab / Math.Sqrt(saa * sbb)) : 0;
    }

    // ══════════ 영상 프레임(입 ROI) ══════════

    /// <summary>절차적 얼굴 프레임 — 피부 배경 + 입 타원(높이 2 + 20·개방도 px).</summary>
    public static void DrawFace(Span<byte> rgba, float openness)
    {
        var px = MemoryMarshal.Cast<byte, uint>(rgba);
        px.Fill(Skin);
        var hh = (2 + 20 * Math.Clamp(openness, 0f, 1f)) / 2f;
        for (var y = (int)(MouthCy - hh - 1); y <= (int)(MouthCy + hh + 1); y++)
        {
            if ((uint)y >= FrameH) continue;
            var dy = (y + 0.5f - MouthCy) / hh;
            if (MathF.Abs(dy) > 1) continue;
            var hw = MouthHalfW * MathF.Sqrt(1 - dy * dy);
            for (var x = (int)(MouthCx - hw); x <= (int)(MouthCx + hw); x++) if ((uint)x < FrameW) px[y * FrameW + x] = Lip;
        }
    }

    /// <summary>중심 열 입 픽셀 높이 → 개방도.</summary>
    public static float MeasureOpenness(ReadOnlySpan<byte> rgba)
    {
        var px = MemoryMarshal.Cast<byte, uint>(rgba);
        var h = 0f;
        for (var y = RoiY; y < RoiY + RoiH; y++)
        {
            var p = px[y * FrameW + MouthCx];
            var r = (byte)p; const byte sk = (byte)(Skin & 0xFF), lp = (byte)(Lip & 0xFF);
            h += Math.Clamp((sk - r) / (float)(sk - lp), 0f, 1f);   // 보간 경계 반영(부분 픽셀)
        }
        return Math.Max(0, (h - 2) / 20f);
    }

    /// <summary>입 ROI 수직 리샘플(중심 고정 · 배율 <paramref name="scale"/>) — Vector&lt;ushort&gt; 행 보간 SIMD + 스칼라 꼬리. <paramref name="scratch"/> ≥ ROI 바이트.</summary>
    public static void WarpMouth(Span<byte> rgba, float scale, Span<byte> scratch, bool simd = true)
    {
        const int rowBytes = RoiW * 4;
        for (var r = 0; r < RoiH; r++) rgba.Slice(((RoiY + r) * FrameW + RoiX) * 4, rowBytes).CopyTo(scratch.Slice(r * rowBytes, rowBytes));
        var inv = 1f / Math.Clamp(scale, 0.25f, 4f);
        for (var r = 0; r < RoiH; r++)
        {
            var sy = (RoiY + r + 0.5f - MouthCy) * inv + MouthCy - 0.5f - RoiY;
            sy = Math.Clamp(sy, 0, RoiH - 1);
            var y0 = (int)sy; var y1 = Math.Min(RoiH - 1, y0 + 1);
            var w = (ushort)((sy - y0) * 256);
            var dst = rgba.Slice(((RoiY + r) * FrameW + RoiX) * 4, rowBytes);
            LerpRow(scratch.Slice(y0 * rowBytes, rowBytes), scratch.Slice(y1 * rowBytes, rowBytes), w, dst, simd);
        }
    }

    /// <summary>dst = (a·(256−w) + b·w) &gt;&gt; 8 — SIMD/스칼라 비트 동일.</summary>
    public static void LerpRow(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ushort w, Span<byte> dst, bool simd = true)
    {
        var n = Math.Min(Math.Min(a.Length, b.Length), dst.Length);
        var i = 0;
        if (simd && Vector.IsHardwareAccelerated && n >= Vector<byte>.Count)
        {
            var wb = new Vector<ushort>(w);
            var wa = new Vector<ushort>((ushort)(256 - w));
            for (; i <= n - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                var va = new Vector<byte>(a.Slice(i)); var vb = new Vector<byte>(b.Slice(i));
                Vector.Widen(va, out var al, out var ah);
                Vector.Widen(vb, out var bl, out var bh);
                var lo = Vector.ShiftRightLogical(al * wa + bl * wb, 8);
                var hi = Vector.ShiftRightLogical(ah * wa + bh * wb, 8);
                Vector.Narrow(lo, hi).CopyTo(dst.Slice(i));
            }
        }
        for (; i < n; i++) dst[i] = (byte)((a[i] * (256 - w) + b[i] * w) >> 8);
    }

    // ══════════ 매칭 ══════════

    /// <summary>
    /// 립싱크 매칭(무할당): 원 입모양 <paramref name="source"/> · 더빙 입모양 <paramref name="target"/> · 더빙 포락 <paramref name="envelope"/> →
    /// 프레임별 선택 원 프레임 · ROI 배율 · 보정 오프셋(ms) · 최종 개방도(실 워프 후 픽셀 측정).
    /// </summary>
    public LipSyncReport Match(ReadOnlySpan<float> source, ReadOnlySpan<float> target, ReadOnlySpan<float> envelope,
        Span<int> chosen, Span<float> scale, Span<float> offsetMs, Span<float> finalOpen, float isochrony = 1)
    {
        lock (_gate)
        {
            int ns = Math.Min(source.Length, MaxFrames), nt = Math.Min(Math.Min(target.Length, MaxFrames), Math.Min(chosen.Length, finalOpen.Length));
            nt = Math.Min(nt, Math.Min(scale.Length, offsetMs.Length));
            if (ns == 0 || nt == 0) return default;
            // 전: 원 입모양을 더빙 길이로 선형 리샘플한 상관
            for (var j = 0; j < nt; j++)
            {
                var x = nt > 1 ? j * (ns - 1f) / (nt - 1) : 0;
                var i0 = (int)x; var i1 = Math.Min(ns - 1, i0 + 1);
                _resampled[j] = source[i0] + (source[i1] - source[i0]) * (x - i0);
            }
            var before = Pearson(_resampled.AsSpan(0, nt), target[..nt]);
            var avBefore = Pearson(_resampled.AsSpan(0, nt), envelope[..Math.Min(nt, envelope.Length)]);

            // 밴드 DTW(대각 중심 ±Band) — 비용 |원 − 목표|
            const int bw = 2 * Band + 1;
            for (var j = 0; j < nt; j++)
            {
                var c = j * (ns - 1f) / Math.Max(1, nt - 1);
                for (var k = 0; k < bw; k++)
                {
                    var i = (int)MathF.Round(c) + k - Band;
                    var idx = j * bw + k;
                    if (i < 0 || i >= ns) { _cost[idx] = float.PositiveInfinity; continue; }
                    var local = MathF.Abs(source[i] - target[j]) + 0.02f * MathF.Abs(i - c) / Band;
                    if (j == 0) { _cost[idx] = local; _back[idx] = 255; continue; }
                    // 이전 행에서 i(정지) 또는 i−1(진행) 또는 i−2(건너뜀)
                    var pc = (j - 1) * (ns - 1f) / Math.Max(1, nt - 1);
                    var best = float.PositiveInfinity; byte arg = 255;
                    for (var d = 0; d <= 2; d++)
                    {
                        var pk = i - d - (int)MathF.Round(pc) + Band;
                        if ((uint)pk >= bw) continue;
                        var v = _cost[(j - 1) * bw + pk] + (d == 0 ? 0.05f : d == 2 ? 0.03f : 0);
                        if (v < best) { best = v; arg = (byte)pk; }
                    }
                    _cost[idx] = best + local; _back[idx] = arg;
                }
            }
            // 역추적
            var last = 0; var lc = float.PositiveInfinity;
            for (var k = 0; k < bw; k++) if (_cost[(nt - 1) * bw + k] < lc) { lc = _cost[(nt - 1) * bw + k]; last = k; }
            for (var j = nt - 1; j >= 0; j--)
            {
                var c = j * (ns - 1f) / Math.Max(1, nt - 1);
                var i = (int)MathF.Round(c) + last - Band;
                chosen[j] = Math.Clamp(i, 0, ns - 1);
                var b = _back[j * bw + last];
                last = b == 255 ? Band : b;
            }
            // 프레임별: 선택 원 프레임 그리기 → ROI 배율 워프 → 측정
            double warpTicks = 0; float sumOff = 0, maxOff = 0, sumErr = 0;
            for (var j = 0; j < nt; j++)
            {
                var src = source[chosen[j]];
                var sc = Math.Clamp((2 + 20 * target[j]) / (2 + 20 * src), 0.4f, 2.5f);
                scale[j] = sc;
                offsetMs[j] = (chosen[j] - j * (ns / (float)nt)) * 1000f / Fps;
                DrawFace(_frame, src);
                var t0 = Stopwatch.GetTimestamp();
                WarpMouth(_frame, sc, _roiCopy);
                warpTicks += Stopwatch.GetTimestamp() - t0;
                finalOpen[j] = MeasureOpenness(_frame);
                var ao = MathF.Abs(offsetMs[j]);
                sumOff += ao; if (ao > maxOff) maxOff = ao;
                sumErr += MathF.Abs(finalOpen[j] - target[j]);
            }
            var after = Pearson(finalOpen[..nt], target[..nt]);
            var avAfter = Pearson(finalOpen[..nt], envelope[..Math.Min(nt, envelope.Length)]);
            _frames += nt; _warps += nt;
            _lastWarpMicros = warpTicks * 1e6 / Stopwatch.Frequency / nt;
            var rep = new LipSyncReport(nt, before, after, avBefore, avAfter, sumOff / nt, maxOff, isochrony, ns * 1000f / Fps, nt * 1000f / Fps,
                warpTicks * 1e6 / Stopwatch.Frequency / nt, sumErr / nt);
            return rep;
        }
    }

    /// <summary>현재 작업 프레임(미리보기) 복사.</summary>
    public void CopyFrame(Span<byte> dst) { lock (_gate) _frame.AsSpan(0, Math.Min(dst.Length, _frame.Length)).CopyTo(dst); }

    internal void Announce(string msg)
    {
        LastAction = msg;
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Localization.Dub.OnChange", ex); }
    }
}
