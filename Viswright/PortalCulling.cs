using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SourceKit;
using SourceKit.Geometry;

namespace Viswright
{
    /// <summary>
    /// What the engine draws from each standing point once areaportals have had their say.
    ///
    /// VVIS's PVS says which leaves a point might see. The engine then floods out from the point's own
    /// area through the open areaportals, and each portal narrows the view to the part of the world seen
    /// through its outline; a leaf in another area is drawn only if it lies in what some way through
    /// the portals leaves of the view. A shut door's portal stops the flood, and so does a
    /// func_areaportalwindow past its fade distance. This does the same over every direction at once,
    /// counting the triangles drawn - world faces and static props, a prop drawn if any leaf it is in
    /// is - with every door shut.
    ///
    /// A portal is valued by taking it out - the flood passes through as if it were not there - and
    /// counting what more is drawn. Only points whose flood reached the portal can change, so only
    /// those are measured again.
    /// </summary>
    public sealed class PortalCulling
    {
        private readonly record struct Plane(Vec3 N, double D)
        {
            public double Distance(Vec3 p) => N.Dot(p) - D;
        }

        private readonly BspVisibilityData vis;
        private readonly int[][] clusterLeaves;
        private readonly int[][] leafProps;
        private readonly int[] propTriangles;
        private readonly Dictionary<int, List<(int Key, int Other)>> byArea = new();
        private readonly Dictionary<int, Vec3[]> polygons = new();
        private readonly Dictionary<int, (Vec3 Mins, Vec3 Maxs)> bounds = new();
        private readonly HashSet<int> doors = new();
        private readonly Dictionary<int, double> fades = new();
        private readonly (Vec3 Eye, int Cluster, int Area)[] points;
        private readonly double[] baseline;
        private readonly HashSet<int>[] touched;

        private const int MaxViewsPerArea = 8, MaxDepth = 12;

        /// <param name="maxPoints">At most this many of the standing points are used, spread evenly through them.</param>
        /// <param name="modelTriangles">The triangles each static prop model draws; without it, props are not counted.</param>
        public PortalCulling(BspVisibilityData vis, BspSpace space, SamplePoints samples, Func<string, int>? modelTriangles = null, int maxPoints = 4000)
        {
            this.vis = vis;
            var perModel = vis.StaticPropModels.Select(m => modelTriangles?.Invoke(m) ?? 0).ToArray();
            propTriangles = vis.StaticProps.Select(p => p.Model < perModel.Length ? perModel[p.Model] : 0).ToArray();
            var propsIn = new List<int>?[vis.Leaves.Count];
            for (int i = 0; i < vis.StaticProps.Count; i++)
            {
                var prop = vis.StaticProps[i];
                if (propTriangles[i] == 0)
                    continue;
                for (int k = prop.FirstLeaf; k < prop.FirstLeaf + prop.LeafCount && k < vis.StaticPropLeaves.Count; k++)
                {
                    int leaf = vis.StaticPropLeaves[k];
                    if (leaf < propsIn.Length)
                        (propsIn[leaf] ??= []).Add(i);
                }
            }
            leafProps = propsIn.Select(l => l?.ToArray() ?? []).ToArray();
            var leavesOf = new List<int>[vis.ClusterCount];
            for (int c = 0; c < leavesOf.Length; c++) leavesOf[c] = [];
            for (int i = 0; i < vis.Leaves.Count; i++)
                if (vis.Leaves[i].Cluster >= 0 && vis.Leaves[i].Cluster < leavesOf.Length)
                    leavesOf[vis.Leaves[i].Cluster].Add(i);
            clusterLeaves = leavesOf.Select(l => l.ToArray()).ToArray();

            foreach (var (key, (a, b)) in vis.AreaPortalLinks)
            {
                if (a == b || !vis.AreaPortalPolygons.TryGetValue(key, out var polygon))
                    continue;
                polygons[key] = polygon.ToArray();
                bounds[key] = (polygon.Aggregate(Vec3.Min), polygon.Aggregate(Vec3.Max));
                Link(a, key, b);
                Link(b, key, a);
            }
            foreach (var e in vis.AreaPortalEntities)
            {
                if (e.Classname == "func_areaportalwindow")
                    fades[e.Number] = e.FadeDist;
                else if (!string.IsNullOrEmpty(e.Target))
                    doors.Add(e.Number);
            }

            var all = samples.Points;
            int step = Math.Max(1, (all.Count + maxPoints - 1) / maxPoints);
            var picked = new List<(Vec3, int, int)>();
            for (int i = 0; i < all.Count; i += step)
            {
                int cluster = space.ClusterAt(all[i]);
                if (cluster >= 0 && cluster < vis.ClusterCount)
                    picked.Add((all[i], cluster, space.AreaAt(all[i])));
            }
            points = picked.ToArray();

            baseline = new double[points.Length];
            touched = new HashSet<int>[points.Length];
            Parallel.For(0, points.Length, SourceKitConcurrency.Options, () => new int[vis.Faces.Count + propTriangles.Length], (i, _, stamp) =>
            {
                var seen = new HashSet<int>();
                baseline[i] = Drawn(i, new HashSet<int>(), seen, stamp);
                touched[i] = seen;
                return stamp;
            }, _ => { });
        }

        private void Link(int area, int key, int other)
        {
            if (!byArea.TryGetValue(area, out var list))
                byArea[area] = list = [];
            list.Add((key, other));
        }

        /// <summary>How many standing points are measured.</summary>
        public int PointCount => points.Length;

        /// <summary>Triangles drawn from a standing point on average, every portal as it is.</summary>
        public double Average => points.Length == 0 ? 0 : baseline.Average();

        /// <summary>Triangles drawn on average with the portals in <paramref name="absent"/> taken out.</summary>
        public double AverageWithout(IReadOnlySet<int> absent) =>
            points.Length == 0 ? 0 : Average + Value(absent);

        /// <summary>How many more triangles a standing point draws on average with <paramref name="keys"/> taken out.</summary>
        public double Value(IReadOnlySet<int> keys)
        {
            if (points.Length == 0 || keys.Count == 0)
                return 0;
            var affected = Enumerable.Range(0, points.Length).Where(i => touched[i].Overlaps(keys)).ToArray();
            double sum = 0;
            object gate = new();
            Parallel.ForEach(affected, SourceKitConcurrency.Options, () => (Stamp: new int[vis.Faces.Count + propTriangles.Length], Sum: 0.0), (i, _, local) =>
            {
                double more = Drawn(i, keys, null, local.Stamp) - baseline[i];
                return (local.Stamp, local.Sum + more);
            }, local => { lock (gate) sum += local.Sum; });
            return sum / points.Length;
        }

        /// <summary>The triangles drawn from point <paramref name="i"/>, with <paramref name="absent"/> passed through as if not there.</summary>
        private double Drawn(int i, IReadOnlySet<int> absent, HashSet<int>? seen, int[] stamp)
        {
            var (eye, cluster, own) = points[i];
            // Each area reached, with the views into it; null is the whole view, unclipped.
            var views = new Dictionary<int, List<Plane[]>?> { [own] = null };
            var queue = new Queue<(int Area, Plane[]? View, int Depth)>();
            queue.Enqueue((own, null, 0));
            while (queue.Count > 0)
            {
                var (area, view, depth) = queue.Dequeue();
                if (depth >= MaxDepth || !byArea.TryGetValue(area, out var links))
                    continue;
                foreach (var (key, other) in links)
                {
                    seen?.Add(key);
                    Plane[]? next;
                    if (absent.Contains(key))
                        next = view;
                    else
                    {
                        if (doors.Contains(key))
                            continue;
                        var (lo, hi) = bounds[key];
                        if (fades.TryGetValue(key, out double fade) && DistanceToBox(eye, lo, hi) > fade)
                            continue;
                        // Standing in the portal itself: it narrows nothing.
                        if (DistanceToBox(eye, lo, hi) < 2)
                            next = view;
                        else
                        {
                            var clipped = Clip(polygons[key], view);
                            if (clipped.Count < 3)
                                continue;
                            next = Through(eye, clipped);
                            if (next == null)
                                continue;
                        }
                    }

                    if (views.TryGetValue(other, out var list))
                    {
                        if (list == null || list.Count >= MaxViewsPerArea)
                            continue;
                        if (next == null)
                            views[other] = null;
                        else
                            list.Add(next);
                    }
                    else
                        views[other] = next == null ? null : [next];
                    queue.Enqueue((other, next, depth + 1));
                }
            }

            // Every leaf the PVS lets through, in an area reached, inside some view of it.
            // Faces sit in more than one leaf; each is counted once.
            const int mark = 1;
            Array.Fill(stamp, 0);
            double drawn = 0;
            foreach (int c in vis.VisibleFrom(cluster))
                foreach (int l in clusterLeaves[c])
                {
                    var leaf = vis.Leaves[l];
                    if (!views.TryGetValue(leaf.Area, out var areaViews))
                        continue;
                    if (areaViews != null && !areaViews.Any(v => Touches(v, leaf.Mins, leaf.Maxs)))
                        continue;
                    for (int f = 0; f < leaf.LeafFaceCount; f++)
                    {
                        int face = vis.LeafFaces[leaf.FirstLeafFace + f];
                        if (stamp[face] == mark)
                            continue;
                        stamp[face] = mark;
                        drawn += vis.Faces[face].Triangles;
                    }
                    foreach (int prop in leafProps[l])
                    {
                        int slot = vis.Faces.Count + prop;
                        if (stamp[slot] == mark)
                            continue;
                        stamp[slot] = mark;
                        drawn += propTriangles[prop];
                    }
                }
            return drawn;
        }

        /// <summary>The view through a portal's outline from the eye: a plane through the eye along each edge, and the portal's own plane.</summary>
        private static Plane[]? Through(Vec3 eye, List<Vec3> polygon)
        {
            var centre = polygon.Aggregate((a, b) => a + b) / polygon.Count;
            var planes = new List<Plane>(polygon.Count + 1);
            for (int k = 0; k < polygon.Count; k++)
            {
                var a = polygon[k] - eye;
                var b = polygon[(k + 1) % polygon.Count] - eye;
                var n = a.Cross(b);
                if (n.Length < 1e-6)
                    continue;
                if (n.Dot(centre - eye) < 0)
                    n = new Vec3(-n.X, -n.Y, -n.Z);
                planes.Add(new Plane(n, n.Dot(eye)));
            }
            var face = (polygon[1] - polygon[0]).Cross(polygon[2] - polygon[0]);
            if (face.Length < 1e-9)
                return null;
            if (face.Dot(centre - eye) < 0)
                face = new Vec3(-face.X, -face.Y, -face.Z);
            planes.Add(new Plane(face, face.Dot(centre)));
            return planes.Count < 4 ? null : planes.ToArray();
        }

        /// <summary>A polygon cut down to the part inside every plane of <paramref name="view"/>.</summary>
        private static List<Vec3> Clip(Vec3[] polygon, Plane[]? view)
        {
            var current = polygon.ToList();
            if (view == null)
                return current;
            foreach (var plane in view)
            {
                if (current.Count < 3)
                    break;
                var next = new List<Vec3>(current.Count + 2);
                for (int k = 0; k < current.Count; k++)
                {
                    var a = current[k];
                    var b = current[(k + 1) % current.Count];
                    double da = plane.Distance(a), db = plane.Distance(b);
                    if (da >= 0) next.Add(a);
                    if ((da >= 0) != (db >= 0))
                        next.Add(a + (b - a) * (da / (da - db)));
                }
                current = next;
            }
            return current;
        }

        /// <summary>Whether a box is at least partly inside every plane.</summary>
        private static bool Touches(Plane[] view, Vec3 mins, Vec3 maxs)
        {
            foreach (var p in view)
            {
                var far = new Vec3(p.N.X >= 0 ? maxs.X : mins.X, p.N.Y >= 0 ? maxs.Y : mins.Y, p.N.Z >= 0 ? maxs.Z : mins.Z);
                if (p.Distance(far) < 0)
                    return false;
            }
            return true;
        }

        private static double DistanceToBox(Vec3 p, Vec3 lo, Vec3 hi)
        {
            double dx = Math.Max(0, Math.Max(lo.X - p.X, p.X - hi.X));
            double dy = Math.Max(0, Math.Max(lo.Y - p.Y, p.Y - hi.Y));
            double dz = Math.Max(0, Math.Max(lo.Z - p.Z, p.Z - hi.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
