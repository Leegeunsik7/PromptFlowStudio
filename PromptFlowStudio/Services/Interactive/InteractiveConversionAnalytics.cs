using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PromptFlowStudio.Services.Interactive;

/// <summary>v16.26.0 인터랙티브 이벤트 종류.</summary>
public enum InteractiveEventKind : byte { ChoiceShown, ChoiceMade, ChoiceAbandon, HotspotImpression, HotspotClick, Conversion }

/// <summary>v16.26.0 분기 지점 집계 스냅샷 — 노출 · 선택(옵션별) · 이탈 · 무응답(기본값 진행).</summary>
public sealed record ChoicePointStats(string NodeId, long Shown, long Abandoned, long[] Picks)
{
    public long Decided { get { long s = 0; foreach (var p in Picks) s += p; return s; } }
    /// <summary>선택 비율(0~1 · 결정 0이면 균등 0).</summary>
    public double Ratio(int option) => (uint)option < (uint)Picks.Length && Decided > 0 ? (double)Picks[option] / Decided : 0;
    /// <summary>분기 지점 이탈률(노출 대비).</summary>
    public double DropOffRate => Shown > 0 ? Math.Min(1, (double)Abandoned / Shown) : 0;
}

/// <summary>v16.26.0 핫스팟(상품 태그) 집계 스냅샷 — 노출 · 클릭 · 구매 전환 · 매출.</summary>
public sealed record HotspotStats(string TagId, long Impressions, long Clicks, long Conversions, double Revenue)
{
    public double Ctr => Impressions > 0 ? Math.Min(1, (double)Clicks / Impressions) : 0;
    /// <summary>클릭 대비 구매 전환율.</summary>
    public double Cvr => Clicks > 0 ? Math.Min(1, (double)Conversions / Clicks) : 0;
}

/// <summary>
/// [PromptFlowStudio] v16.26.0 📊 인터랙티브 선택지 &amp; 쇼핑 전환율 분석기 — A/B 선택 비율 · 분기 지점 이탈률 · 핫스팟 CTR · 구매 전환율 실시간 집계.
///   ▸ 기록(<see cref="Record"/>)은 라이브 투표/플레이어 이벤트 핫패스 — 키 사전 조회 + 고정 배열 카운터 증가만(문자열 생성 · 박싱 · LINQ 0 · 기존 키 0B 테스트 고정).
///   ▸ 키 상한 <see cref="MaxKeys"/>(초과 신규 키는 거부 · 카운트) · 옵션 상한 4 · 구독자 알림은 250ms 스로틀(예외 격리).
///   ▸ 플레이어(player.html) 이벤트 로그 JSON 가져오기(스키마 즉시 검증 · 불량 행 건너뜀) · CSV/JSON 리포트 내보내기.
/// </summary>
public sealed class InteractiveConversionAnalytics
{
    public const int MaxOptions = 4;
    public const int MaxKeys = 512;
    public const int MaxImportEvents = 200_000;

    private sealed class Choice
    {
        public long Shown, Abandoned;
        public readonly long[] Picks = new long[MaxOptions];
    }

    private sealed class Hotspot
    {
        public long Impressions, Clicks, Conversions;
        public double Revenue;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Choice> _choices = new(64, StringComparer.Ordinal);
    private readonly Dictionary<string, Hotspot> _hotspots = new(64, StringComparer.Ordinal);
    private long _events, _rejected, _lastRaise;

    /// <summary>집계 변경 방송(250ms 스로틀).</summary>
    public event Action? OnChange;

    public long TotalEvents => Interlocked.Read(ref _events);
    public long RejectedEvents => Interlocked.Read(ref _rejected);
    public string? LastAction { get; private set; }

    // ══════════════════════ 기록(핫패스) ══════════════════════

    /// <summary>
    /// 이벤트 1건 기록(스레드 안전 · 기존 키 무할당). <paramref name="option"/>은 ChoiceMade 전용(0~3), <paramref name="value"/>는 Conversion 매출.
    /// 반환: 수락 여부(키 상한/옵션 범위 초과 시 false).
    /// </summary>
    public bool Record(InteractiveEventKind kind, string key, int option = -1, double value = 0)
    {
        if (string.IsNullOrEmpty(key)) { Interlocked.Increment(ref _rejected); return false; }
        lock (_gate)
        {
            switch (kind)
            {
                case InteractiveEventKind.ChoiceShown:
                case InteractiveEventKind.ChoiceMade:
                case InteractiveEventKind.ChoiceAbandon:
                {
                    if (!_choices.TryGetValue(key, out var c))
                    {
                        if (_choices.Count >= MaxKeys) { Interlocked.Increment(ref _rejected); return false; }
                        c = new Choice();
                        _choices.Add(key, c);
                    }
                    if (kind == InteractiveEventKind.ChoiceShown) c.Shown++;
                    else if (kind == InteractiveEventKind.ChoiceAbandon) c.Abandoned++;
                    else
                    {
                        if ((uint)option >= MaxOptions) { Interlocked.Increment(ref _rejected); return false; }
                        c.Picks[option]++;
                    }
                    break;
                }
                default:
                {
                    if (!_hotspots.TryGetValue(key, out var h))
                    {
                        if (_hotspots.Count >= MaxKeys) { Interlocked.Increment(ref _rejected); return false; }
                        h = new Hotspot();
                        _hotspots.Add(key, h);
                    }
                    if (kind == InteractiveEventKind.HotspotImpression) h.Impressions++;
                    else if (kind == InteractiveEventKind.HotspotClick) h.Clicks++;
                    else { h.Conversions++; if (value > 0 && double.IsFinite(value)) h.Revenue += value; }
                    break;
                }
            }
        }
        Interlocked.Increment(ref _events);
        Raise(false);
        return true;
    }

    // ══════════════════════ 조회 ══════════════════════

    public ChoicePointStats? ChoiceOf(string nodeId)
    {
        lock (_gate)
            return _choices.TryGetValue(nodeId, out var c) ? new(nodeId, c.Shown, c.Abandoned, (long[])c.Picks.Clone()) : null;
    }

    public HotspotStats? HotspotOf(string tagId)
    {
        lock (_gate)
            return _hotspots.TryGetValue(tagId, out var h) ? new(tagId, h.Impressions, h.Clicks, h.Conversions, h.Revenue) : null;
    }

    public List<ChoicePointStats> Choices()
    {
        lock (_gate)
        {
            var list = new List<ChoicePointStats>(_choices.Count);
            foreach (var kv in _choices) list.Add(new(kv.Key, kv.Value.Shown, kv.Value.Abandoned, (long[])kv.Value.Picks.Clone()));
            list.Sort(static (a, b) => string.CompareOrdinal(a.NodeId, b.NodeId));
            return list;
        }
    }

    public List<HotspotStats> Hotspots()
    {
        lock (_gate)
        {
            var list = new List<HotspotStats>(_hotspots.Count);
            foreach (var kv in _hotspots) list.Add(new(kv.Key, kv.Value.Impressions, kv.Value.Clicks, kv.Value.Conversions, kv.Value.Revenue));
            list.Sort(static (a, b) => b.Clicks.CompareTo(a.Clicks));
            return list;
        }
    }

    /// <summary>전체 핫스팟 CTR/CVR(가중 평균).</summary>
    public (double Ctr, double Cvr, double Revenue) Totals()
    {
        long imp = 0, clk = 0, conv = 0; double rev = 0;
        lock (_gate)
            foreach (var h in _hotspots.Values) { imp += h.Impressions; clk += h.Clicks; conv += h.Conversions; rev += h.Revenue; }
        return (imp > 0 ? Math.Min(1, (double)clk / imp) : 0, clk > 0 ? Math.Min(1, (double)conv / clk) : 0, rev);
    }

    /// <summary>전체 분기 지점 평균 이탈률(노출 가중).</summary>
    public double OverallDropOff()
    {
        long shown = 0, ab = 0;
        lock (_gate) foreach (var c in _choices.Values) { shown += c.Shown; ab += c.Abandoned; }
        return shown > 0 ? Math.Min(1, (double)ab / shown) : 0;
    }

    public void Reset()
    {
        lock (_gate) { _choices.Clear(); _hotspots.Clear(); }
        Interlocked.Exchange(ref _events, 0);
        Interlocked.Exchange(ref _rejected, 0);
        LastAction = "집계 초기화";
        Raise(true);
    }

    // ══════════════════════ 가져오기 / 내보내기 ══════════════════════

    /// <summary>
    /// 플레이어 이벤트 로그 가져오기 — <c>{"events":[{"k":"shown|pick|abandon|imp|click|buy","id":"...","o":0,"v":0}]}</c> 또는 배열 그대로.
    /// 스키마 불일치 시 null, 행 단위 불량은 건너뜀. 반환: (수락, 거부).
    /// </summary>
    public (int Accepted, int Skipped)? ImportEventLog(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array ? ev : default;
            if (arr.ValueKind != JsonValueKind.Array) return null;
            int ok = 0, skip = 0;
            foreach (var e in arr.EnumerateArray())
            {
                if (ok + skip >= MaxImportEvents) break;
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("k", out var k) || k.ValueKind != JsonValueKind.String
                    || !e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) { skip++; continue; }
                var kind = KindOf(k.GetString());
                var key = id.GetString() ?? "";
                if (kind is null || key.Length is 0 or > 64) { skip++; continue; }
                var o = e.TryGetProperty("o", out var oe) && oe.ValueKind == JsonValueKind.Number && oe.TryGetInt32(out var oi) ? oi : -1;
                var v = e.TryGetProperty("v", out var ve) && ve.ValueKind == JsonValueKind.Number && ve.TryGetDouble(out var vd) ? vd : 0;
                if (Record(kind.Value, key, o, v)) ok++; else skip++;
            }
            LastAction = "📥 이벤트 로그 " + ok.ToString(CultureInfo.InvariantCulture) + "건 반영";
            Raise(true);
            return (ok, skip);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>플레이어 이벤트 약어 → 종류.</summary>
    public static InteractiveEventKind? KindOf(string? k) => k switch
    {
        "shown" => InteractiveEventKind.ChoiceShown, "pick" => InteractiveEventKind.ChoiceMade, "abandon" => InteractiveEventKind.ChoiceAbandon,
        "imp" => InteractiveEventKind.HotspotImpression, "click" => InteractiveEventKind.HotspotClick, "buy" => InteractiveEventKind.Conversion,
        _ => null,
    };

    /// <summary>CSV 리포트(분기 + 핫스팟 · InvariantCulture).</summary>
    public string BuildCsv()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(1024);
        sb.Append("type,id,shown_or_impressions,decided_or_clicks,option_a,option_b,option_c,option_d,dropoff_or_ctr,conversions,cvr,revenue\n");
        foreach (var c in Choices())
        {
            sb.Append("choice,").Append(Csv(c.NodeId)).Append(',').Append(c.Shown.ToString(inv)).Append(',').Append(c.Decided.ToString(inv));
            for (var i = 0; i < MaxOptions; i++) sb.Append(',').Append(c.Ratio(i).ToString("0.####", inv));
            sb.Append(',').Append(c.DropOffRate.ToString("0.####", inv)).Append(",,,\n");
        }
        foreach (var h in Hotspots())
        {
            sb.Append("hotspot,").Append(Csv(h.TagId)).Append(',').Append(h.Impressions.ToString(inv)).Append(',').Append(h.Clicks.ToString(inv))
              .Append(",,,,,").Append(h.Ctr.ToString("0.####", inv)).Append(',').Append(h.Conversions.ToString(inv)).Append(',')
              .Append(h.Cvr.ToString("0.####", inv)).Append(',').Append(h.Revenue.ToString("0.##", inv)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>CSV 를 폴더에 저장 → 경로(실패 null · 예외 0).</summary>
    public string? ExportCsv(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "interactive_report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
            File.WriteAllText(path, "﻿" + BuildCsv(), new UTF8Encoding(false));
            LastAction = "📤 리포트 저장";
            Raise(true);
            return path;
        }
        catch (Exception ex) { Program.Log("InteractiveAnalytics.Export", ex); return null; }
    }

    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastRaise) < 250) return;
        Interlocked.Exchange(ref _lastRaise, now);
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("InteractiveAnalytics.OnChange", ex); }
        }
    }
}
