using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright.Review
{
    /// <summary>
    /// A ray-traced 3D picture of a compiled map, for seeing what an optimisation does where it is.
    ///
    /// The world comes from the BSP - every face as compiled, detail and displacements included - shaded
    /// by which way it faces; brush entities (doors, func_brush, breakables) come from the VMF, doors in
    /// brown, see-through ones as glass. Anything above <see cref="Cut"/> is left out, so a camera above
    /// the map looks down into its rooms. Overlays - hints, areaportals, occluders, refused items, leaf
    /// boundaries, leak paths - are see-through shapes with edges drawn on top: solid where nothing is in
    /// front of them, faint where something is.
    /// </summary>
    public sealed class Scene
    {
        private readonly List<Vec3> opaque = [];
        private readonly List<Rgb> opaqueColour = [];
        private readonly List<Vec3> glass = [];
        private readonly List<(Rgb Colour, double Alpha)> glassColour = [];
        private readonly List<(Vec3 A, Vec3 B, Rgb Colour)> lines = [];
        private TriangleBvh? opaqueTree, glassTree;
        private int baseGlass = -1, baseColours, baseLines;

        public static readonly Rgb Background = new(26, 28, 34);

        /// <summary>Faces wholly above this height are left out, so the camera sees into rooms; null for none.</summary>
        public double? Cut { get; }

        public Scene(double? cut = null) => Cut = cut;

        /// <summary>The world as compiled, and the brush entities of <paramref name="map"/>.</summary>
        /// <param name="sky">Draw the sky brushes; left out by default, as they are the map's outer shell and hide what is inside.</param>
        public static Scene FromMap(string bsp, VmfDocument? map, MaterialOpacity? opacity, double? cut = null, bool sky = false)
        {
            var scene = new Scene(cut);
            var geometry = BspGeometry.Load(bsp);
            for (int t = 0; t < geometry.TriangleCount; t++)
            {
                var (a, b, c) = (geometry.Corners[t * 3], geometry.Corners[t * 3 + 1], geometry.Corners[t * 3 + 2]);
                bool isSky = geometry.Sky.Length > t && geometry.Sky[t];
                if (isSky && !sky)
                    continue;
                if (geometry.Blocks[t])
                    scene.AddOpaque(a, b, c, isSky ? new Rgb(105, 145, 200) : Surface(a, b, c));
                else
                    scene.AddGlass(a, b, c, new Rgb(140, 200, 235), 0.35);
            }
            if (map == null)
                return scene;

            foreach (var entity in map.Entities)
            {
                string cls = entity.Classname ?? "";
                if (cls is "func_detail" or "func_areaportal" or "func_areaportalwindow" or "func_occluder" or "func_viscluster" or "func_instance"
                    || cls.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var solid in entity.Children("solid"))
                {
                    if (VmfBrush.Build(solid) is not { } brush)
                        continue;
                    if (brush.Faces.All(f => f.Material.StartsWith("TOOLS/", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    bool seeThrough = opacity != null && brush.Faces.Any(f => !f.Material.StartsWith("TOOLS/", StringComparison.OrdinalIgnoreCase) && !opacity.IsOpaque(f.Material));
                    bool door = cls.Contains("door", StringComparison.OrdinalIgnoreCase);
                    foreach (var face in brush.Faces)
                    {
                        if (face.Winding is not { } w || face.Material.StartsWith("TOOLS/", StringComparison.OrdinalIgnoreCase))
                            continue;
                        for (int k = 1; k + 1 < w.Points.Count; k++)
                        {
                            if (seeThrough)
                                scene.AddGlass(w.Points[0], w.Points[k], w.Points[k + 1], new Rgb(140, 200, 235), 0.35);
                            else
                                scene.AddOpaque(w.Points[0], w.Points[k], w.Points[k + 1], door ? new Rgb(150, 100, 60) : new Rgb(150, 150, 160));
                        }
                    }
                }
            }
            return scene;
        }

        private static Rgb Surface(Vec3 a, Vec3 b, Vec3 c)
        {
            var n = (b - a).Cross(c - a).Normalized();
            return n.Z > 0.7 ? new Rgb(205, 198, 186) : n.Z < -0.7 ? new Rgb(130, 130, 140) : new Rgb(168, 174, 186);
        }

        /// <summary>
        /// The part of a triangle at or below the cut, as triangles: a wall running up past the cut is
        /// trimmed to the cut, as Hammer's 3D view clips, so the rooms behind it show.
        /// </summary>
        private IEnumerable<(Vec3, Vec3, Vec3)> Below(Vec3 a, Vec3 b, Vec3 c)
        {
            if (Cut is not { } cut || Math.Max(a.Z, Math.Max(b.Z, c.Z)) <= cut)
            {
                yield return (a, b, c);
                yield break;
            }
            var kept = new List<Vec3>(4);
            Vec3[] corners = [a, b, c];
            for (int i = 0; i < 3; i++)
            {
                var p = corners[i];
                var q = corners[(i + 1) % 3];
                if (p.Z <= cut) kept.Add(p);
                if ((p.Z <= cut) != (q.Z <= cut))
                    kept.Add(p + (q - p) * ((cut - p.Z) / (q.Z - p.Z)));
            }
            for (int k = 1; k + 1 < kept.Count; k++)
                yield return (kept[0], kept[k], kept[k + 1]);
        }

        public void AddOpaque(Vec3 a, Vec3 b, Vec3 c, Rgb colour)
        {
            foreach (var (p, q, r) in Below(a, b, c))
            {
                opaque.Add(p); opaque.Add(q); opaque.Add(r);
                opaqueColour.Add(colour);
            }
            opaqueTree = null;
        }

        public void AddGlass(Vec3 a, Vec3 b, Vec3 c, Rgb colour, double alpha)
        {
            foreach (var (p, q, r) in Below(a, b, c))
            {
                glass.Add(p); glass.Add(q); glass.Add(r);
                glassColour.Add((colour, alpha));
            }
            glassTree = null;
        }

        public void AddLine(Vec3 a, Vec3 b, Rgb colour) => lines.Add((a, b, colour));

        /// <summary>From here on, what is added can be taken away again with <see cref="ClearOverlays"/>; the map stays.</summary>
        public void MarkBase()
        {
            baseGlass = glass.Count;
            baseColours = glassColour.Count;
            baseLines = lines.Count;
        }

        /// <summary>Takes away everything added since <see cref="MarkBase"/>, for drawing the next item on the same map.</summary>
        public void ClearOverlays()
        {
            if (baseGlass < 0) return;
            glass.RemoveRange(baseGlass, glass.Count - baseGlass);
            glassColour.RemoveRange(baseColours, glassColour.Count - baseColours);
            lines.RemoveRange(baseLines, lines.Count - baseLines);
            glassTree = null;
        }

        /// <summary>A see-through polygon with its outline.</summary>
        public void AddPolygon(IReadOnlyList<Vec3> points, Rgb colour, double alpha, bool outline = true)
        {
            for (int k = 1; k + 1 < points.Count; k++)
                AddGlass(points[0], points[k], points[k + 1], colour, alpha);
            if (outline)
                for (int k = 0; k < points.Count; k++)
                    AddLine(points[k], points[(k + 1) % points.Count], colour);
        }

        /// <summary>A see-through box with its twelve edges.</summary>
        public void AddBox(Vec3 lo, Vec3 hi, Rgb colour, double alpha)
        {
            Vec3 P(int i) => new((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
            int[][] quads = [[0, 1, 3, 2], [4, 5, 7, 6], [0, 1, 5, 4], [2, 3, 7, 6], [0, 2, 6, 4], [1, 3, 7, 5]];
            foreach (var q in quads)
            {
                AddGlass(P(q[0]), P(q[1]), P(q[2]), colour, alpha);
                AddGlass(P(q[0]), P(q[2]), P(q[3]), colour, alpha);
            }
            int[][] edges = [[0, 1], [2, 3], [4, 5], [6, 7], [0, 2], [1, 3], [4, 6], [5, 7], [0, 4], [1, 5], [2, 6], [3, 7]];
            foreach (var e in edges)
                AddLine(P(e[0]), P(e[1]), colour);
        }

        /// <summary>A camera at <paramref name="distance"/> from <paramref name="target"/>, turned <paramref name="yaw"/> degrees round it and looking down <paramref name="pitch"/> degrees.</summary>
        public static (Vec3 Eye, Vec3 Target) Orbit(Vec3 target, double distance, double yaw, double pitch)
        {
            double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
            var back = new Vec3(Math.Cos(p) * Math.Cos(y), Math.Cos(p) * Math.Sin(y), Math.Sin(p));
            return (target + back * distance, target);
        }

        /// <summary>
        /// A camera <paramref name="distance"/> away looking down at <paramref name="target"/> from whichever of
        /// eight sides has nothing in the way, trying the side <paramref name="preferredYaw"/> faces first.
        /// </summary>
        public (Vec3 Eye, Vec3 Target) Aim(Vec3 target, double distance, double pitch = 50, double preferredYaw = 35)
        {
            opaqueTree ??= new TriangleBvh(opaque);
            foreach (double turn in new double[] { 0, 45, -45, 90, -90, 135, -135, 180 })
            {
                var (eye, look) = Orbit(target, distance, preferredYaw + turn, pitch);
                var toward = (look - eye).Normalized();
                if (opaqueTree.Trace(eye, toward, distance - 48) < 0)
                    return (eye, look);
            }
            return Orbit(target, distance, preferredYaw, 80);
        }

        public Canvas Render(Vec3 eye, Vec3 target, int width = 1280, int height = 800, double fov = 70)
        {
            opaqueTree ??= new TriangleBvh(opaque);
            glassTree ??= new TriangleBvh(glass);
            var forward = (target - eye).Normalized();
            var right = forward.Cross(new Vec3(0, 0, 1)).Normalized();
            if (right.Length < 0.5) right = new Vec3(1, 0, 0);
            var up = right.Cross(forward).Normalized();
            double tanHalf = Math.Tan(fov * Math.PI / 360);
            double aspect = width / (double)height;
            var light = new Vec3(0.35, 0.25, 0.9).Normalized();

            var canvas = new Canvas(width, height, Background);
            var depth = new double[width * height];
            Parallel.For(0, height, SourceKitConcurrency.Options, py =>
            {
                var layers = new List<(double T, Rgb Colour, double Alpha)>(8);
                for (int px = 0; px < width; px++)
                {
                    double u = (2 * (px + 0.5) / width - 1) * tanHalf * aspect;
                    double v = (1 - 2 * (py + 0.5) / height) * tanHalf;
                    var dir = (forward + right * u + up * v).Normalized();

                    int hit = opaqueTree.Trace(eye, dir, out double t);
                    Rgb colour = Background;
                    if (hit >= 0)
                    {
                        var (a, b, c) = (opaque[hit * 3], opaque[hit * 3 + 1], opaque[hit * 3 + 2]);
                        var n = (b - a).Cross(c - a).Normalized();
                        if (n.Dot(dir) > 0) n = n * -1;
                        double shade = 0.42 + 0.58 * Math.Max(0, n.Dot(light)) + 0.12 * Math.Max(0, -n.Dot(dir));
                        var baseColour = opaqueColour[hit];
                        colour = new Rgb((byte)Math.Min(255, baseColour.R * shade), (byte)Math.Min(255, baseColour.G * shade), (byte)Math.Min(255, baseColour.B * shade));
                        colour = Rgb.Lerp(colour, Background, Math.Min(0.55, t / 12000));
                    }
                    depth[py * width + px] = hit >= 0 ? t : double.MaxValue;

                    // See-through layers in front of the surface, blended far to near.
                    layers.Clear();
                    double travelled = 0;
                    var from = eye;
                    for (int layer = 0; layer < 8; layer++)
                    {
                        double limit = (hit >= 0 ? t : 1e7) - travelled;
                        int g = glassTree.Trace(from, dir, out double gt, limit);
                        if (g < 0) break;
                        travelled += gt;
                        layers.Add((travelled, glassColour[g].Colour, glassColour[g].Alpha));
                        from = eye + dir * (travelled + 0.05);
                        travelled += 0.05;
                    }
                    for (int i = layers.Count - 1; i >= 0; i--)
                        colour = Rgb.Lerp(colour, layers[i].Colour, layers[i].Alpha);
                    canvas.Set(px, py, colour);
                }
            });

            // Edges: solid where nothing is in front of them, faint where something is.
            (double X, double Y, double D)? Project(Vec3 p)
            {
                var d = p - eye;
                double z = d.Dot(forward);
                if (z < 1) return null;
                double sx = d.Dot(right) / (z * tanHalf * aspect), sy = d.Dot(up) / (z * tanHalf);
                return ((sx + 1) / 2 * width, (1 - sy) / 2 * height, d.Length);
            }
            foreach (var (a, b, c) in lines)
            {
                if (Project(a) is not { } pa || Project(b) is not { } pb)
                    continue;
                int steps = (int)Math.Clamp(Math.Max(Math.Abs(pb.X - pa.X), Math.Abs(pb.Y - pa.Y)) * 1.5, 1, 6000);
                for (int s = 0; s <= steps; s++)
                {
                    double f = s / (double)steps;
                    if (Project(a + (b - a) * f) is not { } p)
                        continue;
                    int x = (int)p.X, y = (int)p.Y;
                    if (x < 0 || y < 0 || x >= width || y >= height)
                        continue;
                    bool seen = p.D <= depth[y * width + x] + 4;
                    canvas.Blend(x, y, c, seen ? 1 : 0.3);
                    if (seen && x + 1 < width) canvas.Blend(x + 1, y, c, 0.6);
                }
            }
            return canvas;
        }
    }
}
