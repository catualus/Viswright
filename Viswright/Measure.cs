using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SourceKit.Compile;
using SourceKit.Geometry;

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

        public static SamplePoints Find(BspSpace space)
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
                            if (!space.IsSolid(eye) && space.ClusterAt(eye) >= 0)
                                points.Add(eye);
                        }
                        aboveWasOpen = !solid;
                    }
                }
            }
            return new SamplePoints(points);
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

            double total = 0;
            int used = 0;
            foreach (var point in samples.Points)
            {
                int cluster = space.ClusterAt(point);
                if (cluster < 0 || cluster >= report.Costs.Count)
                    continue;
                total += report.Costs[cluster].Triangles;
                used++;
            }

            return new CompileMeasure(report.Clusters, portals, report.AverageVisible, used == 0 ? 0 : total / used, used,
                report.Faces, report.LeafFaces, compile.VbspRun?.Elapsed ?? TimeSpan.Zero, compile.VvisRun?.Elapsed ?? TimeSpan.Zero);
        }

        public string Describe() =>
            $"{Clusters:N0} clusters, {Portals:N0} portals, {AverageVisible:N0} clusters visible on average, " +
            $"{SampledTriangles:N0} triangles visible from a standing position ({SamplesUsed:N0} positions), " +
            $"{Faces:N0} faces; vbsp {VbspTime.TotalSeconds:0.0}s, vvis {VvisTime.TotalSeconds:0.0}s";
    }
}
