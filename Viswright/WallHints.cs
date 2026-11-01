using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>
    /// A vertical hint continuing the end of a wall across open space: the plane <paramref name="At"/> on
    /// axis <paramref name="Axis"/> (0: a plane of constant x, 1: of constant y), from
    /// <paramref name="From"/> to <paramref name="To"/> along the other horizontal axis, between heights
    /// <paramref name="Z0"/> and <paramref name="Z1"/>.
    /// </summary>
    public sealed record WallHint(int Axis, double At, double From, double To, double Z0, double Z1, IReadOnlyList<int> Points)
    {
        public string Id => string.Create(CultureInfo.InvariantCulture,
            $"hint:wall:{(Axis == 0 ? "x" : "y")}{Math.Round(At):0}:{Math.Round(From):0}_{Math.Round(To):0}:{Math.Round(Z0):0}");

        public Vec3 Mins => Axis == 0 ? new Vec3(At, From, Z0) : new Vec3(From, At, Z0);
        public Vec3 Maxs => Axis == 0 ? new Vec3(At, To, Z1) : new Vec3(To, At, Z1);

        public Dictionary<string, double> Data => new()
        {
            ["wallAxis"] = Axis, ["at"] = At, ["from"] = From, ["to"] = To, ["z0"] = Z0, ["z1"] = Z1,
        };

        public static WallHint FromData(IReadOnlyDictionary<string, double> d) =>
            new((int)d["wallAxis"], d["at"], d["from"], d["to"], d["z0"], d["z1"], []);
    }

    /// <summary>
    /// Where Valve's hints mostly go, found on any map: the plane of a wall's end, carried across the open
    /// space beside it.
    ///
    /// Two thirds of the hint faces in Valve's TF2 sources are vertical, axis-aligned and about 200 units
    /// across, and drawn on the maps they continue the end of a wall - a doorway's jamb, a building's
    /// corner, the mouth of a passage - across a room, a street or a yard. VBSP has that plane among its
    /// candidates already, as the wall's own end face; a hint makes it split there, so the leaves on the
    /// two sides of the opening stop being one. Which of them help is not guessed: they are measured.
    /// </summary>
    public static class WallHints
    {
        /// <summary>A wall end is a vertical face no wider than this: the wall's thickness.</summary>
        public const double MaxThickness = 64;

        public const double Reach = 2048;
        public const double Around = 512;

        public static List<WallHint> Find(VmfDocument map, BspSpace space, SamplePoints samples)
        {
            var found = new Dictionary<string, WallHint>();
            foreach (var solid in map.World.Children("solid"))
            {
                if (VmfBrush.Build(solid) is not { } brush || brush.HasDisplacement)
                    continue;
                // A wall: drawn, though it may carry nodraw on faces nobody sees; not a hint, clip or trigger.
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
                    double b0 = axis == 0 ? lo.Y : lo.X, b1 = axis == 0 ? hi.Y : hi.X;
                    if (b1 - b0 > MaxThickness || hi.Z - lo.Z < 96)
                        continue;
                    double at = axis == 0 ? lo.X : lo.Y;
                    double outward = axis == 0 ? n.X : n.Y;
                    foreach (int side in new[] { -1, 1 })
                        if (Across(space, axis, at, outward, side < 0 ? b0 : b1, side, lo.Z, hi.Z) is var (from, to, z0, z1) && to - from >= 64)
                        {
                            var hint = new WallHint(axis, at, from, to, z0, z1, []);
                            found.TryAdd(hint.Id, hint);
                        }
                }
            }

            // The standing points around each: where its effect is measured.
            var result = new List<WallHint>();
            foreach (var hint in found.Values.OrderBy(h => h.Id, StringComparer.Ordinal))
            {
                var lo = hint.Mins;
                var hi = hint.Maxs;
                var points = new List<int>();
                for (int i = 0; i < samples.Points.Count; i++)
                {
                    var p = samples.Points[i];
                    double dx = Math.Max(0, Math.Max(lo.X - p.X, p.X - hi.X)), dy = Math.Max(0, Math.Max(lo.Y - p.Y, p.Y - hi.Y));
                    if (dx * dx + dy * dy <= Around * Around && p.Z >= lo.Z - 64 && p.Z <= hi.Z + 160)
                        points.Add(i);
                }
                if (points.Count >= 4)
                    result.Add(hint with { Points = points });
            }
            return result;
        }

        /// <summary>
        /// From a wall's end edge, along the end's plane and away from the wall, how far open space runs
        /// before solid is met again; and the floor and ceiling there. Null if it runs out past
        /// <see cref="Reach"/> or opens into the void.
        /// </summary>
        private static (double From, double To, double Z0, double Z1)? Across(BspSpace space, int axis, double at, double outward, double edge, int side,
            double faceBottom, double faceTop)
        {
            // Half a unit in front of the end face, in the opening the hint would cross.
            double a = at + outward * 0.5;
            Vec3 P(double b, double z) => axis == 0 ? new Vec3(a, b, z) : new Vec3(b, a, z);
            double z = faceBottom + 48;
            var start = P(edge + side * 2, z);
            if (space.IsSolid(start) || space.ClusterAt(start) < 0)
                return null;
            double b = edge + side * 2;
            for (double run = 0; ; run += 16)
            {
                if (run > Reach)
                    return null;
                var p = P(edge + side * (2 + run), z);
                if (space.ClusterAt(p) < 0 && !space.IsSolid(p))
                    return null;
                if (space.IsSolid(p))
                {
                    b = edge + side * (2 + run);
                    break;
                }
            }
            double floor = faceBottom, ceiling = faceTop;
            for (double d = 0; d <= 256; d += 8)
                if (space.IsSolid(start - new Vec3(0, 0, d + 48))) { floor = z - d - 48; break; }
            for (double d = 0; d <= 768; d += 16)
                if (space.IsSolid(start + new Vec3(0, 0, d))) { ceiling = z + d; break; }
            ceiling = Math.Min(ceiling, faceTop);
            if (ceiling - floor < 64)
                return null;
            double from = Math.Min(edge, b), to = Math.Max(edge, b);
            return (from, to, floor, ceiling);
        }

        /// <summary>A brush 8 units thick behind the plane, hint on the face in the plane, skip on the rest.</summary>
        public static VmfNode Brush(WallHint h, long id)
        {
            var lo = h.Axis == 0 ? new Vec3(h.At - 8, h.From, h.Z0) : new Vec3(h.From, h.At - 8, h.Z0);
            var hi = h.Axis == 0 ? new Vec3(h.At, h.To, h.Z1) : new Vec3(h.To, h.At, h.Z1);
            var solid = Review.ReviewVmf.Box(lo, hi, HintPass.SkipMaterial, ref id);
            // Box faces: top, bottom, -x, +x, +y, -y. The face in the plane is +x (axis 0) or +y (axis 1).
            var sides = solid.Children("side").ToList();
            sides[h.Axis == 0 ? 3 : 4].Set("material", HintPass.HintMaterial);
            return solid;
        }
    }
}
