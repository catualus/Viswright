using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using SourceKit;
using SourceKit.Compile;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright.Bench
{
    /// <summary>
    /// The benchmark's tools (docs/benchmark.md):
    ///
    ///   strip   &lt;in.vmf&gt; &lt;out.vmf&gt;                          a map without its visibility optimisation
    ///   points  &lt;map.bsp&gt; &lt;map.vmf&gt; &lt;points.json&gt;            where a player can stand
    ///   measure &lt;map.bsp&gt; &lt;points.json&gt; &lt;game&gt; &lt;out.json&gt;    what is visible from those places
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                switch (args.FirstOrDefault())
                {
                    case "strip": Strip(args[1], args[2]); return 0;
                    case "points": Points(args[1], args[2], args[3]); return 0;
                    case "measure": MeasureCompile(args[1], args[2], args[3], args[4]); return 0;
                    default:
                        Console.Error.WriteLine("usage: viswright-bench strip|points|measure ...");
                        return 2;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"error: {e.Message}");
                return 1;
            }
        }

        private static readonly HashSet<string> Removed = new(StringComparer.OrdinalIgnoreCase)
        {
            "func_areaportal", "func_areaportalwindow", "func_occluder", "func_viscluster",
        };

        /// <summary>
        /// What a map looks like before anyone optimises its visibility: hint and skip brushes gone, and
        /// areaportals, occluders and visclusters gone with the stand-ins their windows show. Detail
        /// stays: every real map has it, and without it VVIS takes half an hour on even the smallest.
        /// </summary>
        private static void Strip(string input, string output)
        {
            var map = VmfDocument.Load(input);
            var world = map.World;
            int hints = 0, removed = 0;

            foreach (var solid in world.Children("solid").ToList())
            {
                var materials = solid.Children("side").Select(s => s.Get("material") ?? "").ToList();
                if (materials.Count > 0 && materials.All(m => m.Equals("TOOLS/TOOLSHINT", StringComparison.OrdinalIgnoreCase)
                        || m.Equals("TOOLS/TOOLSSKIP", StringComparison.OrdinalIgnoreCase)))
                {
                    world.Remove(solid);
                    hints++;
                }
            }

            // the brushes an areaportal window shows once shut are part of the portal
            var standIns = map.Entities.Where(e => string.Equals(e.Classname, "func_areaportalwindow", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Get("target")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in map.Entities.ToList())
            {
                string classname = entity.Classname ?? "";
                if (Removed.Contains(classname) || (entity.Get("targetname") is { } name && standIns.Contains(name)
                        && classname.Equals("func_brush", StringComparison.OrdinalIgnoreCase)))
                {
                    map.Root.Remove(entity);
                    removed++;
                }
            }
            map.Save(output);
            Console.WriteLine($"stripped: {hints} hint/skip brushes, {removed} portal/occluder/viscluster entities");
        }

        private static void Points(string bsp, string vmf, string output)
        {
            var space = BspSpace.Load(bsp);
            string prt = Path.ChangeExtension(bsp, "prt");
            var reachable = File.Exists(prt) ? SamplePoints.Reachable(VmfDocument.Load(vmf), PortalFile.Load(prt), space) : null;
            var samples = SamplePoints.Find(space, reachable);
            using var stream = File.Create(output);
            using var json = new Utf8JsonWriter(stream);
            json.WriteStartArray();
            foreach (var p in samples.Points)
            {
                json.WriteStartArray();
                json.WriteNumberValue(p.X); json.WriteNumberValue(p.Y); json.WriteNumberValue(p.Z);
                json.WriteEndArray();
            }
            json.WriteEndArray();
            Console.WriteLine($"points: {samples.Points.Count} standing points{(reachable == null ? " (no spawn found: every open point)" : " reachable from a spawn")}");
        }

        private static void MeasureCompile(string bsp, string pointsFile, string game, string output)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(pointsFile));
            var points = doc.RootElement.EnumerateArray()
                .Select(p => new Vec3(p[0].GetDouble(), p[1].GetDouble(), p[2].GetDouble())).ToList();
            var samples = SamplePoints.From(points);

            var vis = BspVisibilityData.Load(bsp);
            // Faces counted by their corners, so maps compiled with and without T-junction fixing compare.
            var report = VisReport.Build(vis, FaceCorners.Load(bsp, vis));
            var space = BspSpace.Load(bsp);
            string prt = Path.ChangeExtension(bsp, "prt");
            int portals = File.Exists(prt) ? PortalFile.Load(prt).Portals.Count : -1;

            // world triangles in the PVS of each point's cluster
            var pvs = points.Select(p => space.ClusterAt(p) is var c && c >= 0 && c < report.Costs.Count ? (double)report.Costs[c].Triangles : -1)
                .Where(t => t >= 0).ToArray();
            // world and static-prop triangles the engine draws, areaportals honoured
            var content = GameContent.Open(game);
            var drawn = new PortalCulling(vis, space, samples, content.ModelTriangles, maxPoints: int.MaxValue);

            using var stream = File.Create(output);
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            json.WriteString("bsp", Path.GetFileName(bsp));
            json.WriteNumber("points", points.Count);
            json.WriteNumber("pointsInOpenSpace", pvs.Length);
            json.WriteNumber("clusters", report.Clusters);
            json.WriteNumber("portals", portals);
            json.WriteNumber("faces", report.Faces);
            json.WriteNumber("areaportals", vis.AreaPortalEntities.Count);
            json.WriteNumber("pvsMean", pvs.Length == 0 ? 0 : pvs.Average());
            json.WriteNumber("pvsP90", Percentile(pvs, 0.9));
            json.WriteNumber("drawnMean", drawn.Average);
            json.WriteNumber("drawnP90", Percentile(drawn.PerPoint.ToArray(), 0.9));
            json.WriteNumber("drawnPoints", drawn.PointCount);
            json.WriteEndObject();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"measure: {pvs.Length} points, PVS {pvs.Average():0} mean / {Percentile(pvs, 0.9):0} p90, drawn {drawn.Average:0} mean"));
        }

        private static double Percentile(double[] values, double q)
        {
            if (values.Length == 0)
                return 0;
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * sorted.Length))];
        }
    }
}
