using System;
using System.Collections.Generic;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>
    /// Pass B: faces that are pressed flat against an opaque solid brush become nodraw.
    ///
    /// VBSP removes world faces hidden inside other world brushes, but not world faces hidden behind
    /// func_detail, and not detail faces hidden behind world - so a wall whose face is covered by a
    /// detail trim still gets a lightmap and still draws. A face is changed only when every part of it
    /// is proven covered: the union of opposite-facing, coplanar faces of solid brushes made of opaque
    /// materials contains it. To see it, a player would have to be inside a solid brush.
    /// </summary>
    public sealed class NodrawPass
    {
        public const string Nodraw = "TOOLS/TOOLSNODRAW";

        /// <summary>Area, in square units, below which what is left of a face counts as nothing.</summary>
        private const double AreaSlack = 1.0;

        private readonly List<(VmfNode Side, string Before)> changed = [];
        private readonly List<Vec3> centres = [];

        public IReadOnlyList<(VmfNode Side, string Before)> Changed => changed;

        /// <summary>Where each changed face is, in the same order as <see cref="Changed"/>.</summary>
        public IReadOnlyList<Vec3> Centres => centres;

        /// <summary>
        /// Finds every fully covered face among the world and func_detail brushes of <paramref name="map"/>
        /// and makes it nodraw.
        /// </summary>
        public int Apply(VmfDocument map, MaterialOpacity opacity)
        {
            var brushes = StaticBrushes(map).ToList();

            // A brush can cover only if a player cannot see into it: every side opaque, or nodraw.
            var coverers = brushes.Where(b => b.Brush.Faces.All(f =>
                f.Winding == null
                || string.Equals(f.Material, Nodraw, StringComparison.OrdinalIgnoreCase)
                || opacity.IsOpaque(f.Material))).ToList();

            // Faces of covering brushes, keyed by their plane flipped, so a face finds what lies against it.
            var byPlane = new Dictionary<PlaneKey, List<(VmfBrush Owner, bool Detail, Winding Winding, Vec3 Mins, Vec3 Maxs)>>();
            foreach (var (_, brush, detail) in coverers)
                foreach (var face in brush.Faces)
                    if (face.Winding != null)
                    {
                        var key = PlaneKey.Of(face.Plane.Flipped);
                        if (!byPlane.TryGetValue(key, out var list))
                            byPlane[key] = list = [];
                        var (lo, hi) = Bounds(face.Winding);
                        list.Add((brush, detail, face.Winding, lo, hi));
                    }

            foreach (var (_, brush, isDetail) in brushes)
            {
                foreach (var face in brush.Faces)
                {
                    if (face.Winding == null || face.HasDisplacement)
                        continue;
                    if (face.Material.StartsWith("TOOLS/", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!byPlane.TryGetValue(PlaneKey.Of(face.Plane), out var against))
                        continue;

                    // Only faces whose bounds reach this one can cover any of it: on a floor plane shared by
                    // thousands of brushes, clipping against every one is what made this pass slow.
                    var (faceLo, faceHi) = Bounds(face.Winding);
                    var others = against.Where(c => !ReferenceEquals(c.Owner, brush) && Overlap(c.Mins, c.Maxs, faceLo, faceHi)).ToList();
                    var covers = others.Select(c => c.Winding).ToList();
                    if (covers.Count == 0 || !IsCovered(face.Winding, face.Plane.Normal, covers))
                        continue;

                    // VBSP already removes a world face covered by world, and a detail face covered by
                    // detail. Changing those as well would only make the diff longer.
                    var sameKind = others.Where(c => c.Detail == isDetail).Select(c => c.Winding).ToList();
                    if (sameKind.Count > 0 && IsCovered(face.Winding, face.Plane.Normal, sameKind))
                        continue;

                    {
                        changed.Add((face.Side, face.Material));
                        centres.Add(face.Winding.Centre);
                        face.Side.Set("material", Nodraw);
                    }
                }
            }
            return changed.Count;
        }

        public void RevertAll()
        {
            foreach (var (side, before) in changed)
                side.Set("material", before);
            changed.Clear();
            centres.Clear();
        }

        /// <summary>Gives back their material to the faces whose centre <paramref name="back"/> picks. Returns how many.</summary>
        public int RevertWhere(Func<Vec3, bool> back)
        {
            int n = 0;
            for (int i = changed.Count - 1; i >= 0; i--)
            {
                if (!back(centres[i]))
                    continue;
                changed[i].Side.Set("material", changed[i].Before);
                changed.RemoveAt(i);
                centres.RemoveAt(i);
                n++;
            }
            return n;
        }

        /// <summary>What the pass is allowed to have changed: the material of the sides it made nodraw.</summary>
        public void Declare(ChangeGuard guard)
        {
            var sideIds = new HashSet<string>(changed.Select(c => $"side#{c.Side.Id}/material"));
            guard.Allow("nodraw", change =>
                change.Kind == VmfChangeKind.ValueChanged && change.After == Nodraw
                && sideIds.Contains(string.Join('/', change.Path.Split('/').TakeLast(2))));
        }

        /// <summary>
        /// Whether the covers together contain the face. The face is cut against each cover in turn;
        /// what lies inside a cover is dropped, and the face is covered once nothing is left.
        /// </summary>
        internal static bool IsCovered(Winding face, Vec3 normal, IReadOnlyList<Winding> covers)
        {
            var remaining = new List<Winding> { face };
            foreach (var cover in covers)
            {
                var next = new List<Winding>();
                var edges = EdgePlanes(cover, normal);
                foreach (var piece in remaining)
                {
                    // Each edge plane faces out of the cover. What is in front of one is outside the
                    // cover and stays; what is behind every one is inside and goes.
                    Winding? inside = piece;
                    foreach (var edge in edges)
                    {
                        if (inside == null)
                            break;
                        var outside = inside.ClipBehind(edge.Flipped);
                        if (outside != null && outside.Area > 0.01)
                            next.Add(outside);
                        inside = inside.ClipBehind(edge);
                    }
                }
                remaining = next;
                if (remaining.Sum(w => w.Area) < AreaSlack)
                    return true;
            }
            return remaining.Sum(w => w.Area) < AreaSlack;
        }

        /// <summary>Planes through the cover's edges, at right angles to it, facing out of it.</summary>
        private static List<Plane3> EdgePlanes(Winding cover, Vec3 faceNormal)
        {
            var planes = new List<Plane3>(cover.Points.Count);
            var centre = cover.Centre;
            for (int i = 0; i < cover.Points.Count; i++)
            {
                var a = cover.Points[i];
                var b = cover.Points[(i + 1) % cover.Points.Count];
                var n = (b - a).Cross(faceNormal).Normalized();
                if (n.Length == 0)
                    continue;
                var plane = new Plane3(n, n.Dot(a));
                if (plane.DistanceTo(centre) > 0)
                    plane = plane.Flipped;
                planes.Add(plane);
            }
            return planes;
        }

        private static (Vec3 Mins, Vec3 Maxs) Bounds(Winding w) =>
            (w.Points.Aggregate(Vec3.Min), w.Points.Aggregate(Vec3.Max));

        private static bool Overlap(Vec3 aLo, Vec3 aHi, Vec3 bLo, Vec3 bHi, double slack = 0.5) =>
            aLo.X <= bHi.X + slack && aHi.X >= bLo.X - slack && aLo.Y <= bHi.Y + slack && aHi.Y >= bLo.Y - slack
            && aLo.Z <= bHi.Z + slack && aHi.Z >= bLo.Z - slack;

        /// <summary>World brushes and func_detail brushes: the ones that never move or switch off.</summary>
        private static IEnumerable<(VmfNode Solid, VmfBrush Brush, bool Detail)> StaticBrushes(VmfDocument map)
        {
            var solids = map.World.Children("solid").Select(s => (Solid: s, Detail: false))
                .Concat(map.Entities.Where(e => string.Equals(e.Classname, "func_detail", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(e => e.Children("solid")).Select(s => (Solid: s, Detail: true)));
            foreach (var (solid, detail) in solids)
            {
                // A brush carrying a displacement is dropped from the world by VBSP: it neither seals nor covers.
                if (solid.Children("side").Any(s => s.Child("dispinfo") != null))
                    continue;
                if (VmfBrush.Build(solid) is { } brush)
                    yield return (solid, brush, detail);
            }
        }

        /// <summary>A plane rounded so that two sides on the same plane get the same key.</summary>
        private readonly record struct PlaneKey(long X, long Y, long Z, long D)
        {
            public static PlaneKey Of(Plane3 p) => new(
                (long)Math.Round(p.Normal.X * 10000), (long)Math.Round(p.Normal.Y * 10000),
                (long)Math.Round(p.Normal.Z * 10000), (long)Math.Round(p.Distance * 10));
        }
    }
}
