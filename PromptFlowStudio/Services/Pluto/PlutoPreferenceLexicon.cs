using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🧬 프롬프트 DNA 의미 버킷.</summary>
public enum DnaBucket
{
    Lighting = 0,
    Camera = 1,
    Atmosphere = 2,
}

/// <summary>🧬 추출된 스타일 토큰 1건.</summary>
public readonly record struct DnaToken(DnaBucket Bucket, string Phrase);

/// <summary>
/// 🧬 Pluto Prompt DNA 렉시콘 — 내보낸 프롬프트를 조명·카메라·분위기 토큰으로 분해 →
/// 빈도·성과 가중치를 <c>pluto_dna_memory.json</c> 에 영속. 생성기는 상위 가중치 토큰을 우선 조합.
/// 시드 어휘 + 패턴(…light/…lens/…tone 등) 매칭으로 미등록 어휘도 학습(외부 호출 없음).
/// </summary>
public sealed class PlutoPreferenceLexicon : IDisposable
{
    public const string FileName = "pluto_dna_memory.json";
    public const double ExportReward = 1.0;
    public const double MaxTokenWeight = 50.0;
    public const int MaxEntries = 512;

    private static readonly string[] LightingSeeds =
    {
        "golden hour", "soft morning rays", "cinematic backlight", "soft window light", "warm desk lamp", "dreamy golden haze",
        "lens flare", "rim light", "candlelight", "overcast diffused light", "neon glow", "moonlight", "sunset glow",
        "dappled sunlight", "soft studio light", "afternoon light", "morning light", "blue hour", "warm glow",
    };

    private static readonly string[] CameraSeeds =
    {
        "eye-level dolly", "macro close-up", "slow orbit", "slow dolly-in", "slow push-in", "overhead push-in", "low-angle",
        "tracking pan", "crane rise", "lateral slide", "rack focus", "handheld follow", "top-down slide", "time-lapse",
        "pull-back", "gentle orbit", "slow-motion", "match cut", "zoom-in", "dolly-in", "close-up", "wide shot",
    };

    private static readonly string[] AtmosphereSeeds =
    {
        "pastel tone", "warm bokeh", "nostalgic grain", "film grain", "cozy", "dreamy", "airy", "moody", "whimsical",
        "serene", "playful energy", "warm wood tones", "soft focus", "bokeh", "pastel nursery", "cream knit", "minimalist",
        "nostalgic", "heartwarming", "peaceful", "vintage", "clean aesthetic",
    };

    /// <summary>미등록 어휘 학습용 꼬리 패턴 — "형용사 + 꼬리" 2-그램.</summary>
    private static readonly (string Tail, DnaBucket Bucket)[] TailPatterns =
    {
        ("light", DnaBucket.Lighting), ("rays", DnaBucket.Lighting), ("glow", DnaBucket.Lighting), ("backlight", DnaBucket.Lighting), ("hour", DnaBucket.Lighting),
        ("dolly", DnaBucket.Camera), ("orbit", DnaBucket.Camera), ("pan", DnaBucket.Camera), ("close-up", DnaBucket.Camera), ("lens", DnaBucket.Camera), ("shot", DnaBucket.Camera),
        ("tone", DnaBucket.Atmosphere), ("tones", DnaBucket.Atmosphere), ("bokeh", DnaBucket.Atmosphere), ("grain", DnaBucket.Atmosphere), ("mood", DnaBucket.Atmosphere), ("palette", DnaBucket.Atmosphere),
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private volatile bool _loaded;
    private volatile bool _readOnly;   // 🛡️ 적재 불가(잠금·권한) 세션 — 기존 파일 덮어쓰기 금지
    private int _disposed;

    public PlutoPreferenceLexicon() : this(null) { }

    public PlutoPreferenceLexicon(string? storagePath)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", FileName)
            : storagePath;
    }

    public string StoragePath => _path;

    /// <summary>렉시콘 변경 통지(백그라운드 스레드 가능).</summary>
    public event Action? Changed;

    public sealed class Entry
    {
        public DnaBucket Bucket { get; set; }
        public string Phrase { get; set; } = "";
        public int Frequency { get; set; }
        public double Weight { get; set; }
        public DateTime LastUtc { get; set; }
    }

    /// <summary>시드 어휘(버킷별, 생성기 기본 풀).</summary>
    public static IReadOnlyList<string> Seeds(DnaBucket b) => b switch
    {
        DnaBucket.Lighting => LightingSeeds,
        DnaBucket.Camera => CameraSeeds,
        _ => AtmosphereSeeds,
    };

    private static string Key(DnaBucket b, string phrase) => ((int)b).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + phrase;

    /// <summary>프롬프트 → 의미 토큰(버킷·구문, 중복 제거 · 긴 시드 우선 매칭으로 부분 중복 억제).</summary>
    public static IReadOnlyList<DnaToken> Extract(string? prompt)
    {
        var result = new List<DnaToken>();
        if (string.IsNullOrWhiteSpace(prompt)) return result;
        var text = " " + Normalize(prompt) + " ";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var consumed = new bool[text.Length];
        MatchSeeds(text, consumed, LightingSeeds, DnaBucket.Lighting, result, seen);
        MatchSeeds(text, consumed, CameraSeeds, DnaBucket.Camera, result, seen);
        MatchSeeds(text, consumed, AtmosphereSeeds, DnaBucket.Atmosphere, result, seen);

        // 미등록 2-그램: "<word> <tail>" — 시드에 이미 소비된 구간은 건너뜀.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < words.Length; i++)
        {
            foreach (var (tail, bucket) in TailPatterns)
            {
                if (!string.Equals(words[i], tail, StringComparison.Ordinal)) continue;
                var prev = words[i - 1];
                if (prev.Length < 3 || IsStopWord(prev)) continue;
                var phrase = prev + " " + tail;
                var at = text.IndexOf(" " + phrase + " ", StringComparison.Ordinal);
                if (at >= 0 && consumed[at + 1]) continue;
                if (seen.Add(Key(bucket, phrase))) result.Add(new DnaToken(bucket, phrase));
            }
        }
        return result;
    }

    private static void MatchSeeds(string text, bool[] consumed, string[] seeds, DnaBucket bucket, List<DnaToken> result, HashSet<string> seen)
    {
        // 긴 시드 우선 — "soft window light" 가 "window light" 류보다 먼저 소비.
        var order = new int[seeds.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => seeds[b].Length.CompareTo(seeds[a].Length));
        foreach (var oi in order)
        {
            var s = seeds[oi];
            var needle = " " + s;
            var at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                var end = at + needle.Length;
                var boundary = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (boundary && !consumed[at + 1])
                {
                    for (var k = at + 1; k < end; k++) consumed[k] = true;
                    if (seen.Add(Key(bucket, s))) result.Add(new DnaToken(bucket, s));
                    break;
                }
                at = end;
            }
        }
    }

    private static bool IsStopWord(string w) => w is "the" or "and" or "with" or "a" or "an" or "of" or "in" or "on" or "at" or "to" or "for" or "from" or "under" or "into";

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            var c = char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(c) || c == '-') { sb.Append(c); space = false; }
            else if (!space) { sb.Append(' '); space = true; }
        }
        return sb.ToString().Trim();
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            // 손상 JSON → .corrupt 격리 후 빈 사전 · 일시적 잠금 → 재시도 · 끝내 읽기 불가 → 디스크 덮어쓰기 금지(학습 DNA 보호)
            var (data, status) = await PlutoJsonStore.TryLoadAsync<List<Entry>>(_path, "Pluto.DNA", ct).ConfigureAwait(false);
            _readOnly = status == PlutoLoadStatus.Unreadable;
            lock (_sync)
            {
                _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                if (data is not null)
                    foreach (var e in data)
                        if (e is not null && !string.IsNullOrWhiteSpace(e.Phrase) && Enum.IsDefined(e.Bucket) && double.IsFinite(e.Weight))
                            _entries[Key(e.Bucket, e.Phrase)] = e;
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>내보내기 성공 → 프롬프트 토큰 빈도 +1 · 가중치 +reward(클램프) → 원자적 영속. 반환 = 강화된 토큰.</summary>
    public async Task<IReadOnlyList<DnaToken>> ReinforceAsync(string? prompt, double reward = ExportReward, CancellationToken ct = default)
    {
        var tokens = Extract(prompt);
        if (tokens.Count == 0) return tokens;
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            foreach (var t in tokens)
            {
                var k = Key(t.Bucket, t.Phrase);
                if (!_entries.TryGetValue(k, out var e)) { e = new Entry { Bucket = t.Bucket, Phrase = t.Phrase }; _entries[k] = e; }
                e.Frequency++;
                e.Weight = Math.Clamp(e.Weight + reward, -MaxTokenWeight, MaxTokenWeight);
                e.LastUtc = now;
            }
            Trim();
        }
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
        return tokens;
    }

    /// <summary>용량 초과 시 최저 가중치 → 최오래된 항목부터 제거(호출자 lock 보유).</summary>
    private void Trim()
    {
        while (_entries.Count > MaxEntries)
        {
            string? worst = null;
            Entry? we = null;
            foreach (var kv in _entries)
                if (we is null || kv.Value.Weight < we.Weight || (kv.Value.Weight == we.Weight && kv.Value.LastUtc < we.LastUtc)) { worst = kv.Key; we = kv.Value; }
            if (worst is null) break;
            _entries.Remove(worst);
        }
    }

    public double GetWeight(DnaBucket bucket, string phrase)
    {
        lock (_sync) return _entries.TryGetValue(Key(bucket, phrase), out var e) ? e.Weight : 0;
    }

    public int GetFrequency(DnaBucket bucket, string phrase)
    {
        lock (_sync) return _entries.TryGetValue(Key(bucket, phrase), out var e) ? e.Frequency : 0;
    }

    public int Count { get { lock (_sync) return _entries.Count; } }

    /// <summary>버킷별 상위 가중치 토큰(양수만 · 가중치→빈도 내림차순).</summary>
    public IReadOnlyList<string> TopTokens(DnaBucket bucket, int take)
    {
        // 🛡️ 값 스냅샷을 lock 안에서 복사 — 정렬 중 동시 ReinforceAsync 가 Weight 를 바꾸면 비교 불일치(InvalidOperationException) 가능.
        var picks = new List<(string Phrase, double Weight, int Frequency)>();
        lock (_sync)
            foreach (var kv in _entries)
                if (kv.Value.Bucket == bucket && kv.Value.Weight > 0) picks.Add((kv.Value.Phrase, kv.Value.Weight, kv.Value.Frequency));
        picks.Sort(static (a, b) => { var c = b.Weight.CompareTo(a.Weight); return c != 0 ? c : b.Frequency.CompareTo(a.Frequency); });
        var n = Math.Min(Math.Max(0, take), picks.Count);
        var r = new string[n];
        for (var i = 0; i < n; i++) r[i] = picks[i].Phrase;
        return r;
    }

    /// <summary>
    /// 게이트 획득 후 "최신" 상태를 직렬화해 원자적 기록 — 스냅샷을 게이트 밖에서 뜨면 동시 기록(피드백 연타 + 내보내기 완료) 시 오래된 스냅샷이 나중에 기록되는 Lost-Update 발생.
    /// 실패/읽기 불가 세션 → false([부분 구제] 메모리 내 상태 유지).
    /// </summary>
    private async Task<bool> PersistAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_readOnly) return false;
            string json;
            lock (_sync) json = JsonSerializer.Serialize(new List<Entry>(_entries.Values));
            return await PlutoJsonStore.WriteAtomicAsync(_path, json, "Pluto.DNA", ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DNA] Changed handler: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
