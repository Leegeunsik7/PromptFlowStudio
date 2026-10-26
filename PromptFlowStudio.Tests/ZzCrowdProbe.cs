using Nc = PromptFlowStudio.Services.NeuralCrowd;
public sealed class ZzCrowdProbe
{
    [Xunit.Fact]
    public void Probe()
    {
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