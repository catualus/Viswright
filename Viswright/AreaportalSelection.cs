using System;
using System.Collections.Generic;
using System.Linq;
using SourceKit;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>
    /// Which of the portals a compile placed are worth keeping.
    ///
    /// A portal is worth what the engine stops drawing because of it (see <see cref="PortalCulling"/>):
    /// with every door shut, how many more triangles a player draws on average, everywhere they can
    /// stand, once the portal is taken out. Portals that only cut anything together are valued and kept
    /// as a group: doors between the same two areas (double doors, two doors into one shop), and a door
    /// with the windows and other doorways its rooms only divide with.
    /// </summary>
    public static class AreaportalSelection
    {
        public sealed record Group(IReadOnlyList<VmfNode> Portals, IReadOnlyList<Window> Windows, double Benefit)
        {
            public Group(IReadOnlyList<VmfNode> portals, double benefit) : this(portals, [], benefit) { }

            public double BenefitPerSlot => Benefit / (Portals.Count + Windows.Count);
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

        /// <summary>The window portals of <paramref name="pass"/> that VBSP did not manage to place between two areas.</summary>
        public static List<Window> InvalidWindows(AreaportalPass pass, BspVisibilityData compiled) =>
            pass.AddedWindows.Where(a => !compiled.PortalNumbersByName.TryGetValue(a.Portal.Get("targetname")!, out int key)
                || !compiled.AreaPortalLinks.TryGetValue(key, out var link) || link.AreaA == link.AreaB).Select(a => a.Window).ToList();

        /// <summary>
        /// Values the portals of <paramref name="pass"/> in groups, best value per portal first, by what
        /// <paramref name="culling"/> says taking each group out would cost.
        /// </summary>
        public static List<Group> Rank(AreaportalPass pass, BspVisibilityData compiled, PortalCulling culling)
        {
            int Key(VmfNode portal) => compiled.PortalNumbersByName.GetValueOrDefault(portal.Get("targetname")!, -1);
            var doorPortal = pass.Added.ToDictionary(a => a.Doorway, a => a.Portal);
            var windowPortal = pass.AddedWindows.ToDictionary(a => a.Window, a => a.Portal);

            // Doors between the same two areas start out together; a door that needs another doorway
            // closed joins that doorway's group.
            var placed = pass.Added.Where(a => Key(a.Portal) is var k && k >= 0 && compiled.AreaPortalLinks.ContainsKey(k)).ToList();
            var parent = placed.ToDictionary(a => a.Doorway, a => a.Doorway);
            Doorway Find(Doorway d) { while (parent[d] != d) d = parent[d] = parent[parent[d]]; return d; }
            void Join(Doorway a, Doorway b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }
            foreach (var pair in placed.GroupBy(a => compiled.AreaPortalLinks[Key(a.Portal)] is var (x, y) ? (Math.Min(x, y), Math.Max(x, y)) : default))
            {
                var first = pair.First().Doorway;
                foreach (var a in pair.Skip(1)) Join(a.Doorway, first);
            }
            foreach (var (d, rescue) in pass.Rescued)
                foreach (var need in rescue.Doorways)
                    if (parent.ContainsKey(d) && parent.ContainsKey(need))
                        Join(d, need);

            var groups = new List<Group>();
            foreach (var members in placed.GroupBy(a => Find(a.Doorway)))
            {
                var portals = members.Select(a => a.Portal).ToList();
                var windows = members.SelectMany(a => pass.Rescued.TryGetValue(a.Doorway, out var r) ? r.Windows : [])
                    .Where(windowPortal.ContainsKey).Distinct().ToList();
                var keys = portals.Select(Key).Concat(windows.Select(w => Key(windowPortal[w]))).Where(k => k >= 0).ToHashSet();
                groups.Add(new Group(portals, windows, culling.Value(keys)));
            }

            // Best value per portal first; ties by the first portal's id, so the order is the same every run.
            return groups.OrderByDescending(g => g.BenefitPerSlot).ThenBy(g => g.Portals[0].Id).ToList();
        }

        /// <summary>
        /// The doors and windows to keep within <paramref name="slots"/>: groups with any value, taking
        /// each time the one worth most for the portals it still needs - a window another group already
        /// kept costs nothing more.
        /// </summary>
        public static (HashSet<VmfNode> Doors, HashSet<Window> Windows) Choose(IReadOnlyList<Group> ranked, int slots)
        {
            var doors = new HashSet<VmfNode>();
            var windows = new HashSet<Window>();
            var left = ranked.Where(g => g.Benefit > 0).ToList();
            while (left.Count > 0)
            {
                Group? best = null;
                double bestValue = 0;
                foreach (var g in left)
                {
                    int cost = g.Portals.Count(p => !doors.Contains(p)) + g.Windows.Count(w => !windows.Contains(w));
                    if (doors.Count + windows.Count + cost > slots)
                        continue;
                    double value = cost == 0 ? double.MaxValue : g.Benefit / cost;
                    if (best == null || value > bestValue)
                        (best, bestValue) = (g, value);
                }
                if (best == null)
                    break;
                doors.UnionWith(best.Portals);
                windows.UnionWith(best.Windows);
                left.Remove(best);
            }
            return (doors, windows);
        }

        /// <summary>
        /// Values each group of portals that close with doors, best value per portal first, by the map's
        /// area graph alone: the triangles a shut door cuts off, weighted by how much of the map is on
        /// the near side. Portals that never close are given no value.
        /// </summary>
        public static List<Group> RankByAreas(AreaportalPass pass, BspVisibilityData compiled, BspSpace space, SamplePoints samples)
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
