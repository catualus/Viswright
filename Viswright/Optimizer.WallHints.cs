using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SourceKit.Vmf;
using Viswright.Review;

namespace Viswright
{
    public static partial class Optimizer
    {
        /// <summary>How many times longer VVIS may take with the wall-end hints in.</summary>
        private const double MaxVvisGrowth = 2.5;

        /// <summary>
        /// The wall-end hints, measured: candidates go in batches whose surrounding standing points do not
        /// overlap, so each one's effect is told apart, several batches compiling at a time; a candidate whose
        /// points see <see cref="HintGain"/> less is a winner. The winners are then compiled together, any
        /// that stopped helping are dropped, and the set is kept only if the whole map sees
        /// <see cref="HintMapGain"/> less, with no more than 15% more portals for VVIS and collision unchanged.
        /// </summary>
        private static WorkCompile? RunWallHints(OptimizeOptions options, VmfDocument working, string copy, string folder, string name, Toolchain tools,
            List<BspSpace.BrushShape> collision, SamplePoints samples, ChangeGuard guard, ReviewLog review, WorkCompile reference, TimeSpan budget)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var before = CompileMeasure.Of(reference, samples);
            var referenceCost = CompileMeasure.PerPoint(reference, samples);
            var candidates = WallHints.Find(working, BspSpace.Load(reference.Bsp), samples)
                // Most standing points first: where a split can matter to the most players.
                .OrderByDescending(h => h.Points.Count).ThenBy(h => h.Id, StringComparer.Ordinal).ToList();
            Log.Info($"hints: {candidates.Count} wall end(s) to carry across open space, {budget.TotalMinutes:0} minute(s) to search");
            if (candidates.Count == 0)
                return null;

            // Batches: each takes the next candidates whose points are not already measured in it.
            var batches = new List<List<WallHint>>();
            var left = new List<WallHint>(candidates);
            while (left.Count > 0)
            {
                var taken = new HashSet<int>();
                var batch = new List<WallHint>();
                foreach (var h in left)
                    if (!h.Points.Any(taken.Contains))
                    {
                        batch.Add(h);
                        taken.UnionWith(h.Points);
                    }
                batches.Add(batch);
                left.RemoveAll(batch.Contains);
            }

            var compiled = new (List<WallHint> Batch, double[]? Cost)[batches.Count];
            TimeSpan slowest = reference.VbspRun is { } rv && reference.VvisRun is { } rvv ? rv.Elapsed + rvv.Elapsed : TimeSpan.FromMinutes(1);
            int started = 0;
            var trials = tools.ForTrials(HintLanes);
            using (var lanes = new System.Threading.SemaphoreSlim(HintLanes))
            {
                var running = new List<System.Threading.Tasks.Task>();
                for (int b = 0; b < batches.Count; b++)
                {
                    lanes.Wait();
                    if (clock.Elapsed + slowest * 2 > budget)
                    {
                        lanes.Release();
                        Log.Info($"hints: time is up after {b} of {batches.Count} batch(es) of wall ends");
                        break;
                    }
                    long id = working.MaxId();
                    var doc = VmfDocument.Parse(working.ToString(), working.HasByteOrderMark);
                    foreach (var h in batches[b])
                    {
                        var solid = WallHints.Brush(h, id);
                        id += 8;
                        doc.World.Add(solid);
                    }
                    var compile = WorkCompile.Prepare(doc, copy, $"{folder}-walls-{b + 1}", name);
                    int index = b;
                    compiled[index] = (batches[index], null);
                    started++;
                    running.Add(System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            if (compile.RunVbsp(trials).Succeeded && !compile.Leaked && compile.RunVvis(trials).Succeeded)
                            {
                                // Measured as soon as VVIS ends, so that the batch's folder can go at
                                // once instead of hundreds of them piling up until the search ends.
                                compiled[index] = (batches[index], CompileMeasure.PerPoint(compile, samples));
                                long took = (compile.VbspRun!.Elapsed + compile.VvisRun!.Elapsed).Ticks;
                                lock (compiled)
                                    slowest = TimeSpan.FromTicks(Math.Max(slowest.Ticks, took / HintLanes));
                            }
                        }
                        finally
                        {
                            compile.Discard();
                            lanes.Release();
                        }
                    }));
                }
                System.Threading.Tasks.Task.WaitAll(running.ToArray());
            }
            SourceKitConcurrency.ThrowIfCancelled();

            static double Gain(IReadOnlyList<int> points, double[] reference, double[] cost)
            {
                double was = 0, now = 0;
                foreach (int i in points)
                {
                    if (reference[i] < 0 || cost[i] < 0) continue;
                    was += reference[i];
                    now += cost[i];
                }
                return was <= 0 ? 0 : 1 - now / was;
            }

            var gains = new Dictionary<WallHint, double>();
            for (int b = 0; b < started; b++)
            {
                var (batch, cost) = compiled[b];
                if (cost == null)
                    continue;
                foreach (var h in batch)
                    gains[h] = Gain(h.Points, referenceCost, cost);
            }
            var winners = gains.Where(kv => kv.Value >= HintGain).Select(kv => kv.Key).ToList();
            Log.Info($"hints: {gains.Count} wall end(s) measured, {winners.Count} saw at least {HintGain:P0} less");

            var crashed = new List<WallHint>();
            void Report(IEnumerable<WallHint> kept, IReadOnlyDictionary<WallHint, VmfNode> solids, string? undone)
            {
                var keptSet = kept.ToHashSet();
                foreach (var (h, gain) in gains)
                {
                    bool applied = undone == null && keptSet.Contains(h);
                    string why = applied ? $"carries a wall's end across open space: the points around it see {gain:P1} less"
                        : crashed.Contains(h) ? "VVIS failed with it alongside the other hints"
                        : undone != null && gain >= HintGain ? $"the set was undone: {undone}"
                        : gain >= HintGain ? "helped alone, but stopped helping alongside the other hints"
                        : $"the points around it saw {gain:P1} less (needs {HintGain:P0})";
                    review.Add(new ReviewItem(h.Id, "hints", applied ? ItemStatus.Applied : ItemStatus.Rejected, why, h.Mins, h.Maxs)
                    {
                        Gain = gain,
                        Data = h.Data,
                        NodeIds = applied && solids.TryGetValue(h, out var s) ? [s.Id ?? 0] : [],
                    });
                }
            }

            var placed = new Dictionary<WallHint, VmfNode>();
            void Place(WallHint h)
            {
                var solid = WallHints.Brush(h, working.MaxId());
                working.World.Add(solid);
                placed[h] = solid;
            }
            void Unplace(WallHint h)
            {
                if (placed.Remove(h, out var s))
                    working.World.Remove(s);
            }
            if (winners.Count == 0)
            {
                Report([], placed, null);
                Log.Check("hints: no wall end helped where it stands");
                return null;
            }

            foreach (var h in winners)
                Place(h);
            // More splits mean more portals for VVIS; Valve's own hints double its time. Past
            // MaxVvisGrowth, the weakest half of the set goes and the rest is compiled again.
            TimeSpan allowed = TimeSpan.FromTicks(Math.Max((long)(before.VvisTime.Ticks * MaxVvisGrowth), (before.VvisTime + TimeSpan.FromSeconds(30)).Ticks));
            WorkCompile? final = null;
            // Halving converges in a handful of rounds; the cap is only a guard.
            for (int round = 1; round <= 16 && placed.Count > 0; round++)
            {
                final = CompileLeavingOutFailures(placed.Keys.ToList(), Place, Unplace, () => Compile(working, copy, folder + "-walls", name, tools),
                    h => h.Id, crashed, FailureCompiles(placed.Count));
                if (final == null)
                    break;
                var cost = CompileMeasure.PerPoint(final, samples);
                var lost = placed.Keys.Where(h => Gain(h.Points, referenceCost, cost) < HintGain / 2).ToList();
                if (lost.Count == 0 && final.VvisRun!.Elapsed > allowed && placed.Count > 1)
                {
                    lost = placed.Keys.OrderBy(h => gains[h]).Take(placed.Count / 2).ToList();
                    Log.Info($"hints: VVIS took {final.VvisRun.Elapsed.TotalSeconds:0}s with {placed.Count} wall end(s), over {allowed.TotalSeconds:0}s; the weakest half go");
                }
                else if (lost.Count == 0)
                    break;
                else
                    Log.Info($"hints: {lost.Count} wall end(s) stopped helping alongside the others");
                foreach (var h in lost)
                    Unplace(h);
                final = null;
            }

            string? reason = final == null
                ? (placed.Count == 0 ? "none helped alongside the others" : "the chosen hints did not compile cleanly or settle")
                : BspSpace.CollisionDifferences(BspSpace.Load(final.Bsp).CollisionShapes(CollisionMask), collision) is > 0 and var n
                    ? $"collision changed ({n} brushes)" : null;
            var after = final == null ? null : CompileMeasure.Of(final, samples);
            if (reason == null && after != null)
                reason = after.SampledTriangles > before.SampledTriangles * (1 - HintMapGain)
                        ? $"the map as a whole sees under {HintMapGain:P0} less: {after.SampledTriangles:N0} triangles from a standing position against {before.SampledTriangles:N0}"
                    : after.VvisTime > allowed ? $"VVIS would take {after.VvisTime.TotalSeconds:0}s against {before.VvisTime.TotalSeconds:0}s"
                    : null;
            if (reason != null)
            {
                Report(placed.Keys, placed, reason);
                foreach (var h in placed.Keys.ToList())
                    Unplace(h);
                Log.Warn($"hints: wall ends undone, {reason}");
                return null;
            }

            var paths = placed.Values.Select(s => $"world#{working.World.Id}/solid#{s.Id}").ToHashSet();
            guard.Allow("hints", change => change.Kind == VmfChangeKind.Added && paths.Contains(change.Path));
            Report(placed.Keys, placed, null);
            Log.Check($"hints: {placed.Count} wall end(s) kept; triangles visible from a standing position {before.SampledTriangles:N0} -> {after!.SampledTriangles:N0} " +
                $"({1 - after.SampledTriangles / before.SampledTriangles:P1} less), portals {before.Portals:N0} -> {after.Portals:N0}, " +
                $"vvis {before.VvisTime.TotalSeconds:0}s -> {after.VvisTime.TotalSeconds:0}s");
            return final;
        }
    }
}
