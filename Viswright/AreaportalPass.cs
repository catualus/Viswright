using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>A doorway a portal can go in, found from a door and the walls around it.</summary>
    public sealed record Doorway(
        VmfNode Door,
        int NormalAxis,
        double A0, double A1,
        double W0, double W1,
        double Z0, double Z1,
        bool Closes,
        bool StartOpen,
        double WallA0 = double.NaN,
        double WallA1 = double.NaN)
    {
        public int WidthAxis => NormalAxis == 0 ? 1 : 0;

        public Vec3 Mins => Point(A0, W0, Z0);
        public Vec3 Maxs => Point(A1, W1, Z1);

        public Vec3 Centre => (Mins + Maxs) / 2;

        public Vec3 Point(double a, double w, double z) => NormalAxis == 0 ? new Vec3(a, w, z) : new Vec3(w, a, z);
    }

    /// <summary>
    /// Pass C: an areaportal in the doorway of each door that has none, closing with the door.
    ///
    /// A closed areaportal hides everything on its far side - world, props, entities - and the server
    /// stops sending the entities there. An RP map is full of shut doors with nothing of the kind behind
    /// them. The hard part is where the portal goes: it must fill the opening exactly and be sealed on
    /// all four sides by world brushes, or the two areas leak into one. The opening is found by
    /// walking out from the door through the first compile's structural space until world brush is
    /// hit, left, right, up and down; anything that is not a clean rectangle framed by world brushes
    /// is left alone.
    /// </summary>
    public sealed partial class AreaportalPass
    {
        public const string NamePrefix = "vw_ap_";
        public const string DoorNamePrefix = "vw_door_";

        private const double MaxReach = 512;

        private readonly VmfDocument map;
        private readonly List<(VmfNode Portal, Doorway Doorway, VmfNode Door, bool NamedDoor)> added = [];

        public AreaportalPass(VmfDocument map) => this.map = map;

        public IReadOnlyList<(VmfNode Portal, Doorway Doorway, VmfNode Door, bool NamedDoor)> Added => added;

        /// <summary>Why doors were passed over, for the report.</summary>
        public Dictionary<string, int> Skipped { get; } = new();

        /// <summary>Why doorways got a portal that never closes, for the report.</summary>
        public Dictionary<string, int> OpenOnly { get; } = new();

        /// <summary>
        /// For each doorway dropped because its two sides meet some other way, the openings between leaves
        /// that join them - the shortest way round, with every other portal shut - for showing where the
        /// window or gap is.
        /// </summary>
        public Dictionary<Doorway, List<Winding>> JoinedBy { get; } = new();

        /// <summary>Each door passed over, and why, for the review.</summary>
        public List<(VmfNode Door, string Reason)> SkippedDoors { get; } = [];

        /// <summary>Why each doorway's portal never closes, for the review.</summary>
        public Dictionary<VmfNode, string> OpenOnlyDoors { get; } = new();

        /// <summary>The doorways of every door with no areaportal of its own, one per opening.</summary>
        public List<Doorway> FindDoorways(BspSpace compiled, GameContent content)
        {
            var portalTargets = new HashSet<string>(
                map.Entities.Where(e => string.Equals(e.Classname, "func_areaportal", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Get("target") ?? "").Where(t => t.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            var existing = map.Entities
                .Where(e => Audit.PortalClasses.Contains(e.Classname ?? "", StringComparer.OrdinalIgnoreCase))
                .SelectMany(e => e.Children("solid")).Select(VmfBrush.Build).OfType<VmfBrush>().ToList();

            // Brushes that never move and cannot be seen through: a door frame made of these fills the
            // edges of an opening wider than the door.
            var frames = map.World.Children("solid")
                .Concat(map.Entities.Where(e => string.Equals(e.Classname, "func_detail", StringComparison.OrdinalIgnoreCase)).SelectMany(e => e.Children("solid")))
                .Where(s => !s.Children("side").Any(side => side.Child("dispinfo") != null))
                .Select(VmfBrush.Build).OfType<VmfBrush>()
                .Where(b => b.Faces.All(f => f.Material.StartsWith("TOOLS/TOOLSNODRAW", StringComparison.OrdinalIgnoreCase) || content.Opacity.IsOpaque(f.Material)))
                .ToList();

            var doors = map.Entities.Where(e => Audit.DoorClasses.Contains(e.Classname ?? "", StringComparer.OrdinalIgnoreCase))
                .Where(door => !(door.Get("targetname") is { Length: > 0 } name && portalTargets.Contains(name))).ToList();
            // Finding a door's opening reads nothing but the compiled map and the game's files, so the
            // doors are worked on at once; what follows compares them with each other, in map order.
            var openings = new (Doorway? Doorway, string? Reason, string? OpenOnly)[doors.Count];
            System.Threading.Tasks.Parallel.For(0, doors.Count, SourceKitConcurrency.Options, i =>
            {
                var d = Find(doors[i], compiled, content, frames, out string? why, out string? only);
                openings[i] = (d, why, only);
            });

            var found = new List<Doorway>();
            for (int i = 0; i < doors.Count; i++)
            {
                var door = doors[i];
                var (doorway, reason, openOnly) = openings[i];
                if (openOnly != null)
                {
                    OpenOnly[openOnly] = OpenOnly.GetValueOrDefault(openOnly) + 1;
                    OpenOnlyDoors[door] = openOnly;
                }
                if (doorway == null)
                {
                    Skip(reason!, door);
                    continue;
                }
                if (existing.Any(b => Overlaps(b.Mins, b.Maxs, doorway.Mins, doorway.Maxs)))
                {
                    Skip("an areaportal is already in the opening", door);
                    continue;
                }
                // Double doors share one opening: one portal, closing with the first.
                if (found.Any(d => Overlaps(d.Mins, d.Maxs, doorway.Mins, doorway.Maxs)))
                {
                    Skip("shares its opening with another door", door);
                    continue;
                }
                found.Add(doorway);
            }
            return found;
        }

        private void Skip(string reason, VmfNode door)
        {
            Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;
            SkippedDoors.Add((door, reason));
        }

        /// <summary>
        /// The doorways whose portals would each divide two areas, worked out without compiling.
        ///
        /// VBSP makes areas by flooding the map's leaves without crossing an areaportal; a portal whose
        /// two sides the flood joins some other way - through a window, a gap, a second door with no
        /// portal - has one area on both sides, and VBSP reports it one leak per compile. The same flood
        /// is done here over the first compile's portal file: the openings between leaves that run
        /// through a candidate's doorway, or lie in an existing areaportal, are closed, and a candidate
        /// whose two sides still meet is dropped. Dropping one reopens its doorway, which can join
        /// another's sides, so this repeats until nothing more is dropped.
        /// </summary>
        /// <param name="rescue">
        /// Whether a doorway whose rooms meet another way may be kept by closing the windows and gaps they
        /// meet through as well (see <see cref="Rescued"/>); without it, the windows already in
        /// <see cref="Windows"/> stay closed and nothing more is added.
        /// </param>
        public List<Doorway> SealedDoorways(List<Doorway> doorways, SourceKit.Compile.PortalFile portals, BspSpace compiled, bool rescue = false)
        {
            var centres = portals.Portals.Select(p => p.Winding.Centre).ToArray();

            var existing = map.Entities
                .Where(e => Audit.PortalClasses.Contains(e.Classname ?? "", StringComparer.OrdinalIgnoreCase))
                .SelectMany(e => e.Children("solid")).Select(VmfBrush.Build).OfType<VmfBrush>()
                .Select(b => (Mins: b.Mins - new Vec3(1, 1, 1), Maxs: b.Maxs + new Vec3(1, 1, 1))).ToList();
            var alwaysClosed = new HashSet<int>();
            for (int i = 0; i < centres.Length; i++)
                if (existing.Any(box => Inside(centres[i], box.Mins, box.Maxs)))
                    alwaysClosed.Add(i);

            // The openings each candidate closes, and the clusters either side of it.
            var closes = new Dictionary<Doorway, List<int>>();
            var sides = new Dictionary<Doorway, (int, int)>();
            foreach (var d in doorways)
            {
                double lo = (double.IsNaN(d.WallA0) ? d.A0 : d.WallA0) - 1, hi = (double.IsNaN(d.WallA1) ? d.A1 : d.WallA1) + 1;
                var boxMin = d.Point(lo, d.W0, d.Z0);
                var boxMax = d.Point(hi, d.W1, d.Z1);
                closes[d] = Enumerable.Range(0, centres.Length).Where(i => Inside(centres[i], Vec3.Min(boxMin, boxMax), Vec3.Max(boxMin, boxMax))).ToList();
                sides[d] = (SideCluster(d, lo, -1, compiled), SideCluster(d, hi, +1, compiled));
            }

            List<int> WindowCuts(Window w) => Enumerable.Range(0, centres.Length)
                .Where(i => Inside(centres[i], w.Mins - new Vec3(1, 1, 1), w.Maxs + new Vec3(1, 1, 1))).ToList();
            var boxes = doorways.Select(d =>
            {
                double lo = (double.IsNaN(d.WallA0) ? d.A0 : d.WallA0) - 1, hi = (double.IsNaN(d.WallA1) ? d.A1 : d.WallA1) + 1;
                var a = d.Point(lo, d.W0, d.Z0); var b = d.Point(hi, d.W1, d.Z1);
                return (Doorway: d, Mins: Vec3.Min(a, b), Maxs: Vec3.Max(a, b));
            }).ToList();

            var kept = doorways.ToList();
            int dropped, rounds = 0;
            do
            {
                var closed = new HashSet<int>(alwaysClosed);
                foreach (var d in kept)
                    closed.UnionWith(closes[d]);
                foreach (var w in Windows)
                    closed.UnionWith(WindowCuts(w));

                var parent = Enumerable.Range(0, portals.ClusterCount).ToArray();
                int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
                for (int i = 0; i < portals.Portals.Count; i++)
                    if (!closed.Contains(i))
                    {
                        int a = Find(portals.Portals[i].ClusterA), b = Find(portals.Portals[i].ClusterB);
                        if (a != b) parent[a] = b;
                    }

                var leaky = kept.Where(d =>
                {
                    var (a, b) = sides[d];
                    return a < 0 || b < 0 || Find(a) == Find(b);
                }).ToList();

                // Each leaky doorway whose rooms can be divided by closing what joins them is kept, with
                // what it needs closed; a doorway it needs is kept too, and checked again next round.
                if (rescue && ++rounds <= 20)
                    foreach (var d in leaky.ToList())
                    {
                        var (a, b) = sides[d];
                        if (a < 0 || b < 0 || Rescue(d, a, b, closed, portals, compiled, boxes, x => closes[x], WindowCuts) is not { } found)
                            continue;
                        Rescued[d] = found;
                        foreach (var w in found.Windows.Where(w => !Windows.Contains(w)))
                            Windows.Add(w);
                        foreach (var need in found.Doorways.Where(n => !kept.Contains(n)))
                            kept.Add(need);
                        leaky.Remove(d);
                    }
                dropped = leaky.Count;
                if (leaky.Count > 0)
                {
                    // The way round each: a breadth-first walk over the open openings between clusters.
                    var next = new List<(int Portal, int Cluster)>[portals.ClusterCount];
                    for (int i = 0; i < next.Length; i++) next[i] = [];
                    for (int i = 0; i < portals.Portals.Count; i++)
                        if (!closed.Contains(i))
                        {
                            var p = portals.Portals[i];
                            next[p.ClusterA].Add((i, p.ClusterB));
                            next[p.ClusterB].Add((i, p.ClusterA));
                        }
                    foreach (var d in leaky)
                        if (sides[d] is var (from, to) && from >= 0 && to >= 0)
                            JoinedBy[d] = Path(next, from, to).Select(i => portals.Portals[i].Winding).ToList();
                }
                foreach (var d in leaky)
                {
                    kept.Remove(d);
                    var (a, b) = sides[d];
                    Skip(a < 0 || b < 0 ? "no open space on both sides of the doorway" : "the rooms either side are joined another way (a window, a gap, another door)", d.Door);
                }
            } while (dropped > 0);

            // Windows only the doorways given up needed are left out.
            var needed = WindowsNeededBy(kept);
            Windows.RemoveAll(w => !needed.Contains(w));
            foreach (var d in Rescued.Keys.Where(d => !kept.Contains(d)).ToList())
                Rescued.Remove(d);
            return kept;
        }

        /// <summary>
        /// Where a refused doorway's two sides meet: an opening in the same wall beside or above the door
        /// (a window, a transom, a gap), another way close by, or a long way round through other rooms.
        /// </summary>
        public static string LeakKind(Doorway d, IReadOnlyList<Winding> path)
        {
            double wallLo = (double.IsNaN(d.WallA0) ? d.A0 : d.WallA0) - 2, wallHi = (double.IsNaN(d.WallA1) ? d.A1 : d.WallA1) + 2;
            foreach (var w in path)
            {
                var c = w.Centre;
                double a = d.NormalAxis == 0 ? c.X : c.Y, along = d.NormalAxis == 0 ? c.Y : c.X;
                if (a >= wallLo && a <= wallHi && Math.Abs(along - (d.W0 + d.W1) / 2) < 768 && c.Z > d.Z0 - 64 && c.Z < d.Z1 + 512)
                    return c.Z > d.Z1 + 8 ? "an opening above the door in the same wall (a transom or gap) joins the rooms"
                        : "an opening beside the door in the same wall (a window or gap) joins the rooms";
            }
            return path.Count <= 6 ? "the rooms meet another way close by (another doorway or gap)" : "the rooms meet a long way round, through other rooms";
        }

        /// <summary>
        /// The opening in the doorway's own wall that joins its rooms - the window, transom or gap beside
        /// or above the door - as a box through the wall; null when the rooms meet some other way.
        /// </summary>
        public static (Vec3 Mins, Vec3 Maxs)? WallOpening(Doorway d, IReadOnlyList<Winding> path)
        {
            double wallLo = (double.IsNaN(d.WallA0) ? d.A0 : d.WallA0) - 2, wallHi = (double.IsNaN(d.WallA1) ? d.A1 : d.WallA1) + 2;
            Vec3? lo = null, hi = null;
            foreach (var w in path)
            {
                var c = w.Centre;
                double a = d.NormalAxis == 0 ? c.X : c.Y, along = d.NormalAxis == 0 ? c.Y : c.X;
                if (a < wallLo || a > wallHi || Math.Abs(along - (d.W0 + d.W1) / 2) >= 768 || c.Z <= d.Z0 - 64 || c.Z >= d.Z1 + 512)
                    continue;
                var wlo = w.Points.Aggregate(Vec3.Min);
                var whi = w.Points.Aggregate(Vec3.Max);
                lo = lo is { } l ? Vec3.Min(l, wlo) : wlo;
                hi = hi is { } h ? Vec3.Max(h, whi) : whi;
            }
            if (lo is not { } min || hi is not { } max)
                return null;
            // Through the wall's thickness, as an areaportal window has to be.
            return d.NormalAxis == 0
                ? (min with { X = wallLo + 2 }, max with { X = wallHi - 2 })
                : (min with { Y = wallLo + 2 }, max with { Y = wallHi - 2 });
        }

        private static List<int> Path(List<(int Portal, int Cluster)>[] next, int from, int to)
        {
            var via = new Dictionary<int, (int Portal, int Cluster)> { [from] = (-1, -1) };
            var queue = new Queue<int>();
            queue.Enqueue(from);
            while (queue.Count > 0 && !via.ContainsKey(to))
            {
                int c = queue.Dequeue();
                foreach (var (portal, other) in next[c])
                    if (via.TryAdd(other, (portal, c)))
                        queue.Enqueue(other);
            }
            var path = new List<int>();
            for (int c = to; via.TryGetValue(c, out var step) && step.Portal >= 0; c = step.Cluster)
                path.Add(step.Portal);
            path.Reverse();
            return path;
        }

        /// <summary>The cluster a little way out from the wall on one side of a doorway, or -1 if that is solid.</summary>
        private static int SideCluster(Doorway d, double wallFace, int sign, BspSpace compiled)
        {
            double w = (d.W0 + d.W1) / 2, z = d.Z0 + Math.Min(48, (d.Z1 - d.Z0) / 2);
            foreach (double step in new[] { 8.0, 16, 32 })
            {
                var point = d.Point(wallFace + sign * step, w, z);
                if (!compiled.IsSolid(point))
                    return compiled.ClusterAt(point);
            }
            return -1;
        }

        private static bool Inside(Vec3 p, Vec3 mins, Vec3 maxs) =>
            p.X >= mins.X && p.X <= maxs.X && p.Y >= mins.Y && p.Y <= maxs.Y && p.Z >= mins.Z && p.Z <= maxs.Z;

        /// <summary>The doorway around one door, or null with the reason there is none.</summary>
        internal static Doorway? Find(VmfNode door, BspSpace compiled, GameContent content, IReadOnlyList<VmfBrush> frames,
            out string? reason, out string? openOnly)
        {
            reason = null;
            openOnly = null;
            var box = DoorBox(door, content, out bool opaque, out reason);
            if (box == null)
                return null;
            var (mins, maxs) = box.Value;
            var size = maxs - mins;

            // The door's thin side is the way it faces; only doors square to the grid are handled.
            int normal = size.X <= size.Y ? 0 : 1;
            int width = 1 - normal;
            if (size[normal] > 24 || size[width] < 24 || size.Z < 48)
            {
                reason = "the door is not a slab square to the grid";
                return null;
            }

            var centre = (mins + maxs) / 2;
            if (compiled.IsStructuralSolid(centre))
            {
                reason = "the door is inside a wall";
                return null;
            }

            // Walk out from the door's centre until world brush is reached on each side.
            Vec3 Along(int axis, double distance) => axis switch
            {
                0 => centre with { X = centre.X + distance },
                1 => centre with { Y = centre.Y + distance },
                _ => centre with { Z = centre.Z + distance },
            };
            double? Reach(int axis, int sign)
            {
                for (double d = 1; d <= MaxReach; d += 1)
                    if (compiled.IsStructuralSolid(Along(axis, sign * d)))
                        return Math.Round(centre[axis] + sign * (d - 0.5));
                return null;
            }

            double? w0 = Reach(width, -1), w1 = Reach(width, +1), z0 = Reach(2, -1), z1 = Reach(2, +1);
            if (w0 == null || w1 == null || z0 == null || z1 == null)
            {
                reason = "no world brush frames the opening";
                return null;
            }
            double openWidth = w1.Value - w0.Value, openHeight = z1.Value - z0.Value;
            if (openWidth < 24 || openWidth > 256 || openHeight < 48 || openHeight > 256)
            {
                reason = "the opening is not door-sized";
                return null;
            }

            Vec3 At(double a, double w, double z) => normal == 0 ? new Vec3(a, w, z) : new Vec3(w, a, z);
            double ca = centre[normal];

            // The wall's thickness at the jambs and the head: the portal must sit within all three.
            (double Lo, double Hi)? Thickness(double w, double z)
            {
                if (!compiled.IsStructuralSolid(At(ca, w, z)))
                    return null;
                double lo = ca, hi = ca;
                while (hi - ca < 64 && compiled.IsStructuralSolid(At(hi + 1, w, z))) hi += 1;
                while (ca - lo < 64 && compiled.IsStructuralSolid(At(lo - 1, w, z))) lo -= 1;
                return (lo - 0.5, hi + 0.5);
            }
            double midZ = (z0.Value + z1.Value) / 2, midW = (w0.Value + w1.Value) / 2;
            var left = Thickness(w0.Value - 1, midZ);
            var right = Thickness(w1.Value + 1, midZ);
            var head = Thickness(midW, z1.Value + 1);
            if (left == null || right == null || head == null)
            {
                reason = "no world brush frames the opening";
                return null;
            }
            double aLo = Math.Max(left.Value.Lo, Math.Max(right.Value.Lo, head.Value.Lo));
            double aHi = Math.Min(left.Value.Hi, Math.Min(right.Value.Hi, head.Value.Hi));
            double a0 = Math.Ceiling(Math.Max(aLo, ca - 2)), a1 = Math.Floor(Math.Min(aHi, ca + 2));
            if (a1 - a0 < 1)
            {
                reason = "the door is not inside the wall's thickness";
                return null;
            }

            // A clean rectangle: world brush all the way round, nothing structural inside.
            double am = (a0 + a1) / 2;
            for (double z = z0.Value + 2; z < z1.Value - 1; z += 8)
                if (!compiled.IsStructuralSolid(At(am, w0.Value - 1, z)) || !compiled.IsStructuralSolid(At(am, w1.Value + 1, z)))
                {
                    reason = "the opening is not a rectangle framed by world brushes";
                    return null;
                }
            for (double w = w0.Value + 2; w < w1.Value - 1; w += 8)
            {
                if (!compiled.IsStructuralSolid(At(am, w, z1.Value + 1)) || !compiled.IsStructuralSolid(At(am, w, z0.Value - 1)))
                {
                    reason = "the opening is not a rectangle framed by world brushes";
                    return null;
                }
                for (double z = z0.Value + 2; z < z1.Value - 1; z += 8)
                    if (compiled.IsStructuralSolid(At(am, w, z)))
                    {
                        reason = "the opening is not a rectangle framed by world brushes";
                        return null;
                    }
            }

            // The portal closes with the door only if the door, with any opaque frame around it, fills the
            // opening, and the door cannot be seen through.
            double depthLo = aLo - 8, depthHi = aHi + 8;
            var blockers = new List<(double W0, double W1, double Z0, double Z1)> { (mins[width], maxs[width], mins.Z, maxs.Z) };
            foreach (var f in frames)
                if (f.Maxs[normal] > depthLo && f.Mins[normal] < depthHi
                    && f.Maxs[width] > w0.Value && f.Mins[width] < w1.Value && f.Maxs.Z > z0.Value && f.Mins.Z < z1.Value)
                    blockers.Add((f.Mins[width], f.Maxs[width], f.Mins.Z, f.Maxs.Z));
            bool covers = true;
            for (double w = w0.Value + 1; w < w1.Value && covers; w += 2)
                for (double z = z0.Value + 1; z < z1.Value && covers; z += 2)
                    covers = blockers.Any(b => w >= b.W0 && w <= b.W1 && z >= b.Z0 && z <= b.Z1);
            if (!covers)
                openOnly = "the door and its frame do not fill the opening";
            else if (!opaque)
                openOnly = "the door can be seen through, or its materials were not found";
            bool startOpen = StartsOpen(door);

            // A portal that closes with its door lies inside the door, so the door reaches into the areas
            // on both sides: the engine draws a door from an area only if the door is in it, and a door
            // wholly inside the portal belongs to one side alone and vanishes from the other once shut.
            bool closes = covers && opaque;
            if (closes)
            {
                if (InsideDoor(a0, a1, mins[normal], maxs[normal]) is { } inside)
                    (a0, a1) = inside;
                else
                {
                    closes = false;
                    openOnly = TooThin;
                }
            }

            // One unit into the frame on every side, so the seal does not depend on an exact edge.
            return new Doorway(door, normal, a0, a1, w0.Value - 1, w1.Value + 1, z0.Value - 1, z1.Value + 1,
                Closes: closes, StartOpen: startOpen, WallA0: aLo, WallA1: aHi);
        }

        private const string TooThin = "the door is too thin, or too far from the wall's middle, for a portal inside it";

        /// <summary>
        /// The part of the slab <paramref name="a0"/>..<paramref name="a1"/> half a unit inside a door
        /// running <paramref name="lo"/>..<paramref name="hi"/> through the wall, on the half-unit grid;
        /// null when that is under a unit thick.
        /// </summary>
        private static (double A0, double A1)? InsideDoor(double a0, double a1, double lo, double hi)
        {
            double d0 = Math.Ceiling(Math.Max(a0, lo + 0.5) * 2) / 2, d1 = Math.Floor(Math.Min(a1, hi - 0.5) * 2) / 2;
            return d1 - d0 < 1 ? null : (d0, d1);
        }

        /// <summary>
        /// A doorway decided earlier, its closing portal moved inside its door as <see cref="Find"/> now
        /// places it: decisions written before then put some portals round thin doors instead.
        /// </summary>
        internal static Doorway Seated(Doorway d, GameContent content)
        {
            if (!d.Closes || DoorBox(d.Door, content, out _, out _) is not { } box)
                return d;
            return InsideDoor(d.A0, d.A1, box.Mins[d.NormalAxis], box.Maxs[d.NormalAxis]) is { } inside
                ? d with { A0 = inside.A0, A1 = inside.A1 }
                : d with { Closes = false };
        }

        /// <summary>The door's box in its closed position, as placed.</summary>
        private static (Vec3 Mins, Vec3 Maxs)? DoorBox(VmfNode door, GameContent content, out bool opaque, out string? reason)
        {
            opaque = false;
            reason = null;
            if (string.Equals(door.Classname, "prop_door_rotating", StringComparison.OrdinalIgnoreCase))
            {
                string? modelPath = door.Get("model");
                var model = modelPath == null ? null : content.Model(modelPath);
                if (model == null || !Vec3.TryParse(door.Get("origin") ?? "", out var origin))
                {
                    reason = "the door's model was not found";
                    return null;
                }
                var angles = door.Get("angles") is { } a && Vec3.TryParse(a, out var parsed) ? parsed : Vec3.Zero;
                double yaw = ((angles.Y % 360) + 360) % 360;
                if (Math.Abs(angles.X) > 1 || Math.Abs(angles.Z) > 1 || Math.Abs(yaw - Math.Round(yaw / 90) * 90) > 1)
                {
                    reason = "the door is not square to the grid";
                    return null;
                }
                int quarter = (int)Math.Round(yaw / 90) % 4;
                var lo = new Vec3(model.HullMin.X, model.HullMin.Y, model.HullMin.Z);
                var hi = new Vec3(model.HullMax.X, model.HullMax.Y, model.HullMax.Z);
                // Yaw turns model x towards world y.
                (Vec3, Vec3) Turn(Vec3 l, Vec3 h) => quarter switch
                {
                    1 => (new Vec3(-h.Y, l.X, l.Z), new Vec3(-l.Y, h.X, h.Z)),
                    2 => (new Vec3(-h.X, -h.Y, l.Z), new Vec3(-l.X, -l.Y, h.Z)),
                    3 => (new Vec3(l.Y, -h.X, l.Z), new Vec3(h.Y, -l.X, h.Z)),
                    _ => (l, h),
                };
                var (tl, th) = Turn(lo, hi);
                opaque = content.IsModelOpaque(model);
                return (origin + tl, origin + th);
            }

            var brushes = door.Children("solid").Select(VmfBrush.Build).OfType<VmfBrush>().ToList();
            if (brushes.Count == 0)
            {
                reason = "the door has no brushes";
                return null;
            }
            opaque = brushes.SelectMany(b => b.Faces).All(f =>
                f.Material.StartsWith("TOOLS/", StringComparison.OrdinalIgnoreCase) || content.Opacity.IsOpaque(f.Material));
            return (brushes.Select(b => b.Mins).Aggregate(Vec3.Min), brushes.Select(b => b.Maxs).Aggregate(Vec3.Max));
        }

        /// <summary>
        /// Whether the door begins open: spawnflag 1 on func_door and func_door_rotating, a non-zero
        /// spawnpos on prop_door_rotating.
        /// </summary>
        private static bool StartsOpen(VmfNode door)
        {
            if (string.Equals(door.Classname, "prop_door_rotating", StringComparison.OrdinalIgnoreCase))
                return door.Get("spawnpos") is { } pos && pos.Trim() != "0";
            return int.TryParse(door.Get("spawnflags"), out int flags) && (flags & 1) != 0;
        }

        /// <summary>Adds a func_areaportal in each doorway; one that closes is named to the door, naming the door if it has no name.</summary>
        public void Apply(IEnumerable<Doorway> doorways)
        {
            long id = map.MaxId();
            // Hammer ids are not always unique in a map, so a name built from one is checked against
            // every name in use: two doors sharing a name would each close the other's portal.
            var names = new HashSet<string>(
                map.Root.Descendants().Select(n => n.Get("targetname")).OfType<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var d in doorways)
            {
                var portal = new VmfNode("entity");
                map.Root.Add(portal);
                long portalId = ++id;
                portal.Set("id", Invariant(portalId));
                portal.Set("classname", "func_areaportal");
                portal.Set("targetname", NamePrefix + Invariant(portalId));
                portal.Set("PortalVersion", "1");

                bool named = false;
                if (d.Closes)
                {
                    string? doorName = d.Door.Get("targetname");
                    if (string.IsNullOrEmpty(doorName))
                    {
                        string stem = DoorNamePrefix + Invariant(d.Door.Id ?? portalId);
                        doorName = stem;
                        for (int n = 2; names.Contains(doorName); n++)
                            doorName = stem + "_" + Invariant(n);
                        names.Add(doorName);
                        d.Door.Set("targetname", doorName);
                        named = true;
                    }
                    portal.Set("target", doorName);
                    portal.Set("StartOpen", d.StartOpen ? "1" : "0");
                }
                else
                    portal.Set("StartOpen", "1");

                id = AddBox(portal, d.Mins, d.Maxs, id, _ => "TOOLS/TOOLSAREAPORTAL");
                added.Add((portal, d, d.Door, named));
            }
        }

        /// <summary>Removes the portals for <paramref name="keep"/>'s complement, and any name given to a door that loses its portal.</summary>
        /// <summary>Portals taken out again during the run, and why, for the review.</summary>
        public List<(Doorway Doorway, VmfNode Door, string Reason)> Dropped { get; } = [];

        public void KeepOnly(IReadOnlySet<VmfNode> keep, string? why = null)
        {
            foreach (var entry in added.Where(a => !keep.Contains(a.Portal)).ToList())
            {
                if (why != null)
                    Dropped.Add((entry.Doorway, entry.Door, why));
                map.Root.Remove(entry.Portal);
                if (entry.NamedDoor && entry.Door.GetPair("targetname") is { } pair)
                    entry.Door.Remove(pair);
                added.Remove(entry);
            }
        }

        public void RevertAll(string? why = null)
        {
            KeepOnly(new HashSet<VmfNode>(), why);
            KeepWindows(new HashSet<Window>());
        }

        /// <summary>What the pass may have changed: its own portal entities, and the names it gave doors.</summary>
        public void Declare(ChangeGuard guard)
        {
            var portals = new HashSet<string>(added.Select(a => $"entity#{a.Portal.Id}")
                .Concat(addedWindows.SelectMany(a => a.StandIn == null ? [a.Portal] : new[] { a.Portal, a.StandIn }).Select(n => $"entity#{n.Id}")));
            // By the name given rather than the door's id alone: ids can repeat, and the diff tells two
            // blocks with one id apart by position, which a path built here cannot predict.
            var named = new HashSet<string>(added.Where(a => a.NamedDoor).Select(a => a.Door.Get("targetname")!), StringComparer.OrdinalIgnoreCase);
            guard.Allow("areaportals", change =>
                change.Kind == VmfChangeKind.Added
                && (portals.Contains(change.Path)
                    || (change.Path.StartsWith("entity#", StringComparison.Ordinal) && change.Path.EndsWith("/targetname", StringComparison.Ordinal)
                        && change.Path.Count(c => c == '/') == 1 && change.After != null && named.Contains(change.After))));
        }

        /// <summary>Adds a box brush to <paramref name="entity"/>, each face's material chosen by the axis it faces along.</summary>
        /// <param name="axes">A face's texture axes by its index (top, bottom, -x, +x, +y, -y), where they are not the default.</param>
        private static long AddBox(VmfNode entity, Vec3 lo, Vec3 hi, long id, Func<int, string> material, Func<int, (string U, string V)?>? axes = null)
        {
            string F(double v) => Invariant(Math.Round(v, 2));
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
            entity.Add(solid);
            solid.Set("id", Invariant(++id));
            for (int f = 0; f < sides.Length; f++)
            {
                var (plane, u, v) = sides[f];
                if (axes?.Invoke(f) is var (au, av))
                    (u, v) = (au, av);
                var side = new VmfNode("side");
                solid.Add(side);
                side.Set("id", Invariant(++id));
                side.Set("plane", plane);
                side.Set("material", material(f));
                side.Set("uaxis", u);
                side.Set("vaxis", v);
                side.Set("rotation", "0");
                side.Set("lightmapscale", "16");
                side.Set("smoothing_groups", "0");
            }
            return id;
        }

        private static bool Overlaps(Vec3 aMin, Vec3 aMax, Vec3 bMin, Vec3 bMax) =>
            aMin.X < bMax.X && aMax.X > bMin.X && aMin.Y < bMax.Y && aMax.Y > bMin.Y && aMin.Z < bMax.Z && aMax.Z > bMin.Z;

        private static string Invariant(long v) => v.ToString(CultureInfo.InvariantCulture);
        private static string Invariant(double v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
