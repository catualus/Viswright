using System;
using System.Collections.Generic;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;
using Xunit;

namespace Viswright.Tests
{
    /// <summary>The region hint search's candidates and ranking, on made-up spaces: no compiler needed.</summary>
    public class HintRegionTests
    {
        /// <summary>A map described by functions: what is open, and which leaf a point is in.</summary>
        private sealed class FakeSpace(Vec3 mins, Vec3 maxs, Func<Vec3, bool> open, Func<Vec3, int>? leaf = null) : IHintSpace
        {
            public Vec3 Mins => mins;
            public Vec3 Maxs => maxs;
            public bool Open(Vec3 p) => open(p);
            public int LeafAt(Vec3 p) => leaf?.Invoke(p) ?? 0;
            public int ClusterAt(Vec3 p) => open(p) ? LeafAt(p) : -1;
        }

        /// <summary>
        /// 2,048 units square, ground at 0 and sky at 1,000, with a building 512 units wide and 400 tall
        /// running across the middle from x = 1,024 to 1,536.
        /// </summary>
        private static FakeSpace Street() => new(new Vec3(0, 0, -64), new Vec3(2048, 2048, 1064), p =>
            p.X > 0 && p.X < 2048 && p.Y > 0 && p.Y < 2048 && p.Z > 0 && p.Z < 1000
            && !(p.X >= 1024 && p.X <= 1536 && p.Z < 400));

        /// <summary>A standing point at the centre of every 128-unit cell: on the roof over the building, on the ground elsewhere.</summary>
        private static SamplePoints StreetPoints()
        {
            var points = new List<Vec3>();
            for (int i = 0; i < 16; i++)
                for (int j = 0; j < 16; j++)
                {
                    double x = 64 + 128 * i, y = 64 + 128 * j;
                    points.Add(new Vec3(x, y, (x >= 1024 && x <= 1536 ? 400 : 0) + SamplePoints.EyeHeight));
                }
            return SamplePoints.From(points);
        }

        [Fact]
        public void TheGroundOnEachSideAndTheRoofAreThreeRegions()
        {
            var regions = HintRegions.Regions(Street(), StreetPoints());
            Assert.Equal(3, regions.Count);
            Assert.Equal([64, 64, 128], regions.Select(r => r.Points.Count).OrderBy(n => n));
            Assert.Contains(regions, r => r.Floor == 400);
        }

        [Fact]
        public void TheBuildingIsTheGroundsRimAndTheCapSitsJustAboveIt()
        {
            var space = Street();
            var west = HintRegions.Regions(space, StreetPoints()).Single(r => r.Points.Count == 128);
            var rims = HintRegions.Rims(space, west);
            Assert.NotEmpty(rims);
            Assert.All(rims, z => Assert.InRange(z, 384, 400));

            double sky = west.Cells.Values.Min(c => c.Top);
            var heights = HintRegions.CapHeights(rims, west.Floor, sky);
            Assert.Equal([416.0], heights);
        }

        [Fact]
        public void CapHeightsAreTheCommonestRimsApartAndBetweenGroundAndSky()
        {
            var rims = Enumerable.Repeat(300.0, 40).Concat(Enumerable.Repeat(310.0, 30)).Concat(Enumerable.Repeat(600.0, 20))
                .Concat(Enumerable.Repeat(95.0, 50)).Concat(Enumerable.Repeat(980.0, 50)).Concat(Enumerable.Repeat(700.0, 1)).ToList();
            // 95 is too near the ground, 980 too near the sky; 300 and 310 are one roof line, capped just
            // above its top; 700 is one column.
            Assert.Equal([336.0, 624.0], HintRegions.CapHeights(rims, 0, 1000));
            Assert.Equal([336.0], HintRegions.CapHeights(rims, 0, 1000, most: 1));
            Assert.Empty(HintRegions.CapHeights([], 0, 1000));
        }

        [Fact]
        public void ACapIsOneFaceOverAllTheOpenGroundAtItsHeight()
        {
            var space = Street();
            var regions = HintRegions.Regions(space, StreetPoints());
            var caps = HintRegions.Caps(space, regions);
            // Both sides of the street find the same cap; it is kept once.
            var cap = Assert.Single(caps);
            Assert.Equal(HintShape.Cap, cap.Shape);
            Assert.Equal(416, cap.Height);
            var block = Assert.Single(cap.Blocks);
            Assert.Equal(HintFaces.Top, block.Faces);
            Assert.True(block.Mins.X <= 0 && block.Mins.Y <= 0 && block.Maxs.X >= 2048 && block.Maxs.Y >= 2048);
            Assert.Equal(416 - HintRegions.Thickness, block.Mins.Z);

            // Below the roof the building splits the ground in two, and a cap from the west side covers
            // only that side.
            var west = regions.Single(r => r.Points.Count == 128);
            var low = HintRegions.Cap(space, 300, west.Cells.Keys, "low")!;
            Assert.True(low.Maxs.X <= 1024 + 64 && low.Mins.X <= 0);
        }

        [Fact]
        public void ACoverUsesFewRectanglesAndOnlyAllowedCells()
        {
            // An L of required cells; the corner's missing cell is closed (allowed), so one rectangle does.
            var required = new bool[3, 3];
            var allowed = new bool[3, 3];
            foreach (var (i, j) in new[] { (0, 0), (1, 0), (2, 0), (0, 1), (0, 2) })
                required[i, j] = allowed[i, j] = true;
            allowed[1, 1] = allowed[2, 1] = allowed[1, 2] = allowed[2, 2] = true;
            Assert.Single(HintRegions.Cover(required, allowed));

            // With the corner open to somewhere else (not allowed), two are needed and none covers it.
            allowed[1, 1] = allowed[2, 1] = allowed[1, 2] = allowed[2, 2] = false;
            var rects = HintRegions.Cover(required, allowed);
            Assert.Equal(2, rects.Count);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    int covering = rects.Count(r => i >= r.A0 && i <= r.A1 && j >= r.B0 && j <= r.B1);
                    if (required[i, j]) Assert.True(covering >= 1);
                    if (!allowed[i, j]) Assert.Equal(0, covering);
                }
        }

        [Fact]
        public void AWallsEndIsCarriedAcrossTheYardAsOneCut()
        {
            // A yard 1,024 square and 1,000 tall, with a wall from the south side to y = 600.
            var space = new FakeSpace(new Vec3(0, 0, 0), new Vec3(1024, 1024, 1000), p =>
                p.X > 0 && p.X < 1024 && p.Y > 0 && p.Y < 1024 && p.Z > 0 && p.Z < 1000 && !(p.X >= 500 && p.X <= 516 && p.Y <= 600));
            var end = new WallFace(1, 600, 1, 500, 516, 0, 1000);
            var cut = Assert.Single(HintRegions.Cuts(space, [end]));
            Assert.Equal(HintShape.Cut, cut.Shape);
            Assert.All(cut.Blocks, b => Assert.Equal(HintFaces.PlusY, b.Faces));
            Assert.All(cut.Blocks, b => Assert.Equal(600, b.Maxs.Y));
            Assert.True(cut.Mins.X <= 16 && cut.Maxs.X >= 1008, $"{cut.Mins} {cut.Maxs}");
            Assert.True(cut.Mins.Z <= 16 && cut.Maxs.Z <= 1000 && cut.Maxs.Z >= 960);

            // A face whose end runs into solid has nothing to carry.
            Assert.Null(HintRegions.Cut(space, new WallFace(1, 600, 1, 500, 516, 0, 1000), -50));
        }

        [Fact]
        public void ABuildingUnderACapIsBoxedFromTheGroundToTheCapOnItsFacades()
        {
            var space = Street();
            var regions = HintRegions.Regions(space, StreetPoints());
            var west = regions.Single(r => r.Points.Count == 128);
            var cap = HintRegions.Cap(space, 640, west.Cells.Keys, "high")!;
            WallFace[] facades = [new(0, 1020, -1, 0, 2048, 0, 400), new(0, 1540, 1, 0, 2048, 0, 400)];
            var box = Assert.Single(HintRegions.Boxes(space, cap, regions, facades));
            Assert.Equal(HintShape.Box, box.Shape);
            var block = Assert.Single(box.Blocks);
            Assert.Equal(HintFaces.Sides, block.Faces);
            Assert.Equal(1020, block.Mins.X);
            Assert.Equal(1540, block.Maxs.X);
            Assert.Equal(0, block.Mins.Z);
            Assert.Equal(640, block.Maxs.Z);

            // A cap only just over the roof leaves no room for a box.
            Assert.Empty(HintRegions.Boxes(space, HintRegions.Cap(space, 416, west.Cells.Keys, "low")!, regions, facades));
        }

        [Fact]
        public void ACandidateIsScoredByWhatThePointsInTheClustersItCutsSee()
        {
            // Two leaves, split at x = 1,000.
            var space = new FakeSpace(new Vec3(0, 0, 0), new Vec3(2000, 1000, 1000),
                p => p.X > 0 && p.X < 2000 && p.Y > 0 && p.Y < 1000 && p.Z > 0 && p.Z < 1000, p => p.X < 1000 ? 0 : 1);
            var samples = SamplePoints.From([new(100, 100, 64), new(200, 100, 64), new(300, 100, 64), new(1500, 100, 64)]);
            var weights = HintScoring.Weights(space, samples, [100, 100, 100, 300], out double total);
            Assert.Equal(600, total);

            HintCandidate CutAt(double x) => new(HintShape.Cut, "c", [new HintBlock(new Vec3(x - 8, 0, 0), new Vec3(x, 1000, 1000), HintFaces.PlusX)]);
            Assert.Equal([0], HintScoring.CutClusters(space, CutAt(500)));
            Assert.Equal(0.5, HintScoring.Stake(space, CutAt(500), weights, total), 6);
            // On the plane that already divides the leaves, it splits nothing.
            Assert.Empty(HintScoring.CutClusters(space, CutAt(1000)));
            // A cap over both cuts both.
            var cap = new HintCandidate(HintShape.Cap, "k", [new HintBlock(new Vec3(0, 0, 392), new Vec3(2000, 1000, 400), HintFaces.Top)]);
            Assert.Equal(1.0, HintScoring.Stake(space, cap, weights, total), 6);
        }

        [Fact]
        public void TheShortlistTakesTheMostAtStakeAndLimitsAFamily()
        {
            HintCandidate C(string family, double x, HintShape shape = HintShape.Cut) =>
                new(shape, family, [new HintBlock(new Vec3(x, 0, 0), new Vec3(x + 8, 100, 100), HintFaces.PlusX)]);
            var fresh = new[] { (C("a", 1), 0.9), (C("a", 2), 0.8), (C("a", 3), 0.7), (C("b", 4), 0.5), (C("c", 5), 0.0) };
            // The most at stake, two from family a at most, and nothing with nothing at stake.
            Assert.Equal([1.0, 2.0], HintScoring.Shortlist(fresh, 2).Select(c => c.Mins.X));
            Assert.Equal([1.0, 2.0, 4.0], HintScoring.Shortlist(fresh, 8).Select(c => c.Mins.X));

            // Caps over the same ground at other heights get a third place.
            var caps = new[] { (C("k", 1, HintShape.Cap), 0.9), (C("k", 2, HintShape.Cap), 0.8), (C("k", 3, HintShape.Cap), 0.7), (C("k", 4, HintShape.Cap), 0.6) };
            Assert.Equal(3, HintScoring.Shortlist(caps, 8).Count);

            // A box waiting always has a place, however little it has at stake against long cuts.
            var withBox = fresh.Append((C("x", 9, HintShape.Box), 0.01)).Append((C("x", 10, HintShape.Box), 0.02));
            Assert.Equal([10.0, 1.0], HintScoring.Shortlist(withBox, 2).Select(c => c.Mins.X));
        }

        [Fact]
        public void OnlyCandidatesThatCouldStillWinAreCompiledAgain()
        {
            HintCandidate C(double x) => new(HintShape.Cut, "r", [new HintBlock(new Vec3(x, 0, 0), new Vec3(x + 8, 100, 100), HintFaces.PlusX)]);
            var measured = new[] { (C(6), 0.02), (C(7), 0.05), (C(8), 0.01), (C(9), 0.03) };
            Assert.Equal([7.0, 9.0], HintScoring.Retries(measured, 0.015, 2).Select(c => c.Mins.X));
            Assert.Equal([7.0, 9.0, 6.0], HintScoring.Retries(measured, 0.015, 8).Select(c => c.Mins.X));
            Assert.Empty(HintScoring.Retries(measured, 0.05, 8));
        }

        [Fact]
        public void AGainIsCountedOverThePointsOpenInBoth()
        {
            Assert.Equal(0.25, HintScoring.Gain([100, 100, -1, 50], [75, 75, 10, -1]), 6);
            Assert.Equal(0, HintScoring.Gain([-1], [5]));
        }

        [Fact]
        public void ACandidatesBrushesAreHintWhereItSplitsAndComeBackFromItsData()
        {
            var cap = new HintCandidate(HintShape.Cap, "k", [
                new HintBlock(new Vec3(0, 0, 392), new Vec3(512, 256, 400), HintFaces.Top),
                new HintBlock(new Vec3(512, 0, 392), new Vec3(1024, 512, 400), HintFaces.Top)]);
            var box = new HintCandidate(HintShape.Box, "b", [new HintBlock(new Vec3(0, 0, 0), new Vec3(256, 256, 640), HintFaces.Sides)]);
            var cut = new HintCandidate(HintShape.Cut, "c", [new HintBlock(new Vec3(100, 0, 0), new Vec3(108, 512, 300), HintFaces.PlusX)]);

            foreach (var c in new[] { cap, box, cut })
            {
                var back = HintCandidate.FromData(c.Data);
                Assert.Equal(c.Shape, back.Shape);
                Assert.Equal(c.Blocks, back.Blocks);
                Assert.Equal(c.Id, back.Id);
            }
            // A cap moved in Hammer keeps its outline at the new height.
            var moved = HintCandidate.FromData(new Dictionary<string, double>(cap.Data) { ["height"] = 600 });
            Assert.All(moved.Blocks, b => Assert.Equal(600, b.Maxs.Z));
            Assert.All(moved.Blocks, b => Assert.Equal(600 - HintRegions.Thickness, b.Mins.Z));

            var capBrush = VmfBrush.Build(cap.Brushes(100)[0])!;
            var hint = Assert.Single(capBrush.Faces, f => f.Material == HintPass.HintMaterial);
            Assert.Equal(1, hint.Plane.Normal.Z, 3);
            Assert.Equal(400, hint.Winding!.Centre.Z, 3);

            var boxBrush = VmfBrush.Build(box.Brushes(100)[0])!;
            Assert.Equal(4, boxBrush.Faces.Count(f => f.Material == HintPass.HintMaterial));
            Assert.All(boxBrush.Faces.Where(f => f.Material == HintPass.HintMaterial), f => Assert.Equal(0, f.Plane.Normal.Z, 3));

            var cutBrush = VmfBrush.Build(cut.Brushes(100)[0])!;
            var cutFace = Assert.Single(cutBrush.Faces, f => f.Material == HintPass.HintMaterial);
            Assert.Equal(108, cutFace.Winding!.Centre.X, 3);
        }

        [Fact]
        public void AFaceIsCountedByItsCornersNotTheVerticesOnItsEdges()
        {
            // A square with a T-junction vertex on one edge and a repeated vertex: still four corners, two triangles.
            Vec3[] square = [new(0, 0, 0), new(64, 0, 0), new(128, 0, 0), new(128, 128, 0), new(128, 128, 0), new(0, 128, 0)];
            Assert.Equal(4, FaceCorners.Corners(square));
            Vec3[] triangle = [new(0, 0, 0), new(10, 0, 0), new(0, 10, 0)];
            Assert.Equal(3, FaceCorners.Corners(triangle));
        }
    }
}
