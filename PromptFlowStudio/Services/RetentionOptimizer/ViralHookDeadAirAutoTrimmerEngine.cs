using PromptFlowStudio.Services.Autopilot;
using PromptFlowStudio.Services.VerticalTranscode;
using PromptFlowStudio.Services.VisualPolish;

namespace PromptFlowStudio.Services.RetentionOptimizer;

public enum HookChunkKind : byte { Keep, DeadAir, Stimulus }

/// <summary>v17.75.0 편집 결정 1 구간(원본 ms → 출력 순서대로 이어 붙임 · 값 형식).</summary>
public struct HookSegment
{
    public int SrcStartMs, DurMs;
    public readonly int SrcEndMs => SrcStartMs + DurMs;
}

/// <summary>v17.75.0 훅 트리밍 계획 결과.</summary>
public readonly record struct HookTrimResult(bool Ok, int ScannedChunks, int DeadChunks, int TrimmedMs, int StimulusSrcMs, int StimulusOutMs, bool ColdOpen, int Segments, int SourceMs, int OutDurationMs, double PlanUs);

/// <summary>
/// [PromptFlow Studio v17.75.0] ✂️ 바이럴 훅 오디오/모션 데드에어 오토-트리머 엔진(스캔 · 계획 · 오디오 스플라이스 무할당).
///   ▸ 스캔: 클립 PCM(16-bit · 임의 채널) 100 ms 청크 RMS/피크 dBFS · 모션 에너지(v17.58 52 블렌드쉐이프 프레임 델타 L1 + 제스처 속도 · 또는 디코드 루마 SAD) 청크 평균 0~1.
///   ▸ 판정(첫 3 s 훅 창): 데드에어 = RMS ≤ −30 dBFS 이고 모션 &lt; 정체 임계 — 발화 전 선행 묵음은 길이 무관 절삭 · 중간 묵음은 200 ms 이상 연속만(음절 사이 호흡 보존).
///     자극 = 피크 ≥ −12 dBFS 이고 모션 ≥ 0.35(오디오 피크 + 큰 제스처 동시).
///   ▸ 재배열: 데드에어 제거 후에도 첫 자극이 출력 0.5 s 밖이면 자극 버스트(≤ 0.5 s)를 맨 앞으로 끌어올리는 콜드 오픈 → 나머지는 원순서.
///   ▸ 스플라이싱: 편집 결정 목록(EDL) = 청크 경계(30 fps 에서 정확히 3 프레임) — 영상은 <see cref="TrimmedClipFrameSource"/> 가 출력 프레임 → 원본 프레임 직접 매핑(전환 프레임 0 = 0 프레임 컷) ·
///     오디오는 접합부 3 ms 마이크로 페이드(클릭 제거)로 사전 할당 버퍼에 복사.
/// </summary>
public sealed class ViralHookDeadAirAutoTrimmerEngine
{
    public const int ChunkMs = 100, HookMs = 3000, HookChunks = HookMs / ChunkMs, MaxChunks = 600, MaxSegments = 64, StimulusDeadlineMs = 500, ColdOpenChunks = 5, MinMidDeadRun = 2;

    private readonly float[] _rmsDb = new float[MaxChunks], _peakDb = new float[MaxChunks], _motion = new float[MaxChunks];
    private readonly float[] _motionAcc = new float[MaxChunks];
    private readonly int[] _motionN = new int[MaxChunks];
    private readonly HookChunkKind[] _kind = new HookChunkKind[MaxChunks];
    private readonly short[] _order = new short[MaxChunks];
    private readonly HookSegment[] _seg = new HookSegment[MaxSegments];
    private int _chunks, _segN, _clipMs;
    private bool _hasMotion;

    public float SilenceDbfs { get; set; } = -30f;
    public float MotionStill { get; set; } = 0.08f;
    public float StimulusPeakDbfs { get; set; } = -12f;
    public float StimulusMotion { get; set; } = 0.35f;
    /// <summary>블렌드쉐이프 프레임 델타 평균 → 0~1 정규화 이득.</summary>
    public float FaceGain { get; set; } = 10f;
    public int Chunks => _chunks;
    public int ClipMs => _clipMs;
    public int SegmentCount => _segN;
    public ref readonly HookSegment Segment(int i) => ref _seg[i];
    public float RmsDb(int chunk) => _rmsDb[chunk];
    public float PeakDb(int chunk) => _peakDb[chunk];
    public float Motion(int chunk) => _motion[chunk];
    public HookChunkKind Kind(int chunk) => _kind[chunk];
    public HookTrimResult Last { get; private set; }
    public long Plans { get; private set; }
    public long TotalTrimmedMs { get; private set; }
    public long ColdOpens { get; private set; }

    public static float ToDbfs(double linear) => linear <= 1e-9 ? -120f : (float)Math.Max(-120, 20 * Math.Log10(linear));

    // ══════════════════════ 스캔(무할당) ══════════════════════
    /// <summary>PCM 인터리브 16-bit → 청크 RMS/피크 dBFS(풀스케일 32768) — 모션 누산 초기화.</summary>
    public void ScanAudio(ReadOnlySpan<short> pcm, int sampleRate, int channels = 1)
    {
        channels = Math.Max(1, channels);
        var frames = pcm.Length / channels;
        _clipMs = (int)(frames * 1000L / Math.Max(1, sampleRate));
        _chunks = Math.Min(MaxChunks, (_clipMs + ChunkMs - 1) / ChunkMs);
        var per = sampleRate * ChunkMs / 1000;
        for (var c = 0; c < _chunks; c++)
        {
            var a = c * per * channels; var b = Math.Min(pcm.Length, a + per * channels);
            double sum = 0; var peak = 0;
            for (var i = a; i < b; i++) { int s = pcm[i]; sum += (double)s * s; var m = s < 0 ? -s : s; if (m > peak) peak = m; }
            var n = Math.Max(1, b - a);
            _rmsDb[c] = ToDbfs(Math.Sqrt(sum / n) / 32768.0);
            _peakDb[c] = ToDbfs(peak / 32768.0);
        }
        Array.Clear(_motion, 0, MaxChunks); Array.Clear(_motionAcc, 0, MaxChunks); Array.Clear(_motionN, 0, MaxChunks);
        _hasMotion = false;
    }

    /// <summary>청크 모션 에너지 직접 지정(0~1).</summary>
    public void SetMotion(int chunk, float energy) { if ((uint)chunk < (uint)_chunks) { _motion[chunk] = Math.Clamp(energy, 0f, 1f); _hasMotion = true; } }

    /// <summary>v17.58 안면 52 가중치 프레임 열(stride) + 선택적 제스처 속도(프레임당 0~1) → 청크 모션 = 평균 프레임 델타 L1 × 이득 + 제스처.</summary>
    public void ScanFaceMotion(ReadOnlySpan<float> weights, int stride, double fps, ReadOnlySpan<float> gesture = default)
    {
        var frames = weights.Length / Math.Max(1, stride);
        for (var f = 1; f < frames; f++)
        {
            var c = (int)(f * 1000.0 / fps) / ChunkMs;
            if (c >= _chunks) break;
            var cur = weights.Slice(f * stride, stride); var prev = weights.Slice((f - 1) * stride, stride);
            float d = 0; for (var k = 0; k < stride; k++) d += MathF.Abs(cur[k] - prev[k]);
            var e = d / stride * FaceGain + (f < gesture.Length ? gesture[f] : 0f);
            _motionAcc[c] += e; _motionN[c]++;
        }
        for (var c = 0; c < _chunks; c++) if (_motionN[c] > 0) _motion[c] = Math.Clamp(_motionAcc[c] / _motionN[c], 0f, 1f);
        _hasMotion = true;
    }

    /// <summary>디코드 영상 루마 SAD(8 픽셀 간격) → 청크 모션(평균 절대차 / 24). 버퍼 = 호출자(Width×Height · /2) · 끝나면 되감기.</summary>
    public void ScanVideoMotion(HardwareVideoDemuxFrameDecoder dec, byte[] y, byte[] uv, byte[] prev)
    {
        int w = dec.Width, h = dec.Height; var fps = dec.Fps > 0 ? dec.Fps : 30;
        var f = 0;
        while (dec.NextFrame(y, uv))
        {
            if (f > 0)
            {
                var c = (int)(f * 1000.0 / fps) / ChunkMs;
                if (c >= _chunks) break;
                long sad = 0; var n = 0;
                for (var i = 0; i < w * h; i += 8) { sad += Math.Abs(y[i] - prev[i]); n++; }
                _motionAcc[c] += sad / (float)Math.Max(1, n) / 24f; _motionN[c]++;
            }
            Buffer.BlockCopy(y, 0, prev, 0, w * h);
            f++;
        }
        dec.Rewind();
        for (var c = 0; c < _chunks; c++) if (_motionN[c] > 0) _motion[c] = Math.Clamp(_motionAcc[c] / _motionN[c], 0f, 1f);
        _hasMotion = true;
    }

    // ══════════════════════ 계획(무할당) ══════════════════════
    /// <summary>훅 창 판정 → 데드에어 절삭 + 콜드 오픈 재배열 → EDL.</summary>
    public HookTrimResult Plan()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _segN = 0;
        if (_chunks == 0) return Last = new HookTrimResult(false, 0, 0, 0, -1, -1, false, 0, 0, 0, 0);
        var hook = Math.Min(HookChunks, _chunks);
        // 모션 미측정 = 오디오 단독 판정(모션 조건 통과로 간주)
        var still = _hasMotion ? MotionStill : float.MaxValue;
        var stimM = _hasMotion ? StimulusMotion : 0f;
        for (var c = 0; c < _chunks; c++)
        {
            if (c >= hook) { _kind[c] = HookChunkKind.Keep; continue; }
            if (_rmsDb[c] <= SilenceDbfs && _motion[c] < still) _kind[c] = HookChunkKind.DeadAir;
            else if (_peakDb[c] >= StimulusPeakDbfs && _motion[c] >= stimM) _kind[c] = HookChunkKind.Stimulus;
            else _kind[c] = HookChunkKind.Keep;
        }
        // 선행 묵음 = 전부 절삭 · 중간 묵음 = MinMidDeadRun 이상 연속만(짧은 호흡 복원)
        var lead = 0; while (lead < hook && _kind[lead] == HookChunkKind.DeadAir) lead++;
        if (lead == hook) { for (var c = 0; c < hook; c++) _kind[c] = HookChunkKind.Keep; lead = 0; }   // 훅 전체 무음 = 판정 보류(원본 유지)
        for (var c = lead; c < hook;)
        {
            if (_kind[c] != HookChunkKind.DeadAir) { c++; continue; }
            var e = c; while (e < hook && _kind[e] == HookChunkKind.DeadAir) e++;
            if (e - c < MinMidDeadRun) for (var k = c; k < e; k++) _kind[k] = HookChunkKind.Keep;
            c = e;
        }
        var dead = 0; for (var c = 0; c < hook; c++) if (_kind[c] == HookChunkKind.DeadAir) dead++;
        // 첫 자극 → 출력 시각
        var stim = -1; for (var c = 0; c < hook; c++) if (_kind[c] == HookChunkKind.Stimulus) { stim = c; break; }
        var stimOut = -1; var cold = false; var burstEnd = stim;
        if (stim >= 0)
        {
            var before = 0; for (var c = 0; c < stim; c++) if (_kind[c] != HookChunkKind.DeadAir) before++;
            stimOut = before * ChunkMs;
            if (stimOut > StimulusDeadlineMs)
            {
                cold = true; stimOut = 0;
                burstEnd = stim; while (burstEnd < hook && burstEnd - stim < ColdOpenChunks && _kind[burstEnd] == HookChunkKind.Stimulus) burstEnd++;
            }
        }
        // 출력 순서
        var n = 0;
        if (cold) for (var c = stim; c < burstEnd; c++) _order[n++] = (short)c;
        for (var c = 0; c < _chunks; c++)
        {
            if (_kind[c] == HookChunkKind.DeadAir) continue;
            if (cold && c >= stim && c < burstEnd) continue;
            _order[n++] = (short)c;
        }
        // 원본 연속 청크 병합 → 구간
        var outMs = 0;
        for (var i = 0; i < n;)
        {
            var c0 = _order[i]; var j = i + 1;
            while (j < n && _order[j] == _order[j - 1] + 1) j++;
            if (_segN == MaxSegments - 1)
            {
                // 구간 상한: 남은 청크를 원본 연속 꼬리로 흡수(편집 중단 · 꼬리 보존)
                var start = c0 * ChunkMs; _seg[_segN++] = new HookSegment { SrcStartMs = start, DurMs = Math.Max(0, _clipMs - start) }; outMs += _clipMs - start;
                break;
            }
            var a = c0 * ChunkMs; var b = Math.Min(_clipMs, _order[j - 1] * ChunkMs + ChunkMs);
            _seg[_segN++] = new HookSegment { SrcStartMs = a, DurMs = b - a }; outMs += b - a;
            i = j;
        }
        var trimmed = _clipMs - outMs;
        Plans++; TotalTrimmedMs += Math.Max(0, trimmed); if (cold) ColdOpens++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return Last = new HookTrimResult(true, hook, dead, trimmed, stim >= 0 ? stim * ChunkMs : -1, stimOut, cold, _segN, _clipMs, outMs, us);
    }

    /// <summary>출력 ms → 원본 ms(EDL 순회 · 무할당).</summary>
    public int SourceMsAt(int outMs)
    {
        if (_segN == 0) return outMs;
        var acc = 0;
        for (var i = 0; i < _segN; i++)
        {
            ref readonly var s = ref _seg[i];
            if (outMs < acc + s.DurMs) return s.SrcStartMs + (outMs - acc);
            acc += s.DurMs;
        }
        ref readonly var l = ref _seg[_segN - 1];
        return Math.Max(l.SrcStartMs, l.SrcEndMs - 1);
    }

    /// <summary>EDL 오디오 스플라이스(인터리브 16-bit · 접합부 3 ms 페이드 · 무할당) — 기록 프레임 수.</summary>
    public int SpliceAudio(ReadOnlySpan<short> src, int sampleRate, int channels, Span<short> dst)
    {
        channels = Math.Max(1, channels);
        var fade = Math.Max(1, sampleRate * 3 / 1000);
        var srcFrames = src.Length / channels; var o = 0;
        if (_segN == 0) { var all = Math.Min(src.Length, dst.Length) / channels; src[..(all * channels)].CopyTo(dst); return all; }
        for (var i = 0; i < _segN; i++)
        {
            ref readonly var s = ref _seg[i];
            var a = (int)((long)s.SrcStartMs * sampleRate / 1000); var b = Math.Min(srcFrames, (int)((long)s.SrcEndMs * sampleRate / 1000));
            var len = Math.Min(b - a, dst.Length / channels - o);
            if (len <= 0) break;
            src.Slice(a * channels, len * channels).CopyTo(dst[(o * channels)..]);
            var f = Math.Min(fade, len / 2);
            var spliceIn = i > 0 && _seg[i - 1].SrcEndMs != s.SrcStartMs;
            var spliceOut = i + 1 < _segN && _seg[i + 1].SrcStartMs != s.SrcEndMs;
            for (var k = 0; k < f; k++)
            {
                var g = (float)k / f;
                for (var ch = 0; ch < channels; ch++)
                {
                    if (spliceIn) { ref var v = ref dst[(o + k) * channels + ch]; v = (short)(v * g); }
                    if (spliceOut) { ref var v = ref dst[(o + len - 1 - k) * channels + ch]; v = (short)(v * g); }
                }
            }
            o += len;
        }
        return o;
    }

    /// <summary>EDL 영상 재부호화(0 프레임 컷 · 무음 AAC) — 출력 프레임 수(0 = 디코드/인코더 실패). 백그라운드 전용.</summary>
    public int RenderVideo(SegmentedChunkStream clip, Stream output, int kbps = 3000, bool allowMf = false, bool preferHardwareDecode = false)
    {
        using var src = TrimmedClipFrameSource.TryOpen(clip, this, preferHardwareDecode);
        if (src is null) return 0;
        var dec = src.Decoder;
        var fps = (int)Math.Clamp(Math.Round(dec.Fps), 1, 60);
        var outMs = _segN > 0 ? Last.OutDurationMs : (int)(dec.FrameCount * 1000.0 / Math.Max(1, dec.Fps));
        var enc = new HardwareSubtitleBurnInEngine(dec.Width & ~1, dec.Height & ~1) { Enabled = false };
        return enc.BurnReel(src, outMs / 1000.0, fps, output, kbps, allowMf);
    }
}

/// <summary>
/// v17.75.0 EDL 프레임 소스(<see cref="IBurnInFrameSource"/>) — 출력 프레임 → 원본 시각(<see cref="ViralHookDeadAirAutoTrimmerEngine.SourceMsAt"/>) → 원본 프레임.
/// 전진 점프 = 건너뛰기 디코드 · 후진 점프(콜드 오픈 복귀) = 키프레임 시크(접합부 1 회 · 비핫패스) · 동일 크기 순차는 출력 버퍼 직접 복원.
/// </summary>
public sealed class TrimmedClipFrameSource : IBurnInFrameSource, IDisposable
{
    private readonly HardwareVideoDemuxFrameDecoder _dec;
    private readonly ViralHookDeadAirAutoTrimmerEngine _edl;
    private byte[] _y = Array.Empty<byte>(), _uv = Array.Empty<byte>();
    private int _have = -1;

    public TrimmedClipFrameSource(HardwareVideoDemuxFrameDecoder decoder, ViralHookDeadAirAutoTrimmerEngine edl) { _dec = decoder; _edl = edl; }

    public HardwareVideoDemuxFrameDecoder Decoder => _dec;
    public long Splices { get; private set; }
    public long Seeks { get; private set; }
    public long Skipped { get; private set; }

    public static TrimmedClipFrameSource? TryOpen(SegmentedChunkStream media, ViralHookDeadAirAutoTrimmerEngine edl, bool preferHardware = false)
    {
        var d = new HardwareVideoDemuxFrameDecoder { PreferHardware = preferHardware };
        if (!d.Open(media)) { d.Dispose(); return null; }
        return new TrimmedClipFrameSource(d, edl);
    }

    /// <summary>출력 프레임 → 원본 프레임 번호.</summary>
    public int SourceFrame(long frame, int fps)
    {
        var srcFps = _dec.Fps > 0 ? _dec.Fps : fps;
        // 정수 ms 구간 탐색 + 소수부 보존(프레임 경계 반올림 오차 0)
        var outMs = frame * 1000.0 / Math.Max(1, fps); var oi = (int)outMs;
        var srcMs = _edl.SourceMsAt(oi) + (outMs - oi);
        return (int)Math.Min(_dec.FrameCount - 1, Math.Floor(srcMs * srcFps / 1000.0 + 1e-6));
    }

    public void Render(long frame, int fps, Span<byte> y, Span<byte> uv, int width, int height)
    {
        int sw = _dec.Width, sh = _dec.Height;
        if (_y.Length != sw * sh) { _y = new byte[sw * sh]; _uv = new byte[sw * sh / 2]; }   // 클립 크기 1 회
        var want = SourceFrame(frame, fps);
        if (want != _have + 1 && frame > 0) Splices++;
        if (want < _have)
        {
            if (_dec.Seek(want, _y, _uv) && _dec.NextFrame(_y, _uv)) _have = want; Seeks++;
        }
        while (_have < want) { if (!_dec.NextFrame(_y, _uv)) break; _have++; if (_have < want) Skipped++; }
        if (_have < 0) { y[..(width * height)].Fill(16); uv[..(width * height / 2)].Fill(128); return; }
        if (sw == width && sh == height) { _y.AsSpan(0, sw * sh).CopyTo(y); _uv.AsSpan(0, sw * sh / 2).CopyTo(uv); }
        else DecodedClipFrameSource.Resample(_y, _uv, sw, sh, y, uv, width, height);
    }

    public void Dispose() => _dec.Dispose();
}
