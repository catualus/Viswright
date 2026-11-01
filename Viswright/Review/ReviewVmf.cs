using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright.Review
{
    /// <summary>
    /// The optimised map as a VMF to open in Hammer, with every item findable.
    ///
    /// Everything Viswright did or considered sits under a <c>Viswright</c> visgroup: the real changes
    /// under <c>Applied</c>, by pass; marker brushes for what was refused under <c>Rejected</c>, named with
    /// their number and carrying the reason in their comments; marker brushes for applied changes that
    /// are not objects of their own (a nodraw face, a key on a prop) under <c>Markers</c>; and flat cells
    /// coloured by how much is visible from where players stand under <c>Heatmap</c>, seen in Hammer's
    /// 2D views and its flat-shaded 3D view. Markers and heatmap start hidden, and hidden objects are
    /// left out by VBSP, so compiling this file still gives the optimised map. The camera list holds one
    /// camera per top item, in item order, for Hammer's Camera tool to step through.
    /// </summary>
    public static class ReviewVmf
    {
        public const int CameraCount = 64;

        private static readonly Rgb Applied = new(40, 170, 90), Rejected = new(220, 60, 60), Suggested = new(240, 170, 30);

        public static VmfDocument Build(VmfDocument working, IReadOnlyList<(int Number, ReviewItem Item)> items, SamplePoints samples,
            double[]? before, double[]? after, double[]? slack = null)
        {
            var doc = VmfDocument.Parse(working.ToString(), working.HasByteOrderMark);
            long id = doc.MaxId();
            int nextGroup = doc.Root.Descendants().Where(n => n.Name.Equals("visgroup", StringComparison.OrdinalIgnoreCase))
                .Select(n => int.TryParse(n.Get("visgroupid"), out int v) ? v : 0).DefaultIfEmpty(0).Max() + 1;

            var visgroups = doc.Root.Child("visgroups");
            if (visgroups == null)
            {
                visgroups = new VmfNode("visgroups");
                doc.Root.InsertBefore(doc.World, visgroups);
            }
            VmfNode Group(VmfNode parent, string name, Rgb colour)
            {
                var g = new VmfNode("visgroup");
                g.Set("name", name);
                g.Set("visgroupid", (nextGroup++).ToString(CultureInfo.InvariantCulture));
                g.Set("color", $"{colour.R} {colour.G} {colour.B}");
                parent.Add(g);
                return g;
            }
            var root = Group(visgroups, "Viswright", new Rgb(185, 83, 26));
            var appliedRoot = Group(root, "Applied", Applied);
            var markerRoot = Group(root, "Markers", Applied);
            var rejectedRoot = Group(root, "Rejected", Rejected);
            var suggestedRoot = Group(root, "Suggested", Suggested);
            var heatRoot = Group(root, "Heatmap", new Rgb(30, 140, 150));
            var groups = new Dictionary<string, VmfNode>();
            VmfNode Sub(VmfNode parent, string pass, Rgb colour)
            {
                string key = parent.Get("name") + "/" + pass;
                if (!groups.TryGetValue(key, out var g))
                    groups[key] = g = Group(parent, pass, colour);
                return g;
            }

            // Map objects by id, so applied items can join their visgroup. A solid inside an entity is
            // grouped through its entity; ids can repeat, so every match is grouped.
            var byId = new Dictionary<long, List<VmfNode>>();
            void Index(VmfNode node, VmfNode owner)
            {
                if (node.Id is { } k)
                {
                    if (!byId.TryGetValue(k, out var list)) byId[k] = list = [];
                    list.Add(owner);
                }
            }
            foreach (var solid in doc.World.Children("solid")) Index(solid, solid);
            foreach (var entity in doc.Entities)
            {
                Index(entity, entity);
                foreach (var solid in entity.Children("solid")) Index(solid, entity);
            }

            var hidden = new List<VmfNode>();
            foreach (var (number, item) in items)
            {
                Rgb colour = item.Status switch { ItemStatus.Applied => Applied, ItemStatus.Rejected => Rejected, _ => Suggested };
                if (item.Status == ItemStatus.Applied && item.NodeIds.Count > 0)
                {
                    var group = Sub(appliedRoot, item.Pass, colour);
                    foreach (long nodeId in item.NodeIds)
                        foreach (var node in byId.GetValueOrDefault(nodeId) ?? [])
                            Join(node, group, shown: true);
                    continue;
                }

                var parent = item.Status switch { ItemStatus.Applied => markerRoot, ItemStatus.Rejected => rejectedRoot, _ => suggestedRoot };
                var marker = new VmfNode("entity");
                marker.Set("id", (++id).ToString(CultureInfo.InvariantCulture));
                marker.Set("classname", "func_illusionary");
                marker.Set("targetname", $"vw_{number}_{item.Pass}");
                marker.Set("rendermode", "10");
                string material = item.Status switch
                {
                    ItemStatus.Applied => "TOOLS/TOOLSTRIGGER",
                    ItemStatus.Rejected => "TOOLS/TOOLSPLAYERCLIP",
                    _ => "TOOLS/TOOLSNPCCLIP",
                };
                var (lo, hi) = Grow(item.Mins, item.Maxs, 8);
                marker.Add(Box(lo, hi, material, ref id));
                var editor = Join(marker, Sub(parent, item.Pass, colour), shown: false);
                editor.Set("color", $"{colour.R} {colour.G} {colour.B}");
                editor.Set("comments", $"#{number} {item.Pass} {item.Status.ToString().ToLowerInvariant()}: {item.Reason}"
                    + (item.Detail is { } d ? $" ({d})" : "") + (item.Gain is { } g ? $"; {g:P1} less seen around it" : ""));
                hidden.Add(marker);
            }

            AddHeatmap(doc, heatRoot, Group, samples, before, after, slack, ref id);

            if (hidden.Count > 0)
            {
                var block = new VmfNode("hidden");
                foreach (var marker in hidden)
                    block.Add(marker);
                doc.Root.Add(block);
            }

            WriteCameras(doc, items, samples);
            return doc;
        }

        /// <summary>Puts <paramref name="node"/> in <paramref name="group"/>, keeping any visgroups it is already in.</summary>
        private static VmfNode Join(VmfNode node, VmfNode group, bool shown)
        {
            var editor = node.Child("editor");
            if (editor == null)
            {
                editor = new VmfNode("editor");
                editor.Set("color", "220 30 220");
                node.Add(editor);
            }
            editor.Append("visgroupid", group.Get("visgroupid")!);
            editor.Set("visgroupshown", shown ? "1" : "0");
            editor.Set("visgroupautoshown", "1");
            return editor;
        }

        /// <summary>
        /// One flat cell per 256 units of ground, at floor height, coloured by how much the standing points
        /// in it see: before the passes, after them, and the change. Cells are hidden world brushes.
        /// </summary>
        private static void AddHeatmap(VmfDocument doc, VmfNode heatRoot, Func<VmfNode, string, Rgb, VmfNode> group,
            SamplePoints samples, double[]? before, double[]? after, double[]? slack, ref long id)
        {
            if (before == null)
                return;
            const double Cell = 256;
            var cells = new Dictionary<(int, int, int), (double Before, double After, int N, double Floor, double Slack, int SlackN)>();
            for (int i = 0; i < samples.Points.Count; i++)
            {
                if (before[i] < 0 || (after != null && after[i] < 0))
                    continue;
                var p = samples.Points[i];
                var key = ((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Y / Cell), (int)Math.Floor((p.Z - SamplePoints.EyeHeight) / 128));
                var c = cells.GetValueOrDefault(key);
                bool hasSlack = slack != null && slack[i] >= 0;
                cells[key] = (c.Before + before[i], c.After + (after?[i] ?? 0), c.N + 1,
                    c.N == 0 ? p.Z - SamplePoints.EyeHeight : Math.Min(c.Floor, p.Z - SamplePoints.EyeHeight),
                    c.Slack + (hasSlack ? slack![i] : 0), c.SlackN + (hasSlack ? 1 : 0));
            }
            if (cells.Count == 0)
                return;

            double top = Percentile(cells.Values.Select(c => c.Before / c.N), 0.95);
            var layers = new List<(string Name, Func<(double Before, double After, int N, double Floor, double Slack, int SlackN), Rgb> Colour)>
            {
                ("visible before", c => Rgb.Heat(c.Before / c.N / Math.Max(1, top))),
            };
            if (after != null)
            {
                layers.Add(("visible after", c => Rgb.Heat(c.After / c.N / Math.Max(1, top))));
                layers.Add(("change", c => Rgb.Change((c.After - c.Before) / Math.Max(1, c.Before) * 5)));
            }
            if (slack != null)
                layers.Add(("PVS slack", c => c.SlackN == 0 ? new Rgb(128, 128, 128) : Rgb.Heat(c.Slack / c.SlackN)));

            var world = doc.World;
            var hiddenWorld = new VmfNode("hidden");
            int layer = 0;
            foreach (var (name, colour) in layers)
            {
                var g = group(heatRoot, name, new Rgb(30, 140, 150));
                foreach (var ((x, y, _), c) in cells)
                {
                    // Each layer a unit above the last, so they never share a face.
                    double z = c.Floor + 1 + layer * 3;
                    var solid = Box(new Vec3(x * Cell + 4, y * Cell + 4, z), new Vec3((x + 1) * Cell - 4, (y + 1) * Cell - 4, z + 2), "TOOLS/TOOLSSKIP", ref id);
                    var editor = Join(solid, g, shown: false);
                    var rgb = colour(c);
                    editor.Set("color", $"{rgb.R} {rgb.G} {rgb.B}");
                    hiddenWorld.Add(solid);
                }
                layer++;
            }
            world.Add(hiddenWorld);
        }

        /// <summary>Replaces the camera list with one camera per top item: from where a player stands nearby, looking at it.</summary>
        private static void WriteCameras(VmfDocument doc, IReadOnlyList<(int Number, ReviewItem Item)> items, SamplePoints samples)
        {
            if (doc.Root.Child("cameras") is { } old)
                doc.Root.Remove(old);
            var cameras = new VmfNode("cameras");
            cameras.Set("activecamera", "0");
            foreach (var (_, item) in Highlights(items).Take(CameraCount))
            {
                var (position, look) = Viewpoint(item, samples);
                var camera = new VmfNode("camera");
                camera.Set("position", $"[{F(position.X)} {F(position.Y)} {F(position.Z)}]");
                camera.Set("look", $"[{F(look.X)} {F(look.Y)} {F(look.Z)}]");
                cameras.Add(camera);
            }
            doc.Root.Add(cameras);
        }

        /// <summary>
        /// The items worth a camera and a picture, taken from each pass in turn so that every pass gets
        /// some: applied before rejected, biggest effect first, leaving out the many small fade and nodraw
        /// items.
        /// </summary>
        public static IEnumerable<(int Number, ReviewItem Item)> Highlights(IReadOnlyList<(int Number, ReviewItem Item)> items)
        {
            var queues = items.Where(i => i.Item.Pass is not ("fades" or "nodraw")).GroupBy(i => i.Item.Pass)
                .Select(g => new Queue<(int, ReviewItem)>(g)).ToList();
            while (queues.Any(q => q.Count > 0))
                foreach (var q in queues.Where(q => q.Count > 0))
                    yield return q.Dequeue();
        }

        /// <summary>
        /// Where to look at an item from: the standing point nearest to it, if one is within 1,536 units and
        /// not on top of it, else above and to one side.
        /// </summary>
        public static (Vec3 Position, Vec3 Look) Viewpoint(ReviewItem item, SamplePoints samples)
        {
            var look = item.Centre;
            Vec3? best = null;
            double bestDistance = double.MaxValue;
            foreach (var p in samples.Points)
            {
                double d = (p - look).Length;
                if (d >= 96 && d < bestDistance && d <= 1536)
                    (best, bestDistance) = (p, d);
            }
            return (best ?? look + new Vec3(-384, -384, 256), look);
        }

        private static (Vec3, Vec3) Grow(Vec3 lo, Vec3 hi, double minimum)
        {
            var size = hi - lo;
            var grow = new Vec3(Math.Max(0, minimum - size.X) / 2, Math.Max(0, minimum - size.Y) / 2, Math.Max(0, minimum - size.Z) / 2);
            return (lo - grow, hi + grow);
        }

        private static double Percentile(IEnumerable<double> values, double p)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
        }

        private static string F(double v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>An axis-aligned box brush with every face in <paramref name="material"/>.</summary>
        public static VmfNode Box(Vec3 lo, Vec3 hi, string material, ref long id)
        {
            string P(double x, double y, double z) => $"({F(x)} {F(y)} {F(z)})";
            (string Plane, string U, string V)[] sides =
            [
                ($"{P(lo.X, hi.Y, hi.Z)} {P(hi.X, hi.Y, hi.Z)} {P(hi.X, lo.Y, hi.Z)}", "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25"),
                ($"{P(lo.X, lo.Y, lo.Z)} {P(hi.X, lo.Y, lo.Z)} {P(hi.X, hi.Y, lo.Z)}", "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25"),
                ($"{P(lo.X, hi.Y, hi.Z)} {P(lo.X, lo.Y, hi.Z)} {P(lo.X, lo.Y, lo.Z)}", "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25"),
                ($"{P(hi.X, hi.Y, lo.Z)} {P(hi.X, lo.Y, lo.Z)} {P(hi.X, lo.Y, hi.Z)}", "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25"),
                ($"{P(hi.X, hi.Y, hi.Z)} {P(lo.X, hi.Y, hi.Z)} {P(lo.X, hi.Y, lo.Z)}", "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25"),
                ($"{P(hi.X, lo.Y, lo.Z)} {P(lo.X, lo.Y, lo.Z)} {P(lo.X, lo.Y, hi.Z)}", "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25"),
            ];
            var solid = new VmfNode("solid");
            solid.Set("id", (++id).ToString(CultureInfo.InvariantCulture));
            foreach (var (plane, u, v) in sides)
            {
                var side = new VmfNode("side");
                solid.Add(side);
                side.Set("id", (++id).ToString(CultureInfo.InvariantCulture));
                side.Set("plane", plane);
                side.Set("material", material);
                side.Set("uaxis", u);
                side.Set("vaxis", v);
                side.Set("rotation", "0");
                side.Set("lightmapscale", "16");
                side.Set("smoothing_groups", "0");
            }
            return solid;
        }
    }
}
