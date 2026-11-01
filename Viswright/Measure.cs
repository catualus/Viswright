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

        /// <summary>Points a player can stand at; with <paramref name="reachable"/>, only those in clusters it accepts.</summary>
        public static SamplePoints Find(BspSpace space, Func<int, bool>? reachable = null)
        {
            var points = new List<Vec3>();
            for (double x = Math.Floor(space.Mins.X / Spacing) * Spacing + Spacing / 2; x < space.Maxs.X; x += Spacing)
            {
                for (double y = Math.Floor(space.Mins.Y / Spacing) * Spacing + Spacing / 2; y < space.Maxs.Y; y += Spacing)
                {
                    bool aboveWasOpen = false;
                    for (double z = space.Maxs.Z - 1; z > space.Mins.Z; z -= Step)
                    {
                        var here = new Vec3(x, y, z);
                        bool solid = space.IsSolid(here) || space.ClusterAt(here) < 0;
                        if (solid && aboveWasOpen)
                        {
                            // The floor is just above here; stand on it.
                            var eye = new Vec3(x, y, z + Step + EyeHeight);
                            if (!space.IsSolid(eye) && space.ClusterAt(eye) is >= 0 and var cluster && (reachable == null || reachable(cluster)))
                                points.Add(eye);
                        }
                        aboveWasOpen = !solid;
                    }
                }
            }
            return new SamplePoints(points);
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
            var vis = BspVisibilityData.Load(compile.Bsp);
            var report = VisReport.Build(vis);
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
            PerPoint(BspSpace.Load(compile.Bsp), VisReport.Build(BspVisibilityData.Load(compile.Bsp)), samples);

        private static double[] PerPoint(BspSpace space, VisReport report, SamplePoints samples) =>
            samples.Points.Select(point => space.ClusterAt(point) is var c && c >= 0 && c < report.Costs.Count
                ? (double)report.Costs[c].Triangles : -1).ToArray();

        public string Describe() =>
            $"{Clusters:N0} clusters, {Portals:N0} portals, {AverageVisible:N0} clusters visible on average, " +
            $"{SampledTriangles:N0} triangles visible from a standing position ({SamplesUsed:N0} positions), " +
            $"{Faces:N0} faces; vbsp {VbspTime.TotalSeconds:0.0}s, vvis {VvisTime.TotalSeconds:0.0}s";
    }
}
