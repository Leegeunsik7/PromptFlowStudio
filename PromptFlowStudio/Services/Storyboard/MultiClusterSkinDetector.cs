using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>피부톤 밀도 클러스터 — 무게중심(정규화)·크기·점수(크기×중앙 근접).</summary>
public sealed record SkinCluster(double CxNorm, double CyNorm, int Size, double Score);

/// <summary>
/// [PromptFlowStudio] 다중 클러스터(DBSCAN 밀도) 피부톤 안면 분리기 — 순수 C# BCL(외부 ML/NuGet 0).
///   ▸ <see cref="LumaFaceDetector"/>가 뽑은 YCbCr 피부 픽셀 좌표를 <b>공간 밀도 클러스터링(DBSCAN)</b>으로 분리.
///   ▸ 배경의 따뜻한 색조(가구·목재·조명) 잡음은 <b>희소/외곽 클러스터</b>로 갈라져 제거되고, <b>가장 크고 중앙에 가까운 메인 안면 클러스터</b>만 채택.
///   ▸ 채택 클러스터의 무게중심 Y를 1:1 오토 크롭 Y-Bias 로 사용 → 배경 잡음에 흔들리지 않는 정확한 안면 중심.
/// </summary>
public static class MultiClusterSkinDetector
{
    /// <summary>메인(가장 크고 중앙에 가까운) 피부 클러스터를 찾는다. 클러스터가 없으면 null(호출부가 글로벌 무게중심 폴백).</summary>
    public static SkinCluster? MainCluster(IReadOnlyList<(int X, int Y)> pts, int width, int height, double eps = 4.0, int minPts = 5)
    {
        if (pts is null || pts.Count == 0 || width <= 0 || height <= 0) return null;

        var labels = Dbscan(pts, eps, minPts);
        var groups = new Dictionary<int, (double Sx, double Sy, int Cnt)>();
        for (var i = 0; i < pts.Count; i++)
        {
            var l = labels[i];
            if (l <= 0) continue;   // 0=미할당 · -1=잡음
            var g = groups.TryGetValue(l, out var v) ? v : (0, 0, 0);
            groups[l] = (g.Sx + pts[i].X, g.Sy + pts[i].Y, g.Cnt + 1);
        }
        if (groups.Count == 0) return null;

        SkinCluster? best = null;
        var bestScore = -1.0;
        foreach (var kv in groups)
        {
            var (sx, sy, cnt) = kv.Value;
            var cx = sx / cnt / width;
            var cy = sy / cnt / height;
            var dist = Math.Sqrt((cx - 0.5) * (cx - 0.5) + (cy - 0.5) * (cy - 0.5));   // 중앙(0.5,0.5)에서의 거리
            var score = cnt * (1.0 - Math.Min(dist, 1.0));                              // 크고 + 중앙에 가까울수록↑
            if (score > bestScore)
            {
                bestScore = score;
                best = new SkinCluster(Math.Clamp(cx, 0, 1), Math.Clamp(cy, 0, 1), cnt, score);
            }
        }
        return best;
    }

    /// <summary>DBSCAN(밀도 기반) 클러스터링 — 반환 라벨: 0=미할당, -1=잡음, ≥1=클러스터 id.</summary>
    public static int[] Dbscan(IReadOnlyList<(int X, int Y)> pts, double eps, int minPts)
    {
        var n = pts.Count;
        var labels = new int[n];   // 전부 0(미방문)에서 시작
        var eps2 = eps * eps;
        var cluster = 0;

        for (var i = 0; i < n; i++)
        {
            if (labels[i] != 0) continue;
            var neigh = Region(pts, i, eps2);
            if (neigh.Count < minPts) { labels[i] = -1; continue; }   // 잡음(밀도 부족)

            cluster++;
            labels[i] = cluster;
            var queue = new Queue<int>(neigh);
            while (queue.Count > 0)
            {
                var q = queue.Dequeue();
                if (labels[q] == -1) labels[q] = cluster;   // 잡음 → 경계점으로 편입
                if (labels[q] != 0) continue;
                labels[q] = cluster;
                var qn = Region(pts, q, eps2);
                if (qn.Count >= minPts) foreach (var x in qn) queue.Enqueue(x);   // 코어점 → 확장
            }
        }
        return labels;
    }

    /// <summary>점 i 의 eps 이웃(제곱거리 비교 · 자기 자신 포함).</summary>
    private static List<int> Region(IReadOnlyList<(int X, int Y)> pts, int i, double eps2)
    {
        var res = new List<int>();
        var (px, py) = pts[i];
        for (var j = 0; j < pts.Count; j++)
        {
            double dx = pts[j].X - px, dy = pts[j].Y - py;
            if (dx * dx + dy * dy <= eps2) res.Add(j);
        }
        return res;
    }
}
