using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using PromptFlowStudio.Services.Interactive;

namespace PromptFlowStudio.Services.Spatial;

public enum SpatialAnchorKind : byte { ShoppableTag, ChoiceOption }

public enum SpatialGesture : byte { Pinch, DoubleTap, SwipeLeft, SwipeRight }

/// <summary>시선 레이 1샘플(원점 · 방향 · 단조 시각 ms) — 헤드셋 좌표(미터 · x 우 / y 상 / +z 전방).</summary>
public readonly record struct GazeSample(float Ox, float Oy, float Oz, float Dx, float Dy, float Dz, long TimestampMs);

/// <summary>공간 오버레이 앵커(공간 좌표 미터 · 반경 = 시선 히트 구).</summary>
public readonly record struct SpatialAnchor(SpatialAnchorKind Kind, string Key, string Label, int Option, float X, float Y, float Z, float Radius, string? Url);

/// <summary>활성화 이벤트(핀치/드웰).</summary>
public sealed record SpatialActivation(SpatialAnchorKind Kind, string Key, string Label, int Option, string? Url, bool ByDwell, bool Accepted);

/// <summary>
/// [PromptFlow Studio v16.31.0] 비전 프로 제스처 &amp; 공간 오버레이 브릿지.
///   ▸ v16.26.0 쇼핑 태그(키프레임 보간 2D 박스) → 깊이 맵 샘플(3×3 중앙값) 또는 수동 깊이로 언프로젝션해 3D 플로팅 앵커 배치.
///   ▸ 분기 선택지(투표 중 노드) → 시청자 전방 1.2m 원호 패널(14° 간격) 배치.
///   ▸ 시선 추적: 방향 지수 평활 → 레이-구 교차 최근접 히트 → 드웰(기본 650ms) 자동 활성화 · 최초 응시 시 노출(Impression) 기록.
///   ▸ 제스처: 핀치/더블탭 = 응시 대상 활성화(visionOS look+pinch), 스와이프 = 선택지 포커스 순환.
///   ▸ 활성화: 쇼핑 = 안전 URL 검증 + HotspotClick 기록, 선택지 = 분기 서비스 Vote(분석 자동 기록).
///   ▸ 시선 핫패스(<see cref="ProcessGaze"/>)는 고정 배열 순회만(LINQ/박싱/할당 0) · 대상 변경 시에만 OnChange.
///   ▸ <see cref="BuildUsda"/>: 앵커를 USD(ASCII) 씬으로 내보내 visionOS RealityKit 패키지에 동봉.
/// </summary>
public sealed class ImmersiveVisionProInteractionBridge
{
    public const int MaxAnchors = 64;
    public const float DefaultDepthMeters = 1.5f;
    public const float ChoiceDistance = 1.2f;
    public const float ChoiceSpacingDeg = 14f;
    public const float GazeSmoothing = 0.5f;
    public long DwellMs { get; set; } = 650;

    private readonly ShoppableVideoOverlayEngine? _shop;
    private readonly InteractiveChoiceBranchingService? _branch;
    private readonly InteractiveConversionAnalytics? _analytics;
    private readonly object _gate = new();
    private readonly SpatialAnchor[] _anchors = new SpatialAnchor[MaxAnchors];
    private readonly bool[] _seen = new bool[MaxAnchors];
    private readonly Dictionary<string, float> _depthOverride = new(StringComparer.Ordinal);
    private int _count, _gazed = -1, _focus = -1;
    private long _gazeStart, _gazeSamples, _activations, _dwellActivations, _impressions;
    private bool _dwellFired, _hasDir;
    private float _sdx, _sdy, _sdz;

    public ImmersiveVisionProInteractionBridge(ShoppableVideoOverlayEngine? shop, InteractiveChoiceBranchingService? branch, InteractiveConversionAnalytics? analytics)
    {
        _shop = shop;
        _branch = branch;
        _analytics = analytics;
    }

    public event Action? OnChange;
    public event Action<SpatialActivation>? OnActivated;

    public float HfovDeg { get; set; } = Spatial3dGaussianSplattingEngine.DefaultHfovDeg;
    public int AnchorCount { get { lock (_gate) return _count; } }
    public int GazedIndex => Volatile.Read(ref _gazed);
    public long GazeSamples => Interlocked.Read(ref _gazeSamples);
    public long Activations => Interlocked.Read(ref _activations);
    public long DwellActivations => Interlocked.Read(ref _dwellActivations);
    public long Impressions => Interlocked.Read(ref _impressions);
    public SpatialActivation? LastActivation { get; private set; }
    public string? LastAction { get; private set; }

    public IReadOnlyList<SpatialAnchor> Anchors
    {
        get { lock (_gate) return _anchors.AsSpan(0, _count).ToArray(); }
    }

    public SpatialAnchor? GazedAnchor
    {
        get { lock (_gate) return (uint)_gazed < (uint)_count ? _anchors[_gazed] : null; }
    }

    /// <summary>쇼핑 태그 수동 깊이(미터 · NaN/≤0 = 해제 → 깊이 맵 자동).</summary>
    public void SetDepthOverride(string tagId, float meters)
    {
        if (string.IsNullOrEmpty(tagId)) return;
        lock (_gate)
        {
            if (float.IsNaN(meters) || meters <= 0) _depthOverride.Remove(tagId);
            else _depthOverride[tagId] = Math.Clamp(meters, 0.3f, 10f);
        }
    }

    public float? DepthOverrideOf(string tagId) { lock (_gate) return _depthOverride.TryGetValue(tagId, out var m) ? m : null; }

    // ══════════════════════ 레이아웃 ══════════════════════

    /// <summary>t초 기준 앵커 재배치(활성 쇼핑 태그 + 투표 중 선택지). depth = 정규화 깊이 맵(선택). 반환 = 앵커 수.</summary>
    public int Layout(double t, float[]? depth, int dw, int dh)
    {
        var tags = _shop?.Tags ?? (IReadOnlyList<ShoppableTag>)Array.Empty<ShoppableTag>();
        var node = _branch is { State: ChoiceSessionState.Voting } b ? b.CurrentNode : null;
        lock (_gate)
        {
            _count = 0;
            var f = Spatial3dGaussianSplattingEngine.FocalPx(1000, HfovDeg) / 1000f;   // 정규화 초점(폭 1 기준)
            foreach (var tag in tags)
            {
                if (_count >= MaxAnchors) break;
                if (t < tag.StartSeconds || t > tag.EndSeconds || tag.Keyframes.Count == 0) continue;
                var box = ShoppableVideoOverlayEngine.Interpolate(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tag.Keyframes), t).Clamp();
                var z = _depthOverride.TryGetValue(tag.Id, out var ov) ? ov : SampleDepthMeters(depth, dw, dh, box.CenterX, box.CenterY);
                var aspect = dw > 0 && dh > 0 ? (float)dh / dw : 16f / 9f;
                var x = (box.CenterX - 0.5f) / f * z;
                var y = -(box.CenterY - 0.5f) * aspect / f * z;
                var r = MathF.Max(0.06f, box.W * 0.5f / f * z);
                _anchors[_count++] = new(SpatialAnchorKind.ShoppableTag, tag.Id, tag.ProductName, -1, x, y, z, r, tag.Url);
            }
            if (node is { IsChoicePoint: true })
            {
                var n = node.Options.Count;
                for (var i = 0; i < n && _count < MaxAnchors; i++)
                {
                    var a = (i - (n - 1) * 0.5f) * ChoiceSpacingDeg * (MathF.PI / 180f);
                    _anchors[_count++] = new(SpatialAnchorKind.ChoiceOption, node.Id, node.Options[i].Label, i,
                        ChoiceDistance * MathF.Sin(a), -0.25f, ChoiceDistance * MathF.Cos(a), 0.12f, null);
                }
            }
            Array.Clear(_seen);
            _gazed = -1; _focus = -1; _dwellFired = false;
        }
        LastAction = "📍 공간 앵커 " + AnchorCount.ToString(CultureInfo.InvariantCulture) + "개 배치";
        Raise();
        return AnchorCount;
    }

    /// <summary>정규화 좌표(u,v) 3×3 중앙값 깊이 → 미터(맵 없으면 기본 1.5m). 무할당.</summary>
    public static float SampleDepthMeters(ReadOnlySpan<float> depth, int w, int h, float u, float v)
    {
        if (w < 3 || h < 3 || depth.Length < w * h) return DefaultDepthMeters;
        var cx = Math.Clamp((int)(u * (w - 1)), 1, w - 2);
        var cy = Math.Clamp((int)(v * (h - 1)), 1, h - 2);
        Span<float> s = stackalloc float[9];
        var k = 0;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++) s[k++] = depth[(cy + dy) * w + cx + dx];
        s.Sort();
        return Spatial3dGaussianSplattingEngine.DepthToMeters(s[4]);
    }

    private static float SampleDepthMeters(float[]? depth, int w, int h, float u, float v)
        => depth is null ? DefaultDepthMeters : SampleDepthMeters(depth.AsSpan(), w, h, u, v);

    // ══════════════════════ 시선 ══════════════════════

    /// <summary>레이-구 교차 최근접 앵커(없으면 -1) · 무할당.</summary>
    public static int RaycastNearest(ReadOnlySpan<SpatialAnchor> anchors, float ox, float oy, float oz, float dx, float dy, float dz)
    {
        var best = -1;
        var bestT = float.PositiveInfinity;
        for (var i = 0; i < anchors.Length; i++)
        {
            ref readonly var a = ref anchors[i];
            float lx = a.X - ox, ly = a.Y - oy, lz = a.Z - oz;
            var tc = lx * dx + ly * dy + lz * dz;
            if (tc <= 0) continue;
            var d2 = lx * lx + ly * ly + lz * lz - tc * tc;
            var r2 = a.Radius * a.Radius;
            if (d2 > r2) continue;
            var th = tc - MathF.Sqrt(r2 - d2);
            if (th < bestT) { bestT = th; best = i; }
        }
        return best;
    }

    /// <summary>시선 샘플 처리(핫패스 · 무할당) — 평활 → 히트 → 드웰 활성화. 반환 = 응시 앵커 인덱스(-1 없음).</summary>
    public int ProcessGaze(in GazeSample g)
    {
        var len = MathF.Sqrt(g.Dx * g.Dx + g.Dy * g.Dy + g.Dz * g.Dz);
        if (len < 1e-6f) return Volatile.Read(ref _gazed);
        float dx = g.Dx / len, dy = g.Dy / len, dz = g.Dz / len;
        Interlocked.Increment(ref _gazeSamples);
        int hit, prev;
        var changed = false;
        var dwell = false;
        lock (_gate)
        {
            if (_hasDir)
            {
                _sdx += GazeSmoothing * (dx - _sdx); _sdy += GazeSmoothing * (dy - _sdy); _sdz += GazeSmoothing * (dz - _sdz);
                var sl = MathF.Sqrt(_sdx * _sdx + _sdy * _sdy + _sdz * _sdz);
                if (sl > 1e-6f) { _sdx /= sl; _sdy /= sl; _sdz /= sl; }
            }
            else { _sdx = dx; _sdy = dy; _sdz = dz; _hasDir = true; }
            hit = RaycastNearest(_anchors.AsSpan(0, _count), g.Ox, g.Oy, g.Oz, _sdx, _sdy, _sdz);
            prev = _gazed;
            if (hit != prev)
            {
                _gazed = hit;
                _gazeStart = g.TimestampMs;
                _dwellFired = false;
                changed = true;
                if (hit >= 0)
                {
                    _focus = hit;
                    if (!_seen[hit])
                    {
                        _seen[hit] = true;
                        if (_anchors[hit].Kind == SpatialAnchorKind.ShoppableTag)
                        {
                            Interlocked.Increment(ref _impressions);
                            _analytics?.Record(InteractiveEventKind.HotspotImpression, _anchors[hit].Key);
                        }
                    }
                }
            }
            else if (hit >= 0 && !_dwellFired && g.TimestampMs - _gazeStart >= DwellMs)
            {
                _dwellFired = true;
                dwell = true;
            }
        }
        if (dwell) Activate(hit, true);
        else if (changed) Raise();
        return hit;
    }

    /// <summary>제스처 — 핀치/더블탭 = 응시(없으면 포커스) 대상 활성화, 스와이프 = 선택지 포커스 순환.</summary>
    public SpatialActivation? ProcessGesture(SpatialGesture gesture)
    {
        if (gesture is SpatialGesture.Pinch or SpatialGesture.DoubleTap)
        {
            int idx;
            lock (_gate) idx = _gazed >= 0 ? _gazed : _focus;
            return idx >= 0 ? Activate(idx, false) : null;
        }
        lock (_gate)
        {
            var step = gesture == SpatialGesture.SwipeRight ? 1 : -1;
            var cur = _focus;
            for (var k = 1; k <= _count; k++)
            {
                var i = cur < 0 ? (step > 0 ? k - 1 : _count - k) : ((cur + step * k) % _count + _count) % _count;
                if (_anchors[i].Kind != SpatialAnchorKind.ChoiceOption) continue;
                _focus = i;
                break;
            }
        }
        Raise();
        return null;
    }

    public int FocusIndex => Volatile.Read(ref _focus);

    /// <summary>UI 시뮬레이션 — 원점에서 앵커 중심을 향한 시선 레이(드웰 없이 응시만).</summary>
    public int GazeAt(int index, long timestampMs)
    {
        SpatialAnchor a;
        lock (_gate)
        {
            if ((uint)index >= (uint)_count) return -1;
            a = _anchors[index];
            _hasDir = false;   // 시뮬레이션 점프 → 평활 초기화
        }
        return ProcessGaze(new GazeSample(0, 0, 0, a.X, a.Y, a.Z, timestampMs));
    }

    private SpatialActivation? Activate(int index, bool byDwell)
    {
        SpatialAnchor a;
        lock (_gate)
        {
            if ((uint)index >= (uint)_count) return null;
            a = _anchors[index];
        }
        bool accepted;
        if (a.Kind == SpatialAnchorKind.ShoppableTag)
        {
            accepted = ShoppableVideoOverlayEngine.IsSafeUrl(a.Url);
            if (accepted) _analytics?.Record(InteractiveEventKind.HotspotClick, a.Key);
        }
        else accepted = _branch?.Vote(a.Option) ?? false;
        var act = new SpatialActivation(a.Kind, a.Key, a.Label, a.Option, accepted ? a.Url : null, byDwell, accepted);
        Interlocked.Increment(ref _activations);
        if (byDwell) Interlocked.Increment(ref _dwellActivations);
        LastActivation = act;
        LastAction = (accepted ? "✋ " : "⛔ ") + (a.Kind == SpatialAnchorKind.ShoppableTag ? "쇼핑 · " : "선택지 · ") + a.Label + (byDwell ? " (드웰)" : " (핀치)");
        try { OnActivated?.Invoke(act); } catch (Exception ex) { Program.Log("VisionBridge.OnActivated", ex); }
        Raise();
        return act;
    }

    // ══════════════════════ USD 씬 ══════════════════════

    /// <summary>앵커 → USD ASCII 씬(루트 Xform + 비디오 스크린 + 앵커별 Xform/translate · 커스텀 메타). 비디오 파일명은 상대 asset 경로.</summary>
    public string BuildUsda(string videoFileName)
    {
        var anchors = Anchors;
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(512 + anchors.Count * 256);
        sb.Append("#usda 1.0\n(\n    defaultPrim = \"SpatialScene\"\n    metersPerUnit = 1\n    upAxis = \"Y\"\n    doc = \"PromptFlow Studio v16.31.0 spatial scene\"\n)\n\n");
        sb.Append("def Xform \"SpatialScene\"\n{\n");
        sb.Append("    def Xform \"VideoScreen\"\n    {\n        custom asset pfs:video = @./").Append(UsdAsset(videoFileName)).Append("@\n");
        sb.Append("        custom float pfs:hfovDeg = ").Append(HfovDeg.ToString("0.###", ci)).Append('\n');
        sb.Append("        double3 xformOp:translate = (0, 0, -2)\n        uniform token[] xformOpOrder = [\"xformOp:translate\"]\n    }\n");
        for (var i = 0; i < anchors.Count; i++)
        {
            var a = anchors[i];
            sb.Append("\n    def Xform \"").Append(a.Kind == SpatialAnchorKind.ShoppableTag ? "Hotspot_" : "Choice_").Append(i.ToString(ci)).Append("\"\n    {\n");
            sb.Append("        custom string pfs:kind = \"").Append(a.Kind == SpatialAnchorKind.ShoppableTag ? "shoppable" : "choice").Append("\"\n");
            sb.Append("        custom string pfs:key = \"").Append(UsdString(a.Key)).Append("\"\n");
            sb.Append("        custom string pfs:label = \"").Append(UsdString(a.Label)).Append("\"\n");
            if (a.Kind == SpatialAnchorKind.ChoiceOption) sb.Append("        custom int pfs:option = ").Append(a.Option.ToString(ci)).Append('\n');
            if (a.Url is not null && ShoppableVideoOverlayEngine.IsSafeUrl(a.Url)) sb.Append("        custom string pfs:url = \"").Append(UsdString(a.Url)).Append("\"\n");
            sb.Append("        custom float pfs:radius = ").Append(a.Radius.ToString("0.###", ci)).Append('\n');
            // RealityKit(-Z 전방) 좌표계로 변환
            sb.Append("        double3 xformOp:translate = (").Append(Coord(a.X)).Append(", ").Append(Coord(a.Y)).Append(", ").Append(Coord(-a.Z)).Append(")\n");
            sb.Append("        uniform token[] xformOpOrder = [\"xformOp:translate\"]\n    }\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    private static string Coord(float v) => (v == 0 ? 0f : v).ToString("0.####", CultureInfo.InvariantCulture);   // -0 → 0

    /// <summary>USD 문자열 리터럴 이스케이프(역슬래시 · 따옴표 · 개행 · 제어문자 제거).</summary>
    public static string UsdString(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                default: if (!char.IsControl(ch)) sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static string UsdAsset(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) if (ch is not ('@' or '\\' or '/' or '\n' or '\r')) sb.Append(ch);
        return sb.ToString();
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("VisionBridge.OnChange", ex); }
        }
    }
}
