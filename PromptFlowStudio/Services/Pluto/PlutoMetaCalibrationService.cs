using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🎯 메타 보정 관측 1건 — 합성 점수 0~1 척도. 예측 불가(기준선 없음) → <see cref="PredictedExpectedScore"/> = NaN(보정 오차 미집계),
/// 대안 없음 → <see cref="BestAlternativeExpectedScore"/> = NaN(후회 미집계).
/// </summary>
public sealed record PlutoCalibrationObservation(double PredictedExpectedScore, double RealizedScore, double BestAlternativeExpectedScore)
{
    /// <summary>대안 라벨(예: <c>hook:…</c> · A/B 상대 변형) — 후회 귀속 집계용(없음 → null).</summary>
    public string? BestAlternativeLabel { get; init; }
    /// <summary>출처(<c>closed-loop</c> · <c>ab</c>).</summary>
    public string? Source { get; init; }

    public bool HasPrediction => double.IsFinite(PredictedExpectedScore) && double.IsFinite(RealizedScore);
    public bool HasAlternative => double.IsFinite(BestAlternativeExpectedScore) && double.IsFinite(RealizedScore);
    /// <summary>반사실 후회 = <c>max(0, 최선 대안 − 실현)</c>(대안 없음 → 0).</summary>
    public double Regret => HasAlternative ? Math.Max(0.0, BestAlternativeExpectedScore - RealizedScore) : 0.0;
}

/// <summary>
/// 🎯 반사실 예측 스냅샷 — 기준선 · 선택 구성 예측 점수 · 최선 미선택 대안 예측 점수(없음 → NaN) · 대안 라벨(<c>hook:…</c>/<c>look:…</c>) ·
/// 예측에 참여한 성립 패턴 수.
/// </summary>
public sealed record PlutoExpectancyForecast(bool HasBaseline, double Baseline, double PredictedScore, double BestAlternativeScore, string? BestAlternativeLabel, int EstablishedPatterns)
{
    public static readonly PlutoExpectancyForecast None = new(false, 0.0, double.NaN, double.NaN, null, 0);
    public bool HasAlternative => HasBaseline && double.IsFinite(BestAlternativeScore);
}

/// <summary>💸 후회 귀속 — 선택되지 않은 대안별 누적 반사실 후회.</summary>
public sealed record PlutoRegretAttribution(string Label, double CumulativeRegret, int Count);

/// <summary>🎯 메타 보정 현황 스냅샷(두뇌 리포트 · 랭커 공용).</summary>
public sealed record PlutoCalibrationStatus(int CalibrationSamples, double BrierScore, double Rmse, double Bias, int RegretSamples, double MeanRegret,
    double CumulativeRegret, int TotalRecords, double ExplorationScale)
{
    public static readonly PlutoCalibrationStatus Empty = new(0, 0.0, 0.0, 0.0, 0, 0.0, 0.0, 0, 1.0);

    /// <summary>💸 누적 후회가 가장 큰 미선택 대안(없음 → null).</summary>
    public PlutoRegretAttribution? TopRegretAlternative { get; init; }
    /// <summary>보정 정확도 0~100(= 100 × (1 − RMSE) · 표본 0 → 0).</summary>
    public double AccuracyPct => CalibrationSamples > 0 ? Math.Round(Math.Clamp(1.0 - Rmse, 0.0, 1.0) * 100.0, 1, MidpointRounding.AwayFromZero) : 0.0;
    /// <summary>판정에 충분한 표본.</summary>
    public bool HasEvidence => CalibrationSamples >= PlutoMetaCalibrationService.MinSamples || RegretSamples >= PlutoMetaCalibrationService.MinSamples;
    /// <summary>과신(예측 &gt; 실현 편향이 허용 폭 초과).</summary>
    public bool IsOverconfident => CalibrationSamples >= PlutoMetaCalibrationService.MinSamples && Bias > PlutoMetaCalibrationService.BiasDeadBand;
    /// <summary>탐색 증폭 중(배수 &gt; 1).</summary>
    public bool IsAmplifying => ExplorationScale > 1.0 + 1e-9;
    /// <summary>활용 집중 중(배수 &lt; 1).</summary>
    public bool IsTightening => ExplorationScale < 1.0 - 1e-9;
}

/// <summary>
/// 🎯 Pluto 메타 보정 — 예측 기대 점수 vs 실현 점수(Brier/RMSE · 편향)와 반사실 후회(선택하지 않은 최선 대안 대비 기회 비용)를 감사해
/// UCB1 탐색 상수의 메타 배수를 산출한다.
/// </summary>
public interface IPlutoMetaCalibrationService
{
    /// <summary>상태 선적재(멱등 · 실패 → 빈 상태로 [부분 구제] · 취소만 전파).</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>관측 1건 기록(원자 영속 · 영속 실패 → 메모리 유지). 예측 · 대안이 모두 없으면 무변경.</summary>
    Task<PlutoCalibrationStatus> RecordOutcomeAsync(PlutoCalibrationObservation observation, CancellationToken ct = default);

    /// <summary>현재 보정 현황(미적재 → <see cref="PlutoCalibrationStatus.Empty"/>).</summary>
    PlutoCalibrationStatus GetStatus();

    /// <summary>
    /// 🎚️ 메타 보정 탐색 배수 — 과신 · 후회 상승 → 1 초과(최대 <see cref="PlutoMetaCalibrationService.MaxScale"/>) ·
    /// 보정 양호 + 후회 낮음 → 1 미만(최소 <see cref="PlutoMetaCalibrationService.MinScale"/>) · 표본 부족 → 1.
    /// </summary>
    double GetCalibratedExplorationScale();

    /// <summary>기록 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>
/// 🎯 <see cref="IPlutoMetaCalibrationService"/> 구현(순수 BCL · 결정론) — <c>pluto_calibration_state.json</c>(☁️ Drive 볼트 · 로컬 폴백 · 원자 기록).
///   ▸ 롤링 창 = 최근 <see cref="WindowCapacity"/>건(보정 오차 · 평균 후회) · 누적 후회 · 대안별 후회 귀속은 창과 무관하게 영속.
///   ▸ Brier = mean((예측 − 실현)²) · RMSE = √Brier · 편향 = mean(예측 − 실현)(양수 = 과신).
///   ▸ 압력 = max(과신 압력, 후회 압력) — 각각 허용 폭(dead band) 초과분을 선형 정규화 → 배수 = 1 + (Max − 1) × 압력.
///   ▸ 압력 0 + RMSE ≤ <see cref="WellCalibratedRmse"/> → 표본 수에 비례해 <see cref="MinScale"/> 쪽으로 수축(고신뢰 활용).
///   ▸ 표본 &lt; <see cref="MinSamples"/> → 1.0(기존 동작과 완전 동일).
/// 적재 불가(잠금·권한) 세션은 디스크 덮어쓰기 금지.
/// </summary>
public sealed class PlutoMetaCalibrationService : IPlutoMetaCalibrationService, IDisposable
{
    public const string FileName = "pluto_calibration_state.json";
    public const int WindowCapacity = 64;
    public const int MinSamples = 3;
    /// <summary>수축(활용 집중)이 최대에 이르는 표본 수.</summary>
    public const int FullConfidenceSamples = 10;
    public const double MinScale = 0.6, MaxScale = 2.0;
    /// <summary>과신 편향 허용 폭 · 최대 압력 지점(합성 점수 0~1).</summary>
    public const double BiasDeadBand = 0.04, BiasFull = 0.15;
    /// <summary>평균 후회 허용 폭 · 최대 압력 지점.</summary>
    public const double RegretDeadBand = 0.03, RegretFull = 0.15;
    public const double WellCalibratedRmse = 0.08;
    public const int MaxAttributions = 32;
    public const int MaxLabelLength = 120;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _path;
    private readonly IPlutoStorageManager? _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<RecordDto> _window = new(WindowCapacity);
    private readonly Dictionary<string, AttributionDto> _attributions = new(StringComparer.Ordinal);
    private double _cumulativeRegret;
    private int _totalRecords;
    private volatile bool _loaded;
    private volatile bool _readOnly;
    private int _disposed;

    /// <summary>☁️ <see cref="IPlutoStorageManager"/> 경유 — Drive 우선 · 로컬 폴백.</summary>
    public PlutoMetaCalibrationService(IPlutoStorageManager storage) : this((string?)null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public PlutoMetaCalibrationService(string? storagePath = null)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", FileName)
            : storagePath;
    }

    /// <summary>🕰️ 결정론 시계 주입(테스트용).</summary>
    public Func<DateTime>? UtcNowProvider { get; init; }

    public string StoragePath => _storage?.ResolveDataPath(FileName) ?? _path;

    /// <summary>🛡️ 적재 불가 세션(디스크 보존 · 메모리만 갱신).</summary>
    public bool IsReadOnlySession => _readOnly;

    public event Action? Changed;

    /// <summary>직렬화 DTO — 관측 1건(NaN 은 null 로 영속).</summary>
    public sealed class RecordDto
    {
        public double? P { get; set; }
        public double R { get; set; }
        public double? A { get; set; }
        public DateTime Utc { get; set; }
        public string? Src { get; set; }
    }

    public sealed class AttributionDto
    {
        public double Regret { get; set; }
        public int Count { get; set; }
    }

    /// <summary>영속 스냅샷(버전 필드로 향후 스키마 확장 · 미지 필드는 무시되어 상·하위 호환).</summary>
    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public double CumulativeRegret { get; set; }
        public int TotalRecords { get; set; }
        public List<RecordDto>? Window { get; set; }
        public Dictionary<string, AttributionDto>? Attributions { get; set; }
    }

    // ── 🧮 순수 함수 ──

    /// <summary>Brier(평균 제곱 오차) — 유한 쌍만 집계 · 표본 0 → (0, 0, 0).</summary>
    public static (double Brier, double Bias, int Samples) ComputeCalibration(IReadOnlyList<(double Predicted, double Realized)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        double sq = 0, bias = 0; var n = 0;
        for (var i = 0; i < pairs.Count; i++)
        {
            var (p, r) = pairs[i];
            if (!double.IsFinite(p) || !double.IsFinite(r)) continue;
            var d = Clamp01(p) - Clamp01(r);
            sq += d * d; bias += d; n++;
        }
        return n == 0 ? (0.0, 0.0, 0) : (sq / n, bias / n, n);
    }

    /// <summary>
    /// 🎚️ 메타 탐색 배수(순수 함수) — 과신/후회 압력 → 증폭 · 양호 보정 → 수축 · 표본 부족 → 1.
    /// </summary>
    public static double ComputeExplorationScale(int calibrationSamples, double rmse, double bias, int regretSamples, double meanRegret)
    {
        var over = calibrationSamples >= MinSamples && double.IsFinite(bias) ? Ramp(bias, BiasDeadBand, BiasFull) : 0.0;
        var regret = regretSamples >= MinSamples && double.IsFinite(meanRegret) ? Ramp(meanRegret, RegretDeadBand, RegretFull) : 0.0;
        var pressure = Math.Max(over, regret);
        if (pressure > 0) return 1.0 + (MaxScale - 1.0) * pressure;
        // 수축은 예측 근거(보정 표본)가 있을 때만 — 후회 표본만으로는 '예측이 맞았다'고 말할 수 없다
        if (calibrationSamples >= MinSamples && double.IsFinite(rmse) && rmse <= WellCalibratedRmse)
        {
            var confidence = Math.Clamp((calibrationSamples - (MinSamples - 1)) / (double)(FullConfidenceSamples - (MinSamples - 1)), 0.0, 1.0);
            return 1.0 - (1.0 - MinScale) * confidence;
        }
        return 1.0;
    }

    private static double Ramp(double v, double deadBand, double full) => Math.Clamp((v - deadBand) / (full - deadBand), 0.0, 1.0);
    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);

    /// <summary>🎯 보정 상태 1줄(결정론 한국어).</summary>
    public static string BuildCalibrationLabel(PlutoCalibrationStatus? s)
    {
        if (s is null || s.CalibrationSamples == 0) return "기록 없음 · 예측 " + MinSamples.ToString(Inv) + "건부터 보정";
        var verdict = s.CalibrationSamples < MinSamples ? "수집 중" : s.IsOverconfident ? "과신" : s.Bias < -BiasDeadBand ? "과소 예측" : s.Rmse <= WellCalibratedRmse ? "양호" : "편차 큼";
        return verdict + " · 정확도 " + s.AccuracyPct.ToString("0.#", Inv) + "% · RMSE " + s.Rmse.ToString("0.000", Inv) + " · Brier " + s.BrierScore.ToString("0.000", Inv)
            + " · 편향 " + (s.Bias >= 0 ? "+" : "−") + Math.Abs(s.Bias).ToString("0.000", Inv) + " · n=" + s.CalibrationSamples.ToString(Inv)
            + " · 탐색 배수 ×" + s.ExplorationScale.ToString("0.00", Inv) + (s.IsAmplifying ? "(증폭)" : s.IsTightening ? "(활용 집중)" : "");
    }

    /// <summary>💸 반사실 후회 상태 1줄(결정론 한국어).</summary>
    public static string BuildRegretLabel(PlutoCalibrationStatus? s)
    {
        if (s is null || s.RegretSamples == 0) return "기록 없음";
        var label = "평균 " + s.MeanRegret.ToString("0.000", Inv) + " · 누적 " + s.CumulativeRegret.ToString("0.00", Inv) + " · n=" + s.RegretSamples.ToString(Inv)
            + (s.MeanRegret > RegretDeadBand && s.RegretSamples >= MinSamples ? " · 상승(탐색 증폭)" : " · 낮음");
        if (s.TopRegretAlternative is { } top && top.CumulativeRegret > 0)
            label += " · 가장 아쉬운 대안: " + top.Label + "(" + top.CumulativeRegret.ToString("0.00", Inv) + " · " + top.Count.ToString(Inv) + "회)";
        return label;
    }

    // ── 💾 상태 ──

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsureLoadedLockedAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedLockedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.MetaCalibration", ct).ConfigureAwait(false);
        _readOnly = status == PlutoLoadStatus.Unreadable;
        lock (_sync)
        {
            _window.Clear();
            _attributions.Clear();
            _cumulativeRegret = 0;
            _totalRecords = 0;
            if (data is not null)
            {
                if (data.Window is { } w)
                {
                    var start = Math.Max(0, w.Count - WindowCapacity);
                    for (var i = start; i < w.Count; i++)
                    {
                        var r = w[i];
                        if (r is null || !double.IsFinite(r.R)) continue;
                        _window.Add(new RecordDto { P = Finite(r.P), R = Clamp01(r.R), A = Finite(r.A), Utc = r.Utc, Src = Clip(r.Src) });
                    }
                }
                if (data.Attributions is { } attrs)
                    foreach (var kv in attrs)
                    {
                        if (_attributions.Count >= MaxAttributions) break;
                        if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null || !double.IsFinite(kv.Value.Regret) || kv.Value.Regret < 0) continue;
                        _attributions[Clip(kv.Key)!] = new AttributionDto { Regret = kv.Value.Regret, Count = Math.Max(0, kv.Value.Count) };
                    }
                _cumulativeRegret = double.IsFinite(data.CumulativeRegret) && data.CumulativeRegret > 0 ? data.CumulativeRegret : 0.0;
                _totalRecords = Math.Max(Math.Max(0, data.TotalRecords), _window.Count);
            }
        }
        _loaded = true;
    }

    private static double? Finite(double? v) => v is { } x && double.IsFinite(x) ? Clamp01(x) : null;

    private static string? Clip(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var v = s.Trim();
        return v.Length > MaxLabelLength ? v[..MaxLabelLength] : v;
    }

    public async Task<PlutoCalibrationStatus> RecordOutcomeAsync(PlutoCalibrationObservation observation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ct.ThrowIfCancellationRequested();
        if (!observation.HasPrediction && !observation.HasAlternative) return GetStatus();   // 감사할 근거 없음
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            string json;
            lock (_sync)
            {
                var rec = new RecordDto
                {
                    P = observation.HasPrediction ? Clamp01(observation.PredictedExpectedScore) : null,
                    R = Clamp01(observation.RealizedScore),
                    A = observation.HasAlternative ? Clamp01(observation.BestAlternativeExpectedScore) : null,
                    Utc = UtcNowProvider?.Invoke() ?? DateTime.UtcNow,
                    Src = Clip(observation.Source),
                };
                _window.Add(rec);
                if (_window.Count > WindowCapacity) _window.RemoveRange(0, _window.Count - WindowCapacity);
                _totalRecords = _totalRecords == int.MaxValue ? int.MaxValue : _totalRecords + 1;
                if (rec.A is { } alt)
                {
                    var regret = Math.Max(0.0, alt - rec.R);
                    _cumulativeRegret += regret;
                    if (regret > 0 && Clip(observation.BestAlternativeLabel) is { } label) Attribute(label, regret);
                }
                json = JsonSerializer.Serialize(new Snapshot
                {
                    CumulativeRegret = _cumulativeRegret, TotalRecords = _totalRecords, Window = new List<RecordDto>(_window),
                    Attributions = new Dictionary<string, AttributionDto>(_attributions, StringComparer.Ordinal),
                }, JsonOpts);
            }
            if (!_readOnly)
            {
                // 호출자 취소가 기록 도중 끼어들어도 상태는 이미 메모리에 반영됨 → 영속은 끝까지 수행
                if (_storage is not null) await _storage.WriteAllTextAtomicAsync(FileName, json, CancellationToken.None).ConfigureAwait(false);
                else await PlutoJsonStore.WriteAtomicAsync(_path, json, "Pluto.MetaCalibration", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return GetStatus();
    }

    /// <summary>대안별 후회 누적 — 용량 초과 시 누적 후회 최소(동률 → 키 사전순) 항목 축출(결정론).</summary>
    private void Attribute(string label, double regret)
    {
        if (!_attributions.TryGetValue(label, out var a))
        {
            if (_attributions.Count >= MaxAttributions)
            {
                string? victim = null; var min = double.MaxValue;
                foreach (var kv in _attributions)
                    if (kv.Value.Regret < min || (kv.Value.Regret == min && string.CompareOrdinal(kv.Key, victim) < 0)) { min = kv.Value.Regret; victim = kv.Key; }
                if (victim is not null) _attributions.Remove(victim);
            }
            a = new AttributionDto();
            _attributions[label] = a;
        }
        a.Regret += regret;
        a.Count = a.Count == int.MaxValue ? int.MaxValue : a.Count + 1;
    }

    public PlutoCalibrationStatus GetStatus()
    {
        lock (_sync)
        {
            if (_window.Count == 0 && _totalRecords == 0) return PlutoCalibrationStatus.Empty;
            double sq = 0, bias = 0, regretSum = 0; int n = 0, rn = 0;
            for (var i = 0; i < _window.Count; i++)
            {
                var r = _window[i];
                if (r.P is { } p) { var d = p - r.R; sq += d * d; bias += d; n++; }
                if (r.A is { } a) { regretSum += Math.Max(0.0, a - r.R); rn++; }
            }
            var brier = n > 0 ? sq / n : 0.0;
            var rmse = Math.Sqrt(brier);
            var meanBias = n > 0 ? bias / n : 0.0;
            var meanRegret = rn > 0 ? regretSum / rn : 0.0;
            PlutoRegretAttribution? top = null;
            foreach (var kv in _attributions)
                if (top is null || kv.Value.Regret > top.CumulativeRegret || (kv.Value.Regret == top.CumulativeRegret && string.CompareOrdinal(kv.Key, top.Label) < 0))
                    top = new PlutoRegretAttribution(kv.Key, kv.Value.Regret, kv.Value.Count);
            return new PlutoCalibrationStatus(n, brier, rmse, meanBias, rn, meanRegret, _cumulativeRegret, _totalRecords,
                ComputeExplorationScale(n, rmse, meanBias, rn, meanRegret)) { TopRegretAlternative = top };
        }
    }

    public double GetCalibratedExplorationScale() => GetStatus().ExplorationScale;

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.MetaCalibration] Changed handler: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
