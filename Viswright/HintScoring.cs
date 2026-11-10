using System;
using System.Collections.Generic;
using System.Linq;
using SourceKit.Geometry;

namespace Viswright
{
    /// <summary>
    /// Ranks hint candidates before any is compiled, by the PVS at stake where players stand in the
    /// clusters each one would cut.
    ///
    /// A hint changes what is visible only by splitting leaves, and a split pays where the points standing
    /// in the split cluster see a lot. On Valve's TF2 maps, that - the triangles a cluster's PVS holds,
    /// counted once for every standing point in it - picked out the places Valve hinted with an AUC of
    /// 0.82 to 0.85, where slack, nearness to spawns, being outdoors and sightline length did poorly. It
    /// says nothing about how much a split saves, only where one can; compiling decides the rest.
    /// </summary>
    public static class HintScoring
    {
        /// <summary>How far apart a candidate's faces are probed for the leaves they cut.</summary>
        public const double Spacing = 64;

        /// <summary>
        /// Each cluster's weight: the triangles visible from each standing point in it, summed. With
        /// <paramref name="cost"/> what each point sees (negative where a point is not in open space).
        /// </summary>
        public static Dictionary<int, double> Weights(IHintSpace space, SamplePoints samples, IReadOnlyList<double> cost, out double total)
        {
            var weights = new Dictionary<int, double>();
            total = 0;
            for (int i = 0; i < samples.Points.Count; i++)
            {
                if (cost[i] < 0 || space.ClusterAt(samples.Points[i]) is not (>= 0 and var cluster))
                    continue;
                weights[cluster] = weights.GetValueOrDefault(cluster) + cost[i];
                total += cost[i];
            }
            return weights;
        }

        /// <summary>
        /// The clusters <paramref name="candidate"/>'s hint faces pass through the inside of: where a point
        /// just in front of a face and one just behind it are in one leaf, the face would split that leaf.
        /// </summary>
        public static HashSet<int> CutClusters(IHintSpace space, HintCandidate candidate)
        {
            var clusters = new HashSet<int>();
            var leaves = new HashSet<int>();
            foreach (var (point, normal) in candidate.FacePoints(Spacing))
            {
                var front = point + normal * 2;
                var back = point - normal * 2;
                if (!space.Open(front) || !space.Open(back))
                    continue;
                int leaf = space.LeafAt(front);
                if (leaf != space.LeafAt(back) || !leaves.Add(leaf))
                    continue;
                if (space.ClusterAt(front) is >= 0 and var cluster)
                    clusters.Add(cluster);
            }
            return clusters;
        }

        /// <summary>The share of all that standing points see that is seen from the clusters <paramref name="candidate"/> cuts.</summary>
        public static double Stake(IHintSpace space, HintCandidate candidate, IReadOnlyDictionary<int, double> weights, double total) =>
            total <= 0 ? 0 : CutClusters(space, candidate).Sum(c => weights.GetValueOrDefault(c)) / total;

        /// <summary>
        /// The unmeasured candidates to compile this round, at most <paramref name="size"/>: those with the
        /// most at stake, no more than <paramref name="perFamily"/> from one family (three for caps, whose
        /// family is the same ground at other heights, which ranking cannot tell apart) so that alternatives
        /// to one another do not crowd out everything else. Nothing at stake is never compiled. A box always
        /// has a place while one is waiting: a long cut crosses more clusters than a box round one building,
        /// but under a cap, Valve's boxes are worth as much as all its cuts.
        /// </summary>
        public static List<HintCandidate> Shortlist(IEnumerable<(HintCandidate Candidate, double Stake)> fresh, int size, int perFamily = 2)
        {
            int Most(HintCandidate c) => c.Shape == HintShape.Cap ? perFamily + 1 : perFamily;
            var list = new List<HintCandidate>();
            var families = new Dictionary<string, int>();
            var ranked = fresh.Where(f => f.Stake > 0).OrderByDescending(f => f.Stake).ThenBy(f => f.Candidate.Id, StringComparer.Ordinal).ToList();
            if (size > 0 && ranked.FirstOrDefault(f => f.Candidate.Shape == HintShape.Box).Candidate is { } box)
            {
                list.Add(box);
                families[box.Family] = 1;
            }
            foreach (var (candidate, _) in ranked)
            {
                if (list.Contains(candidate))
                    continue;
                if (list.Count >= size)
                    break;
                if (families.GetValueOrDefault(candidate.Family) >= Most(candidate))
                    continue;
                families[candidate.Family] = families.GetValueOrDefault(candidate.Family) + 1;
                list.Add(candidate);
            }
            return list;
        }

        /// <summary>
        /// The candidates measured in an earlier round worth compiling again, at most <paramref name="size"/>,
        /// best first: those whose last gain beats <paramref name="best"/>, the best this round has found.
        /// Hints stand in for each other, so one's gain only shrinks as others go in; its last gain is as much
        /// as it can still save, and one that cannot beat what is already in hand is not compiled.
        /// </summary>
        public static List<HintCandidate> Retries(IEnumerable<(HintCandidate Candidate, double Gain)> measured, double best, int size) =>
            measured.Where(m => m.Gain > best).OrderByDescending(m => m.Gain).ThenBy(m => m.Candidate.Id, StringComparer.Ordinal)
                .Take(size).Select(m => m.Candidate).ToList();

        /// <summary>How much less <paramref name="after"/> sees than <paramref name="before"/>, over the points open in both.</summary>
        public static double Gain(IReadOnlyList<double> before, IReadOnlyList<double> after)
        {
            double was = 0, now = 0;
            for (int i = 0; i < before.Count; i++)
            {
                if (before[i] < 0 || after[i] < 0)
                    continue;
                was += before[i];
                now += after[i];
            }
            return was <= 0 ? 0 : 1 - now / was;
        }
    }
}
