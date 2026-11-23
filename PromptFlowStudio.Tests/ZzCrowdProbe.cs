using Nc = PromptFlowStudio.Services.NeuralCrowd;
/// <summary>
/// 군중 시뮬 스모크 — 원래 단언 0 건의 디버그 덤프(1 800 + 900 스텝 ≈ 13 s · %TEMP% 기록)였다(Step 35 감사).
/// 기본 실행은 짧은 창(600 평시 + 300 패닉 스텝)에서 유한성 · 월드 경계 · 활성 수 불변식을 실제로 단언하고,
/// 원래의 장기 덤프는 <c>PFS_CROWD_PROBE_DUMP=1</c> 일 때만 수행한다(디버그 재현 경로 보존).
/// </summary>
public sealed class ZzCrowdProbe
{
    private const float World = 64f;

    private static void AssertSane(Nc.NeuralCrowdStudioRunner r, string tag)
    {
        var e = r.Engine;
        Xunit.Assert.True(e.Count > 0 && e.ActiveCount > 0 && e.ActiveCount <= e.Count, tag + " 활성 " + e.ActiveCount + "/" + e.Count);
        Xunit.Assert.True(float.IsFinite(e.MeanSpeed) && e.MeanSpeed >= 0f, tag + " 평균 속도 " + e.MeanSpeed);
        for (var i = 0; i < e.Count; i++)
        {
            if (e.Active[i] == 0) continue;
            Xunit.Assert.True(float.IsFinite(e.PX[i]) && float.IsFinite(e.PY[i]) && float.IsFinite(e.VX[i]) && float.IsFinite(e.VY[i]), tag + " 비유한 에이전트 " + i);
            Xunit.Assert.True(e.PX[i] >= 0f && e.PX[i] <= World && e.PY[i] >= 0f && e.PY[i] <= World, tag + " 월드 이탈 " + i + " (" + e.PX[i] + "," + e.PY[i] + ")");
        }
    }

    [Xunit.Fact]
    public void Probe()
    {
        var dump = System.Environment.GetEnvironmentVariable("PFS_CROWD_PROBE_DUMP") == "1";
        if (!dump)
        {
            using var calm = new Nc.NeuralCrowdStudioRunner();
            for (var i = 0; i < 600; i++) calm.Step();
            AssertSane(calm, "평시");
            using var panic = new Nc.NeuralCrowdStudioRunner();
            panic.SetMood(Nc.CrowdMood.Panic, 0.9f);
            for (var i = 0; i < 300; i++) panic.Step();
            AssertSane(panic, "패닉");
            return;
        }

        var sb = new System.Text.StringBuilder();
        void Dump(string tag, Nc.NeuralCrowdStudioRunner r) => sb.AppendLine(tag + $" act={r.Engine.ActiveCount} spd={r.Engine.MeanSpeed:0.00} arr={r.Engine.Arrivals} evac={r.Engine.Evacuated} peakD={r.Guard.PeakDensity:0.0} pen={r.Guard.PeakPenetrationSeen:0.000} panic={r.Social.PanicCount} fear={r.Social.MeanFear:0.00}");
        void Map(Nc.NeuralCrowdStudioRunner r)
        {
            var e = r.Engine;
            for (var by = 15; by >= 0; by--)
            {
                for (var bx = 0; bx < 16; bx++)
                {
                    int cnt = 0, solid = 0;
                    for (var i = 0; i < e.Count; i++) if (e.Active[i] != 0 && (int)(e.PX[i] / 4) == bx && (int)(e.PY[i] / 4) == by) cnt++;
                    for (var y = 0; y < 4; y++) for (var x = 0; x < 4; x++) if (e.Solid((by * 4 + y) * 64 + bx * 4 + x)) solid++;
                    sb.Append(cnt == 0 ? (solid > 0 ? " ## " : "  . ") : cnt.ToString().PadLeft(3) + " ");
                }
                sb.AppendLine();
            }
        }
        var r = new Nc.NeuralCrowdStudioRunner();
        for (var i = 0; i < 1800; i++) { r.Step(); if (i % 600 == 599) { Dump("calm f" + i, r); } }
        Map(r);
        var p = new Nc.NeuralCrowdStudioRunner(); p.SetMood(Nc.CrowdMood.Panic, 0.9f); for (var i = 0; i < 900; i++) p.Step(); Dump("panic", p); Map(p);
        for (var i = 0; i < p.Engine.Count; i++) if (p.Engine.PY[i] < 3f || p.Engine.PY[i] > 61f) sb.AppendLine($"i={i} p=({p.Engine.PX[i]:0.00},{p.Engine.PY[i]:0.00}) v=({p.Engine.VX[i]:0.00},{p.Engine.VY[i]:0.00}) ew={p.Engine.ExitWeight[i]:0.00} fear={p.Social.Fear(i):0.00} dir={p.Engine.SampleDir(2, p.Engine.PX[i], p.Engine.PY[i])} d={p.Engine.FieldDistance(2, (int)p.Engine.PY[i]*64+(int)p.Engine.PX[i]):0.0}");
        var probeDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PromptFlowStudioProbe");
        System.IO.Directory.CreateDirectory(probeDir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(probeDir, "probe.txt"), sb.ToString());
    }
}