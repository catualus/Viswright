using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>A square of open ground a hint can be tried over, and the sample points standing in it.</summary>
    public sealed record HintSquare(int X, int Y, double Floor, double Ceiling, IReadOnlyList<int> Points)
    {
        public Vec3 Mins => new(X * HintPass.SquareSize, Y * HintPass.SquareSize, Floor);
        public Vec3 Maxs => new((X + 1) * HintPass.SquareSize, (Y + 1) * HintPass.SquareSize, Ceiling);

        /// <summary>The heights to try a hint at, lowest first: steps above the floor, below the sky.</summary>
        public IEnumerable<double> Heights()
        {
            for (int step = 1; step <= 5; step++)
            {
                double z = Floor + step * HintPass.HeightStep;
                if (z < Ceiling - 32)
                    yield return z;
            }
        }
    }

    /// <summary>
    /// Pass D: horizontal hints over open ground, kept only where they measurably help.
    ///
    /// A leaf of open street reaching up to the sky sees over everything its upper part can see over.
    /// A hint face across the street at about roof height cuts that leaf in two, and the half players
    /// stand in may see less. Whether it does depends on the buildings around, so nothing is assumed:
    /// each square of open ground is tried at a few heights, one compile per height with every square
    /// hinted at once, and a square keeps the height that lowered what its own standing points see
    /// the most. A hint is one brush, toolshint on top and toolsskip on every other face: not solid,
    /// never drawn, and unable to leak.
    /// </summary>
    public sealed class HintPass
    {
        /// <summary>The side of a square hints are tried over: VBSP's own default block size.</summary>
        public const double SquareSize = 1024;

        public const double HeightStep = 192;

        /// <summary>Open space above a standing point for it to count as outdoors.</summary>
        public const double OpenAbove = 384;

        /// <summary>A square needs this many points outdoors to be measured on its own.</summary>
        public const int MinimumPoints = 8;

        public const string HintMaterial = "TOOLS/TOOLSHINT";
        public const string SkipMaterial = "TOOLS/TOOLSSKIP";

        private readonly VmfDocument map;
        private readonly Dictionary<HintSquare, (VmfNode Solid, double Height)> placed = new();

        public HintPass(VmfDocument map) => this.map = map;

        public IReadOnlyDictionary<HintSquare, (VmfNode Solid, double Height)> Placed => placed;

        /// <summary>
        /// The squares of open ground: grouped by square, the sample points with open sky above them,
        /// with the square's floor and how high the open space goes. Squares the 3D skybox or an
        /// ignored region overlaps are left out.
        /// </summary>
        public static List<HintSquare> Squares(SamplePoints samples, BspSpace space, Func<Vec3, bool> excluded)
        {
            var outdoor = new List<(int Index, Vec3 Point, double Top)>();
            for (int i = 0; i < samples.Points.Count; i++)
            {
                var p = samples.Points[i];
                if (excluded(p))
                    continue;
                double up = 0;
                while (up < 8192 && !Blocked(space, p + new Vec3(0, 0, up + 32)))
                    up += 32;
                if (up >= OpenAbove)
                    outdoor.Add((i, p, p.Z + up));
            }

            var squares = new List<HintSquare>();
            foreach (var g in outdoor.GroupBy(o => ((int)Math.Floor(o.Point.X / SquareSize), (int)Math.Floor(o.Point.Y / SquareSize)))
                .OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Item2))
            {
                if (g.Count() < MinimumPoints)
                    continue;
                // The floor most points stand on, and the lowest sky above them: a hint above that would
                // be cut off by the sky brush anyway.
                double floor = Median(g.Select(o => o.Point.Z - SamplePoints.EyeHeight));
                double ceiling = g.Min(o => o.Top);
                squares.Add(new HintSquare(g.Key.Item1, g.Key.Item2, floor, ceiling, g.Select(o => o.Index).ToList()));
            }
            return squares;
        }

        private static bool Blocked(BspSpace space, Vec3 p) => space.IsSolid(p) || space.ClusterAt(p) < 0;

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted[sorted.Count / 2];
        }

        /// <summary>Puts a hint across <paramref name="square"/> at <paramref name="height"/>, replacing any already there.</summary>
        public void Place(HintSquare square, double height)
        {
            Remove(square);
            var solid = Slab(square, height, map.MaxId());
            map.World.Add(solid);
            placed[square] = (solid, height);
        }

        public void Remove(HintSquare square)
        {
            if (placed.Remove(square, out var old))
                map.World.Remove(old.Solid);
        }

        public void RevertAll()
        {
            foreach (var square in placed.Keys.ToList())
                Remove(square);
        }

        /// <summary>What the pass may have changed: the hint brushes it added to the world.</summary>
        public void Declare(ChangeGuard guard)
        {
            var paths = placed.Values.Select(p => $"world#{map.World.Id}/solid#{p.Solid.Id}").ToHashSet();
            guard.Allow("hints", change => change.Kind == VmfChangeKind.Added && paths.Contains(change.Path));
        }

        /// <summary>
        /// A slab 8 units thick whose top is <paramref name="height"/>, one unit in from the square's edges
        /// so it never reaches a neighbour: hint on top, skip everywhere else.
        /// </summary>
        private static VmfNode Slab(HintSquare square, double height, long id)
        {
            var lo = new Vec3(square.X * SquareSize + 1, square.Y * SquareSize + 1, height - 8);
            var hi = new Vec3((square.X + 1) * SquareSize - 1, (square.Y + 1) * SquareSize - 1, height);
            string F(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
            string P(Vec3 p) => $"({F(p.X)} {F(p.Y)} {F(p.Z)})";
            Vec3 V(double x, double y, double z) => new(x, y, z);
            (string Plane, string U, string V)[] sides =
            [
                ($"{P(V(lo.X, hi.Y, hi.Z))} {P(V(hi.X, hi.Y, hi.Z))} {P(V(hi.X, lo.Y, hi.Z))}", "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25"),
                ($"{P(V(lo.X, lo.Y, lo.Z))} {P(V(hi.X, lo.Y, lo.Z))} {P(V(hi.X, hi.Y, lo.Z))}", "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25"),
                ($"{P(V(lo.X, hi.Y, hi.Z))} {P(V(lo.X, lo.Y, hi.Z))} {P(V(lo.X, lo.Y, lo.Z))}", "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25"),
                ($"{P(V(hi.X, hi.Y, lo.Z))} {P(V(hi.X, lo.Y, lo.Z))} {P(V(hi.X, lo.Y, hi.Z))}", "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25"),
                ($"{P(V(hi.X, hi.Y, hi.Z))} {P(V(lo.X, hi.Y, hi.Z))} {P(V(lo.X, hi.Y, lo.Z))}", "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25"),
                ($"{P(V(hi.X, lo.Y, lo.Z))} {P(V(lo.X, lo.Y, lo.Z))} {P(V(lo.X, lo.Y, hi.Z))}", "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25"),
            ];

            var solid = new VmfNode("solid");
            solid.Set("id", (++id).ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < sides.Length; i++)
            {
                var side = new VmfNode("side");
                solid.Add(side);
                side.Set("id", (++id).ToString(CultureInfo.InvariantCulture));
                side.Set("plane", sides[i].Plane);
                side.Set("material", i == 0 ? HintMaterial : SkipMaterial);
                side.Set("uaxis", sides[i].U);
                side.Set("vaxis", sides[i].V);
                side.Set("rotation", "0");
                side.Set("lightmapscale", "16");
                side.Set("smoothing_groups", "0");
            }
            return solid;
        }
    }
}
