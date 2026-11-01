using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SourceKit.Geometry;

namespace Viswright.Review
{
    /// <summary>A top-down window onto the map: which part, at what eye height, and how many units a pixel covers.</summary>
    public sealed record PlanView(double X0, double Y0, double X1, double Y1, double Z, double Scale)
    {
        public int Width => (int)Math.Ceiling((X1 - X0) / Scale);
        public int Height => (int)Math.Ceiling((Y1 - Y0) / Scale);

        /// <summary>North up: y grows downwards on the picture.</summary>
        public (int X, int Y) ToPixel(double x, double y) => ((int)((x - X0) / Scale), (int)((Y1 - y) / Scale));

        public Vec3 ToWorld(int px, int py) => new(X0 + (px + 0.5) * Scale, Y1 - (py + 0.5) * Scale, Z);

        /// <summary>A view of the box round <paramref name="points"/>, with a margin, no wider or taller than <paramref name="pixels"/>.</summary>
        public static PlanView Around(IEnumerable<Vec3> points, double z, double margin, int pixels)
        {
            var list = points.ToList();
            double x0 = list.Min(p => p.X) - margin, x1 = list.Max(p => p.X) + margin;
            double y0 = list.Min(p => p.Y) - margin, y1 = list.Max(p => p.Y) + margin;
            double scale = Math.Max(1, Math.Ceiling(Math.Max(x1 - x0, y1 - y0) / pixels));
            return new PlanView(x0, y0, x1, y1, z, scale);
        }
    }

    /// <summary>
    /// Draws plans of a compiled map from its BSP: a slice through the map at eye height, world brushes
    /// dark, detail brown, open floor shaded by how far below it lies; then the heatmap, items and
    /// visibility drawn over it. Nothing but the BSP is needed, so it works on any map.
    /// </summary>
    public static class PlanRenderer
    {
        public static readonly Rgb Wall = new(58, 62, 72), Detail = new(132, 108, 82), Void = new(24, 26, 32), Drop = new(150, 160, 178);
        public static readonly Rgb Applied = new(40, 190, 90), Rejected = new(230, 60, 60), Suggested = new(250, 175, 30);

        /// <summary>
        /// The eye heights most standing points share, one per floor of the map: the busiest 64-unit bands
        /// that hold at least 3% of the points, at least 192 units apart, at most <paramref name="most"/>.
        /// </summary>
        public static List<double> Levels(SamplePoints samples, int most = 4)
        {
            if (samples.Points.Count == 0)
                return [];
            var bins = samples.Points.GroupBy(p => Math.Floor(p.Z / 64)).Select(g => (Z: g.Average(p => p.Z), N: g.Count()))
                .OrderByDescending(b => b.N).ToList();
            var levels = new List<double>();
            foreach (var (z, n) in bins)
            {
                if (n < samples.Points.Count * 0.03 && levels.Count > 0) break;
                if (levels.All(l => Math.Abs(l - z) >= 192)) levels.Add(z);
                if (levels.Count == most) break;
            }
            levels.Sort();
            return levels;
        }

        /// <summary>The slice at the view's eye height. <paramref name="open"/> marks pixels a player could see through.</summary>
        public static Canvas Base(BspSpace space, PlanView view, out bool[] open)
        {
            var canvas = new Canvas(view.Width, view.Height, Void);
            var isOpen = new bool[view.Width * view.Height];
            Parallel.For(0, view.Height, py =>
            {
                double q = view.Scale / 4;
                for (int px = 0; px < view.Width; px++)
                {
                    var p = view.ToWorld(px, py);
                    // A wall thinner than a pixel still shows: the pixel is solid if any quarter of it is.
                    Vec3? solidAt = null;
                    foreach (var offset in new[] { new Vec3(-q, -q, 0), new Vec3(q, -q, 0), new Vec3(-q, q, 0), new Vec3(q, q, 0) })
                        if (space.IsSolid(p + offset)) { solidAt = p + offset; break; }
                    Rgb c;
                    if (solidAt is { } s)
                        c = space.IsOutside(s) ? Void : space.IsStructuralSolid(s) ? Wall : Detail;
                    else if (space.ClusterAt(p) < 0)
                        c = Void;
                    else
                    {
                        isOpen[py * view.Width + px] = true;
                        double depth = 512;
                        for (double d = 32; d <= 512; d += 32)
                            if (space.IsSolid(p - new Vec3(0, 0, d))) { depth = d; break; }
                        // Indoors - a ceiling within 256 units - warm; outdoors cool.
                        bool roofed = false;
                        for (double d = 64; d <= 256 && !roofed; d += 64)
                            roofed = space.IsSolid(p + new Vec3(0, 0, d));
                        var floor = roofed ? new Rgb(232, 224, 208) : new Rgb(240, 242, 245);
                        c = depth >= 512 ? Drop : Rgb.Lerp(floor, new Rgb(170, 174, 182), depth / 512);
                    }
                    canvas.Set(px, py, c);
                }
            });
            open = isOpen;
            return canvas;
        }

        /// <summary>
        /// Colours the ground round each standing point near the view's height by <paramref name="colour"/>
        /// of its value; walls stay as they are.
        /// </summary>
        public static void Heat(Canvas canvas, bool[] open, PlanView view, SamplePoints samples, double[] values, Func<double, Rgb> colour,
            double band = 96, double alpha = 0.6)
        {
            double half = SamplePoints.Spacing / 2;
            for (int i = 0; i < samples.Points.Count; i++)
            {
                var p = samples.Points[i];
                if (values[i] < 0 || Math.Abs(p.Z - view.Z) > band)
                    continue;
                var (x0, y1) = view.ToPixel(p.X - half, p.Y - half);
                var (x1, y0) = view.ToPixel(p.X + half, p.Y + half);
                var c = colour(values[i]);
                for (int y = Math.Max(0, y0); y <= Math.Min(canvas.Height - 1, y1); y++)
                    for (int x = Math.Max(0, x0); x <= Math.Min(canvas.Width - 1, x1); x++)
                        if (open[y * canvas.Width + x])
                            canvas.Blend(x, y, c, alpha);
            }
        }

        /// <summary>Whether an item reaches the floor a view looks at.</summary>
        public static bool OnLevel(ReviewItem item, double eyeZ) => item.Mins.Z <= eyeZ + 192 && item.Maxs.Z >= eyeZ - 160;

        /// <summary>
        /// Outlines each item on the view's floor in its status colour: applied items solid, the rest faint
        /// so they do not bury them. Items in <paramref name="numbered"/> get their number beside them.
        /// </summary>
        public static void Items(Canvas canvas, PlanView view, IEnumerable<(int Number, ReviewItem Item)> items, IReadOnlySet<int>? numbered = null)
        {
            var labels = new List<(int X, int Y, string Text, Rgb Colour)>();
            foreach (var (number, item) in items.Where(i => OnLevel(i.Item, view.Z)).OrderBy(i => i.Item.Status == ItemStatus.Applied))
            {
                var colour = item.Status switch { ItemStatus.Applied => Applied, ItemStatus.Rejected => Rejected, _ => Suggested };
                var (x0, y1) = view.ToPixel(item.Mins.X, item.Mins.Y);
                var (x1, y0) = view.ToPixel(item.Maxs.X, item.Maxs.Y);
                if (x1 - x0 < 3) { x0 -= 2; x1 += 2; }
                if (y1 - y0 < 3) { y0 -= 2; y1 += 2; }
                if (item.Status == ItemStatus.Applied)
                    canvas.DrawRect(x0, y0, x1, y1, colour, 2);
                else
                    Faint(canvas, x0, y0, x1, y1, colour);
                if (numbered?.Contains(number) == true)
                    labels.Add((x1 + 2, y0 - 2, number.ToString(System.Globalization.CultureInfo.InvariantCulture), colour));
            }
            // Numbers last, so no outline crosses them.
            foreach (var (x, y, text, colour) in labels)
                canvas.Label(x, y, text, colour);
        }

        private static void Faint(Canvas canvas, int x0, int y0, int x1, int y1, Rgb colour)
        {
            for (int x = x0; x <= x1; x++) { canvas.Blend(x, y0, colour, 0.45); canvas.Blend(x, y1, colour, 0.45); }
            for (int y = y0 + 1; y < y1; y++) { canvas.Blend(x0, y, colour, 0.45); canvas.Blend(x1, y, colour, 0.45); }
        }

        /// <summary>Tints open ground green where <paramref name="from"/> can see it, by the compiled PVS, and dims the rest.</summary>
        public static void Pvs(Canvas canvas, bool[] open, PlanView view, BspSpace space, BspVisibilityData vis, Vec3 from)
        {
            int cluster = space.ClusterAt(from);
            if (cluster < 0 || !vis.HasVisibility)
                return;
            Parallel.For(0, view.Height, py =>
            {
                for (int px = 0; px < view.Width; px++)
                {
                    if (!open[py * view.Width + px])
                        continue;
                    int c = space.ClusterAt(view.ToWorld(px, py));
                    if (c >= 0 && vis.CanSee(cluster, c))
                        canvas.Blend(px, py, new Rgb(60, 200, 90), 0.45);
                    else
                        canvas.Blend(px, py, new Rgb(40, 40, 50), 0.35);
                }
            });
            var (x, y) = view.ToPixel(from.X, from.Y);
            canvas.Disc(x, y, 6, new Rgb(20, 20, 20));
            canvas.Disc(x, y, 4, new Rgb(255, 255, 255));
        }
    }
}
