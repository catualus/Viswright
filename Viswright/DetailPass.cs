using System;
using System.Collections.Generic;
using System.Linq;
using SourceKit.Compile;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>
    /// Pass A: world brushes that only cut the map into more pieces become func_detail.
    ///
    /// A world brush splits the BSP and adds leaves and portals for VVIS to flow through; a detail
    /// brush does not, and is otherwise the same - same collision, same lighting. Small and complex
    /// brushes (trim, railings, pillars, stairs) cost the most splits and hide the least, so they are
    /// the candidates. What can go wrong is a leak, because detail does not seal; the run compiles,
    /// and every candidate the leak path runs through goes back to being world.
    /// </summary>
    public sealed class DetailPass
    {
        /// <summary>Visgroup that keeps its brushes out of every pass.</summary>
        public const string IgnoreVisgroup = "viswright_ignore";

        private readonly VmfDocument map;
        private readonly Dictionary<VmfNode, VmfBrush> moved = new();
        private VmfNode? entity;

        public DetailPass(VmfDocument map) => this.map = map;

        public IReadOnlyCollection<VmfNode> Moved => moved.Keys;
        public long? EntityId => entity?.Id;

        /// <summary>
        /// The world brushes this pass may make detail, in file order. With <paramref name="compiled"/>,
        /// a compile of the same map, brushes that form the map's outer shell are left out: each
        /// becoming detail would be a leak, found one compile at a time.
        /// </summary>
        public static List<(VmfNode Solid, VmfBrush Brush)> Candidates(VmfDocument map, BspSpace? compiled = null)
        {
            var frames = PortalFrames(map);
            var ignored = IgnoredVisgroups(map);
            var result = new List<(VmfNode, VmfBrush)>();

            foreach (var solid in map.World.Children("solid"))
            {
                if (!Audit.LooksLikeDetail(solid))
                    continue;
                if (InVisgroup(solid, ignored))
                    continue;
                if (solid.Children("side").Any(s => IsLiquid(s.Get("material"))))
                    continue;

                var brush = VmfBrush.Build(solid)!;
                var size = brush.Size;
                // Very thin brushes can vanish as detail.
                if (Math.Min(size.X, Math.Min(size.Y, size.Z)) < 2)
                    continue;
                // An areaportal has to be sealed by world brushes; detail around it leaks the area.
                if (frames.Any(f => Touches(f, brush)))
                    continue;
                if (compiled != null && Seals(brush, compiled))
                    continue;

                result.Add((solid, brush));
            }
            return result;
        }

        /// <summary>Moves <paramref name="candidates"/> into one new func_detail entity.</summary>
        public void Apply(IReadOnlyCollection<(VmfNode Solid, VmfBrush Brush)> candidates)
        {
            // No entity for nothing: VBSP stops on a brush entity with no brushes.
            if (candidates.Count == 0)
                return;
            entity = new VmfNode("entity");
            map.Root.Add(entity);
            entity.Set("id", (map.MaxId() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            entity.Set("classname", "func_detail");

            foreach (var (solid, brush) in candidates)
            {
                map.World.Remove(solid);
                entity.Add(solid);
                moved[solid] = brush;
            }
        }

        /// <summary>
        /// Puts back every moved brush the leak path passes within <paramref name="reach"/> units of.
        /// Returns how many went back.
        /// </summary>
        public int RevertNear(PointFile leak, double reach)
        {
            var points = Densify(leak, 8).ToList();
            var hit = moved.Where(kv => points.Any(p => DistanceToBox(p, kv.Value.Mins, kv.Value.Maxs) <= reach))
                .Select(kv => kv.Key).ToList();
            foreach (var solid in hit)
                Revert(solid);
            return hit.Count;
        }

        /// <summary>Puts back every moved brush <paramref name="back"/> picks. Returns how many went back.</summary>
        public int RevertWhere(Func<VmfBrush, bool> back)
        {
            var hit = moved.Where(kv => back(kv.Value)).Select(kv => kv.Key).ToList();
            foreach (var solid in hit)
                Revert(solid);
            return hit.Count;
        }

        /// <summary>Puts every moved brush back and removes the func_detail entity.</summary>
        public void RevertAll()
        {
            foreach (var solid in moved.Keys.ToList())
                Revert(solid);
        }

        private void Revert(VmfNode solid)
        {
            entity!.Remove(solid);
            map.World.Add(solid);
            moved.Remove(solid);
            if (moved.Count == 0)
            {
                map.Root.Remove(entity);
                entity = null;
            }
        }

        /// <summary>What the pass is allowed to have changed: world solids it moved, and its own entity.</summary>
        public void Declare(ChangeGuard guard)
        {
            var ids = new HashSet<string>(moved.Keys.Select(s => $"world#{map.World.Id}/solid#{s.Id}"));
            string? own = entity is null ? null : $"entity#{entity.Id}";
            guard.Allow("detail", change =>
                (change.Kind == VmfChangeKind.Removed && ids.Contains(change.Path))
                || (change.Kind == VmfChangeKind.Added && change.Path == own));
        }

        private static List<(Vec3 Mins, Vec3 Maxs)> PortalFrames(VmfDocument map)
        {
            var frames = new List<(Vec3, Vec3)>();
            foreach (var e in map.Entities)
            {
                if (!Audit.PortalClasses.Contains(e.Classname ?? "", StringComparer.OrdinalIgnoreCase))
                    continue;
                foreach (var solid in e.Children("solid"))
                    if (VmfBrush.Build(solid) is { } b)
                        frames.Add((b.Mins - new Vec3(2, 2, 2), b.Maxs + new Vec3(2, 2, 2)));
            }
            return frames;
        }

        /// <summary>
        /// Whether the brush seals something detail could not: a face looks out into the void, or its
        /// faces look into two different areas, so that it is part of what keeps an areaportal's two sides
        /// apart. Points are tested half a unit in front of each face - so that a brush plugging a thin
        /// seam is caught too, its face looking into the seam - at the centre and between the centre and
        /// each corner and edge.
        /// </summary>
        private static bool Seals(VmfBrush brush, BspSpace compiled)
        {
            int area = -1;
            bool Sees(Vec3 point)
            {
                if (compiled.IsOutside(point))
                    return true;
                if (compiled.IsSolid(point))
                    return false;
                int here = compiled.AreaAt(point);
                if (area >= 0 && here != area)
                    return true;
                area = here;
                return false;
            }

            foreach (var face in brush.Faces)
            {
                if (face.Winding == null)
                    continue;
                var points = face.Winding.Points;
                var centre = face.Winding.Centre;
                var offset = face.Plane.Normal * 0.5;
                if (Sees(centre + offset))
                    return true;
                for (int i = 0; i < points.Count; i++)
                {
                    var corner = points[i];
                    var middle = (corner + points[(i + 1) % points.Count]) / 2;
                    foreach (var edgePoint in new[] { corner, middle })
                        foreach (double toward in new[] { 0.1, 0.5 })
                            if (Sees(edgePoint + (centre - edgePoint) * toward + offset))
                                return true;
                }
            }
            return false;
        }

        private static bool Touches((Vec3 Mins, Vec3 Maxs) box, VmfBrush brush) =>
            brush.Mins.X <= box.Maxs.X && brush.Maxs.X >= box.Mins.X
            && brush.Mins.Y <= box.Maxs.Y && brush.Maxs.Y >= box.Mins.Y
            && brush.Mins.Z <= box.Maxs.Z && brush.Maxs.Z >= box.Mins.Z;

        private static bool IsLiquid(string? material) =>
            material != null && (material.Contains("WATER", StringComparison.OrdinalIgnoreCase)
                || material.Contains("SLIME", StringComparison.OrdinalIgnoreCase));

        private static HashSet<string> IgnoredVisgroups(VmfDocument map)
        {
            var ids = new HashSet<string>();
            void Walk(VmfNode node)
            {
                foreach (var group in node.Children("visgroup"))
                {
                    if (string.Equals(group.Get("name"), IgnoreVisgroup, StringComparison.OrdinalIgnoreCase))
                        Collect(group);
                    else
                        Walk(group);
                }
            }
            void Collect(VmfNode group)
            {
                if (group.Get("visgroupid") is { } id)
                    ids.Add(id);
                foreach (var child in group.Children("visgroup"))
                    Collect(child);
            }
            if (map.Root.Child("visgroups") is { } visgroups)
                Walk(visgroups);
            return ids;
        }

        private static bool InVisgroup(VmfNode solid, HashSet<string> ids) =>
            ids.Count > 0 && solid.Child("editor") is { } editor
            && editor.KeyValues.Any(kv => kv.Key.Equals("visgroupid", StringComparison.OrdinalIgnoreCase) && ids.Contains(kv.Value));

        private static IEnumerable<Vec3> Densify(PointFile leak, double step)
        {
            if (leak.Points.Count == 1)
                yield return leak.Points[0];
            foreach (var (a, b) in leak.Segments())
            {
                double length = (b - a).Length;
                int n = Math.Max(1, (int)Math.Ceiling(length / step));
                for (int i = 0; i <= n; i++)
                    yield return a + (b - a) * (i / (double)n);
            }
        }

        internal static double DistanceToBox(Vec3 p, Vec3 mins, Vec3 maxs)
        {
            double dx = Math.Max(0, Math.Max(mins.X - p.X, p.X - maxs.X));
            double dy = Math.Max(0, Math.Max(mins.Y - p.Y, p.Y - maxs.Y));
            double dz = Math.Max(0, Math.Max(mins.Z - p.Z, p.Z - maxs.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
