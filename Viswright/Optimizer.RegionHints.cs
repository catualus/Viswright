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
        /// <summary>A round's best candidate must cut what the whole map sees by this much to go in; below it the search stops.</summary>
        internal const double RegionStopGain = 0.003;

        /// <summary>Candidates compiled in the first round, which tries the caps at their several heights.</summary>
        internal const int RegionShortlist = 8;

        /// <summary>
        /// Unmeasured candidates compiled in each later round, on top of those measured before that could
        /// still win the round.
        /// </summary>
        internal const int RegionFresh = 4;

        /// <summary>Rounds of the search at most: each accepts one candidate.</summary>
        internal const int RegionRounds = 16;

        /// <summary>
        /// Pass D, the way Valve places hints: few large candidates - caps over open-sky regions at their roof
        /// heights, vertical cuts carrying walls across open space, and once a cap is in, boxes round the
        /// buildings under it - ranked before compiling by the PVS at stake in the clusters each would cut.
        /// Each round compiles the best-ranked few, each alone on top of the hints accepted so far, measures
        /// the whole map, then compiles again any measured in an earlier round whose gain then could still beat
        /// the round's best, and accepts the one that cut what it sees the most, until the best cuts under
        /// <see cref="RegionStopGain"/>. The set is then compiled with the tools as they are and kept only if
        /// the whole map sees <see cref="HintMapGain"/> less, VVIS stays within its time, and collision is
        /// unchanged.
        /// </summary>
        private static WorkCompile? RunRegionHints(OptimizeOptions options, VmfDocument working, string copy, string folder, string name, Toolchain tools,
            List<BspSpace.BrushShape> collision, SamplePoints samples, ChangeGuard guard, ReviewLog review, WorkCompile reference)
        {
            string vvis = Path.GetFileName(tools.Vvis);
            if (!vvis.Contains("++", StringComparison.Ordinal) && !vvis.Contains("plusplus", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("hints: skipped, the search compiles the map several times and needs VVIS++ to do it in reasonable time");
                return null;
            }
            var budget = TimeSpan.FromMinutes(options.HintMinutes);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var before = CompileMeasure.Of(reference, samples);
            var referenceCost = CompileMeasure.PerPoint(reference, samples);
            IHintSpace space = new BspHintSpace(BspSpace.Load(reference.Bsp));
            var faces = HintRegions.Faces(working);
            var regions = HintRegions.Regions(space, samples);
            var pool = HintRegions.Caps(space, regions);
            int capCount = pool.Count;
            pool.AddRange(HintRegions.Cuts(space, faces));
            var known = pool.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            Log.Info($"hints: {regions.Count} region(s) of open ground; {capCount} cap(s) and {pool.Count - capCount} cut(s) to rank, " +
                $"{budget.TotalMinutes:0} minute(s) to search");
            if (pool.Count == 0)
                return null;

            var placed = new Dictionary<HintCandidate, List<VmfNode>>();
            void Place(HintCandidate c)
            {
                var solids = c.Brushes(working.MaxId());
                foreach (var s in solids)
                    working.World.Add(s);
                placed[c] = solids;
            }
            void Unplace(HintCandidate c)
            {
                if (placed.Remove(c, out var solids))
                    foreach (var s in solids)
                        working.World.Remove(s);
            }

            var accepted = new List<(HintCandidate Candidate, double Gain, int Round)>();
            var measured = new Dictionary<HintCandidate, double>();
            var refused = new Dictionary<HintCandidate, string>();
            double[] current = referenceCost;
            int trialCompiles = 0;
            TimeSpan slowest = reference.VbspRun is { } rv && reference.VvisRun is { } rvv ? rv.Elapsed + rvv.Elapsed : TimeSpan.FromMinutes(1);
            var trials = tools.ForTrials(HintLanes);

            int folders = 0;
            for (int round = 1; round <= RegionRounds; round++)
            {
                // Leave time for the compile that proves the set.
                if (clock.Elapsed + slowest * 3 > budget)
                {
                    Log.Info($"hints: time is up after {round - 1} round(s)");
                    break;
                }
                var weights = HintScoring.Weights(space, samples, current, out double total);
                var fresh = pool.Where(c => !measured.ContainsKey(c)).ToList();
                var stakes = new double[fresh.Count];
                var scoring = space;
                System.Threading.Tasks.Parallel.For(0, fresh.Count, SourceKitConcurrency.Options, i => stakes[i] = HintScoring.Stake(scoring, fresh[i], weights, total));
                var shortlist = HintScoring.Shortlist(fresh.Zip(stakes), round == 1 ? RegionShortlist : RegionFresh);

                // This round's compiles: kept until the round is decided, for the winner's tree.
                var tried = new List<(HintCandidate Candidate, WorkCompile Compile, double[]? Cost)>();
                bool outOfTime = false;
                void Try(IReadOnlyList<HintCandidate> batch)
                {
                    // Each written out with the hints accepted so far, then compiled a few at a time.
                    var prepared = new List<(HintCandidate Candidate, WorkCompile Compile)>();
                    foreach (var candidate in batch)
                    {
                        Place(candidate);
                        prepared.Add((candidate, WorkCompile.Prepare(working, copy, $"{folder}-t{++folders}", name)));
                        Unplace(candidate);
                    }
                    var results = new double[]?[prepared.Count];
                    int started = 0;
                    using (var lanes = new System.Threading.SemaphoreSlim(HintLanes))
                    {
                        var running = new List<System.Threading.Tasks.Task>();
                        for (int i = 0; i < prepared.Count; i++)
                        {
                            lanes.Wait();
                            if (clock.Elapsed + slowest * 2 > budget)
                            {
                                lanes.Release();
                                outOfTime = true;
                                break;
                            }
                            int k = i;
                            started++;
                            running.Add(System.Threading.Tasks.Task.Run(() =>
                            {
                                try
                                {
                                    var c = prepared[k].Compile;
                                    if (c.RunVbsp(trials).Succeeded && !c.Leaked && c.RunVvis(trials).Succeeded)
                                    {
                                        results[k] = CompileMeasure.PerPoint(c, samples);
                                        long took = (c.VbspRun!.Elapsed + c.VvisRun!.Elapsed).Ticks;
                                        lock (results)
                                            slowest = TimeSpan.FromTicks(Math.Max(slowest.Ticks, took / HintLanes));
                                    }
                                }
                                finally
                                {
                                    lanes.Release();
                                }
                            }));
                        }
                        System.Threading.Tasks.Task.WaitAll(running.ToArray());
                    }
                    SourceKitConcurrency.ThrowIfCancelled();
                    trialCompiles += started;
                    for (int i = 0; i < prepared.Count; i++)
                    {
                        var (candidate, compile) = prepared[i];
                        if (i >= started)
                        {
                            compile.Discard();
                            continue;
                        }
                        if (results[i] is { } cost)
                            measured[candidate] = HintScoring.Gain(current, cost);
                        else
                        {
                            refused[candidate] = "it did not compile cleanly alongside the hints accepted before it";
                            pool.Remove(candidate);
                        }
                        tried.Add((candidate, compile, results[i]));
                    }
                }
                double Best() => tried.Where(t => t.Cost != null).Select(t => measured[t.Candidate]).DefaultIfEmpty(double.NegativeInfinity).Max();

                if (shortlist.Count > 0)
                    Try(shortlist);
                // Then those measured in earlier rounds that could still beat the best so far, a lane's worth at a time.
                while (!outOfTime)
                {
                    var retries = HintScoring.Retries(pool.Where(c => measured.ContainsKey(c) && !tried.Any(t => ReferenceEquals(t.Candidate, c)))
                        .Select(c => (c, measured[c])), Math.Max(Best(), RegionStopGain - 1e-9), HintLanes);
                    if (retries.Count == 0)
                        break;
                    Try(retries);
                }
                if (tried.Count == 0)
                {
                    Log.Info(outOfTime ? $"hints: time is up after {round - 1} round(s)" : "hints: no candidate left that cuts a cluster players stand in");
                    break;
                }

                var winner = tried.Where(t => t.Cost != null).OrderByDescending(t => measured[t.Candidate]).FirstOrDefault();
                double bestGain = winner.Cost == null ? 0 : measured[winner.Candidate];
                if (winner.Cost != null && bestGain >= RegionStopGain)
                    space = new BspHintSpace(BspSpace.Load(winner.Compile.Bsp));
                foreach (var (_, compile, _) in tried)
                    compile.Discard();
                Log.Info($"hints: round {round}: " + string.Join(", ", tried.Where(t => t.Cost != null).OrderByDescending(t => measured[t.Candidate])
                    .Take(6).Select(t => $"{t.Candidate.Shape.ToString().ToLowerInvariant()} {measured[t.Candidate]:P1}")) + $" ({tried.Count} compiled)");

                if (winner.Cost == null || bestGain < RegionStopGain)
                {
                    foreach (var (candidate, _, cost) in tried)
                        if (cost != null)
                            refused[candidate] = $"the whole map saw {measured[candidate]:P1} less with it (needs {RegionStopGain:P1})";
                    Log.Info($"hints: round {round}: the best saw {bestGain:P2} less; under {RegionStopGain:P1}, the search stops");
                    break;
                }

                var chosen = winner.Candidate;
                Place(chosen);
                pool.Remove(chosen);
                refused.Remove(chosen);
                accepted.Add((chosen, bestGain, round));
                current = winner.Cost;
                // Tried and not worth the bar: measured, and done with.
                foreach (var (candidate, _, cost) in tried)
                    if (cost != null && measured[candidate] < RegionStopGain && !ReferenceEquals(candidate, chosen))
                    {
                        pool.Remove(candidate);
                        refused[candidate] = $"the whole map saw {measured[candidate]:P1} less with it (needs {RegionStopGain:P1})";
                    }
                string added = "";
                if (chosen.Shape == HintShape.Cap)
                {
                    var boxes = HintRegions.Boxes(space, chosen, regions, faces).Where(b => known.Add(b.Id)).ToList();
                    pool.AddRange(boxes);
                    if (boxes.Count > 0)
                        added = $"; {boxes.Count} box(es) round the buildings under it join the candidates";
                }
                Log.Info($"hints: round {round}: {Describe(chosen)} accepted, the whole map sees {bestGain:P1} less{added}");
                if (outOfTime)
                {
                    Log.Info($"hints: time is up after {round} round(s)");
                    break;
                }
            }

            var crashed = new List<HintCandidate>();
            void Report(string? undone)
            {
                foreach (var (candidate, gain, round) in accepted)
                {
                    bool applied = undone == null && placed.ContainsKey(candidate);
                    string why = applied ? $"{Describe(candidate)}: the whole map saw {gain:P1} less when it went in (round {round})"
                        : crashed.Contains(candidate) ? "VVIS failed with it alongside the other hints"
                        : undone ?? "left out to keep VVIS within its time";
                    // The round goes with it: a pinned run puts the hints back in the order they went in.
                    var data = candidate.Data;
                    data["round"] = round;
                    review.Add(new ReviewItem(candidate.Id, "hints", applied ? ItemStatus.Applied : ItemStatus.Rejected, why, candidate.Mins, candidate.Maxs)
                    {
                        Gain = gain,
                        Data = data,
                        NodeIds = applied ? placed[candidate].Select(s => s.Id ?? 0).ToList() : [],
                    });
                }
                foreach (var (candidate, why) in refused)
                    review.Add(new ReviewItem(candidate.Id, "hints", ItemStatus.Rejected, $"{Describe(candidate)}: {why}", candidate.Mins, candidate.Maxs)
                    {
                        Gain = measured.TryGetValue(candidate, out double g) ? g : null,
                        Data = candidate.Data,
                    });
            }

            if (accepted.Count == 0)
            {
                Report(null);
                Log.Check($"hints: none kept, no candidate cut what the map sees by {RegionStopGain:P1}; {trialCompiles} trial compile(s)");
                return null;
            }

            // The chosen set, compiled with the tools as they are. More splits mean more portals for VVIS;
            // past its allowance the hints accepted last, which were worth least, go first.
            TimeSpan allowed = TimeSpan.FromTicks(Math.Max((long)(before.VvisTime.Ticks * MaxVvisGrowth), (before.VvisTime + TimeSpan.FromSeconds(30)).Ticks));
            int finalCompiles = 0;
            WorkCompile? final = null;
            while (placed.Count > 0)
            {
                var order = accepted.Select(a => a.Candidate).Where(placed.ContainsKey).ToList();
                final = CompileLeavingOutFailures(order, Place, Unplace, () => { finalCompiles++; return Compile(working, copy, folder, name, tools); },
                    Describe, crashed, FailureCompiles(order.Count));
                if (final == null || final.VvisRun!.Elapsed <= allowed || placed.Count == 1)
                    break;
                var last = accepted.Last(a => placed.ContainsKey(a.Candidate)).Candidate;
                Log.Info($"hints: VVIS took {final.VvisRun.Elapsed.TotalSeconds:0}s with {placed.Count} hint(s), over {allowed.TotalSeconds:0}s; leaving out the last accepted");
                Unplace(last);
                final = null;
            }

            string? reason = final == null
                ? "the chosen hints did not compile cleanly"
                : BspSpace.CollisionDifferences(BspSpace.Load(final.Bsp).CollisionShapes(CollisionMask), collision) is > 0 and var n
                    ? $"collision changed ({n} brushes)" : null;
            var after = final == null ? null : CompileMeasure.Of(final, samples);
            if (reason == null && after != null)
                reason = after.SampledTriangles > before.SampledTriangles * (1 - HintMapGain)
                        ? $"the map as a whole sees under {HintMapGain:P0} less: {after.SampledTriangles:N0} triangles from a standing position against {before.SampledTriangles:N0}"
                    : after.VvisTime > allowed ? $"VVIS would take {after.VvisTime.TotalSeconds:0}s against {before.VvisTime.TotalSeconds:0}s"
                    : after.Faces >= 65536 * 85 / 100 || after.LeafFaces >= 65536 * 85 / 100 ? "the map would be within 15% of the face or leaf face limit"
                    : null;
            if (reason != null)
            {
                Report(reason);
                foreach (var c in placed.Keys.ToList())
                    Unplace(c);
                Log.Warn($"hints: undone, {reason}");
                return null;
            }

            var paths = placed.Values.SelectMany(s => s).Select(s => $"world#{working.World.Id}/solid#{s.Id}").ToHashSet();
            guard.Allow("hints", change => change.Kind == VmfChangeKind.Added && paths.Contains(change.Path));
            Report(null);
            string list = Path.Combine(Path.GetDirectoryName(folder)!, "hints.txt");
            File.WriteAllLines(list, accepted.Where(a => placed.ContainsKey(a.Candidate))
                .Select(a => $"round {a.Round}: {Describe(a.Candidate)}, {a.Gain:P1} less seen"));
            Log.Check($"hints: {placed.Count} kept; triangles visible from a standing position {before.SampledTriangles:N0} -> {after!.SampledTriangles:N0} " +
                $"({1 - after.SampledTriangles / before.SampledTriangles:P1} less), portals {before.Portals:N0} -> {after.Portals:N0}, " +
                $"vvis {before.VvisTime.TotalSeconds:0}s -> {after.VvisTime.TotalSeconds:0}s; {trialCompiles} trial and {finalCompiles} final compile(s); list in {list}");
            return final;
        }

        /// <summary>A candidate in words, for the log and the review.</summary>
        internal static string Describe(HintCandidate c)
        {
            var lo = c.Mins;
            var hi = c.Maxs;
            return c.Shape switch
            {
                HintShape.Cap => $"a cap at z {c.Height:0} over {hi.X - lo.X:0} by {hi.Y - lo.Y:0} units ({c.Blocks.Count} brush(es))",
                HintShape.Cut => $"a cut on {(c.Blocks[0].Faces == HintFaces.PlusX ? "x" : "y")} {(c.Blocks[0].Faces == HintFaces.PlusX ? hi.X : hi.Y):0}, " +
                    $"{(c.Blocks[0].Faces == HintFaces.PlusX ? hi.Y - lo.Y : hi.X - lo.X):0} units long, z {lo.Z:0} to {hi.Z:0}",
                _ when c.Blocks.Count > 1 => $"{c.Blocks.Count} boxes round the buildings under a cap at z {hi.Z:0}",
                _ => $"a box from ({lo.X:0} {lo.Y:0} {lo.Z:0}) to ({hi.X:0} {hi.Y:0} {hi.Z:0})",
            };
        }
    }
}
