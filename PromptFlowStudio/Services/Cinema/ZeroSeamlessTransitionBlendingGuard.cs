using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.Cinema;

/// <summary>
/// [PromptFlow Studio v16.78.0] 🎞 무단절 비디오 &amp; 오디오 트랜지션 블렌딩 가드.
/// 비디오: RGB 프레임 SIMD 고정소수 크로스페이드(Vector&lt;byte&gt; 확장 → ushort 곱합 → 축소) · 3DGS/4D 스플랫 장면 대응 인덱스 보간 + 잉여 불투명도 페이드 ·
/// 스무스스텝 곡선 · 프레임 예산 초과 감시(초과 시에도 직전 혼합 프레임 유지 → 드롭 0). 오디오: 나가는 스트림 영교차 정렬(최대 5 ms 대기) →
/// 등전력(sin/cos) 크로스페이드 · 들어오는 스트림 RMS 라우드니스 정합(±6 dB, 페이드 종료 시 1로 수렴) · 팝 감지(입력 대비 초과 기울기). 무할당.
/// </summary>
public sealed class ZeroSeamlessTransitionBlendingGuard
{
    public const float PopThreshold = 0.05f, FrameBudgetMs = 4f;

    private readonly object _gate = new();
    private readonly int _fs;
    private float _videoW, _videoMs, _videoFadeMs, _audioFadeSamples, _audioPos, _match = 1f;
    private bool _active, _audioStarted, _videoDone, _audioDone, _justCompleted;
    private int _waitSamples;
    private float _prevOutL, _prevOutR, _prevAL, _prevAR, _prevBL, _prevBR;
    private bool _havePrev;

    public ZeroSeamlessTransitionBlendingGuard(int sampleRate = 48000) => _fs = sampleRate;

    public int SampleRate => _fs;
    public bool Active => _active;
    /// <summary>비디오 혼합 가중치(스무스스텝 적용 · 0 = 나가는 · 1 = 들어오는).</summary>
    public float VideoWeight => _active ? Smooth(_videoW) : 0f;
    public float AudioWeight => _active && _audioStarted ? Math.Clamp(_audioPos / MathF.Max(1f, _audioFadeSamples), 0f, 1f) : 0f;
    public float LoudnessMatch => _match;
    public long Transitions { get; private set; }
    public long Completed { get; private set; }
    public long Pops { get; private set; }
    public long OverBudgetFrames { get; private set; }
    public long FramesBlended { get; private set; }
    public float MaxExcessSlope { get; private set; }
    public float LastBlendMs { get; private set; }
    public int LastZeroCrossWait { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>트랜지션 시작(진행 중이면 거부 · 반환 false). fade 0 = 하드 컷(영교차 정렬만).</summary>
    public bool Begin(float videoFadeMs, float audioFadeMs)
    {
        lock (_gate)
        {
            if (_active) return false;
            _active = true; _audioStarted = false; _videoDone = false; _audioDone = false; _justCompleted = false;
            _videoW = 0; _videoMs = 0; _videoFadeMs = MathF.Max(0f, videoFadeMs);
            _audioFadeSamples = MathF.Max(0f, audioFadeMs) * 0.001f * _fs; _audioPos = 0; _waitSamples = 0; _match = 1f;
            Transitions++;
            return true;
        }
    }

    /// <summary>완료 직후 1회 true(호출자 슬롯 교체 신호).</summary>
    public bool ConsumeCompleted()
    {
        lock (_gate)
        {
            if (!_justCompleted) return false;
            _justCompleted = false;
            return true;
        }
    }

    /// <summary>비디오 시계 진행(프레임당 1회).</summary>
    public void AdvanceFrame(float dtMs)
    {
        lock (_gate)
        {
            if (!_active) return;
            _videoMs += dtMs;
            _videoW = _videoFadeMs <= 0 ? 1f : Math.Clamp(_videoMs / _videoFadeMs, 0f, 1f);
            if (_videoW >= 1f) _videoDone = true;
            TryComplete();
        }
    }

    private void TryComplete()
    {
        if (_active && _videoDone && _audioDone)
        {
            _active = false; _justCompleted = true; Completed++;
            LastAction = "✅ 트랜지션 완료";
        }
    }

    private static float Smooth(float x) => x * x * (3f - 2f * x);

    // ══════════════════════ 비디오 ══════════════════════

    /// <summary>RGB(A) 프레임 혼합 → dst. 비활성 시 a 복사. 무할당 SIMD.</summary>
    public void BlendVideo(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst)
    {
        var t0 = Stopwatch.GetTimestamp();
        var w = VideoWeight;
        if (!_active) a.CopyTo(dst);
        else BlendBytes(a, b, dst, (int)(w * 256f + 0.5f));
        FinishFrame(t0);
    }

    /// <summary>고정소수 혼합: dst = (a·(256−w) + b·w) &gt;&gt; 8 · w ∈ [0, 256].</summary>
    public static void BlendBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst, int w256)
    {
        w256 = Math.Clamp(w256, 0, 256);
        var n = Math.Min(a.Length, Math.Min(b.Length, dst.Length));
        var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<byte>.Count)
        {
            var vw = new Vector<ushort>((ushort)w256); var vi = new Vector<ushort>((ushort)(256 - w256));
            for (; i + Vector<byte>.Count <= n; i += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(a.Slice(i)), out var al, out var ah);
                Vector.Widen(new Vector<byte>(b.Slice(i)), out var bl, out var bh);
                var lo = Vector.ShiftRightLogical(al * vi + bl * vw, 8);
                var hi = Vector.ShiftRightLogical(ah * vi + bh * vw, 8);
                Vector.Narrow(lo, hi).CopyTo(dst.Slice(i));
            }
        }
        for (; i < n; i++) dst[i] = (byte)((a[i] * (256 - w256) + b[i] * w256) >> 8);
    }

    /// <summary>3DGS/4D 스플랫 장면 혼합(대응 인덱스 위치/스케일/SH 보간 · 회전 nlerp · 잉여 불투명도 페이드). 용량 충분 시 무할당.</summary>
    public void BlendSplats(GaussianScene a, GaussianScene b, GaussianScene dst)
    {
        var t0 = Stopwatch.GetTimestamp();
        var w = _active ? VideoWeight : 0f;
        var na = a.Count; var nb = b.Count; var n = Math.Max(na, nb);
        dst.EnsureCapacity(n);
        var common = Math.Min(na, nb);
        var iw = 1f - w;
        for (var i = 0; i < common; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                dst.Pos[i * 3 + c] = a.Pos[i * 3 + c] * iw + b.Pos[i * 3 + c] * w;
                dst.Scale[i * 3 + c] = a.Scale[i * 3 + c] * iw + b.Scale[i * 3 + c] * w;
            }
            var qa = a.Rotation(i); var qb = b.Rotation(i);
            if (Quaternion.Dot(qa, qb) < 0) qb = Quaternion.Negate(qb);
            var q = Quaternion.Normalize(Quaternion.Lerp(qa, qb, w));
            dst.Rot[i * 4] = q.W; dst.Rot[i * 4 + 1] = q.X; dst.Rot[i * 4 + 2] = q.Y; dst.Rot[i * 4 + 3] = q.Z;
            dst.Opacity[i] = a.Opacity[i] * iw + b.Opacity[i] * w;
            for (var s = 0; s < GaussianScene.ShStride; s++) dst.Sh[i * GaussianScene.ShStride + s] = a.Sh[i * GaussianScene.ShStride + s] * iw + b.Sh[i * GaussianScene.ShStride + s] * w;
        }
        var src = na > nb ? a : b; var fade = na > nb ? iw : w;
        for (var i = common; i < n; i++)
        {
            src.Pos.AsSpan(i * 3, 3).CopyTo(dst.Pos.AsSpan(i * 3)); src.Scale.AsSpan(i * 3, 3).CopyTo(dst.Scale.AsSpan(i * 3));
            src.Rot.AsSpan(i * 4, 4).CopyTo(dst.Rot.AsSpan(i * 4)); src.Sh.AsSpan(i * GaussianScene.ShStride, GaussianScene.ShStride).CopyTo(dst.Sh.AsSpan(i * GaussianScene.ShStride));
            dst.Opacity[i] = src.Opacity[i] * fade;
        }
        dst.Count = n; dst.Version++;
        LastBlendMs = (float)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private void FinishFrame(long t0)
    {
        LastBlendMs = (float)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        FramesBlended++;
        if (LastBlendMs > FrameBudgetMs) OverBudgetFrames++;
    }

    // ══════════════════════ 오디오 ══════════════════════

    /// <summary>
    /// 스테레오 블록 혼합 → outL/outR(덮어쓰기). 비활성 = a 통과. 활성 = 영교차 대기 → 등전력 페이드(라우드니스 정합 → 1) → 완료 후 b 통과. 팝 감지 포함. 무할당.
    /// </summary>
    public void BlendAudio(ReadOnlySpan<float> aL, ReadOnlySpan<float> aR, ReadOnlySpan<float> bL, ReadOnlySpan<float> bR, Span<float> outL, Span<float> outR)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Cinema.BlendAudio");
            var n = Math.Min(Math.Min(aL.Length, aR.Length), Math.Min(outL.Length, outR.Length));
            if (_active && !_audioStarted)
            {
                // 라우드니스 정합: 블록 RMS 비(±6 dB)
                double ea = 0, eb = 0;
                for (var i = 0; i < n; i++) { ea += aL[i] * aL[i] + aR[i] * aR[i]; eb += bL[i] * bL[i] + bR[i] * bR[i]; }
                _match = eb > 1e-12 ? Math.Clamp((float)Math.Sqrt(ea / eb), 0.5f, 2f) : 1f;
            }
            var maxWait = _fs / 200; // 5 ms
            for (var i = 0; i < n; i++)
            {
                float l, r;
                if (!_active || (!_audioStarted && !StartsHere(aL, aR, i, maxWait)))
                {
                    // 비활성: 완료 후 슬롯 교체 전까지는 b(들어온 스트림) 유지
                    var useB = !_active && _audioDone;
                    l = useB ? bL[i] : aL[i]; r = useB ? bR[i] : aR[i];
                }
                else if (_audioPos >= _audioFadeSamples)
                {
                    l = bL[i]; r = bR[i];
                    if (!_audioDone) { _audioDone = true; TryComplete(); }
                }
                else
                {
                    var x = _audioPos / _audioFadeSamples;
                    var go = MathF.Cos(x * MathF.PI * 0.5f); var gi = MathF.Sin(x * MathF.PI * 0.5f);
                    var m = _match + (1f - _match) * x;
                    l = aL[i] * go + bL[i] * gi * m; r = aR[i] * go + bR[i] * gi * m;
                    _audioPos++;
                }
                if (_havePrev)
                {
                    var inSlope = MathF.Max(MathF.Max(MathF.Abs(aL[i] - _prevAL), MathF.Abs(aR[i] - _prevAR)), MathF.Max(MathF.Abs(bL[i] - _prevBL), MathF.Abs(bR[i] - _prevBR)));
                    var ex = MathF.Max(MathF.Abs(l - _prevOutL), MathF.Abs(r - _prevOutR)) - inSlope;
                    if (ex > MaxExcessSlope) MaxExcessSlope = ex;
                    if (ex > PopThreshold) Pops++;
                }
                _prevOutL = l; _prevOutR = r; _prevAL = aL[i]; _prevAR = aR[i]; _prevBL = bL[i]; _prevBR = bR[i]; _havePrev = true;
                outL[i] = l; outR[i] = r;
            }
        }
    }

    /// <summary>오디오 페이드 시작 판정: 나가는 스트림(L+R) 부호 전환 또는 최대 대기 초과.</summary>
    private bool StartsHere(ReadOnlySpan<float> aL, ReadOnlySpan<float> aR, int i, int maxWait)
    {
        var cur = aL[i] + aR[i];
        var prev = i > 0 ? aL[i - 1] + aR[i - 1] : _prevAL + _prevAR;
        if ((prev <= 0 && cur >= 0) || (prev >= 0 && cur <= 0) || _waitSamples >= maxWait)
        {
            _audioStarted = true; LastZeroCrossWait = _waitSamples;
            if (_audioFadeSamples < 1f) _audioPos = _audioFadeSamples; // 하드 컷
            return true;
        }
        _waitSamples++;
        return false;
    }

    /// <summary>슬롯 교체 후 오디오 상태 정리(다음 블록부터 새 a = 이전 b).</summary>
    public void ResetAudioRouting()
    {
        lock (_gate)
        {
            _audioDone = false;
            (_prevAL, _prevBL) = (_prevBL, _prevAL); (_prevAR, _prevBR) = (_prevBR, _prevAR);
        }
    }
}
