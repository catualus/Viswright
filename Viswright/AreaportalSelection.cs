using System;
using System.Collections.Generic;
using System.Linq;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>
    /// Which of the portals a compile placed are worth keeping.
    ///
    /// A shut door hides everything its portal cuts off: when removing the portal from the map's area
    /// graph splits it in two, a player on one side stops drawing the other. The value of a portal is
    /// that, averaged over everywhere a player can stand - the triangles of the far side, weighted by how
    /// much of the map is on the near side, both ways round. Portals between the same two areas (double
    /// doors, two doors into one shop) only cut anything together, so they are valued and kept as a
    /// group.
    /// </summary>
    public static class AreaportalSelection
    {
        public sealed record Group(IReadOnlyList<VmfNode> Portals, double Benefit)
        {
            public double BenefitPerSlot => Benefit / Portals.Count;
        }

        /// <summary>The portals of <paramref name="pass"/> that VBSP did not manage to place between two areas.</summary>
        public static List<VmfNode> Invalid(AreaportalPass pass, BspVisibilityData compiled)
        {
            var invalid = new List<VmfNode>();
            foreach (var (portal, _, _, _) in pass.Added)
            {
                string name = portal.Get("targetname")!;
                if (!compiled.PortalNumbersByName.TryGetValue(name, out int key)
                    || !compiled.AreaPortalLinks.TryGetValue(key, out var link)
                    || link.AreaA == link.AreaB)
                    invalid.Add(portal);
            }
            return invalid;
        }

        /// <summary>
        /// Values each group of portals that close with doors, best value per portal first. Portals that
        /// never close are given no value: they are kept only if there is room.
        /// </summary>
        public static List<Group> Rank(AreaportalPass pass, BspVisibilityData compiled, BspSpace space, SamplePoints samples)
        {
            // What each area holds and how many standing positions are in it.
            var triangles = new Dictionary<int, long>();
            var counted = new bool[compiled.Faces.Count];
            foreach (var leaf in compiled.Leaves)
            {
                for (int i = 0; i < leaf.LeafFaceCount; i++)
                {
                    int face = compiled.LeafFaces[leaf.FirstLeafFace + i];
                    if (counted[face]) continue;
                    counted[face] = true;
                    triangles[leaf.Area] = triangles.GetValueOrDefault(leaf.Area) + compiled.Faces[face].Triangles;
                }
            }
            var standing = new Dictionary<int, int>();
            int total = 0;
            foreach (var point in samples.Points)
            {
                if (space.ClusterAt(point) < 0) continue;
                int area = space.AreaAt(point);
                standing[area] = standing.GetValueOrDefault(area) + 1;
                total++;
            }

            // Every placed portal is an edge between its two areas.
            var edges = compiled.AreaPortalLinks.Where(kv => kv.Value.AreaA != kv.Value.AreaB)
                .Select(kv => (Key: kv.Key, kv.Value.AreaA, kv.Value.AreaB)).ToList();

            var closing = pass.Added.Where(a => a.Doorway.Closes)
                .Select(a => (a.Portal, Key: compiled.PortalNumbersByName.GetValueOrDefault(a.Portal.Get("targetname")!, -1)))
                .Where(p => p.Key >= 0 && compiled.AreaPortalLinks.ContainsKey(p.Key))
                .ToList();

            var groups = new List<Group>();
            foreach (var group in closing.GroupBy(p =>
            {
                var (a, b) = compiled.AreaPortalLinks[p.Key];
                return (Math.Min(a, b), Math.Max(a, b));
            }))
            {
                var keys = group.Select(p => p.Key).ToHashSet();
                var (start, other) = group.Key;
                var near = Reachable(start, edges.Where(e => !keys.Contains(e.Key)));
                double benefit = 0;
                if (!near.Contains(other) && total > 0)
                {
                    var far = Reachable(other, edges.Where(e => !keys.Contains(e.Key)));
                    long nearTriangles = near.Sum(a => triangles.GetValueOrDefault(a)), farTriangles = far.Sum(a => triangles.GetValueOrDefault(a));
                    long nearStanding = near.Sum(a => (long)standing.GetValueOrDefault(a)), farStanding = far.Sum(a => (long)standing.GetValueOrDefault(a));
                    benefit = (farTriangles * (double)nearStanding + nearTriangles * (double)farStanding) / total;
                }
                groups.Add(new Group(group.Select(p => p.Portal).ToList(), benefit));
            }

            // Best value per portal first; ties by the first portal's id, so the order is the same every run.
            return groups.OrderByDescending(g => g.BenefitPerSlot).ThenBy(g => g.Portals[0].Id).ToList();
        }

        /// <summary>
        /// The portals to keep within <paramref name="slots"/>: groups with any value, best first, then
        /// portals that never close while room remains.
        /// </summary>
        public static HashSet<VmfNode> Choose(AreaportalPass pass, IReadOnlyList<Group> ranked, int slots)
        {
            var keep = new HashSet<VmfNode>();
            foreach (var group in ranked.Where(g => g.Benefit > 0))
            {
                if (keep.Count + group.Portals.Count > slots) continue;
                keep.UnionWith(group.Portals);
            }
            foreach (var (portal, doorway, _, _) in pass.Added.Where(a => !a.Doorway.Closes).OrderBy(a => a.Portal.Id))
            {
                if (keep.Count >= slots) break;
                keep.Add(portal);
            }
            return keep;
        }

        /// <summary>
        /// Triangles visible from the standing positions with every portal in <paramref name="shut"/>
        /// closed, against the same with them open: what a closed portal hides beyond what VVIS already
        /// culls. A position sees a leaf only if VVIS says it can and the leaf's area is reachable from
        /// the position's own without passing a closed portal.
        /// </summary>
        public static (double Open, double Shut) VisibleWithDoorsShut(BspVisibilityData compiled, BspSpace space,
            SamplePoints samples, IReadOnlySet<int> shut)
        {
            var edges = compiled.AreaPortalLinks.Where(kv => kv.Value.AreaA != kv.Value.AreaB && !shut.Contains(kv.Key))
                .Select(kv => (kv.Key, kv.Value.AreaA, kv.Value.AreaB)).ToList();
            var component = new Dictionary<int, int>();
            foreach (var area in compiled.Leaves.Select(l => l.Area).Distinct().OrderBy(a => a))
            {
                if (component.ContainsKey(area)) continue;
                foreach (int reached in Reachable(area, edges))
                    component[reached] = area;
            }

            // Each cluster's faces, by the component of the leaf they are in.
            var faces = new Dictionary<int, List<(int Face, int Component)>>();
            foreach (var leaf in compiled.Leaves.Where(l => l.Cluster >= 0))
            {
                if (!faces.TryGetValue(leaf.Cluster, out var list)) faces[leaf.Cluster] = list = [];
                for (int i = 0; i < leaf.LeafFaceCount; i++)
                    list.Add((compiled.LeafFaces[leaf.FirstLeafFace + i], component.GetValueOrDefault(leaf.Area, leaf.Area)));
            }

            var cache = new Dictionary<(int Cluster, int Component), (long Open, long Shut)>();
            double open = 0, closed = 0;
            int used = 0;
            var seen = new HashSet<int>();
            foreach (var point in samples.Points)
            {
                int cluster = space.ClusterAt(point);
                if (cluster < 0) continue;
                int own = component.GetValueOrDefault(space.AreaAt(point), space.AreaAt(point));
                if (!cache.TryGetValue((cluster, own), out var cost))
                {
                    seen.Clear();
                    long o = 0, s = 0;
                    foreach (int visible in compiled.VisibleFrom(cluster))
                        foreach (var (face, comp) in faces.GetValueOrDefault(visible, []))
                            if (seen.Add(face))
                            {
                                int t = compiled.Faces[face].Triangles;
                                o += t;
                                if (comp == own) s += t;
                            }
                    cache[(cluster, own)] = cost = (o, s);
                }
                open += cost.Open;
                closed += cost.Shut;
                used++;
            }
            return used == 0 ? (0, 0) : (open / used, closed / used);
        }

        private static HashSet<int> Reachable(int start, IEnumerable<(int Key, int AreaA, int AreaB)> edges)
        {
            var neighbours = new Dictionary<int, List<int>>();
            foreach (var (_, a, b) in edges)
            {
                if (!neighbours.TryGetValue(a, out var la)) neighbours[a] = la = [];
                if (!neighbours.TryGetValue(b, out var lb)) neighbours[b] = lb = [];
                la.Add(b);
                lb.Add(a);
            }
            var seen = new HashSet<int> { start };
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
                foreach (int next in neighbours.GetValueOrDefault(queue.Dequeue(), []))
                    if (seen.Add(next))
                        queue.Enqueue(next);
            return seen;
        }
    }
}
