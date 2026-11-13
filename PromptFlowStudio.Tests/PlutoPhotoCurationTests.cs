using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>✨ 배치 베스트 컷 큐레이션 — 라플라시안 선명도 · dHash 연사 중복 · 3막 배열 · 최소 컷 구제.</summary>
public sealed class PlutoPhotoCurationTests : IDisposable
{
    private const int Size = 128;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs-curation-" + Guid.NewGuid().ToString("N"));
    private readonly PlutoPhotoCurationEngine _engine = new();

    public PlutoPhotoCurationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 시드별 9×8 무작위 블록 휘도(0.25~0.75 · dHash 격자 정렬) 회색 장면.
    /// sharp → 2px 체커(±0.15) 고주파 디테일 · !sharp → 블록 중심 간 쌍선형 보간(저주파) · offset → 전역 밝기 가산.
    /// </summary>
    private string MakeScene(string name, int seed, bool sharp, double offset = 0.0)
    {
        var rng = new Random(seed);
        var levels = new double[8, 9];
        for (var gy = 0; gy < 8; gy++)
            for (var gx = 0; gx < 9; gx++)
                levels[gy, gx] = 0.25 + 0.5 * rng.NextDouble();
        var path = Path.Combine(_dir, name);
        using var bmp = new System.Drawing.Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                double v;
                if (sharp)
                {
                    v = levels[y * 8 / Size, x * 9 / Size] + ((((x >> 1) + (y >> 1)) & 1) == 0 ? 0.15 : -0.15);
                }
                else
                {
                    var fx = Math.Clamp((x + 0.5) * 9.0 / Size - 0.5, 0.0, 8.0);
                    var fy = Math.Clamp((y + 0.5) * 8.0 / Size - 0.5, 0.0, 7.0);
                    int x0 = Math.Min(7, (int)fx), y0 = Math.Min(6, (int)fy);
                    double tx = fx - x0, ty = fy - y0;
                    var top = levels[y0, x0] * (1 - tx) + levels[y0, x0 + 1] * tx;
                    var bot = levels[y0 + 1, x0] * (1 - tx) + levels[y0 + 1, x0 + 1] * tx;
                    v = top * (1 - ty) + bot * ty;
                }
                var b = (int)Math.Round(Math.Clamp(v + offset, 0.0, 1.0) * 255.0);
                bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(255, b, b, b));
            }
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    [Fact]
    public async Task CurationEngine_RejectsBlurryShot_ViaLaplacianSharpness()
    {
        var crisp = MakeScene("crisp.png", 11, sharp: true);
        var soft = MakeScene("soft.png", 11, sharp: false);
        var f = PlutoImageFocalDetector.Fallback;
        var crispScore = _engine.ComputeLaplacianSharpness(crisp, f);
        var softScore = _engine.ComputeLaplacianSharpness(soft, f);
        Assert.InRange(crispScore, PlutoPhotoCurationEngine.BlurThreshold, 1.0);
        Assert.InRange(softScore, 0.0, PlutoPhotoCurationEngine.BlurThreshold - 1e-9);
        Assert.True(crispScore - softScore > 0.5, $"crisp={crispScore} soft={softScore}");

        var batch = new[] { MakeScene("s1.png", 21, true), MakeScene("s2.png", 22, true), MakeScene("s3.png", 23, true), MakeScene("s4.png", 24, true), soft };
        var r = await _engine.CurateBatchAsync(batch, targetCutCount: 4);
        Assert.Equal(4, r.SelectedCuts.Count);
        Assert.Equal(1, r.BlurryRejectedCount);
        Assert.Equal(PlutoCurationRejectionReason.BlurryOutOfFocus, r.AllEvaluated[4].RejectionReason);
        Assert.False(r.AllEvaluated[4].IsSelected);
    }

    [Fact]
    public async Task CurationEngine_DeduplicatesBurstShots_ViaPerceptualDHash()
    {
        var a = MakeScene("burst-a.png", 31, sharp: true);
        var b = MakeScene("burst-b.png", 31, sharp: true, offset: 0.03);
        var h1 = _engine.ComputePerceptualDHash(a);
        var h2 = _engine.ComputePerceptualDHash(b);
        Assert.True(BitOperations.PopCount(h1 ^ h2) <= PlutoPhotoCurationEngine.DuplicateHammingThreshold, $"hamming={BitOperations.PopCount(h1 ^ h2)}");
        var other = _engine.ComputePerceptualDHash(MakeScene("other.png", 32, sharp: true));
        Assert.True(BitOperations.PopCount(h1 ^ other) > PlutoPhotoCurationEngine.DuplicateHammingThreshold);

        var batch = new[] { a, b, MakeScene("u1.png", 33, true), MakeScene("u2.png", 34, true), MakeScene("u3.png", 35, true), MakeScene("u4.png", 36, true) };
        var r = await _engine.CurateBatchAsync(batch, targetCutCount: 4);
        Assert.Equal(1, r.DuplicateRejectedCount);
        var ca = r.AllEvaluated[0];
        var cb = r.AllEvaluated[1];
        var (keep, drop) = ca.CompositeScore >= cb.CompositeScore ? (ca, cb) : (cb, ca);
        Assert.Equal(PlutoCurationRejectionReason.DuplicateBurstShot, drop.RejectionReason);
        Assert.False(drop.IsSelected);
        Assert.NotEqual(PlutoCurationRejectionReason.DuplicateBurstShot, keep.RejectionReason);
    }

    [Fact]
    public async Task CurationEngine_SelectsTop4AndSequences3ActArc_FromLargeBatch()
    {
        var batch = new[]
        {
            MakeScene("l1.png", 41, true),
            MakeScene("l2.png", 42, false),           // 흐림
            MakeScene("l3.png", 43, true),
            MakeScene("l4.png", 41, true, 0.03),      // l1 연사 중복
            MakeScene("l5.png", 45, true),
            MakeScene("l6.png", 46, false),           // 흐림
            MakeScene("l7.png", 47, true),
            MakeScene("l8.png", 48, true),
        };
        var r = await _engine.CurateBatchAsync(batch, targetCutCount: 4);
        Assert.Equal(8, r.TotalInputCount);
        Assert.Equal(8, r.AllEvaluated.Count);
        Assert.Equal(4, r.SelectedCuts.Count);
        Assert.Equal(2, r.BlurryRejectedCount);
        Assert.Equal(1, r.DuplicateRejectedCount);
        Assert.Equal(PlutoPhotoCurationEngine.Act1Label, r.SelectedCuts[0].NarrativeRoleLabel);
        Assert.Equal(PlutoPhotoCurationEngine.Act2Label, r.SelectedCuts[1].NarrativeRoleLabel);
        Assert.Equal(PlutoPhotoCurationEngine.Act2Label, r.SelectedCuts[2].NarrativeRoleLabel);
        Assert.Equal(PlutoPhotoCurationEngine.Act3Label, r.SelectedCuts[3].NarrativeRoleLabel);
        for (var i = 0; i < r.SelectedCuts.Count; i++)
        {
            var c = r.SelectedCuts[i];
            Assert.True(c.IsSelected);
            Assert.Equal(i + 1, c.NarrativeActOrder);
            Assert.Equal(PlutoCurationRejectionReason.None, c.RejectionReason);
            Assert.InRange(c.SharpnessScore, PlutoPhotoCurationEngine.BlurThreshold, 1.0);
            Assert.InRange(c.FocalPoint.X, 0.20, 0.80);
            Assert.InRange(c.FocalPoint.Y, 0.20, 0.80);
        }
        // Act 1 = 후킹 점수 최대 · Act 3 = 나머지 중 Composite 최대
        static double Hook(PlutoCuratedPhotoCandidate c) => 0.6 * c.SaliencyScore + 0.4 * c.SharpnessScore;
        for (var i = 1; i < 4; i++) Assert.True(Hook(r.SelectedCuts[0]) >= Hook(r.SelectedCuts[i]));
        for (var i = 1; i < 3; i++) Assert.True(r.SelectedCuts[3].CompositeScore >= r.SelectedCuts[i].CompositeScore);
        var lowerRank = 0;
        foreach (var c in r.AllEvaluated) if (c.RejectionReason == PlutoCurationRejectionReason.LowerRankCut) lowerRank++;
        Assert.Equal(1, lowerRank);
    }

    [Fact]
    public async Task CurationEngine_RescuesCandidates_WhenAllShotsAreLowSharpness()
    {
        var batch = new[] { MakeScene("b1.png", 51, false), MakeScene("b2.png", 52, false), MakeScene("b3.png", 53, false), MakeScene("b4.png", 54, false), MakeScene("b5.png", 55, false) };
        var r = await _engine.CurateBatchAsync(batch, targetCutCount: 4);
        Assert.Equal(4, r.SelectedCuts.Count);
        Assert.Equal(1, r.BlurryRejectedCount);
        Assert.Equal(PlutoPhotoCurationEngine.Act1Label, r.SelectedCuts[0].NarrativeRoleLabel);
        Assert.Equal(PlutoPhotoCurationEngine.Act3Label, r.SelectedCuts[3].NarrativeRoleLabel);

        // 판독 불가만 섞인 배치 → 빈 결과 대신 판독 가능 컷 보존 · 누락 파일은 UnreadableFile
        var mixed = new[] { batch[0], batch[1], Path.Combine(_dir, "missing.png") };
        var m = await _engine.CurateBatchAsync(mixed, targetCutCount: 4);
        Assert.Equal(2, m.SelectedCuts.Count);
        Assert.Equal(PlutoCurationRejectionReason.UnreadableFile, m.AllEvaluated[2].RejectionReason);
    }
}
