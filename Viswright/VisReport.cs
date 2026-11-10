using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SourceKit.Geometry;

namespace Viswright
{
    /// <summary>One cluster's share of the rendering cost: what it can see, in clusters and in triangles.</summary>
    public sealed record ClusterCost(int Cluster, int VisibleClusters, long Triangles, Vec3 Centre);

    /// <summary>
    /// How expensive a compiled map's visibility is, measured from the BSP alone.
    ///
    /// VVIS reports how many clusters each cluster sees on average, which treats a broom cupboard and a
    /// plaza the same. This also counts what those clusters hold: the world and displacement triangles
    /// in every leaf a cluster can see, each face once however many leaves it crosses. That is the
    /// number a pass has to bring down, and the number before and after a change is compared on.
    ///
    /// Triangles are counted from each face's corners (<see cref="FaceCorners"/>) when the report is
    /// loaded from a file: the vertices T-junction fixing adds to a face's edge are not triangles of its
    /// shape, and counting them made the same map differ by a third between compiles.
    ///
    /// Not counted yet: static props and entities in the visible leaves, and anything areaportals or
    /// occluders cull at run time, which the PVS knows nothing about.
    /// </summary>
    public sealed class VisReport
    {
        public int Clusters { get; }
        public long WorldTriangles { get; }

        /// <summary>
        /// Visible clusters summed over every cluster, in the PVS the map ships with. VVIS prints its
        /// "Total clusters visible" before a last pass drops visibility that only runs one way, and
        /// prints how much that pass dropped as "Optimized: N visible clusters"; this equals the first
        /// minus the second.
        /// </summary>
        public long TotalVisible { get; }

        /// <summary>Clusters each cluster can see, on average, rounded down as VVIS rounds its own.</summary>
        public long AverageVisible => Clusters == 0 ? 0 : TotalVisible / Clusters;

        public double MeanTriangles { get; }
        public long P95Triangles { get; }
        public long MaxTriangles { get; }

        /// <summary>The clusters that see the most triangles, worst first.</summary>
        public IReadOnlyList<ClusterCost> Worst { get; }

        /// <summary>Every cluster's cost, indexed by cluster number.</summary>
        public IReadOnlyList<ClusterCost> Costs { get; }

        public int AreaCount { get; }
        public int AreaPortalEntities { get; }
        public int HighestPortalNumber { get; }
        public int Faces { get; }
        public int LeafFaces { get; }

        private VisReport(BspVisibilityData bsp, ClusterCost[] costs, long worldTriangles)
        {
            Clusters = bsp.ClusterCount;
            WorldTriangles = worldTriangles;
            TotalVisible = costs.Sum(c => (long)c.VisibleClusters);

            var sorted = costs.Select(c => c.Triangles).OrderBy(t => t).ToArray();
            MeanTriangles = sorted.Length == 0 ? 0 : sorted.Average();
            P95Triangles = sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
            MaxTriangles = sorted.Length == 0 ? 0 : sorted[^1];

            // Ties broken by cluster number, so the list is the same on every run.
            Worst = costs.OrderByDescending(c => c.Triangles).ThenBy(c => c.Cluster).Take(10).ToList();
            Costs = costs;

            AreaCount = bsp.AreaCount;
            AreaPortalEntities = bsp.AreaPortalEntityCount;
            HighestPortalNumber = bsp.HighestPortalNumber;
            Faces = bsp.Faces.Count;
            LeafFaces = bsp.LeafFaces.Count;
        }

        /// <summary>The report of the BSP at <paramref name="path"/>, its faces counted by their corners.</summary>
        public static VisReport Load(string path)
        {
            var vis = BspVisibilityData.Load(path);
            return Build(vis, FaceCorners.Load(path, vis));
        }

        /// <summary>
        /// The report of <paramref name="bsp"/>, each face costing <paramref name="faceTriangles"/>[face], or
        /// the triangles its edge lists when that is not given.
        /// </summary>
        public static VisReport Build(BspVisibilityData bsp, IReadOnlyList<int>? faceTriangles = null)
        {
            int Cost(int face) => faceTriangles != null ? faceTriangles[face] : bsp.Faces[face].Triangles;

            if (!bsp.HasVisibility)
                throw new InvalidOperationException("The BSP has no visibility data: run VVIS before the report.");

            int clusters = bsp.ClusterCount;

            // Each cluster's own faces, without repeats, and where it is.
            var clusterFaces = new List<int>[clusters];
            var centreSum = new Vec3[clusters];
            var leafCount = new int[clusters];
            for (int c = 0; c < clusters; c++)
                clusterFaces[c] = [];

            var seenInCluster = new int[bsp.Faces.Count];
            Array.Fill(seenInCluster, -1);
            // Leaves in cluster order, so each cluster's faces are gathered in one sweep.
            var leaves = bsp.Leaves.Where(l => l.Cluster >= 0).OrderBy(l => l.Cluster).ToList();
            foreach (var leaf in leaves)
            {
                centreSum[leaf.Cluster] += (leaf.Mins + leaf.Maxs) / 2;
                leafCount[leaf.Cluster]++;
                for (int i = 0; i < leaf.LeafFaceCount; i++)
                {
                    int face = bsp.LeafFaces[leaf.FirstLeafFace + i];
                    if (seenInCluster[face] == leaf.Cluster || Cost(face) == 0)
                        continue;
                    seenInCluster[face] = leaf.Cluster;
                    clusterFaces[leaf.Cluster].Add(face);
                }
            }

            long worldTriangles = 0;
            var counted = new bool[bsp.Faces.Count];
            foreach (var list in clusterFaces)
                foreach (int face in list)
                    if (!counted[face])
                    {
                        counted[face] = true;
                        worldTriangles += Cost(face);
                    }

            var costs = new ClusterCost[clusters];
            Parallel.For(0, clusters, SourceKitConcurrency.Options,
                () => new int[bsp.Faces.Count],
                (c, _, stamp) =>
                {
                    // Stamps are cluster+1 so a fresh array (all zero) marks nothing as seen.
                    int mark = c + 1;
                    long triangles = 0;
                    int visible = 0;
                    foreach (int v in bsp.VisibleFrom(c))
                    {
                        visible++;
                        foreach (int face in clusterFaces[v])
                        {
                            if (stamp[face] == mark)
                                continue;
                            stamp[face] = mark;
                            triangles += Cost(face);
                        }
                    }
                    var centre = leafCount[c] == 0 ? Vec3.Zero : centreSum[c] / leafCount[c];
                    costs[c] = new ClusterCost(c, visible, triangles, centre);
                    return stamp;
                },
                _ => { });

            return new VisReport(bsp, costs, worldTriangles);
        }

        /// <summary>What GMod refuses at load time: an areaportal numbered above this.</summary>
        public const int GmodMaxPortalNumber = 191;

        public IEnumerable<string> Lines()
        {
            yield return $"{Clusters:N0} clusters, {WorldTriangles:N0} world triangles";
            yield return $"clusters visible: average {AverageVisible:N0} ({100.0 * AverageVisible / Math.Max(1, Clusters):0.0}% of the map), total {TotalVisible:N0}";
            yield return $"triangles visible per cluster: mean {MeanTriangles:N0}, 95th percentile {P95Triangles:N0}, worst {MaxTriangles:N0}";
            foreach (var c in Worst.Take(5))
                yield return $"  cluster {c.Cluster} near ({c.Centre}): {c.Triangles:N0} triangles in {c.VisibleClusters:N0} clusters";
            yield return $"areaportals: {AreaPortalEntities} entities, highest number {HighestPortalNumber} of {GmodMaxPortalNumber} Garry's Mod allows; {AreaCount - 1} areas";
            yield return $"faces {Faces:N0} of 65,536, leaf faces {LeafFaces:N0} of 65,536";
        }
    }
}
