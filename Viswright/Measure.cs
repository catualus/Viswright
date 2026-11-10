using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SourceKit.Compile;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>
    /// Places a player could stand, taken from one compile so every version of the map is measured at
    /// the same points.
    ///
    /// Two compiles of a map do not share clusters - turning a brush into detail merges some - so their
    /// per-cluster figures cannot be compared. Points can: what can be seen from here, in this compile
    /// and in that one. The points are found once, from the baseline, as positions at eye height above
    /// solid ground on a grid, and then looked up in each compile's tree.
    /// </summary>
    public sealed class SamplePoints
    {
        public const double Spacing = 128;
        public const double Step = 16;
        public const double EyeHeight = 64;

        public IReadOnlyList<Vec3> Points { get; }

        private SamplePoints(IReadOnlyList<Vec3> points) => Points = points;

        /// <summary>Points found earlier and kept, so another compile is measured at the same places.</summary>
        public static SamplePoints From(IReadOnlyList<Vec3> points) => new(points);

        /// <summary>
        /// Points a player can stand at; with <paramref name="reachable"/>, only those in clusters it accepts.
        ///
        /// Each column of the grid is walked down from the top of the map. A player stands on a solid
        /// brush, or on a displacement - terrain - that is no steeper than they can walk. The tree has no
        /// displacements in it, so the space under terrain is open there, down to the brush the terrain was
        /// made from; below a displacement is taken as inside the ground, with nothing there a place to
        /// stand until a solid brush is passed. Which way a displacement faces does not tell terrain from
        /// an overhang - terrain is often sculpted from a brush's side - so a cave under a displacement
        /// roof goes unmeasured.
        /// </summary>
        public static SamplePoints Find(BspSpace space, Func<int, bool>? reachable = null)
        {
            double x0 = Math.Floor(space.Mins.X / Spacing) * Spacing + Spacing / 2;
            double y0 = Math.Floor(space.Mins.Y / Spacing) * Spacing + Spacing / 2;
            var terrain = TerrainByColumn(space.Displacements, x0, y0);
            int columns = Math.Max(0, (int)Math.Ceiling((space.Maxs.X - x0) / Spacing));
            var perColumn = new List<Vec3>[columns];
            System.Threading.Tasks.Parallel.For(0, columns, SourceKitConcurrency.Options, column =>
            {
                double x = x0 + column * Spacing;
                var points = perColumn[column] = [];
                int row = 0;
                for (double y = y0; y < space.Maxs.Y; y += Spacing, row++)
                {
                    void Stand(Vec3 eye)
                    {
                        if (!space.IsSolid(eye) && space.ClusterAt(eye) is >= 0 and var cluster && (reachable == null || reachable(cluster)))
                            points.Add(eye);
                    }

                    // the displacement surfaces this column passes through, from the top down
                    var surfaces = terrain.TryGetValue((column, row), out var list) ? Crossings(list, x, y) : [];
                    int next = 0;
                    bool aboveWasOpen = false, underground = false;
                    for (double z = space.Maxs.Z - 1; z > space.Mins.Z; z -= Step)
                    {
                        for (; next < surfaces.Count && surfaces[next].Z > z; next++)
                        {
                            var surface = surfaces[next];
                            if (!underground && surface.Walkable)
                                Stand(new Vec3(x, y, surface.Z + EyeHeight));
                            underground = true;
                        }
                        var here = new Vec3(x, y, z);
                        bool solid = space.IsSolid(here) || space.ClusterAt(here) < 0;
                        // The floor is just above here; stand on it.
                        if (solid && aboveWasOpen && !underground)
                            Stand(new Vec3(x, y, z + Step + EyeHeight));
                        if (solid)
                            underground = false;
                        aboveWasOpen = !solid;
                    }
                }
            });
            return new SamplePoints(perColumn.SelectMany(c => c).ToList());
        }

        /// <summary>Steepest ground a player walks up: Source's 0.7 floor normal, about 45 degrees.</summary>
        private const double WalkableNormal = 0.7;

        /// <summary>The displacement triangles over each grid column (by column and row index).</summary>
        private static Dictionary<(int, int), List<BspSpace.Triangle>> TerrainByColumn(IReadOnlyList<BspSpace.Triangle> triangles, double x0, double y0)
        {
            var byColumn = new Dictionary<(int, int), List<BspSpace.Triangle>>();
            foreach (var t in triangles)
            {
                double minX = Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)), maxX = Math.Max(t.A.X, Math.Max(t.B.X, t.C.X));
                double minY = Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)), maxY = Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y));
                for (int c = (int)Math.Ceiling((minX - x0) / Spacing); x0 + c * Spacing <= maxX; c++)
                    for (int r = (int)Math.Ceiling((minY - y0) / Spacing); y0 + r * Spacing <= maxY; r++)
                    {
                        if (!byColumn.TryGetValue((c, r), out var list))
                            byColumn[(c, r)] = list = [];
                        list.Add(t);
                    }
            }
            return byColumn;
        }

        /// <summary>Where the vertical line at (x, y) passes through the triangles, highest first.</summary>
        private static List<(double Z, bool Walkable)> Crossings(List<BspSpace.Triangle> triangles, double x, double y)
        {
            var crossings = new List<(double Z, bool Walkable)>();
            foreach (var t in triangles)
            {
                // barycentric coordinates of (x, y) in the triangle seen from above
                double d = (t.B.Y - t.C.Y) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Y - t.C.Y);
                if (Math.Abs(d) < 1e-9)
                    continue;
                double a = ((t.B.Y - t.C.Y) * (x - t.C.X) + (t.C.X - t.B.X) * (y - t.C.Y)) / d;
                double b = ((t.C.Y - t.A.Y) * (x - t.C.X) + (t.A.X - t.C.X) * (y - t.C.Y)) / d;
                double c = 1 - a - b;
                if (a < -1e-6 || b < -1e-6 || c < -1e-6)
                    continue;
                var normal = (t.B - t.A).Cross(t.C - t.A).Normalized();
                crossings.Add((a * t.A.Z + b * t.B.Z + c * t.C.Z, Math.Abs(normal.Z) >= WalkableNormal));
            }
            crossings.Sort((p, q) => q.Z.CompareTo(p.Z));
            return crossings;
        }

        /// <summary>Entities a player starts at or is sent to.</summary>
        private static readonly string[] Arrivals =
        [
            "info_player_start", "info_player_deathmatch", "info_player_combine", "info_player_rebel", "info_player_counterterrorist",
            "info_player_terrorist", "info_player_teamspawn", "gmod_player_start", "info_teleport_destination",
        ];

        /// <summary>
        /// The clusters joined by open space to somewhere a player arrives: a spawn point, or where a
        /// teleport sends them. A sealed room no player can get to - a lighting box, a storage room
        /// above the skybox - can be bigger than the map's streets, and measuring it would drown out
        /// what the passes do where people play. Null when the map has no arrival point to start from.
        /// </summary>
        public static Func<int, bool>? Reachable(VmfDocument map, PortalFile portals, BspSpace space)
        {
            var targets = map.Entities.Where(e => string.Equals(e.Classname, "trigger_teleport", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Get("target")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seeds = new List<int>();
            foreach (var e in map.Entities)
            {
                bool arrival = Arrivals.Contains(e.Classname ?? "", StringComparer.OrdinalIgnoreCase)
                    || (e.Get("targetname") is { } name && targets.Contains(name));
                if (!arrival || !Vec3.TryParse(e.Get("origin") ?? "", out var origin))
                    continue;
                // A spawn sits on the floor; look a little above it.
                int cluster = space.ClusterAt(origin + new Vec3(0, 0, 16));
                if (cluster >= 0 && cluster < portals.ClusterCount)
                    seeds.Add(cluster);
            }
            if (seeds.Count == 0)
                return null;

            var parent = Enumerable.Range(0, portals.ClusterCount).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            foreach (var p in portals.Portals)
            {
                int a = Find(p.ClusterA), b = Find(p.ClusterB);
                if (a != b) parent[a] = b;
            }
            var roots = seeds.Select(Find).ToHashSet();
            return cluster => cluster < parent.Length && roots.Contains(Find(cluster));
        }
    }

    /// <summary>One compile's figures, the ones a pass is judged on.</summary>
    public sealed record CompileMeasure(
        int Clusters,
        int Portals,
        long AverageVisible,
        double SampledTriangles,
        int SamplesUsed,
        int Faces,
        int LeafFaces,
        TimeSpan VbspTime,
        TimeSpan VvisTime)
    {
        public static CompileMeasure Of(WorkCompile compile, SamplePoints samples)
        {
            var report = VisReport.Load(compile.Bsp);
            var space = BspSpace.Load(compile.Bsp);
            int portals = File.Exists(compile.Prt) ? PortalFile.Load(compile.Prt).Portals.Count : 0;

            var perPoint = PerPoint(space, report, samples);
            double total = perPoint.Where(t => t >= 0).Sum();
            int used = perPoint.Count(t => t >= 0);

            return new CompileMeasure(report.Clusters, portals, report.AverageVisible, used == 0 ? 0 : total / used, used,
                report.Faces, report.LeafFaces, compile.VbspRun?.Elapsed ?? TimeSpan.Zero, compile.VvisRun?.Elapsed ?? TimeSpan.Zero);
        }

        /// <summary>Triangles visible from each sample point in this compile, or -1 where a point is not in open space.</summary>
        public static double[] PerPoint(WorkCompile compile, SamplePoints samples) =>
            PerPoint(BspSpace.Load(compile.Bsp), VisReport.Load(compile.Bsp), samples);

        internal static double[] PerPoint(BspSpace space, VisReport report, SamplePoints samples) =>
            samples.Points.Select(point => space.ClusterAt(point) is var c && c >= 0 && c < report.Costs.Count
                ? (double)report.Costs[c].Triangles : -1).ToArray();

        public string Describe() =>
            $"{Clusters:N0} clusters, {Portals:N0} portals, {AverageVisible:N0} clusters visible on average, " +
            $"{SampledTriangles:N0} triangles visible from a standing position ({SamplesUsed:N0} positions), " +
            $"{Faces:N0} faces; vbsp {VbspTime.TotalSeconds:0.0}s, vvis {VvisTime.TotalSeconds:0.0}s";
    }
}
