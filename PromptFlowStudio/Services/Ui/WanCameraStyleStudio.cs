using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.Telemetry;

namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [PromptFlowStudio] v15.6.0 🎥 Wan 2.1 시네마틱 카메라 & 그래픽 스타일 스튜디오 — 그룹 배타(카메라 1개 · 스타일 1개) 토큰 결합.
///   ▸ 같은 토큰 재클릭 → 현재 참조 그대로(0B), 그룹 내 다른 토큰이 있으면 그 자리(구분자 포함)를 빼고 새 토큰을 꼬리에 결합.
///   ▸ 결합은 span 두 조각 + 토큰을 <c>string.Create</c> 1회로 기록(중간 문자열 0). 순수 BCL · 모바일 링크.
///   ▸ v15.15.0 단일 패스 다중 토큰 탐색: 그룹 토큰 선두 문자를 <see cref="SearchValues{T}"/>(char · 벡터화)로 건너뛰며
///     후보 위치에서만 버킷 비교 — 토큰 N개 × IndexOf N회 → 스캔 1회(.NET 8 엔 SearchValues&lt;string&gt; 부재 · 동일 결과 보장).
/// </summary>
public static class WanCameraStyleStudio
{
    /// <summary>Wan 2.1 카메라 모션 6종(그룹 내 배타).</summary>
    public static readonly PromptChip[] Cameras =
    {
        new("Orbit",      "slow 360-degree orbit shot around the subject", "🔄"),
        new("FPV Drone",  "FPV drone shot swooping through the scene",      "🚁"),
        new("Crane Up",   "crane up shot rising to reveal the scene",       "🏗️"),
        new("Dolly Zoom", "dolly zoom vertigo effect on the subject",       "🔍"),
        new("Smooth Pan", "smooth cinematic pan across the scene",          "➡️"),
        new("Tilt Roll",  "dynamic tilt and roll camera move",              "🌀"),
    };

    /// <summary>그래픽 아트 스타일 5종(그룹 내 배타).</summary>
    public static readonly PromptChip[] Styles =
    {
        new("8K Photoreal",   "8K photorealistic, ultra realistic textures",            "📷"),
        new("3D Render Art",  "3D render art, octane render, soft global illumination", "🧊"),
        new("Watercolor",     "watercolor painting style, soft bleeding pigments",      "🎨"),
        new("Cyberpunk Neon", "cyberpunk neon glow, rain-soaked reflections",           "🌆"),
        new("16mm Vintage",   "16mm vintage film look, grain and light leaks",          "🎞️"),
    };

    private const int MaxGroup = 16;
    private static readonly TokenScanner CameraScan = new(Cameras);
    private static readonly TokenScanner StyleScan = new(Styles);

    public static string ApplyCamera(string? current, int index) => ApplyExclusive(current, Cameras, index);
    public static string ApplyStyle(string? current, int index) => ApplyExclusive(current, Styles, index);

    /// <summary>프롬프트에 들어 있는 그룹 토큰 인덱스(없으면 -1) — 칩 활성 표시용, 0B.</summary>
    public static int ActiveIndex(string? prompt, PromptChip[] group)
    {
        if (string.IsNullOrEmpty(prompt) || group.Length == 0) return -1;
        var scan = ScannerFor(group);
        if (scan is null)
        {
            for (var i = 0; i < group.Length; i++)
                if (prompt.Contains(group[i].Snippet, StringComparison.Ordinal)) return i;
            return -1;
        }
        Span<int> first = stackalloc int[MaxGroup];
        scan.FirstOccurrences(prompt, first);
        return LowestHit(first[..group.Length], -1);
    }

    /// <summary>그룹 배타 결합 — 범위 밖 인덱스·이미 선택된 토큰은 현재 참조 유지(0B).</summary>
    public static string ApplyExclusive(string? current, PromptChip[] group, int index)
    {
        current ??= "";
        if ((uint)index >= (uint)group.Length) return current;
        var tok = group[index].Snippet;

        int at = -1, len = 0;
        var scan = ScannerFor(group);
        if (scan is null)
        {
            if (current.Contains(tok, StringComparison.Ordinal)) return current;
            for (var i = 0; i < group.Length; i++)
            {
                if (i == index) continue;
                var p = current.IndexOf(group[i].Snippet, StringComparison.Ordinal);
                if (p >= 0) { at = p; len = group[i].Snippet.Length; break; }
            }
        }
        else
        {
            Span<int> first = stackalloc int[MaxGroup];
            scan.FirstOccurrences(current, first);
            if (first[index] >= 0) return current;                 // 재클릭 → 현재 참조(0B)
            var hit = LowestHit(first[..group.Length], index);
            if (hit >= 0) { at = first[hit]; len = group[hit].Snippet.Length; }
        }
        if (at < 0) return BabyStudioMatrix.AppendModifier(current, tok);

        // 기존 토큰 + 인접 구분자(", ") 제거 범위 [s, e).
        int s = at, e = at + len;
        if (s >= 2 && current[s - 2] == ',' && current[s - 1] == ' ') s -= 2;
        else if (e + 2 <= current.Length && current[e] == ',' && current[e + 1] == ' ') e += 2;

        var b = TrimTail(current.AsSpan(e));
        var a = b.Length == 0 ? TrimTail(current.AsSpan(0, s)) : current.AsSpan(0, s);
        int aLen = a.Length, bLen = b.Length, head = aLen + bLen;

        return string.Create(head + (head > 0 ? 2 : 0) + tok.Length, (current, aLen, e, bLen, tok), static (dst, st) =>
        {
            st.current.AsSpan(0, st.aLen).CopyTo(dst);
            st.current.AsSpan(st.e, st.bLen).CopyTo(dst[st.aLen..]);
            var pos = st.aLen + st.bLen;
            if (pos > 0) { dst[pos++] = ','; dst[pos++] = ' '; }
            st.tok.AsSpan().CopyTo(dst[pos..]);
        });
    }

    // 사전 구축 그룹만 단일 패스 — 그 외(사용자 정의 배열)는 기존 IndexOf 경로.
    private static TokenScanner? ScannerFor(PromptChip[] group)
        => ReferenceEquals(group, Cameras) ? CameraScan : ReferenceEquals(group, Styles) ? StyleScan : null;

    // 그룹 인덱스 오름차순 첫 적중(skip 제외) — 기존 "낮은 인덱스 우선" 순회와 동일 판정.
    private static int LowestHit(ReadOnlySpan<int> first, int skip)
    {
        for (var i = 0; i < first.Length; i++)
            if (i != skip && first[i] >= 0) return i;
        return -1;
    }

    /// <summary>
    /// 그룹 토큰 선두 문자 집합(<see cref="SearchValues{T}"/>) + 선두 문자별 토큰 버킷.
    /// <see cref="FirstOccurrences"/> 는 토큰마다 <c>IndexOf(Ordinal)</c> 결과(첫 출현 위치 · 없으면 -1)를 1회 스캔으로 채운다(0B).
    /// </summary>
    /// <remarks>
    /// v15.16.0 Zenith: <see cref="SearchValues{T}"/> 앞단에 <see cref="Vector256{T}"/> 바이그램 프리필터 —
    ///   16 char 블록마다 (선두, 둘째) 문자쌍을 동시 비교해 후보 위치만 산출(선두 단독보다 거짓 후보 대폭 감소).
    ///   하드웨어 미가속·꼬리 17자 미만은 기존 SearchValues 경로로 폴백. 후보 ⊇ 실제 적중 · 오름차순 → 결과 동일.
    /// </remarks>
    private sealed class TokenScanner
    {
        private readonly string[] _tokens;
        private readonly SearchValues<char> _heads;
        private readonly Vector256<ushort>[] _first;
        private readonly Vector256<ushort>[] _second;

        public TokenScanner(PromptChip[] group)
        {
            if (group.Length > MaxGroup) throw new ArgumentOutOfRangeException(nameof(group));
            _tokens = new string[group.Length];
            var heads = new char[group.Length];
            for (var i = 0; i < group.Length; i++) { _tokens[i] = group[i].Snippet; heads[i] = group[i].Snippet[0]; }
            _heads = SearchValues.Create(heads);

            // 고유 바이그램만 브로드캐스트 벡터로 보관(토큰은 모두 2자 이상).
            Span<uint> pairs = stackalloc uint[MaxGroup];
            var n = 0;
            for (var i = 0; i < _tokens.Length; i++)
            {
                var t = _tokens[i];
                var pair = t.Length > 1 ? (uint)t[0] | (uint)t[1] << 16 : uint.MaxValue;
                if (pair == uint.MaxValue || pairs[..n].Contains(pair)) continue;
                pairs[n++] = pair;
            }
            // 1자 토큰이 있으면 바이그램 필터가 불완전 → SIMD 비활성(빈 배열).
            var simdSafe = Array.TrueForAll(_tokens, static t => t.Length > 1);
            _first = new Vector256<ushort>[simdSafe ? n : 0];
            _second = new Vector256<ushort>[simdSafe ? n : 0];
            for (var i = 0; i < _first.Length; i++)
            {
                _first[i] = Vector256.Create((ushort)pairs[i]);
                _second[i] = Vector256.Create((ushort)(pairs[i] >> 16));
            }
        }

        public void FirstOccurrences(ReadOnlySpan<char> text, Span<int> first)
        {
            var etw = ZenithEventSource.Log;
            if (!etw.SimdTracing) { FirstOccurrences(text, first, Vector256.IsHardwareAccelerated); return; }
            var t0 = Stopwatch.GetTimestamp();   // v15.17.0 ETW: 구독 시에만 계측(0B)
            FirstOccurrences(text, first, Vector256.IsHardwareAccelerated);
            etw.OnSimdScan(Stopwatch.GetTimestamp() - t0, text.Length);
        }

        public void FirstOccurrences(ReadOnlySpan<char> text, Span<int> first, bool simd)
        {
            var tokens = _tokens;
            first[..tokens.Length].Fill(-1);
            int pending = tokens.Length, pos = 0;
            while (pending > 0)
            {
                pos = NextCandidate(text, pos, simd);
                if (pos < 0) return;
                var rest = text[pos..];
                var c = rest[0];
                for (var i = 0; i < tokens.Length; i++)
                {
                    var t = tokens[i];
                    if (first[i] < 0 && t[0] == c && rest.StartsWith(t.AsSpan(), StringComparison.Ordinal)) { first[i] = pos; pending--; }
                }
                pos++;
            }
        }

        // pos 이상 첫 후보 위치(없으면 -1). SIMD 블록이 바이그램 적중을 못 찾은 구간은 확정 배제.
        private int NextCandidate(ReadOnlySpan<char> text, int pos, bool simd)
        {
            if (simd && _first.Length > 0)
            {
                ref var src = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
                var last = text.Length - Vector256<ushort>.Count - 1;   // pos+1 블록까지 경계 내
                var first = _first;
                var second = _second;
                for (; pos <= last; pos += Vector256<ushort>.Count)
                {
                    var a = Vector256.LoadUnsafe(ref src, (nuint)pos);
                    var b = Vector256.LoadUnsafe(ref src, (nuint)(pos + 1));
                    var hit = Vector256<ushort>.Zero;
                    for (var i = 0; i < first.Length; i++)
                        hit |= Vector256.Equals(a, first[i]) & Vector256.Equals(b, second[i]);
                    var mask = hit.ExtractMostSignificantBits();
                    if (mask != 0) return pos + BitOperations.TrailingZeroCount(mask);
                }
            }
            if (pos >= text.Length) return -1;
            var rel = text[pos..].IndexOfAny(_heads);
            return rel < 0 ? -1 : pos + rel;
        }
    }

    /// <summary>테스트 전용 — 임의 그룹으로 스캐너를 만들어 SIMD/스칼라 경로를 강제 실행.</summary>
    internal static void ScanForTest(PromptChip[] group, ReadOnlySpan<char> text, Span<int> first, bool simd)
        => new TokenScanner(group).FirstOccurrences(text, first, simd);

    /// <summary>현재 하드웨어에서 Vector256 SIMD 프리필터 활성 여부.</summary>
    public static bool SimdAccelerated => Vector256.IsHardwareAccelerated;

    private static ReadOnlySpan<char> TrimTail(ReadOnlySpan<char> s)
    {
        while (s.Length > 0 && (s[^1] == ',' || char.IsWhiteSpace(s[^1]))) s = s[..^1];
        return s;
    }
}
