using System;
using System.Buffers;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Pluto.Dsp;

/// <summary>🎚️ SFX 큐 구간 BGM 사이드체인 더킹(−4 dB · 25ms 어택 · 180ms 지수 릴리스).</summary>
public interface IPlutoSidechainDucker
{
    /// <summary>단일 채널 인플레이스 더킹 — SFX 합산 전 BGM 에 적용.</summary>
    void ApplyDucking(Span<float> bgmSamples, IReadOnlyList<PlutoSfxCue> sfxCues, int sampleRate = 48000, float duckingGainDb = -4.0f);

    /// <summary>스테레오 인플레이스 더킹(동일 엔벨로프 · <paramref name="right"/> 비어 있으면 모노).</summary>
    void ApplyDucking(Span<float> left, Span<float> right, IReadOnlyList<PlutoSfxCue> sfxCues, int sampleRate = 48000, float duckingGainDb = -4.0f);
}

/// <summary>
/// 🎚️ 결정론 사이드체인 더커 — 큐 구간 [T−25ms, T_end+180ms] 목표 게인 → 1-pole 스무딩
/// (α_att = 1−e^(−1/(0.025·fs)), α_rel = 1−e^(−1/(0.180·fs))). 구간 정렬·병합 후 단일 스윕(O(n + k log k)) · 처리 루프 할당 0.
/// 상태 없음(스레드 안전) — DI 싱글톤은 <see cref="Shared"/>.
/// </summary>
public sealed class PlutoSidechainDucker : IPlutoSidechainDucker
{
    public static readonly PlutoSidechainDucker Shared = new();

    public const float DefaultDuckingGainDb = -4.0f, MinDuckingGainDb = -24.0f;
    public const double AttackSec = 0.025, ReleaseSec = 0.180;
    public const double PrePadSec = AttackSec, PostPadSec = ReleaseSec;
    private const int StackWindows = 32;

    public void ApplyDucking(Span<float> bgmSamples, IReadOnlyList<PlutoSfxCue> sfxCues, int sampleRate = 48000, float duckingGainDb = DefaultDuckingGainDb)
        => ApplyDucking(bgmSamples, Span<float>.Empty, sfxCues, sampleRate, duckingGainDb);

    public void ApplyDucking(Span<float> left, Span<float> right, IReadOnlyList<PlutoSfxCue> sfxCues, int sampleRate = 48000, float duckingGainDb = DefaultDuckingGainDb)
    {
        var n = left.Length;
        var stereo = !right.IsEmpty;
        if (n == 0 || sfxCues is null || sfxCues.Count == 0 || sampleRate < 8000 || (stereo && right.Length != n)) return;
        if (!float.IsFinite(duckingGainDb) || duckingGainDb >= 0f) return;
        var target = (float)Math.Pow(10.0, Math.Max(duckingGainDb, MinDuckingGainDb) / 20.0);

        var k = sfxCues.Count;
        int[]? rented = null;
        Span<int> win = k <= StackWindows ? stackalloc int[StackWindows * 2] : (rented = ArrayPool<int>.Shared.Rent(k * 2));
        try
        {
            var m = BuildWindows(sfxCues, sampleRate, n, win);
            if (m == 0) return;
            Process(left, right, stereo, win[..(m * 2)], m, target, sampleRate);
        }
        finally
        {
            if (rented is not null) ArrayPool<int>.Shared.Return(rented);
        }
    }

    /// <summary>큐 → 패딩 샘플 구간 [s, e) — 시작 정렬(삽입) 후 겹침 병합. 반환 = 구간 수.</summary>
    internal static int BuildWindows(IReadOnlyList<PlutoSfxCue> cues, int sampleRate, int n, Span<int> win)
    {
        var m = 0;
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            if (c.OffsetMs < 0) continue;
            var startSec = c.OffsetMs * 0.001 - PrePadSec;
            var endSec = (c.OffsetMs + PlutoAudioSynthesizer.DurationMs(c.SfxType)) * 0.001 + PostPadSec;
            var s = (int)Math.Clamp(Math.Floor(startSec * sampleRate), 0, n);
            var e = (int)Math.Clamp(Math.Ceiling(endSec * sampleRate), 0, n);
            if (e <= s) continue;
            var j = m++;
            while (j > 0 && win[(j - 1) * 2] > s) { win[j * 2] = win[(j - 1) * 2]; win[j * 2 + 1] = win[(j - 1) * 2 + 1]; j--; }
            win[j * 2] = s; win[j * 2 + 1] = e;
        }
        if (m <= 1) return m;
        var w = 0;
        for (var i = 1; i < m; i++)
        {
            if (win[i * 2] <= win[w * 2 + 1]) { if (win[i * 2 + 1] > win[w * 2 + 1]) win[w * 2 + 1] = win[i * 2 + 1]; }
            else { w++; win[w * 2] = win[i * 2]; win[w * 2 + 1] = win[i * 2 + 1]; }
        }
        return w + 1;
    }

    private static void Process(Span<float> left, Span<float> right, bool stereo, ReadOnlySpan<int> win, int m, float target, int sampleRate)
    {
        var aAtt = (float)(1.0 - Math.Exp(-1.0 / (AttackSec * sampleRate)));
        var aRel = (float)(1.0 - Math.Exp(-1.0 / (ReleaseSec * sampleRate)));
        var n = left.Length;
        var g = 1f;
        var w = 0;
        var i = win[0];   // 첫 구간 이전은 g = 1(무변경)
        for (; i < n; i++)
        {
            while (w < m && i >= win[w * 2 + 1]) w++;
            var inside = w < m && i >= win[w * 2];
            if (inside) g += aAtt * (target - g);
            else
            {
                g += aRel * (1f - g);
                if (w >= m && 1f - g < 1e-6f) break;   // 마지막 구간 릴리스 완료 → 잔여 무변경
            }
            left[i] *= g;
            if (stereo) right[i] *= g;
        }
    }

    /// <summary>테스트/시각화용 — 단위 신호의 게인 엔벨로프(dB 아님 · 선형).</summary>
    public static float[] ComputeGainEnvelope(IReadOnlyList<PlutoSfxCue> cues, int frames, int sampleRate = 48000, float duckingGainDb = DefaultDuckingGainDb)
    {
        var env = new float[Math.Max(0, frames)];
        Array.Fill(env, 1f);
        Shared.ApplyDucking(env, cues, sampleRate, duckingGainDb);
        return env;
    }
}
