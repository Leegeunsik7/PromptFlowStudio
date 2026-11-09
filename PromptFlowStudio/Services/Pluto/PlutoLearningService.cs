using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🪐 사용자 상호작용 종류 — 학습 가중치 증감.</summary>
public enum InteractionType
{
    Adopted = 0,
    ExportCompleted = 1,
    ThumbsDown = 2,
    ThumbsUp = 3,
}

/// <summary>
/// 🪐 Pluto 자가 적응 학습 — 채택/내보내기/평가 → 트렌드별 가중치를 <c>pluto_trend_memory.json</c> 에 영속.
/// 순위: 80% 활용(가중치 내림차순) + 20% 탐색(상위 2개 밖 최소 상호작용 카드를 2번째 슬롯으로 승격).
/// </summary>
public sealed class PlutoLearningService : IDisposable
{
    public const double AdoptedDelta = 0.05;
    public const double ExportDelta = 0.15;
    public const double ThumbsDownDelta = -0.10;
    public const double ThumbsUpDelta = 0.05;
    public const double ExplorationRate = 0.20;
    public const double MinWeight = -1.0;
    public const double MaxWeight = 5.0;
    public const string FileName = "pluto_trend_memory.json";
    /// <summary>🧠 XP 가중 — 채택 1 · 내보내기 완료 3 · 👍 1. 레벨당 10 XP.</summary>
    public const int AdoptedXp = 1;
    public const int ExportXp = 3;
    public const int ThumbsUpXp = 1;
    public const int XpPerLevel = 10;

    private readonly string _path;
    private readonly Random _rng;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _loaded;
    private int _disposed;

    public PlutoLearningService() : this(null, null) { }

    public PlutoLearningService(string? storagePath, Random? rng = null)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", FileName)
            : storagePath;
        _rng = rng ?? new Random();
    }

    public string StoragePath => _path;

    /// <summary>상태 변경 통지(백그라운드 스레드 가능).</summary>
    public event Action? Changed;

    public sealed class Entry
    {
        public double Weight { get; set; }
        public int Interactions { get; set; }
        public DateTime LastUtc { get; set; }
        public int Adopted { get; set; }
        public int Exports { get; set; }
        public int ThumbsUp { get; set; }
        public int ThumbsDown { get; set; }
        /// <summary>📈 외부 학습 보너스 XP(SNS 레이더 채널 학습 등).</summary>
        public int BonusXp { get; set; }
    }

    /// <summary>🧠 누적 XP = 채택×1 + 내보내기×3 + 👍×1.</summary>
    public static int ComputeXp(int adopted, int exports, int thumbsUp)
        => Math.Max(0, adopted) * AdoptedXp + Math.Max(0, exports) * ExportXp + Math.Max(0, thumbsUp) * ThumbsUpXp;

    /// <summary>🧠 레벨 = 1 + XP / 10.</summary>
    public static int LevelFor(int xp) => 1 + Math.Max(0, xp) / XpPerLevel;

    /// <summary>🧠 누적 XP(전체 트렌드 합산).</summary>
    public int CurrentXP
    {
        get
        {
            int a = 0, x = 0, u = 0, b = 0;
            lock (_sync)
                foreach (var kv in _entries) { a += kv.Value.Adopted; x += kv.Value.Exports; u += kv.Value.ThumbsUp; b += Math.Max(0, kv.Value.BonusXp); }
            return ComputeXp(a, x, u) + b;
        }
    }

    /// <summary>🧠 현재 레벨.</summary>
    public int CurrentLevel => LevelFor(CurrentXP);

    /// <summary>🧠 다음 레벨 도달 XP(누적 기준).</summary>
    public int NextLevelXP => CurrentLevel * XpPerLevel;

    /// <summary>🧠 누적 피드백 횟수(전 유형).</summary>
    public int TotalInteractions
    {
        get
        {
            var n = 0;
            lock (_sync) foreach (var kv in _entries) n += kv.Value.Interactions;
            return n;
        }
    }

    public static double Delta(InteractionType t) => t switch
    {
        InteractionType.Adopted => AdoptedDelta,
        InteractionType.ExportCompleted => ExportDelta,
        InteractionType.ThumbsDown => ThumbsDownDelta,
        InteractionType.ThumbsUp => ThumbsUpDelta,
        _ => 0,
    };

    public double GetWeight(string trendId)
    {
        lock (_sync) return _entries.TryGetValue(trendId, out var e) ? e.Weight : 0;
    }

    public int GetInteractions(string trendId)
    {
        lock (_sync) return _entries.TryGetValue(trendId, out var e) ? e.Interactions : 0;
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            Dictionary<string, Entry>? data = null;
            try
            {
                if (File.Exists(_path))
                {
                    await using var fs = File.OpenRead(_path);
                    data = await JsonSerializer.DeserializeAsync<Dictionary<string, Entry>>(fs, cancellationToken: ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("[Pluto] memory load failed: " + ex.Message);   // 손상 파일 → 빈 메모리로 재시작
            }
            lock (_sync)
            {
                _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                if (data is not null)
                    foreach (var kv in data)
                        if (kv.Value is not null && !string.IsNullOrWhiteSpace(kv.Key) && double.IsFinite(kv.Value.Weight))
                            _entries[kv.Key] = kv.Value;
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>상호작용 기록 → 가중치 갱신(±클램프) → 즉시 원자적 영속. 반환 = 갱신 후 가중치.</summary>
    public async Task<double> RecordInteractionAsync(string trendId, InteractionType type, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trendId)) throw new ArgumentException("trendId required", nameof(trendId));
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        double w;
        string json;
        lock (_sync)
        {
            if (!_entries.TryGetValue(trendId, out var e)) { e = new Entry(); _entries[trendId] = e; }
            e.Weight = Math.Clamp(e.Weight + Delta(type), MinWeight, MaxWeight);
            e.Interactions++;
            switch (type)
            {
                case InteractionType.Adopted: e.Adopted++; break;
                case InteractionType.ExportCompleted: e.Exports++; break;
                case InteractionType.ThumbsUp: e.ThumbsUp++; break;
                case InteractionType.ThumbsDown: e.ThumbsDown++; break;
            }
            e.LastUtc = DateTime.UtcNow;
            w = e.Weight;
            json = JsonSerializer.Serialize(_entries);
        }
        await PersistAsync(json, ct).ConfigureAwait(false);
        RaiseChanged();
        return w;
    }

    /// <summary>
    /// 📈 보너스 XP 적립(예: SNS 레이더 채널 학습 +2) — 출처 키 엔트리에 누적 · 가중치 불변 · 상호작용 +1 · 즉시 원자적 영속. 반환 = 갱신 후 누적 XP.
    /// </summary>
    public async Task<int> AddBonusXpAsync(string sourceKey, int xp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) throw new ArgumentException("sourceKey required", nameof(sourceKey));
        if (xp <= 0) throw new ArgumentOutOfRangeException(nameof(xp));
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        string json;
        lock (_sync)
        {
            if (!_entries.TryGetValue(sourceKey, out var e)) { e = new Entry(); _entries[sourceKey] = e; }
            e.BonusXp = Math.Max(0, e.BonusXp) + xp;
            e.Interactions++;
            e.LastUtc = DateTime.UtcNow;
            json = JsonSerializer.Serialize(_entries);
        }
        await PersistAsync(json, ct).ConfigureAwait(false);
        RaiseChanged();
        return CurrentXP;
    }

    private async Task PersistAsync(string json, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto] memory persist failed: " + ex.Message);   // [부분 구제] 메모리 내 가중치 유지
        }
        finally { _gate.Release(); }
    }

    /// <summary>80% 활용(가중치 내림차순 · 안정 정렬) + 20% 탐색(상위 2개 밖 최소 상호작용 카드 → 2번째 슬롯).</summary>
    public IReadOnlyList<PlutoTrend> Rank(IReadOnlyList<PlutoTrend> trends)
    {
        ArgumentNullException.ThrowIfNull(trends);
        var n = trends.Count;
        var list = new List<PlutoTrend>(n);
        for (var i = 0; i < n; i++) list.Add(trends[i]);
        if (n < 2) return list;

        var weights = new double[n];
        var counts = new int[n];
        var idx = new int[n];
        lock (_sync)
        {
            for (var i = 0; i < n; i++)
            {
                idx[i] = i;
                if (_entries.TryGetValue(list[i].Id, out var e)) { weights[i] = e.Weight; counts[i] = e.Interactions; }
            }
        }
        Array.Sort(idx, (a, b) => { var c = weights[b].CompareTo(weights[a]); return c != 0 ? c : a.CompareTo(b); });
        var ranked = new List<PlutoTrend>(n);
        for (var i = 0; i < n; i++) ranked.Add(list[idx[i]]);

        double roll;
        lock (_rng) roll = _rng.NextDouble();
        if (roll < ExplorationRate)
        {
            var pick = -1;   // 상위 2개 밖에서 가장 덜 노출된(신선한) 카드
            for (var i = 2; i < n; i++)
                if (pick < 0 || counts[idx[i]] < counts[idx[pick]]) pick = i;
            if (pick >= 2)
            {
                var t = ranked[pick];
                ranked.RemoveAt(pick);
                ranked.Insert(1, t);
            }
        }
        return ranked;
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto] Changed handler: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
