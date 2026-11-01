using System;
using System.Linq;
using System.Threading.Tasks;
using SourceKit.Geometry;

namespace Viswright
{
    /// <summary>
    /// PVS slack: how much more the compiled visibility lets the engine draw from a place than can really
    /// be seen from it.
    ///
    /// The PVS is decided per leaf, for every point in the leaf at once, and conservatively, so a leaf's
    /// PVS holds whatever any point in it might see. What one standing point really sees is found here by
    /// casting rays from it in every direction against the world's triangles and noting the faces they
    /// hit first. Slack is then 1 - (triangles really seen) / (triangles the PVS lets through). Where
    /// slack is low, the PVS is already tight and no split - a hint, a structural brush - can buy much;
    /// where it is high, a better-placed split might.
    ///
    /// Slack has two parts. What the points of one cluster see between them is the most any split of
    /// that cluster could bring its PVS down to; the rest, between that and the PVS, is VVIS being
    /// conservative across the cluster's portals, which no split inside it removes. <see cref="SplitSlack"/>
    /// is the first part at each point, <see cref="FlowSlack"/> the second.
    /// </summary>
    public sealed class Slack
    {
        /// <summary>Rays per point: about one per 1.4 degrees.</summary>
        public const int DefaultRays = 16384;

        /// <summary>Triangles really seen from each sample point, or -1 for a point not measured.</summary>
        public double[] Seen { get; }

        /// <summary>Slack at each sample point, or -1 where it could not be worked out.</summary>
        public double[] Values { get; }

        /// <summary>
        /// At each point, the share of its PVS that other points in its cluster see and it does not: what
        /// cutting the cluster up could save.
        /// </summary>
        public double[] SplitSlack { get; }

        /// <summary>At each point, the share of its PVS no point in its cluster sees.</summary>
        public double[] FlowSlack { get; }

        /// <summary>Triangles the PVS lets through from each point, as measured against.</summary>
        public double[] Pvs { get; private init; } = [];

        private Slack(double[] seen, double[] values, double[] split, double[] flow)
        {
            Seen = seen;
            Values = values;
            SplitSlack = split;
            FlowSlack = flow;
        }

        /// <summary>Rays per point on the GPU, which can afford enough for the count of faces seen to settle.</summary>
        public const int GpuRays = 65536;

        /// <summary>Which device cast the rays, and how many per point, for the log.</summary>
        public string How { get; private set; } = "";

        /// <param name="pvs">Triangles the PVS lets through from each point, as <see cref="CompileMeasure.PerPoint"/> gives.</param>
        /// <param name="gpu">Cast the rays on the GPU when there is one; the CPU otherwise.</param>
        public static Slack Measure(string bsp, SamplePoints samples, double[] pvs, bool gpu = true, int? rays = null)
        {
            var geometry = BspGeometry.Load(bsp);
            var vis = BspVisibilityData.Load(bsp);
            var faces = vis.Faces;
            var space = BspSpace.Load(bsp);
            var clusterOf = samples.Points.Select(space.ClusterAt).ToArray();
            var bvh = new TriangleBvh(geometry.Corners, geometry.Blocks);
            int words = (geometry.FaceCount + 63) / 64;

            var device = gpu && OperatingSystem.IsWindowsVersionAtLeast(6, 2) ? Viswright.GpuRays.TryOpen(bvh, Directions(rays ?? GpuRays)) : null;
            int count = device != null ? rays ?? GpuRays : rays ?? DefaultRays;
            var directions = Directions(count);
            string how = OperatingSystem.IsWindowsVersionAtLeast(6, 2) && device != null ? $"{count:N0} rays a point on {device.DeviceName}" : $"{count:N0} rays a point on the CPU";

            var seen = new double[samples.Points.Count];
            // What every point of a cluster sees between them, as one bit per face.
            var union = new System.Collections.Generic.Dictionary<int, long[]>();
            foreach (int c in clusterOf.Where(c => c >= 0).Distinct())
                union[c] = new long[words];

            // Points go through in blocks: every ray of a block is cast, then each point's hits are tallied.
            var points = samples.Points.ToArray();
            int block = Math.Max(1, (1 << 24) / count);
            var hits = new int[Math.Min(block, Math.Max(1, points.Length)) * count];
            using (device)
            {
                for (int p0 = 0; p0 < points.Length; p0 += block)
                {
                    SourceKitConcurrency.ThrowIfCancelled();
                    int n = Math.Min(block, points.Length - p0);
                    if (OperatingSystem.IsWindowsVersionAtLeast(6, 2) && device != null)
                        device.Trace(points.AsSpan(p0, n), hits);
                    else
                        Parallel.For(0, n, SourceKitConcurrency.Options, i =>
                        {
                            for (int r = 0; r < count; r++)
                                hits[i * count + r] = bvh.Trace(points[p0 + i], directions[r]);
                        });

                    Parallel.For(0, n, SourceKitConcurrency.Options,
                        () => new ulong[words],
                        (i, _, mine) =>
                        {
                            Array.Clear(mine);
                            double triangles = 0;
                            for (int r = 0; r < count; r++)
                            {
                                int t = hits[i * count + r];
                                if (t < 0)
                                    continue;
                                int face = geometry.FaceOf[t];
                                ulong bit = 1UL << (face & 63);
                                if ((mine[face >> 6] & bit) != 0)
                                    continue;
                                mine[face >> 6] |= bit;
                                if (face < faces.Count)
                                    triangles += faces[face].Triangles;
                            }
                            int point = p0 + i;
                            seen[point] = triangles;
                            if (clusterOf[point] >= 0)
                            {
                                var shared = union[clusterOf[point]];
                                for (int w = 0; w < words; w++)
                                    if (mine[w] != 0)
                                        System.Threading.Interlocked.Or(ref shared[w], (long)mine[w]);
                            }
                            return mine;
                        },
                        _ => { });
                }
            }

            var unionTriangles = union.ToDictionary(kv => kv.Key, kv =>
            {
                double sum = 0;
                for (int w = 0; w < words; w++)
                    for (ulong bits = (ulong)kv.Value[w]; bits != 0; bits &= bits - 1)
                    {
                        int face = w * 64 + System.Numerics.BitOperations.TrailingZeroCount(bits);
                        if (face < faces.Count) sum += faces[face].Triangles;
                    }
                return sum;
            });

            var values = new double[seen.Length];
            var split = new double[seen.Length];
            var flow = new double[seen.Length];
            for (int i = 0; i < seen.Length; i++)
            {
                if (pvs[i] <= 0 || clusterOf[i] < 0)
                {
                    values[i] = split[i] = flow[i] = -1;
                    continue;
                }
                double shared = Math.Min(unionTriangles[clusterOf[i]], pvs[i]);
                values[i] = Math.Clamp(1 - seen[i] / pvs[i], 0, 1);
                split[i] = Math.Clamp((shared - seen[i]) / pvs[i], 0, 1);
                flow[i] = Math.Clamp((pvs[i] - shared) / pvs[i], 0, 1);
            }
            return new Slack(seen, values, split, flow) { How = how, Pvs = pvs };
        }

        /// <summary>Directions spread evenly over the sphere (a Fibonacci lattice), the same every run.</summary>
        public static Vec3[] Directions(int count)
        {
            var result = new Vec3[count];
            double golden = Math.PI * (3 - Math.Sqrt(5));
            for (int i = 0; i < count; i++)
            {
                double z = 1 - (i + 0.5) * 2 / count;
                double r = Math.Sqrt(1 - z * z);
                double a = golden * i;
                result[i] = new Vec3(r * Math.Cos(a), r * Math.Sin(a), z);
            }
            return result;
        }

        /// <summary>The average slack over the points that have one, weighting each point the same.</summary>
        public double Average => Values.Where(v => v >= 0).DefaultIfEmpty(0).Average();
    }
}
