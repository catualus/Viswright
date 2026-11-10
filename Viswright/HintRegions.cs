using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>The three shapes Valve's hints make: a cap over open ground, a vertical cut across it, a box round a building under a cap.</summary>
    public enum HintShape { Cap = 1, Cut = 2, Box = 3 }

    /// <summary>Which faces of a hint brush are hint; every other face is skip.</summary>
    [Flags]
    public enum HintFaces { Top = 1, PlusX = 2, PlusY = 4, Sides = 8 }

    /// <summary>One brush of a hint: its bounds and which of its faces split.</summary>
    public readonly record struct HintBlock(Vec3 Mins, Vec3 Maxs, HintFaces Faces);

    /// <summary>
    /// A hint as one candidate of the search: one or more brushes that only make sense together, such as
    /// the rectangles a cap over an irregular yard is cut into. <paramref name="Family"/> groups the
    /// candidates that are alternatives to one another, such as caps over the same ground at other heights.
    /// </summary>
    public sealed record HintCandidate(HintShape Shape, string Family, IReadOnlyList<HintBlock> Blocks)
    {
        /// <summary>A cap's height: the top of its brushes.</summary>
        public double Height => Blocks.Count == 0 ? 0 : Blocks.Max(b => b.Maxs.Z);

        public Vec3 Mins => Blocks.Select(b => b.Mins).Aggregate(Vec3.Min);
        public Vec3 Maxs => Blocks.Select(b => b.Maxs).Aggregate(Vec3.Max);

        /// <summary>Anchored to the map, so the same candidate has the same id on the next run.</summary>
        public string Id
        {
            get
            {
                var lo = Mins;
                var hi = Maxs;
                string kind = Shape switch { HintShape.Cap => "cap", HintShape.Cut => "cut", _ => "box" };
                return string.Create(CultureInfo.InvariantCulture,
                    $"hint:{kind}:{Math.Round(lo.X):0}_{Math.Round(lo.Y):0}_{Math.Round(lo.Z):0}:{Math.Round(hi.X):0}_{Math.Round(hi.Y):0}_{Math.Round(hi.Z):0}:{Blocks.Count}");
            }
        }

        /// <summary>What a pinned run needs to put it back, in a decisions file's numbers.</summary>
        public Dictionary<string, double> Data
        {
            get
            {
                var d = new Dictionary<string, double> { ["shape"] = (int)Shape, ["blocks"] = Blocks.Count };
                if (Shape == HintShape.Cap)
                    d["height"] = Height;
                for (int i = 0; i < Blocks.Count; i++)
                {
                    var b = Blocks[i];
                    d[$"b{i}x0"] = b.Mins.X; d[$"b{i}y0"] = b.Mins.Y; d[$"b{i}z0"] = b.Mins.Z;
                    d[$"b{i}x1"] = b.Maxs.X; d[$"b{i}y1"] = b.Maxs.Y; d[$"b{i}z1"] = b.Maxs.Z;
                    d[$"b{i}f"] = (int)b.Faces;
                }
                return d;
            }
        }

        /// <summary>
        /// The candidate a decisions file's numbers describe. A cap moved in Hammer keeps its outline and
        /// takes the height it was moved to.
        /// </summary>
        public static HintCandidate FromData(IReadOnlyDictionary<string, double> d)
        {
            var shape = (HintShape)(int)d["shape"];
            int count = (int)d["blocks"];
            var blocks = new List<HintBlock>(count);
            for (int i = 0; i < count; i++)
            {
                var lo = new Vec3(d[$"b{i}x0"], d[$"b{i}y0"], d[$"b{i}z0"]);
                var hi = new Vec3(d[$"b{i}x1"], d[$"b{i}y1"], d[$"b{i}z1"]);
                if (shape == HintShape.Cap && d.TryGetValue("height", out double height))
                {
                    lo = lo with { Z = height - HintRegions.Thickness };
                    hi = hi with { Z = height };
                }
                blocks.Add(new HintBlock(lo, hi, (HintFaces)(int)d[$"b{i}f"]));
            }
            return new HintCandidate(shape, "decided", blocks);
        }

        /// <summary>The brushes: toolshint on the faces that split, toolsskip on the rest.</summary>
        public List<VmfNode> Brushes(long id)
        {
            var solids = new List<VmfNode>();
            foreach (var block in Blocks)
            {
                var solid = Review.ReviewVmf.Box(block.Mins, block.Maxs, HintPass.SkipMaterial, ref id);
                // Box faces: top, bottom, -x, +x, +y, -y.
                var sides = solid.Children("side").ToList();
                if (block.Faces.HasFlag(HintFaces.Top)) sides[0].Set("material", HintPass.HintMaterial);
                if (block.Faces.HasFlag(HintFaces.PlusX)) sides[3].Set("material", HintPass.HintMaterial);
                if (block.Faces.HasFlag(HintFaces.PlusY)) sides[4].Set("material", HintPass.HintMaterial);
                if (block.Faces.HasFlag(HintFaces.Sides))
                    foreach (int s in new[] { 2, 3, 4, 5 })
                        sides[s].Set("material", HintPass.HintMaterial);
                solids.Add(solid);
            }
            return solids;
        }

        /// <summary>
        /// Points on the candidate's hint faces, <paramref name="spacing"/> apart or closer, each with the
        /// face's normal: where it would split the leaves it passes through.
        /// </summary>
        public IEnumerable<(Vec3 Point, Vec3 Normal)> FacePoints(double spacing)
        {
            foreach (var b in Blocks)
            {
                if (b.Faces.HasFlag(HintFaces.Top))
                    foreach (var p in Grid(b.Mins.X, b.Maxs.X, b.Mins.Y, b.Maxs.Y, spacing))
                        yield return (new Vec3(p.A, p.B, b.Maxs.Z), new Vec3(0, 0, 1));
                bool plusX = b.Faces.HasFlag(HintFaces.PlusX) || b.Faces.HasFlag(HintFaces.Sides);
                bool plusY = b.Faces.HasFlag(HintFaces.PlusY) || b.Faces.HasFlag(HintFaces.Sides);
                if (plusX)
                    foreach (var p in Grid(b.Mins.Y, b.Maxs.Y, b.Mins.Z, b.Maxs.Z, spacing))
                        yield return (new Vec3(b.Maxs.X, p.A, p.B), new Vec3(1, 0, 0));
                if (plusY)
                    foreach (var p in Grid(b.Mins.X, b.Maxs.X, b.Mins.Z, b.Maxs.Z, spacing))
                        yield return (new Vec3(p.A, b.Maxs.Y, p.B), new Vec3(0, 1, 0));
                if (b.Faces.HasFlag(HintFaces.Sides))
                {
                    foreach (var p in Grid(b.Mins.Y, b.Maxs.Y, b.Mins.Z, b.Maxs.Z, spacing))
                        yield return (new Vec3(b.Mins.X, p.A, p.B), new Vec3(-1, 0, 0));
                    foreach (var p in Grid(b.Mins.X, b.Maxs.X, b.Mins.Z, b.Maxs.Z, spacing))
                        yield return (new Vec3(p.A, b.Mins.Y, p.B), new Vec3(0, -1, 0));
                }
            }
        }

        private static IEnumerable<(double A, double B)> Grid(double a0, double a1, double b0, double b1, double spacing)
        {
            int na = Math.Max(1, (int)Math.Ceiling((a1 - a0) / spacing));
            int nb = Math.Max(1, (int)Math.Ceiling((b1 - b0) / spacing));
            for (int i = 0; i < na; i++)
                for (int j = 0; j < nb; j++)
                    yield return (a0 + (i + 0.5) * (a1 - a0) / na, b0 + (j + 0.5) * (b1 - b0) / nb);
        }
    }

    /// <summary>What candidate generation and scoring ask of a compiled map.</summary>
    public interface IHintSpace
    {
        Vec3 Mins { get; }
        Vec3 Maxs { get; }

        /// <summary>Whether <paramref name="point"/> is open space inside the map: not in a world brush, not in the void.</summary>
        bool Open(Vec3 point);

        int LeafAt(Vec3 point);

        int ClusterAt(Vec3 point);
    }

    /// <summary>A compiled map as candidate generation sees it: only world brushes close space, since only they split leaves.</summary>
    public sealed class BspHintSpace(BspSpace space) : IHintSpace
    {
        public Vec3 Mins => space.Mins;
        public Vec3 Maxs => space.Maxs;
        public bool Open(Vec3 point) => !space.IsStructuralSolid(point) && space.ClusterAt(point) >= 0;
        public int LeafAt(Vec3 point) => space.LeafAt(point);
        public int ClusterAt(Vec3 point) => space.ClusterAt(point);
    }

    /// <summary>A vertical, axis-aligned face of a world brush: on the plane <paramref name="At"/> of axis <paramref name="Axis"/>, facing <paramref name="Outward"/>.</summary>
    public readonly record struct WallFace(int Axis, double At, int Outward, double B0, double B1, double Z0, double Z1);

    /// <summary>Ground with open sky above, as the standing points on it see it.</summary>
    public sealed record OutdoorRegion(int Id, IReadOnlyDictionary<(int X, int Y), (double Floor, double Top)> Cells, IReadOnlyList<int> Points)
    {
        public double Floor => Cells.Values.Select(c => c.Floor).OrderBy(f => f).ElementAt(Cells.Count / 2);
    }

    /// <summary>
    /// The candidates of the hint search, made the way Valve's own hints are: few and large.
    ///
    /// <b>Caps.</b> Ground open to the sky is grouped into regions, and each region's rim - the tops of the
    /// roofs and walls around it - gives the heights a cap is tried at. A cap is one horizontal face at one
    /// height over everything open at that height that joins the region, cut into as few rectangles as cover
    /// it, so no square is left out and none sits at another height. Over Gravelpit's pit, one face like that
    /// measured 18.9% less seen where 1,024-unit tiles at heights of their own gave 11.8%.
    ///
    /// <b>Cuts.</b> The plane of a vertical world face - a facade, a corner, a wall's end - carried past the
    /// face's end across the open space beside it, as far as that space runs, up to the wall's top.
    ///
    /// <b>Boxes.</b> Once a cap is in, the buildings standing under it are each wrapped in a box of
    /// vertical hint faces from the ground to the cap, so the air over a roof is a room of its own.
    /// </summary>
    public static class HintRegions
    {
        /// <summary>The grid caps and regions are worked out on: the standing points' own spacing.</summary>
        public const double Cell = SamplePoints.Spacing;

        /// <summary>The grid a cut is traced on, in its plane.</summary>
        public const double CutCell = 32;

        /// <summary>Hint brushes are this thick, behind their hint face.</summary>
        public const double Thickness = 8;

        /// <summary>Open space above a standing point for it to count as outdoors.</summary>
        public const double OpenAbove = 384;

        /// <summary>A region needs this many standing points to be capped.</summary>
        public const int MinimumPoints = 8;

        /// <summary>Heights tried per region: the commonest roof and wall tops around it.</summary>
        public const int HeightsPerRegion = 3;

        /// <summary>How far a cut is carried along its plane from where it starts.</summary>
        public const double Reach = 3072;

        /// <summary>Two neighbouring columns whose ground differs by more than this are on two sides of a cliff or a wall's top.</summary>
        public const double Step = 128;

        /// <summary>
        /// Regions of ground open to the sky: standing points with <see cref="OpenAbove"/> of open space over
        /// them, joined to their neighbours on the grid where the ground runs on without a drop of more than
        /// <see cref="Step"/> and the air between them is open. A pit and the ground round its edge are two
        /// regions, so the edge is the pit's rim.
        /// </summary>
        public static List<OutdoorRegion> Regions(IHintSpace space, SamplePoints samples)
        {
            var cells = new Dictionary<(int X, int Y), (double Floor, double Top, int Point)>();
            for (int i = 0; i < samples.Points.Count; i++)
            {
                var p = samples.Points[i];
                if (!space.Open(p))
                    continue;
                double up = 0;
                while (up < 16384 && space.Open(p + new Vec3(0, 0, up + 32)))
                    up += 32;
                if (up < OpenAbove)
                    continue;
                var key = Key(p);
                double floor = p.Z - SamplePoints.EyeHeight;
                // Where two standing points share a column, the one under the most open sky.
                if (!cells.TryGetValue(key, out var had) || had.Top - had.Floor < up)
                    cells[key] = (floor, p.Z + up, i);
            }

            var keys = cells.Keys.OrderBy(k => k.X).ThenBy(k => k.Y).ToList();
            var parent = keys.ToDictionary(k => k, k => k);
            (int, int) Find((int, int) k) { while (!parent[k].Equals(k)) k = parent[k] = parent[parent[k]]; return k; }
            foreach (var k in keys)
                foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (1, 1), (1, -1) })
                {
                    var n = (k.X + dx, k.Y + dy);
                    if (!cells.TryGetValue(n, out var other))
                        continue;
                    var a = cells[k];
                    if (Math.Abs(a.Floor - other.Floor) > Step)
                        continue;
                    double z = Math.Min(a.Top, other.Top) - 48;
                    var mid = new Vec3((k.X + 0.5 + dx / 2.0) * Cell, (k.Y + 0.5 + dy / 2.0) * Cell, z);
                    if (space.Open(mid))
                        parent[Find(k)] = Find(n);
                }

            var regions = new List<OutdoorRegion>();
            foreach (var group in keys.GroupBy(Find).OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Item2))
            {
                var members = group.ToList();
                if (members.Count < MinimumPoints)
                    continue;
                regions.Add(new OutdoorRegion(regions.Count,
                    members.ToDictionary(k => k, k => (cells[k].Floor, cells[k].Top)),
                    members.Select(k => cells[k].Point).ToList()));
            }
            return regions;
        }

        /// <summary>The grid cell a point is in.</summary>
        public static (int X, int Y) Key(Vec3 p) => ((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Y / Cell));

        /// <summary>
        /// The tops of the roofs, walls and higher ground around <paramref name="region"/>: down from the
        /// region's sky in each column one or two cells beyond its edge, the first solid met, if it stands well
        /// above the ground beside it. A column already solid where the walk starts - a wall up to the sky, or
        /// a lower sky - has no top under the sky to cap at.
        /// </summary>
        public static List<double> Rims(IHintSpace space, OutdoorRegion region)
        {
            var rims = new List<double>();
            foreach (var ((x, y), (floor, top)) in region.Cells)
                for (int dx = -2; dx <= 2; dx++)
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        if ((dx == 0 && dy == 0) || region.Cells.ContainsKey((x + dx, y + dy)))
                            continue;
                        double cx = (x + dx + 0.5) * Cell, cy = (y + dy + 0.5) * Cell;
                        if (!space.Open(new Vec3(cx, cy, top - 16)))
                            continue;
                        for (double z = top - 32; z > floor + 96; z -= 16)
                            if (!space.Open(new Vec3(cx, cy, z)))
                            {
                                rims.Add(z + 8);
                                break;
                            }
                    }
            return rims;
        }

        /// <summary>
        /// The heights to cap a region at: the commonest rim heights, at most <paramref name="most"/>, at
        /// least 96 units apart, each just above the roofs it sits on and below the sky.
        /// </summary>
        public static List<double> CapHeights(IReadOnlyList<double> rims, double floor, double sky, int most = HeightsPerRegion)
        {
            if (rims.Count == 0)
                return [];
            var bins = rims.GroupBy(z => Math.Floor(z / 32)).Select(g => (Top: g.Max(), Count: g.Count()))
                .OrderByDescending(b => b.Count).ThenByDescending(b => b.Top).ToList();
            int least = Math.Max(2, bins[0].Count / 10);
            var heights = new List<double>();
            foreach (var (top, count) in bins)
            {
                if (count < least || heights.Count >= most)
                    break;
                double h = Math.Ceiling((top + 16) / 16) * 16;
                if (h < floor + 128 || h > sky - 32 || heights.Any(o => Math.Abs(o - h) < 96))
                    continue;
                heights.Add(h);
            }
            return heights;
        }

        /// <summary>Caps over the same ground closer in height than this are one candidate.</summary>
        public const double CapSeparation = 48;

        /// <summary>
        /// A cap for every region at each of its rim heights. Caps over much the same ground - their bounds
        /// the same to within half a block - are one family, the same cap at other heights, and of two less
        /// than <see cref="CapSeparation"/> apart, found from two regions, the first is kept.
        /// </summary>
        public static List<HintCandidate> Caps(IHintSpace space, IReadOnlyList<OutdoorRegion> regions)
        {
            var caps = new List<HintCandidate>();
            foreach (var region in regions.OrderByDescending(r => r.Points.Count).ThenBy(r => r.Id))
            {
                // A cap higher than the sky over most of the region would be open over too little of it.
                double sky = region.Cells.Values.Select(c => c.Top).OrderBy(t => t).ElementAt(region.Cells.Count / 2);
                foreach (double h in CapHeights(Rims(space, region), region.Floor, sky))
                {
                    if (Cap(space, h, region.Cells.Keys, "") is not { } cap)
                        continue;
                    var lo = cap.Mins;
                    var hi = cap.Maxs;
                    string footprint = string.Create(CultureInfo.InvariantCulture,
                        $"cap:{Math.Round(lo.X / 512)}_{Math.Round(lo.Y / 512)}_{Math.Round(hi.X / 512)}_{Math.Round(hi.Y / 512)}");
                    if (caps.Any(c => c.Family == footprint && Math.Abs(c.Height - h) < CapSeparation))
                        continue;
                    caps.Add(cap with { Family = footprint });
                }
            }
            return caps;
        }

        /// <summary>
        /// A cap at <paramref name="height"/> over everything open at that height that joins
        /// <paramref name="seeds"/>, as the fewest rectangles that cover it. Closed cells inside it may be
        /// covered too: a hint face inside a wall splits nothing.
        /// </summary>
        public static HintCandidate? Cap(IHintSpace space, double height, IEnumerable<(int X, int Y)> seeds, string family)
        {
            int x0 = (int)Math.Floor(space.Mins.X / Cell), y0 = (int)Math.Floor(space.Mins.Y / Cell);
            int nx = (int)Math.Ceiling(space.Maxs.X / Cell) - x0, ny = (int)Math.Ceiling(space.Maxs.Y / Cell) - y0;
            if (nx <= 0 || ny <= 0)
                return null;
            var open = new bool[nx, ny];
            System.Threading.Tasks.Parallel.For(0, nx, SourceKitConcurrency.Options, i =>
            {
                for (int j = 0; j < ny; j++)
                    open[i, j] = space.Open(new Vec3((x0 + i + 0.5) * Cell, (y0 + j + 0.5) * Cell, height));
            });

            var inCap = new bool[nx, ny];
            var stack = new Stack<(int, int)>();
            foreach (var (sx, sy) in seeds)
            {
                int i = sx - x0, j = sy - y0;
                if (i >= 0 && j >= 0 && i < nx && j < ny && open[i, j] && !inCap[i, j])
                {
                    inCap[i, j] = true;
                    stack.Push((i, j));
                }
            }
            int count = 0;
            int lo0 = nx, lo1 = ny, hi0 = -1, hi1 = -1;
            while (stack.Count > 0)
            {
                var (i, j) = stack.Pop();
                count++;
                lo0 = Math.Min(lo0, i); hi0 = Math.Max(hi0, i); lo1 = Math.Min(lo1, j); hi1 = Math.Max(hi1, j);
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int a = i + di, b = j + dj;
                    if (a >= 0 && b >= 0 && a < nx && b < ny && open[a, b] && !inCap[a, b])
                    {
                        inCap[a, b] = true;
                        stack.Push((a, b));
                    }
                }
            }
            if (count < 4)
                return null;

            int w = hi0 - lo0 + 1, d = hi1 - lo1 + 1;
            var required = new bool[w, d];
            var allowed = new bool[w, d];
            for (int i = 0; i < w; i++)
                for (int j = 0; j < d; j++)
                {
                    required[i, j] = inCap[lo0 + i, lo1 + j];
                    allowed[i, j] = required[i, j] || !open[lo0 + i, lo1 + j];
                }
            // Open at this height but not joined to the cap: a room the cap must not reach into.
            bool Foreign(int i, int j) => i >= 0 && j >= 0 && i < nx && j < ny && open[i, j] && !inCap[i, j];
            var blocks = new List<HintBlock>();
            foreach (var (a0, b0, a1, b1) in Cover(required, allowed))
            {
                int gi0 = lo0 + a0, gj0 = lo1 + b0, gi1 = lo0 + a1, gj1 = lo1 + b1;
                // Half a cell further on each side with nothing foreign beyond, so the cap meets the walls.
                bool west = !Enumerable.Range(gj0, gj1 - gj0 + 1).Any(j => Foreign(gi0 - 1, j));
                bool east = !Enumerable.Range(gj0, gj1 - gj0 + 1).Any(j => Foreign(gi1 + 1, j));
                bool south = !Enumerable.Range(gi0, gi1 - gi0 + 1).Any(i => Foreign(i, gj0 - 1));
                bool north = !Enumerable.Range(gi0, gi1 - gi0 + 1).Any(i => Foreign(i, gj1 + 1));
                double half = Cell / 2;
                blocks.Add(new HintBlock(
                    new Vec3((x0 + gi0) * Cell - (west ? half : 0), (y0 + gj0) * Cell - (south ? half : 0), height - Thickness),
                    new Vec3((x0 + gi1 + 1) * Cell + (east ? half : 0), (y0 + gj1 + 1) * Cell + (north ? half : 0), height),
                    HintFaces.Top));
            }
            return new HintCandidate(HintShape.Cap, family, blocks);
        }

        /// <summary>
        /// The fewest rectangles, greedily, that cover every <paramref name="required"/> cell using only
        /// <paramref name="allowed"/> ones, as inclusive cell ranges: each grows along the first axis as far
        /// as it can, then along the second while the whole run stays allowed.
        /// </summary>
        public static List<(int A0, int B0, int A1, int B1)> Cover(bool[,] required, bool[,] allowed)
        {
            int w = required.GetLength(0), d = required.GetLength(1);
            var covered = new bool[w, d];
            var rects = new List<(int, int, int, int)>();
            for (int j = 0; j < d; j++)
                for (int i = 0; i < w; i++)
                {
                    if (!required[i, j] || covered[i, j])
                        continue;
                    int i1 = i;
                    while (i1 + 1 < w && allowed[i1 + 1, j])
                        i1++;
                    // Trim allowed-only cells off the end: they cover nothing on this row.
                    while (i1 > i && !required[i1, j])
                        i1--;
                    int j1 = j;
                    while (j1 + 1 < d && Enumerable.Range(i, i1 - i + 1).All(a => allowed[a, j1 + 1]))
                        j1++;
                    while (j1 > j && !Enumerable.Range(i, i1 - i + 1).Any(a => required[a, j1] && !covered[a, j1]))
                        j1--;
                    for (int a = i; a <= i1; a++)
                        for (int b = j; b <= j1; b++)
                            covered[a, b] = true;
                    rects.Add((i, j, i1, j1));
                }
            return rects;
        }

        /// <summary>The vertical, axis-aligned faces of the world's drawn brushes, 96 units tall or more.</summary>
        public static List<WallFace> Faces(VmfDocument map)
        {
            var faces = new List<WallFace>();
            foreach (var solid in map.World.Children("solid"))
            {
                if (VmfBrush.Build(solid) is not { } brush || brush.HasDisplacement)
                    continue;
                if (brush.Faces.All(f => f.Material.StartsWith("TOOLS/", StringComparison.OrdinalIgnoreCase))
                    || brush.Faces.Any(f => Audit.NonVisibleTools.Contains(f.Material)))
                    continue;
                foreach (var face in brush.Faces)
                {
                    if (face.Winding is not { } w)
                        continue;
                    var n = face.Plane.Normal;
                    int axis = Math.Abs(n.X) > 0.999 ? 0 : Math.Abs(n.Y) > 0.999 ? 1 : -1;
                    if (axis < 0)
                        continue;
                    var lo = w.Points.Aggregate(Vec3.Min);
                    var hi = w.Points.Aggregate(Vec3.Max);
                    if (hi.Z - lo.Z < 96)
                        continue;
                    faces.Add(new WallFace(axis, Math.Round(axis == 0 ? lo.X : lo.Y, 2), (axis == 0 ? n.X : n.Y) > 0 ? 1 : -1,
                        axis == 0 ? lo.Y : lo.X, axis == 0 ? hi.Y : hi.X, lo.Z, hi.Z));
                }
            }
            return faces;
        }

        /// <summary>
        /// Every face's plane carried past each of its ends across the open space beside it: traced on a
        /// grid in the plane from just past the end, through open space, no higher than the face's top and
        /// no further than <see cref="Reach"/>. The same opening found from two faces is kept once.
        /// </summary>
        public static List<HintCandidate> Cuts(IHintSpace space, IReadOnlyList<WallFace> faces)
        {
            var seeds = new List<(WallFace Face, double Along)>();
            foreach (var f in faces)
            {
                seeds.Add((f, f.B0 - 8));
                seeds.Add((f, f.B1 + 8));
            }
            var found = new HintCandidate?[seeds.Count];
            System.Threading.Tasks.Parallel.For(0, seeds.Count, SourceKitConcurrency.Options, s =>
            {
                var (face, along) = seeds[s];
                found[s] = Cut(space, face, along);
            });
            var cuts = new Dictionary<string, HintCandidate>();
            foreach (var cut in found)
                if (cut != null)
                    cuts.TryAdd(cut.Id, cut);
            return cuts.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>The cut that carries <paramref name="face"/>'s plane past the end at <paramref name="along"/>, or null.</summary>
        public static HintCandidate? Cut(IHintSpace space, WallFace face, double along)
        {
            double across = face.At + face.Outward;
            Vec3 P(double b, double z) => face.Axis == 0 ? new Vec3(across, b, z) : new Vec3(b, across, z);
            double seedZ = face.Z0 + 48;
            if (!space.Open(P(along, seedZ)))
                return null;

            double b0 = Math.Floor((along - Reach) / CutCell) * CutCell, zBase = Math.Floor((face.Z0 - 256) / CutCell) * CutCell;
            int nb = (int)Math.Ceiling(2 * Reach / CutCell) + 1, nz = (int)Math.Ceiling((face.Z1 - zBase) / CutCell);
            if (nz <= 0)
                return null;
            // 0 unknown, 1 open, 2 closed; traced lazily, since most of the plane is never reached.
            var state = new byte[nb, nz];
            bool OpenAt(int i, int j)
            {
                if (state[i, j] == 0)
                    state[i, j] = space.Open(P(b0 + (i + 0.5) * CutCell, zBase + (j + 0.5) * CutCell)) ? (byte)1 : (byte)2;
                return state[i, j] == 1;
            }

            int si = (int)Math.Floor((along - b0) / CutCell), sj = (int)Math.Floor((seedZ - zBase) / CutCell);
            if (si < 0 || si >= nb || sj < 0 || sj >= nz || !OpenAt(si, sj))
                return null;
            var inCut = new bool[nb, nz];
            inCut[si, sj] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((si, sj));
            int count = 0, lo0 = si, hi0 = si, lo1 = sj, hi1 = sj;
            while (stack.Count > 0)
            {
                var (i, j) = stack.Pop();
                count++;
                lo0 = Math.Min(lo0, i); hi0 = Math.Max(hi0, i); lo1 = Math.Min(lo1, j); hi1 = Math.Max(hi1, j);
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int a = i + di, b = j + dj;
                    if (a >= 0 && b >= 0 && a < nb && b < nz && !inCut[a, b] && OpenAt(a, b))
                    {
                        inCut[a, b] = true;
                        stack.Push((a, b));
                    }
                }
            }
            if (count < 8 || (hi0 - lo0 + 1) * CutCell < 64 || (hi1 - lo1 + 1) * CutCell < 64)
                return null;

            int w = hi0 - lo0 + 1, d = hi1 - lo1 + 1;
            var required = new bool[w, d];
            var allowed = new bool[w, d];
            for (int i = 0; i < w; i++)
                for (int j = 0; j < d; j++)
                {
                    required[i, j] = inCut[lo0 + i, lo1 + j];
                    allowed[i, j] = required[i, j] || !OpenAt(lo0 + i, lo1 + j);
                }
            bool Foreign(int i, int j) => i >= 0 && j >= 0 && i < nb && j < nz && !inCut[i, j] && OpenAt(i, j);
            var blocks = new List<HintBlock>();
            foreach (var (a0, c0, a1, c1) in Cover(required, allowed))
            {
                int gi0 = lo0 + a0, gj0 = lo1 + c0, gi1 = lo0 + a1, gj1 = lo1 + c1;
                double half = CutCell / 2;
                double e0 = Enumerable.Range(gj0, gj1 - gj0 + 1).Any(j => Foreign(gi0 - 1, j)) ? 0 : half;
                double e1 = Enumerable.Range(gj0, gj1 - gj0 + 1).Any(j => Foreign(gi1 + 1, j)) ? 0 : half;
                double f0 = Enumerable.Range(gi0, gi1 - gi0 + 1).Any(i => Foreign(i, gj0 - 1)) ? 0 : half;
                double f1 = gj1 + 1 >= nz || Enumerable.Range(gi0, gi1 - gi0 + 1).Any(i => Foreign(i, gj1 + 1)) ? 0 : half;
                double lb = b0 + gi0 * CutCell - e0, hb = b0 + (gi1 + 1) * CutCell + e1;
                double lz = zBase + gj0 * CutCell - f0, hz = Math.Min(face.Z1, zBase + (gj1 + 1) * CutCell + f1);
                blocks.Add(face.Axis == 0
                    ? new HintBlock(new Vec3(face.At - Thickness, lb, lz), new Vec3(face.At, hb, hz), HintFaces.PlusX)
                    : new HintBlock(new Vec3(lb, face.At - Thickness, lz), new Vec3(hb, face.At, hz), HintFaces.PlusY));
            }
            // Parallel cuts a few units apart are alternatives to one another.
            return new HintCandidate(HintShape.Cut, string.Create(CultureInfo.InvariantCulture, $"cut:{face.Axis}:{Math.Floor(face.At / 256)}"), blocks);
        }

        /// <summary>
        /// The buildings standing under <paramref name="cap"/>, each wrapped in a box of vertical hint faces
        /// from the ground beside it up to the cap, and, where there are two or more, all of the boxes as one
        /// candidate: each may be worth little alone where together they are worth a lot. A building is a run
        /// of columns whose top, down from the cap, is well above the ground of the regions around; each side
        /// of its box is moved onto the facade nearest it, if one is within a cell.
        /// </summary>
        public static List<HintCandidate> Boxes(IHintSpace space, HintCandidate cap, IReadOnlyList<OutdoorRegion> regions, IReadOnlyList<WallFace> faces)
        {
            double h = cap.Height;
            var ground = new Dictionary<(int X, int Y), double>();
            foreach (var region in regions)
                foreach (var (key, cell) in region.Cells)
                    ground[key] = cell.Floor;
            var lo = cap.Mins;
            var hi = cap.Maxs;
            int x0 = (int)Math.Floor(lo.X / Cell), y0 = (int)Math.Floor(lo.Y / Cell);
            int nx = (int)Math.Ceiling(hi.X / Cell) - x0, ny = (int)Math.Ceiling(hi.Y / Cell) - y0;
            if (nx <= 0 || ny <= 0)
                return [];

            var roof = new double[nx, ny];
            var groundAt = new double[nx, ny];
            var building = new bool[nx, ny];
            System.Threading.Tasks.Parallel.For(0, nx, SourceKitConcurrency.Options, i =>
            {
                for (int j = 0; j < ny; j++)
                {
                    var key = (x0 + i, y0 + j);
                    double near = double.MaxValue;
                    for (int dx = -3; dx <= 3; dx++)
                        for (int dy = -3; dy <= 3; dy++)
                            if (ground.TryGetValue((key.Item1 + dx, key.Item2 + dy), out double f))
                                near = Math.Min(near, f);
                    groundAt[i, j] = near;
                    if (near == double.MaxValue)
                        continue;
                    double cx = (key.Item1 + 0.5) * Cell, cy = (key.Item2 + 0.5) * Cell;
                    if (!space.Open(new Vec3(cx, cy, h - 8)))
                        continue;
                    for (double z = h - 24; z > near; z -= 16)
                        if (!space.Open(new Vec3(cx, cy, z)))
                        {
                            roof[i, j] = z + 8;
                            building[i, j] = z + 8 >= near + 128 && z + 8 <= h - 48;
                            break;
                        }
                }
            });

            var boxes = new List<HintCandidate>();
            var seen = new bool[nx, ny];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    if (!building[i, j] || seen[i, j])
                        continue;
                    var members = new List<(int, int)>();
                    var stack = new Stack<(int, int)>();
                    stack.Push((i, j));
                    seen[i, j] = true;
                    while (stack.Count > 0)
                    {
                        var (a, b) = stack.Pop();
                        members.Add((a, b));
                        foreach (var (da, db) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        {
                            int p = a + da, q = b + db;
                            if (p >= 0 && q >= 0 && p < nx && q < ny && building[p, q] && !seen[p, q])
                            {
                                seen[p, q] = true;
                                stack.Push((p, q));
                            }
                        }
                    }
                    int mi0 = members.Min(m => m.Item1), mi1 = members.Max(m => m.Item1);
                    int mj0 = members.Min(m => m.Item2), mj1 = members.Max(m => m.Item2);
                    if (members.Count < 2 || mi1 - mi0 >= 24 || mj1 - mj0 >= 24)
                        continue;
                    double floor = members.Min(m => groundAt[m.Item1, m.Item2]);
                    double top = members.Min(m => roof[m.Item1, m.Item2]);
                    double bx0 = (x0 + mi0) * Cell, bx1 = (x0 + mi1 + 1) * Cell, by0 = (y0 + mj0) * Cell, by1 = (y0 + mj1 + 1) * Cell;
                    bx0 = Snap(faces, 0, -1, bx0, by0, by1, top);
                    bx1 = Snap(faces, 0, 1, bx1, by0, by1, top);
                    by0 = Snap(faces, 1, -1, by0, bx0, bx1, top);
                    by1 = Snap(faces, 1, 1, by1, bx0, bx1, top);
                    if (bx1 - bx0 < 64 || by1 - by0 < 64)
                        continue;
                    boxes.Add(new HintCandidate(HintShape.Box, "box:" + cap.Id,
                        [new HintBlock(new Vec3(bx0, by0, floor), new Vec3(bx1, by1, h), HintFaces.Sides)]));
                }
            if (boxes.Count >= 2)
                boxes.Insert(0, new HintCandidate(HintShape.Box, "box:" + cap.Id, boxes.SelectMany(b => b.Blocks).ToList()));
            return boxes;
        }

        /// <summary>
        /// The facade a box side at <paramref name="at"/> belongs on: the plane of the nearest face within a
        /// cell facing <paramref name="outward"/>, overlapping the side's span and reaching near the roof.
        /// </summary>
        private static double Snap(IReadOnlyList<WallFace> faces, int axis, int outward, double at, double b0, double b1, double roof)
        {
            double best = at, distance = Cell;
            foreach (var f in faces)
                if (f.Axis == axis && f.Outward == outward && Math.Abs(f.At - at) < distance && f.B1 > b0 && f.B0 < b1 && f.Z1 >= roof - 64)
                {
                    best = f.At;
                    distance = Math.Abs(f.At - at);
                }
            return best;
        }
    }
}
