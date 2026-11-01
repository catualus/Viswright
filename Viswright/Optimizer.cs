using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SourceKit.Vmf;
using Viswright.Review;

namespace Viswright
{
    /// <summary>What a run is asked to do.</summary>
    public sealed record OptimizeOptions
    {
        /// <summary>The VMF to read: the queued map, or an earlier step's copy of it.</summary>
        public required string Input { get; init; }

        /// <summary>The map as queued. Names the copy; defaults to <see cref="Input"/>.</summary>
        public string? Source { get; init; }

        /// <summary>Work everything out and report it, but leave the compile on its own map.</summary>
        public bool DryRun { get; init; }

        /// <summary>Pass A: make small and complex world brushes func_detail.</summary>
        public bool Detail { get; init; }

        /// <summary>Pass B: make faces hidden against opaque brushes nodraw.</summary>
        public bool Nodraw { get; init; }

        /// <summary>Pass C: put an areaportal in the doorway of each door that has none.</summary>
        public bool Areaportals { get; init; }

        /// <summary>Pass E: give static props with no fade one, where they shrink to <see cref="FadePixels"/> pixels.</summary>
        public bool Fades { get; init; }

        public double FadePixels { get; init; } = 6;

        /// <summary>Pass F: turn entities that do nothing an entity can do into static props and func_detail.</summary>
        public bool Slim { get; init; }

        /// <summary>Pass D: horizontal hints over open ground, each kept only if it measurably helps.</summary>
        public bool Hints { get; init; }

        /// <summary>How long the hint search may spend compiling, in minutes.</summary>
        public double HintMinutes { get; init; } = 15;

        /// <summary>Folders of Lua to scan for class lookups, on top of the game's own gamemodes and addons.</summary>
        public IReadOnlyList<string> LuaPaths { get; init; } = [];

        /// <summary>Write the review: a Hammer VMF of every item, pictures of the map, and a page listing them.</summary>
        public bool Review { get; init; }

        /// <summary>
        /// How the run goes. Null chooses as Compile Pal's default does: a review while the map has no
        /// decisions file or items still waiting for one, pinned once they are all decided.
        /// </summary>
        public RunMode? Mode { get; init; } = RunMode.Auto;

        /// <summary>Accept every item still waiting for a decision, then run pinned.</summary>
        public bool Accept { get; init; }

        /// <summary>Cast the review's rays on the CPU even when there is a GPU.</summary>
        public bool Cpu { get; init; }

        /// <summary>The compilers every pass is checked with. Needed when any pass is on.</summary>
        public Toolchain? Tools { get; init; }
    }

    /// <summary>What a run did.</summary>
    public sealed record OptimizeResult(string? CopyPath, IReadOnlyList<VmfChange> Changes, int Detailed = 0, int Nodrawn = 0, int Portals = 0);

    /// <summary>
    /// The optimiser: read the map, run the enabled passes on it, prove each with the real compilers,
    /// check that only declared changes were made, and write the copy the rest of the compile uses.
    ///
    /// A pass that cannot be made to compile cleanly, changes collision, or makes the map more
    /// expensive to draw is undone and reported; the map goes on without it. A run only fails - and
    /// stops the compile - when something is wrong with the map or the tool itself.
    /// </summary>
    public static partial class Optimizer
    {
        /// <summary>How much a pass may raise the triangles visible from a standing position and still be kept.</summary>
        public const double CostTolerance = 1.01;

        private const int LeakRepairRounds = 16;

        public static OptimizeResult Run(OptimizeOptions options)
        {
            string input = Path.GetFullPath(options.Input);
            string source = Path.GetFullPath(options.Source ?? options.Input);
            if (!File.Exists(input))
                throw new FileNotFoundException($"The map {input} does not exist.", input);

            Log.Vmf($"{Path.GetFileName(source)}");
            var original = VmfDocument.Load(input);
            var working = VmfDocument.Load(input);
            SourceKitConcurrency.ThrowIfCancelled();

            var findings = Audit.Run(original);
            Audit.Print(findings);
            if (findings.Any(f => f.Level == FindingLevel.Error))
                throw new InvalidOperationException("The map has a problem that stops it loading in game; see above.");

            string copy = Handoff.CopyPath(source);
            var guard = ChangeGuard.ForRelocation();

            int relocated = VmfRelocation.RetargetInstances(working, input, copy);
            if (relocated > 0)
                Log.Info($"{relocated} instance path(s) re-pointed so the copy finds the same files");

            bool anyPass = options.Detail || options.Nodraw || options.Areaportals || options.Fades || options.Slim || options.Hints;
            var decisions = DecisionFile.Load(DecisionFile.PathFor(source));
            if (anyPass && options.Mode != RunMode.Auto)
            {
                int edited = RoundTrip.Apply(decisions, Path.Combine(Path.GetDirectoryName(copy)!, "review"));
                if (edited > 0)
                    Log.Info($"review: {edited} decision(s) taken from what you changed in the review VMF");
                if (options.Accept && decisions.Exists)
                    Log.Info($"review: {decisions.AcceptPending()} waiting item(s) accepted");
                if (edited > 0 || (options.Accept && decisions.Exists))
                    decisions.Save();
            }
            var mode = !anyPass ? RunMode.Auto
                : options.Mode ?? (decisions.Exists && decisions.Pending == 0 ? RunMode.Pinned : RunMode.Review);

            int detailed = 0, nodrawn = 0, portals = 0;
            ReviewLog? found = null;
            if (mode == RunMode.Pinned)
                (detailed, nodrawn, portals) = RunPinned(options, working, copy, guard, decisions);
            else if (anyPass)
                (detailed, nodrawn, portals, found) = RunPasses(mode == RunMode.Review ? options with { Review = true } : options, working, copy, guard);

            var changes = VmfDiff.Compare(original, working);
            var undeclared = guard.Undeclared(changes);
            if (undeclared.Count > 0)
                throw new InvalidOperationException(
                    "The optimised map differs from the original in ways no pass declared, so it was not used:"
                    + Environment.NewLine + string.Join(Environment.NewLine, undeclared.Take(20).Select(c => "  " + c)));

            Log.Check($"{changes.Count} change(s), all declared");

            if (mode == RunMode.Review)
            {
                if (found != null)
                {
                    decisions.Merge(found.Items);
                    decisions.Save();
                }
                Log.Out($"review: this compile uses the map as it is. {decisions.Pending} item(s) wait for a decision in {decisions.Path}: " +
                    "set each to accept or deny there, or delete from the review VMF in Hammer what you do not want, then compile with Accept the review");
                return new OptimizeResult(null, changes, detailed, nodrawn, portals);
            }

            if (options.DryRun)
            {
                Log.Out("dry run: this compile uses the map as it is");
                return new OptimizeResult(null, changes, detailed, nodrawn, portals);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            working.Save(copy);
            Log.Out(copy);
            return new OptimizeResult(copy, changes, detailed, nodrawn, portals);
        }

        /// <summary>
        /// Collision is compared without areaportal brushes: they are not solid, and pass C adds them on
        /// purpose.
        /// </summary>
        private const int CollisionMask = ~0x8000;

        private static (int Detailed, int Nodrawn, int Portals, ReviewLog? Found) RunPasses(OptimizeOptions options, VmfDocument working, string copy, ChangeGuard guard)
        {
            var tools = options.Tools ?? throw new ArgumentException("The passes need -vbsp, -vvis and -game to check their changes.");
            tools.Validate();

            string name = Path.GetFileNameWithoutExtension(copy);
            string work = Path.Combine(Path.GetDirectoryName(copy)!, "work");
            var setup = System.Diagnostics.Stopwatch.StartNew();
            // Reading the game's Lua needs nothing from a compile; it runs while the baseline compiles.
            var luaScan = options.Slim
                ? System.Threading.Tasks.Task.Run(() => LuaGuard.Scan(LuaGuard.GameFolders(tools.GameFolder).Concat(options.LuaPaths)))
                : null;

            var baseline = Baseline(working, copy, Path.Combine(work, "baseline"), name, tools);
            if (baseline == null)
                return (0, 0, 0, null);

            var baselineSpace = BspSpace.Load(baseline.Bsp);
            var reachable = SamplePoints.Reachable(working, SourceKit.Compile.PortalFile.Load(baseline.Prt), baselineSpace);
            var samples = SamplePoints.Find(baselineSpace, reachable);
            if (reachable == null)
                Log.Info("measure: the map has no spawn point, so every floor counts as somewhere a player stands");
            var before = CompileMeasure.Of(baseline, samples);
            var collision = BspSpace.Load(baseline.Bsp).CollisionShapes(CollisionMask);
            Log.Check($"before: {before.Describe()}");
            Log.Info($"time: baseline and measuring {setup.Elapsed.TotalSeconds:0}s, of which {WorkCompile.CompilerTime.TotalSeconds:0}s in {WorkCompile.CompilerRuns} compiler run(s)");

            int detailed = 0, nodrawn = 0, portals = 0;
            CompileMeasure current = before;
            var content = GameContent.Open(tools.GameFolder);
            var review = new ReviewLog();
            var timings = new List<(string Step, TimeSpan Total, TimeSpan Compiling, int Runs)>();
            var stepClock = System.Diagnostics.Stopwatch.StartNew();
            var stepCompiling = WorkCompile.CompilerTime;
            int stepRuns = WorkCompile.CompilerRuns;
            void Lap(string step)
            {
                timings.Add((step, stepClock.Elapsed, WorkCompile.CompilerTime - stepCompiling, WorkCompile.CompilerRuns - stepRuns));
                stepClock.Restart();
                stepCompiling = WorkCompile.CompilerTime;
                stepRuns = WorkCompile.CompilerRuns;
            }

            // Before detail, so the detail pass sees the new portals and leaves their frames as world.
            // Each pass is judged against the map as the passes before it left it.
            WorkCompile reference = baseline;
            // The newest VBSP and VVIS compile of the map as it stands, and the newest VBSP compile: a later
            // step that needs one uses it rather than compiling the same map again.
            WorkCompile lastVis = baseline;
            WorkCompile latest = baseline;
            if (options.Areaportals)
            {
                portals = RunAreaportals(working, copy, Path.Combine(work, "areaportals"), name, tools, baseline, samples, collision, content, guard,
                    review, out var portalCompile);
                if (portalCompile is { VvisRun.Succeeded: true })
                {
                    reference = lastVis = latest = portalCompile;
                    current = CompileMeasure.Of(portalCompile, samples);
                }
                Lap("areaportals");
            }

            if (options.Detail)
            {
                var pass = new DetailPass(working);
                var excluded = new List<(VmfBrush Brush, string Reason)>();
                var candidates = DetailPass.Candidates(working, BspSpace.Load(reference.Bsp), excluded);
                pass.Apply(candidates);
                Log.Info($"detail: {candidates.Count} world brush(es) are candidates");

                var compile = CompileUntilSealed(pass, working, copy, Path.Combine(work, "detail"), name, tools);
                if (compile != null && pass.Moved.Count > 0)
                {
                    compile.RunVvis(tools);
                    if (compile.VvisRun!.Succeeded)
                        compile = KeepDetailWhereNoMoreIsSeen(pass, compile, reference, samples, working, copy, Path.Combine(work, "detail"), name, tools, review);
                }
                if (compile != null && pass.Moved.Count > 0)
                {
                    var after = CompileMeasure.Of(compile, samples);
                    string? reason = Rejection(compile, collision, current, after);
                    if (reason != null)
                    {
                        pass.RevertAll($"the pass was undone: {reason}");
                        Log.Warn($"detail: undone, {reason}");
                    }
                    else
                    {
                        detailed = pass.Moved.Count;
                        pass.Declare(guard);
                        current = after;
                        lastVis = latest = compile;
                        Log.Check($"detail: {detailed} brush(es) made func_detail; after: {after.Describe()}");
                    }
                }

                static string BrushKey(VmfBrush b) => "detail:" + ReviewLog.Anchor(b.Mins) + ":" + ReviewLog.Anchor(b.Maxs);
                foreach (var (brush, why) in excluded)
                    review.Add(new ReviewItem(BrushKey(brush), "detail", ItemStatus.Rejected, why, brush.Mins, brush.Maxs));
                foreach (var (_, brush, why) in pass.Returned)
                    if (!review.Items.Any(i => i.Id == BrushKey(brush)))
                        review.Add(new ReviewItem(BrushKey(brush), "detail", ItemStatus.Rejected, why, brush.Mins, brush.Maxs));
                foreach (var solid in pass.Moved)
                {
                    var brush = pass.BrushOf(solid);
                    review.Add(new ReviewItem(BrushKey(brush), "detail", ItemStatus.Applied, "made func_detail; nothing around it sees more", brush.Mins, brush.Maxs)
                        { NodeIds = [solid.Id ?? 0] });
                }
                Lap("detail");
            }

            // Hints change only how the map is split, and are judged on what is visible; the passes after
            // them change neither, so the compile detail left is the one to measure against.
            if (options.Hints)
            {
                if (RunHints(options, working, copy, Path.Combine(work, "hints"), name, tools, collision, samples, guard, review, lastVis) is { } kept)
                {
                    lastVis = latest = kept;
                    // Hint planes split faces: what nodraw is checked against has to include them.
                    current = CompileMeasure.Of(kept, samples);
                }
                Lap("hints");
            }

            if (options.Nodraw)
            {
                var pass = new NodrawPass();
                int found = pass.Apply(working, content.Opacity);
                if (found > 0)
                {
                    var compile = WorkCompile.Prepare(working, copy, Path.Combine(work, "nodraw"), name);
                    var run = compile.RunVbsp(tools);
                    string? reason = compile.Leaked ? "the map leaked"
                        : !run.Succeeded ? $"VBSP failed: {LastLines(run)}"
                        : BspSpace.CollisionDifferences(BspSpace.Load(compile.Bsp).CollisionShapes(CollisionMask), collision) is > 0 and var n ? $"collision changed ({n} brushes)"
                        : BspVisibilityData.Load(compile.Bsp).Faces.Count > current.Faces ? "the map has more faces, not fewer"
                        : null;
                    var box = new SourceKit.Geometry.Vec3(4, 4, 4);
                    foreach (var (side, centre) in pass.Changed.Zip(pass.Centres))
                        review.Add(new ReviewItem("nodraw:" + ReviewLog.Anchor(centre), "nodraw", reason == null ? ItemStatus.Applied : ItemStatus.Rejected,
                            reason == null ? "pressed against opaque brushes on all of its area" : $"the pass was undone: {reason}", centre - box, centre + box)
                            { Detail = side.Before });
                    if (reason != null)
                    {
                        pass.RevertAll();
                        Log.Warn($"nodraw: undone, {reason}");
                    }
                    else
                    {
                        nodrawn = found;
                        pass.Declare(guard);
                        latest = compile;
                        int faces = BspVisibilityData.Load(compile.Bsp).Faces.Count;
                        Log.Check($"nodraw: {found} hidden face(s) made nodraw; faces {current.Faces:N0} -> {faces:N0}");
                        foreach (var (side, centre) in pass.Changed.Zip(pass.Centres).Take(5))
                            Log.Info($"  e.g. side {side.Side.Id} ({side.Before}) at ({centre})");
                    }
                }
                else
                    Log.Check("nodraw: no face is fully hidden");
                Lap("nodraw");
            }

            if (options.Fades)
            {
                RunFades(options, working, reference, content, guard, Path.Combine(work, "fades.txt"), review);
                Lap("fades");
            }
            if (options.Slim)
            {
                RunSlimming(working, copy, Path.Combine(work, "slim"), name, tools, collision, content, guard, review, latest, luaScan!.Result);
                Lap("slim");
            }

            if (options.Review)
            {
                // What the map sees with every change kept: the passes after the last visibility compile
                // (nodraw, fades, entities) change no leaf and no PVS, so that compile shows it.
                // How much more each place's PVS lets through than can be seen from there, before and after.
                var slackBefore = Slack.Measure(baseline.Bsp, samples, CompileMeasure.PerPoint(baseline, samples), gpu: !options.Cpu);
                var slackAfter = ReferenceEquals(lastVis, baseline) ? null : Slack.Measure(lastVis.Bsp, samples, CompileMeasure.PerPoint(lastVis, samples), gpu: !options.Cpu);
                Log.Info($"slack: a standing point really sees {slackBefore.Seen.Where(v => v >= 0).Average():N0} triangles; " +
                    $"PVS slack {slackBefore.Average:P1} before" + (slackAfter != null ? $", {slackAfter.Average:P1} after" : "") + $" ({slackBefore.How})");
                string page = ReviewOutput.Write(Path.Combine(Path.GetDirectoryName(copy)!, "review"), name, working, review, samples, baseline, lastVis,
                    slackBefore, slackAfter, DecisionFile.PathFor(options.Source ?? options.Input));
                Log.Out($"review: {review.Items.Count} item(s); open {page}");
                Lap("review");
            }

            foreach (var (step, total, compiling, runs) in timings)
                Log.Info($"time: {step} {total.TotalSeconds:0}s, of which {compiling.TotalSeconds:0}s in {runs} compiler run(s)");

            return (detailed, nodrawn, portals, review);
        }

        private static void RunFades(OptimizeOptions options, VmfDocument working, WorkCompile reference, GameContent content,
            ChangeGuard guard, string listFile, ReviewLog review)
        {
            // The 3D skybox is the area the sky_camera stands in.
            var space = BspSpace.Load(reference.Bsp);
            int skyArea = -1;
            var camera = working.Entities.FirstOrDefault(e => string.Equals(e.Classname, "sky_camera", StringComparison.OrdinalIgnoreCase));
            if (camera != null && SourceKit.Geometry.Vec3.TryParse(camera.Get("origin") ?? "", out var cameraOrigin))
                skyArea = space.AreaAt(cameraOrigin);

            var pass = new PropFadePass(options.FadePixels, (space.Maxs - space.Mins).Length);
            int count = pass.Apply(working, content, p => skyArea >= 0 && space.AreaAt(p) == skyArea);
            pass.Declare(guard);
            foreach (var (prop, _, _) in pass.Changed)
            {
                var (lo, hi) = ReviewLog.Bounds(prop);
                review.Add(new ReviewItem("fade:" + ReviewLog.Anchor((lo + hi) / 2), "fades", ItemStatus.Applied,
                    $"fades out by {prop.Get("fademaxdist")} units", lo, hi) { Detail = prop.Get("model"), NodeIds = [prop.Id ?? 0] });
            }
            foreach (var (prop, why) in pass.SkippedProps)
            {
                var (lo, hi) = ReviewLog.Bounds(prop);
                review.Add(new ReviewItem("fade:" + ReviewLog.Anchor((lo + hi) / 2), "fades", ItemStatus.Rejected, why, lo, hi) { Detail = prop.Get("model") });
            }
            Log.Check($"fades: {count} static prop(s) given a fade, where they shrink to {options.FadePixels:0.#} pixels at 1080p and 90 degrees");
            foreach (var (reason, n) in pass.Skipped.OrderByDescending(kv => kv.Value))
                Log.Info($"  {n} left alone: {reason}");
            foreach (var (model, (n, max)) in pass.Models.OrderByDescending(kv => kv.Value.Count).Take(8))
                Log.Info($"  {model}: {n} prop(s), fading out by {max:N0} units");
            if (pass.Models.Count > 0)
            {
                File.WriteAllLines(listFile, pass.Models.Select(kv => $"{kv.Key}\t{kv.Value.Count}\t{kv.Value.Max}"));
                Log.Info($"  every model and its distance: {listFile}");
            }
        }

        private static void RunSlimming(VmfDocument working, string copy, string folder, string name,
            Toolchain tools, List<BspSpace.BrushShape> collision, GameContent content, ChangeGuard guard, ReviewLog review, WorkCompile latest, LuaGuard lua)
        {
            void Report(SlimmingPass pass, string? undone)
            {
                foreach (var (entity, why) in pass.SkippedEntities)
                {
                    var (lo, hi) = ReviewLog.Bounds(entity);
                    review.Add(new ReviewItem("slim:" + ReviewLog.Anchor((lo + hi) / 2), "slim", ItemStatus.Rejected, why, lo, hi)
                        { Detail = $"{entity.Classname} {entity.Get("model")}".Trim() });
                }
                foreach (var (entity, before) in pass.Converted)
                {
                    var (lo, hi) = ReviewLog.Bounds(entity);
                    review.Add(new ReviewItem("slim:" + ReviewLog.Anchor((lo + hi) / 2), "slim", undone == null ? ItemStatus.Applied : ItemStatus.Rejected,
                        undone ?? $"{before} made {entity.Classname}: one entity fewer", lo, hi)
                        { Detail = entity.Get("model"), NodeIds = undone == null ? [entity.Id ?? 0] : [] });
                }
            }

            Log.Info($"slim: {lua.FilesScanned:N0} Lua file(s) scanned; {lua.Classes.Count} class(es) looked up by class name");

            var pass = new SlimmingPass();
            int count = pass.Apply(working, content, lua);
            foreach (var (why, many) in pass.Skipped.OrderByDescending(kv => kv.Value).Take(8))
                Log.Info($"  {many} left as entities: {why}");
            foreach (var (cls, lookup, file) in pass.Protected)
                Log.Info($"  {cls} protected: Lua looks up \"{lookup}\" in {file}");
            if (pass.LightingChanges > 0)
                Log.Info($"  {pass.LightingChanges} func_brush left alone: as func_detail it would cast a lightmap shadow it does not cast now");
            if (count == 0)
            {
                Report(pass, null);
                Log.Check("slim: no entity can be made static");
                return;
            }

            // The map as the passes before this one left it, to count against: the newest compile is it,
            // as far as entities and static props go - fades, the only pass that may run between, changes
            // neither count.
            var countsBefore = EntityCounts(latest.Bsp);

            // VBSP refuses a static prop whose model was not built for it, and deletes the prop.
            var compile = WorkCompile.Prepare(working, copy, folder, name);
            var run = compile.RunVbsp(tools);
            var refused = RefusedModels(run);
            int back = refused.Count > 0 ? pass.RevertModels(refused) : 0;
            if (back > 0)
            {
                foreach (string model in refused.Take(5))
                    Log.Info($"  VBSP would not take {model} as a static prop");
                compile = WorkCompile.Prepare(working, copy, folder, name);
                run = compile.RunVbsp(tools);
            }

            string? reason = compile.Leaked ? "the map leaked"
                : !run.Succeeded ? $"VBSP failed: {LastLines(run)}"
                : BspSpace.CollisionDifferences(BspSpace.Load(compile.Bsp).CollisionShapes(CollisionMask), collision) is > 0 and var n
                    ? $"collision changed ({n} brushes)" : null;
            if (reason != null)
            {
                Report(pass, $"the pass was undone: {reason}");
                pass.RevertAll();
                Log.Warn($"slim: undone, {reason}");
                return;
            }

            int props = pass.Converted.Count(c => c.Before.StartsWith("prop_", StringComparison.OrdinalIgnoreCase));
            int brushes = pass.Converted.Count - props;

            // Every prop converted must arrive as a static prop: VBSP drops one it will not take with no
            // more than a line in its log.
            var countsAfter = EntityCounts(compile.Bsp);
            if (countsBefore.Entities - countsAfter.Entities != pass.Converted.Count || countsAfter.StaticProps - countsBefore.StaticProps != props)
            {
                Report(pass, "the pass was undone: the entity and static prop counts did not add up");
                pass.RevertAll();
                Log.Warn($"slim: undone, the counts do not add up: entities {countsBefore.Entities} -> {countsAfter.Entities}, " +
                    $"static props {countsBefore.StaticProps} -> {countsAfter.StaticProps}, for {props} prop(s) and {brushes} brush(es) converted");
                return;
            }

            pass.Declare(guard);
            Report(pass, null);
            Log.Check($"slim: {props} prop_dynamic made prop_static, {brushes} func_brush made func_detail: {pass.Converted.Count} fewer entities" +
                (back > 0 ? $" ({back} put back: VBSP would not take their model as static)" : ""));
        }

        /// <summary>How far from a detailed brush standing points count as around it.</summary>
        private const double DetailReach = 384;

        /// <summary>
        /// Puts back as world every detailed brush whose surroundings see more than before, and compiles
        /// again, up to three times. Detail merges the leaves it no longer splits, and a merged leaf sees
        /// everything any of its parts saw: near a wall of trim or a run of pillars, the street can end up
        /// seeing more, which the leaves cut by those brushes kept apart. Brushes where nobody stands
        /// nearby, or where the points around them see the same or less, stay detail.
        /// </summary>
        private static WorkCompile? KeepDetailWhereNoMoreIsSeen(DetailPass pass, WorkCompile compile, WorkCompile reference, SamplePoints samples,
            VmfDocument working, string copy, string folder, string name, Toolchain tools, ReviewLog review)
        {
            var before = CompileMeasure.PerPoint(reference, samples);
            for (int round = 1; round <= 3 && pass.Moved.Count > 0; round++)
            {
                var after = CompileMeasure.PerPoint(compile, samples);
                // Per brush: the change in what the points around it see, and what they saw before.
                var around = new Dictionary<VmfBrush, (double Change, double Before)>();
                foreach (var solid in pass.Moved)
                {
                    var brush = pass.BrushOf(solid);
                    double change = 0, was = 0;
                    for (int i = 0; i < before.Length; i++)
                    {
                        if (before[i] < 0 || after[i] < 0 || DetailPass.DistanceToBox(samples.Points[i], brush.Mins, brush.Maxs) > DetailReach)
                            continue;
                        change += after[i] - before[i];
                        was += before[i];
                    }
                    around[brush] = (change, was);
                }

                int back = pass.RevertWhere(brush => around[brush].Change > 0,
                    brush => $"the points within {DetailReach:0} units see {around[brush].Change / Math.Max(1, around[brush].Before):P1} more with it as detail");
                if (back == 0)
                    break;
                Log.Info($"detail: round {round}: {back} brush(es) stay world, the points around them see more as detail");
                if (pass.Moved.Count == 0)
                    return null;

                // Putting world back cannot open a leak, but a compile that fails is still no compile.
                compile = WorkCompile.Prepare(working, copy, folder, name);
                if (!compile.RunVbsp(tools).Succeeded || compile.Leaked || !compile.RunVvis(tools).Succeeded)
                {
                    pass.RevertAll("the pass was undone: the map stopped compiling cleanly once brushes were put back");
                    Log.Warn("detail: undone, the map stopped compiling cleanly once brushes were put back");
                    return null;
                }
            }
            return compile;
        }

        /// <summary>Gain a square's own standing points must see for its hint to be kept.</summary>
        private const double HintGain = 0.02;

        /// <summary>How much less the whole map must see for the hints to be worth their extra portals.</summary>
        private const double HintMapGain = 0.01;

        /// <summary>
        /// Pass D: compiles once as the map stands, then once per height with a hint over every square of
        /// open ground, and gives each square the height that cut what its points see the most, if by
        /// <see cref="HintGain"/>. The squares kept are compiled together, any that stopped helping are
        /// dropped, and the set is kept only if the whole map sees at least <see cref="HintMapGain"/> less
        /// and VVIS has no more than 15%
        /// more portals to work through.
        /// </summary>
        private static Dictionary<string, double> HintData(HintSquare square, double height) => new()
        {
            ["x"] = square.X, ["y"] = square.Y, ["floor"] = square.Floor, ["ceiling"] = square.Ceiling, ["height"] = height,
        };

        /// <summary>How many hint heights compile at once.</summary>
        private const int HintLanes = 2;

        private static WorkCompile? RunHints(OptimizeOptions options, VmfDocument working, string copy, string folder, string name, Toolchain tools,
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
            var squares = HintPass.Squares(samples, BspSpace.Load(reference.Bsp), _ => false);
            Log.Info($"hints: {squares.Count} square(s) of open ground to try, {budget.TotalMinutes:0} minute(s) to search");
            if (squares.Count == 0)
                return null;

            var pass = new HintPass(working);
            var best = new Dictionary<HintSquare, (double Height, double Gain)>();
            var tried = new Dictionary<HintSquare, (double Height, double Gain)>();
            var lostTogether = new HashSet<HintSquare>();
            void Report(string? undone)
            {
                foreach (var square in squares)
                {
                    var lo = new SourceKit.Geometry.Vec3(square.Mins.X, square.Mins.Y, square.Floor);
                    string id = $"hint:{square.X}_{square.Y}";
                    if (!tried.TryGetValue(square, out var t))
                        continue;
                    if (undone == null && pass.Placed.TryGetValue(square, out var placed))
                    {
                        var g = best[square];
                        review.Add(new ReviewItem(id, "hints", ItemStatus.Applied, $"a hint at z {placed.Height:0}: the points here see {g.Gain:P1} less", lo,
                            new SourceKit.Geometry.Vec3(square.Maxs.X, square.Maxs.Y, placed.Height)) { Gain = g.Gain, NodeIds = [placed.Solid.Id ?? 0], Data = HintData(square, placed.Height) });
                        continue;
                    }
                    var (height, gain) = best.TryGetValue(square, out var b) ? b : t;
                    string why = undone != null && best.ContainsKey(square) ? $"the set was undone: {undone}"
                        : lostTogether.Contains(square) ? $"saw {gain:P1} less alone, but stopped helping alongside the other hints"
                        : $"its points saw at most {gain:P1} less with a hint (needs {HintGain:P0})";
                    review.Add(new ReviewItem(id, "hints", ItemStatus.Rejected, why, lo,
                        new SourceKit.Geometry.Vec3(square.Maxs.X, square.Maxs.Y, height)) { Gain = gain, Data = HintData(square, height) });
                }
            }
            // A cached baseline has no timings; assume a minute a compile until one has been timed.
            TimeSpan slowest = reference.VbspRun is { } rv && reference.VvisRun is { } rvv ? rv.Elapsed + rvv.Elapsed : TimeSpan.FromMinutes(1);

            // Every height is its own map: written out first, then compiled two at a time, so one
            // compile's single-threaded VBSP stages run beside the other's VVIS.
            var rounds = new List<(List<(HintSquare Square, double Height)> Tries, WorkCompile Compile)>();
            for (int round = 0; ; round++)
            {
                var tries = squares.Select(s => (Square: s, Height: s.Heights().Skip(round).FirstOrDefault(double.NaN)))
                    .Where(t => !double.IsNaN(t.Height)).ToList();
                if (tries.Count == 0)
                    break;
                foreach (var (square, height) in tries)
                    pass.Place(square, height);
                rounds.Add((tries, WorkCompile.Prepare(working, copy, $"{folder}-{round + 1}", name)));
                pass.RevertAll();
            }
            var compiled = new WorkCompile?[rounds.Count];
            int started = 0;
            using (var lanes = new System.Threading.SemaphoreSlim(HintLanes))
            {
                var running = new List<System.Threading.Tasks.Task>();
                for (int round = 0; round < rounds.Count; round++)
                {
                    lanes.Wait();
                    // Leave time for the compile that proves the squares chosen.
                    if (clock.Elapsed + slowest * 2 > budget)
                    {
                        lanes.Release();
                        Log.Info($"hints: time is up after {round} height(s)");
                        break;
                    }
                    int r = round;
                    started++;
                    running.Add(System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            var c = rounds[r].Compile;
                            if (c.RunVbsp(tools).Succeeded && !c.Leaked && c.RunVvis(tools).Succeeded)
                            {
                                compiled[r] = c;
                                long took = (c.VbspRun!.Elapsed + c.VvisRun!.Elapsed).Ticks;
                                lock (compiled)
                                    slowest = TimeSpan.FromTicks(Math.Max(slowest.Ticks, took / HintLanes));
                            }
                        }
                        finally { lanes.Release(); }
                    }));
                }
                System.Threading.Tasks.Task.WaitAll(running.ToArray());
            }
            SourceKitConcurrency.ThrowIfCancelled();

            for (int round = 0; round < started; round++)
            {
                var (tries, compile) = (rounds[round].Tries, compiled[round]);
                if (compile == null)
                {
                    Log.Warn($"hints: height {round + 1} did not compile cleanly; skipped");
                    continue;
                }
                var cost = CompileMeasure.PerPoint(compile, samples);
                int better = 0;
                foreach (var (square, height) in tries)
                {
                    double gain = Gain(square, referenceCost, cost);
                    if (!tried.TryGetValue(square, out var t) || gain > t.Gain)
                        tried[square] = (height, gain);
                    if (gain >= HintGain && gain > best.GetValueOrDefault(square).Gain)
                    {
                        best[square] = (height, gain);
                        better++;
                    }
                }
                Log.Info($"hints: height {round + 1}: {better} of {tries.Count} square(s) see at least {HintGain:P0} less");
            }

            if (best.Count == 0)
            {
                Report(null);
                Log.Check("hints: none kept, no square saw less with a hint");
                return null;
            }

            // Together, hints can undo each other; drop any square that stops helping and try again.
            foreach (var (square, (height, _)) in best)
                pass.Place(square, height);
            WorkCompile? final = null;
            for (int round = 1; round <= 3 && pass.Placed.Count > 0; round++)
            {
                final = Compile(working, copy, folder, name, tools);
                if (final == null)
                    break;
                var cost = CompileMeasure.PerPoint(final, samples);
                var lost = pass.Placed.Keys.Where(s => Gain(s, referenceCost, cost) < HintGain / 2).ToList();
                if (lost.Count == 0)
                    break;
                foreach (var square in lost)
                {
                    pass.Remove(square);
                    lostTogether.Add(square);
                }
                Log.Info($"hints: {lost.Count} square(s) stopped helping alongside the others and lost their hint");
                final = null;
            }

            string? reason = final == null
                ? (pass.Placed.Count == 0 ? "none helped alongside the others" : "the chosen hints did not compile cleanly or settle")
                : BspSpace.CollisionDifferences(BspSpace.Load(final.Bsp).CollisionShapes(CollisionMask), collision) is > 0 and var n
                    ? $"collision changed ({n} brushes)" : null;
            CompileMeasure? after = final == null ? null : CompileMeasure.Of(final, samples);
            if (reason == null && after != null)
                reason = after.SampledTriangles > before.SampledTriangles * (1 - HintMapGain)
                        ? $"the map as a whole sees under {HintMapGain:P0} less: {after.SampledTriangles:N0} triangles from a standing position against {before.SampledTriangles:N0}"
                    : after.Portals > before.Portals * 1.15 ? $"VVIS would have {after.Portals:N0} portals to work through against {before.Portals:N0}"
                    : after.Faces >= 65536 * 85 / 100 || after.LeafFaces >= 65536 * 85 / 100 ? "the map would be within 15% of the face or leaf face limit"
                    : null;
            if (reason != null)
            {
                Report(reason);
                pass.RevertAll();
                Log.Warn($"hints: undone, {reason}");
                return null;
            }

            pass.Declare(guard);
            Report(null);
            string list = Path.Combine(folder, "hints.txt");
            File.WriteAllLines(list, pass.Placed.OrderBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Y)
                .Select(kv => $"{kv.Key.Mins.X:0} {kv.Key.Mins.Y:0} to {kv.Key.Maxs.X:0} {kv.Key.Maxs.Y:0} at z {kv.Value.Height:0}: {best[kv.Key].Gain:P1} less seen there"));
            Log.Check($"hints: {pass.Placed.Count} kept; triangles visible from a standing position {before.SampledTriangles:N0} -> {after!.SampledTriangles:N0} " +
                $"({1 - after.SampledTriangles / before.SampledTriangles:P1} less), portals {before.Portals:N0} -> {after.Portals:N0}, " +
                $"vvis {before.VvisTime.TotalSeconds:0}s -> {after.VvisTime.TotalSeconds:0}s; list in {list}");
            return final;
        }

        /// <summary>A VBSP and VVIS compile of the map as it stands, or null if either failed or it leaked.</summary>
        private static WorkCompile? Compile(VmfDocument working, string copy, string folder, string name, Toolchain tools)
        {
            var compile = WorkCompile.Prepare(working, copy, folder, name);
            if (!compile.RunVbsp(tools).Succeeded || compile.Leaked || !compile.RunVvis(tools).Succeeded)
                return null;
            return compile;
        }

        /// <summary>How much less a square's points see in <paramref name="cost"/> than in <paramref name="reference"/>.</summary>
        private static double Gain(HintSquare square, double[] reference, double[] cost)
        {
            double was = 0, now = 0;
            foreach (int i in square.Points)
            {
                if (reference[i] < 0 || cost[i] < 0)
                    continue;
                was += reference[i];
                now += cost[i];
            }
            return was <= 0 ? 0 : 1 - now / was;
        }

        /// <summary>Models VBSP said it would not take as static props: it deletes such a prop with no more than a line.</summary>
        private static HashSet<string> RefusedModels(CompileRun run) => run.Output
            .Where(l => l.Contains("staticprop", StringComparison.OrdinalIgnoreCase) || l.Contains("static prop", StringComparison.OrdinalIgnoreCase)
                || l.Contains("prop_static", StringComparison.OrdinalIgnoreCase))
            .SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l, @"models[/\\][^""\s!]+?\.mdl", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(m => m.Value.Replace('\\', '/').ToLowerInvariant()).ToHashSet();

        /// <summary>Entities in a compiled map's entity lump, and props in its static prop lump.</summary>
        private static (int Entities, int StaticProps) EntityCounts(string bsp)
        {
            var bytes = File.ReadAllBytes(bsp);
            int entityOffset = BitConverter.ToInt32(bytes, 8), entityLength = BitConverter.ToInt32(bytes, 12);
            string entities = System.Text.Encoding.Latin1.GetString(bytes, entityOffset, entityLength);
            int count = 0;
            for (int at = 0; (at = entities.IndexOf("\"classname\"", at, StringComparison.Ordinal)) >= 0; at++)
                count++;

            // Game lump 35: a count, then 16-byte headers; the static prop lump is 'sprp'. Its body is a
            // model dictionary of 128-byte names, a leaf list of shorts, then the prop count.
            int gameOffset = BitConverter.ToInt32(bytes, 8 + 35 * 16);
            int lumps = BitConverter.ToInt32(bytes, gameOffset);
            for (int i = 0; i < lumps; i++)
            {
                int header = gameOffset + 4 + i * 16;
                if (BitConverter.ToInt32(bytes, header) != 0x73707270)
                    continue;
                int at = BitConverter.ToInt32(bytes, header + 8);
                at += 4 + BitConverter.ToInt32(bytes, at) * 128;
                at += 4 + BitConverter.ToInt32(bytes, at) * 2;
                return (count, BitConverter.ToInt32(bytes, at));
            }
            return (count, 0);
        }

        /// <summary>
        /// Pass C: finds the doorways, compiles with a portal in every one, keeps those VBSP placed
        /// between two areas, then keeps the most valuable within the areaportal budget and compiles
        /// again to prove the set it keeps.
        /// </summary>
        private static int RunAreaportals(VmfDocument working, string copy, string folder, string name, Toolchain tools,
            WorkCompile baseline, SamplePoints samples, List<BspSpace.BrushShape> collision, GameContent content, ChangeGuard guard,
            ReviewLog review, out WorkCompile? final)
        {
            var pass = new AreaportalPass(working);
            int added = RunAreaportals(pass, working, copy, folder, name, tools, baseline, samples, collision, content, guard, out final);

            static string DoorKey(VmfNode door) => door.Get("targetname") is { Length: > 0 } n && !n.StartsWith(AreaportalPass.DoorNamePrefix, StringComparison.Ordinal)
                ? n : "door@" + ReviewLog.Anchor(ReviewLog.Bounds(door).Mins);
            foreach (var (door, reason) in pass.SkippedDoors)
            {
                var (lo, hi) = ReviewLog.Bounds(door);
                review.Add(new ReviewItem("areaportal:" + DoorKey(door), "areaportals", ItemStatus.Rejected, reason, lo, hi)
                    { Detail = door.Classname });
            }
            foreach (var (doorway, door, reason) in pass.Dropped)
                review.Add(new ReviewItem("areaportal:" + DoorKey(door), "areaportals", ItemStatus.Rejected, reason, doorway.Mins, doorway.Maxs)
                    { Detail = door.Classname, Data = DecisionFile.DoorwayData(doorway) });
            foreach (var (portal, doorway, door, named) in pass.Added)
            {
                string closes = doorway.Closes ? $"closes with {door.Get("targetname")}"
                    : $"never closes: {pass.OpenOnlyDoors.GetValueOrDefault(door, "the door does not fill the opening")}";
                review.Add(new ReviewItem("areaportal:" + DoorKey(door), "areaportals", ItemStatus.Applied,
                    named ? $"{closes} (door given that name)" : closes, doorway.Mins, doorway.Maxs)
                {
                    Detail = portal.Get("targetname"),
                    NodeIds = named ? [portal.Id ?? 0, door.Id ?? 0] : [portal.Id ?? 0],
                    Data = DecisionFile.DoorwayData(doorway),
                });
            }
            return added;
        }

        private static int RunAreaportals(AreaportalPass pass, VmfDocument working, string copy, string folder, string name, Toolchain tools,
            WorkCompile baseline, SamplePoints samples, List<BspSpace.BrushShape> collision, GameContent content, ChangeGuard guard,
            out WorkCompile? final)
        {
            final = null;
            int existing = working.Entities.Count(e => Audit.PortalClasses.Contains(e.Classname ?? "", StringComparer.OrdinalIgnoreCase));
            int slots = Audit.PortalBudget - existing;
            if (slots <= 0)
            {
                Log.Warn($"areaportals: the map already has {existing}, Garry's Mod's limit; none added");
                return 0;
            }

            var space = BspSpace.Load(baseline.Bsp);
            var doorways = pass.FindDoorways(space, content);
            int framed = doorways.Count;
            doorways = pass.SealedDoorways(doorways, SourceKit.Compile.PortalFile.Load(baseline.Prt), space);
            Log.Info($"areaportals: {framed} framed doorway(s), {doorways.Count} of them dividing two areas, {doorways.Count(d => d.Closes)} with a door that can close them; {slots} slot(s) free");
            foreach (var (reason, count) in pass.Skipped.OrderByDescending(kv => kv.Value))
                Log.Info($"  {count} door(s) skipped: {reason}");
            foreach (var (reason, count) in pass.OpenOnly.OrderByDescending(kv => kv.Value))
                Log.Info($"  {count} doorway(s) get a portal that never closes: {reason}");
            if (doorways.Count == 0)
                return 0;

            pass.Apply(doorways);

            WorkCompile? compile = null;
            BspVisibilityData? vis = null;
            for (int round = 1; round <= 6 && pass.Added.Count > 0; round++)
            {
                compile = WorkCompile.Prepare(working, copy, folder, name);
                var run = compile.RunVbsp(tools);
                if (!run.Succeeded && !compile.Leaked)
                {
                    pass.RevertAll("VBSP failed with the portals in, so the pass was undone");
                    Log.Warn($"areaportals: undone, VBSP failed: {LastLines(run)}");
                    return 0;
                }
                if (compile.Leaked)
                {
                    // An areaportal that does not seal its two areas makes VBSP write a pointfile.
                    var leak = compile.LeakPath!;
                    var near = pass.Added.Where(a => leak.Points.Any(p => Distance(p, a.Doorway) < 64)).Select(a => a.Portal).ToHashSet();
                    if (near.Count == 0)
                    {
                        pass.RevertAll("the map leaked somewhere no new portal is near, so the pass was undone");
                        Log.Warn("areaportals: undone, the map leaked somewhere no new portal is near");
                        return 0;
                    }
                    pass.KeepOnly(pass.Added.Select(a => a.Portal).Where(p => !near.Contains(p)).ToHashSet(),
                        "a leak ran through its doorway: the portal did not seal it");
                    Log.Info($"areaportals: round {round} leaked; {near.Count} portal(s) on the leak path removed");
                    continue;
                }

                vis = BspVisibilityData.Load(compile.Bsp);
                var invalid = AreaportalSelection.Invalid(pass, vis);
                if (invalid.Count == 0)
                    break;
                pass.KeepOnly(pass.Added.Select(a => a.Portal).Except(invalid).ToHashSet(),
                    "VBSP did not put it between two areas: an open window or gap joins them");
                Log.Info($"areaportals: round {round}: {invalid.Count} portal(s) do not divide two areas (an open window or gap joins them) and are removed");
                vis = null;
            }

            if (compile == null || vis == null || pass.Added.Count == 0)
            {
                pass.RevertAll("no portal could be placed, so the pass was undone");
                Log.Warn("areaportals: none could be placed");
                return 0;
            }

            var ranked = AreaportalSelection.Rank(pass, vis, BspSpace.Load(compile.Bsp), samples);
            var keep = AreaportalSelection.Choose(pass, ranked, slots);
            int dropped = pass.Added.Count - keep.Count;
            pass.KeepOnly(keep, $"left out to stay within Garry's Mod's {Audit.PortalBudget} areaportals: it hides less than the ones kept");
            if (dropped > 0)
                Log.Info($"areaportals: {dropped} portal(s) worth least left out to stay within the budget");

            // Leaving portals out reopens their doorways, which can join the two sides of one that is kept.
            // The flood finds those before compiling; anything it misses shows up as a leak below.
            var stillSealed = pass.SealedDoorways(pass.Added.Select(a => a.Doorway).ToList(),
                SourceKit.Compile.PortalFile.Load(baseline.Prt), space).ToHashSet();
            int unsealed = pass.Added.Count(a => !stillSealed.Contains(a.Doorway));
            if (unsealed > 0)
            {
                pass.KeepOnly(pass.Added.Where(a => stillSealed.Contains(a.Doorway)).Select(a => a.Portal).ToHashSet(),
                    "no longer divides two areas once the portals left out reopened their doorways");
                Log.Info($"areaportals: {unsealed} more no longer divide two areas once the others are left out");
            }

            // The set kept is proven by its own compile.
            for (int round = 1; round <= 6 && pass.Added.Count > 0; round++)
            {
                compile = WorkCompile.Prepare(working, copy, folder, name);
                var run = compile.RunVbsp(tools);
                if (compile.Leaked)
                {
                    var leak = compile.LeakPath!;
                    var near = pass.Added.Where(a => leak.Points.Any(p => Distance(p, a.Doorway) < 64)).Select(a => a.Portal).ToHashSet();
                    if (near.Count == 0)
                    {
                        pass.RevertAll("the chosen set leaked somewhere no new portal is near, so the pass was undone");
                        Log.Warn("areaportals: undone, the chosen set leaks somewhere no new portal is near");
                        return 0;
                    }
                    pass.KeepOnly(pass.Added.Select(a => a.Portal).Where(p => !near.Contains(p)).ToHashSet(),
                        "a leak ran through its doorway: the portal did not seal it");
                    Log.Info($"areaportals: the chosen set leaked; {near.Count} portal(s) on the leak path removed");
                    continue;
                }
                if (!run.Succeeded)
                {
                    pass.RevertAll("VBSP failed with the portals in, so the pass was undone");
                    Log.Warn($"areaportals: undone, VBSP failed: {LastLines(run)}");
                    return 0;
                }
                vis = BspVisibilityData.Load(compile.Bsp);
                var invalid = AreaportalSelection.Invalid(pass, vis);
                if (invalid.Count == 0)
                    break;
                pass.KeepOnly(pass.Added.Select(a => a.Portal).Except(invalid).ToHashSet(),
                    "VBSP did not put it between two areas: an open window or gap joins them");
            }
            if (pass.Added.Count == 0)
            {
                Log.Warn("areaportals: none could be placed");
                return 0;
            }

            int differences = BspSpace.CollisionDifferences(BspSpace.Load(compile!.Bsp).CollisionShapes(CollisionMask), collision);
            if (differences > 0 || vis!.HighestPortalNumber > Audit.PortalBudget)
            {
                pass.RevertAll(differences > 0 ? "collision changed, so the pass was undone" : "a portal number went past what Garry's Mod loads, so the pass was undone");
                Log.Warn(differences > 0
                    ? $"areaportals: undone, collision changed ({differences} brushes)"
                    : $"areaportals: undone, a portal would be numbered {vis.HighestPortalNumber}, past what Garry's Mod loads");
                return 0;
            }

            // What the closing portals hide beyond what VVIS already culls needs this set's own visibility.
            var visRun = compile.RunVvis(tools);
            string shutReport = "";
            if (visRun.Succeeded)
            {
                vis = BspVisibilityData.Load(compile.Bsp);
                var shut = pass.Added.Where(a => a.Doorway.Closes)
                    .Select(a => vis.PortalNumbersByName.GetValueOrDefault(a.Portal.Get("targetname")!, -1)).Where(k => k >= 0).ToHashSet();
                var (open, closedCost) = AreaportalSelection.VisibleWithDoorsShut(vis, BspSpace.Load(compile.Bsp), samples, shut);
                shutReport = $"with those doors shut, {closedCost:N0} triangles are visible from a standing position on average, against {open:N0} with them open";
            }

            pass.Declare(guard);
            int closing = pass.Added.Count(a => a.Doorway.Closes);
            int named = pass.Added.Count(a => a.NamedDoor);
            Log.Check($"areaportals: {pass.Added.Count} added ({closing} close with their door, {named} door(s) given a name); " +
                $"{vis.HighestPortalNumber} of {Audit.PortalBudget} portal numbers used; {vis.AreaCount - 1} areas");
            if (shutReport.Length > 0)
                Log.Check($"areaportals: {shutReport}");
            final = compile;
            return pass.Added.Count;
        }

        private static double Distance(SourceKit.Geometry.Vec3 p, Doorway d)
        {
            var lo = d.Mins;
            var hi = d.Maxs;
            double dx = Math.Max(0, Math.Max(lo.X - p.X, p.X - hi.X));
            double dy = Math.Max(0, Math.Max(lo.Y - p.Y, p.Y - hi.Y));
            double dz = Math.Max(0, Math.Max(lo.Z - p.Z, p.Z - hi.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Compiles the map as it is, for every pass to be measured against. Reused when nothing that
        /// would change it has: the map, the compilers, and this version of Viswright.
        /// </summary>
        private static WorkCompile? Baseline(VmfDocument working, string copy, string folder, string name, Toolchain tools)
        {
            string key = BaselineKey(working, tools);
            string keyFile = Path.Combine(folder, "key.txt");
            string bsp = Path.Combine(folder, name + ".bsp");
            if (File.Exists(keyFile) && File.Exists(bsp) && File.ReadAllText(keyFile) == key)
            {
                Log.Info("baseline: reusing the compile from last time, nothing it depends on has changed");
                return WorkCompile.Existing(Path.Combine(folder, name + ".vmf"));
            }

            var baseline = WorkCompile.Prepare(working, copy, folder, name);
            var run = baseline.RunVbsp(tools);
            if (baseline.Leaked || !run.Succeeded)
            {
                Log.Warn(baseline.Leaked
                    ? "baseline: the map leaks as it is, so no pass can be checked; compiling it unchanged"
                    : $"baseline: VBSP failed on the map as it is ({LastLines(run)}); compiling it unchanged");
                return null;
            }
            var vis = baseline.RunVvis(tools);
            if (!vis.Succeeded)
            {
                Log.Warn($"baseline: VVIS failed ({LastLines(vis)}); compiling the map unchanged");
                return null;
            }
            File.WriteAllText(keyFile, key);
            return baseline;
        }

        /// <summary>
        /// Compiles with the pass's brushes moved, putting back whichever ones each leak runs through,
        /// until the map seals. Returns the sealed compile, or null if the pass had to be undone.
        /// </summary>
        private static WorkCompile? CompileUntilSealed(DetailPass pass, VmfDocument working, string copy, string folder, string name, Toolchain tools)
        {
            for (int round = 1; round <= LeakRepairRounds && pass.Moved.Count > 0; round++)
            {
                var compile = WorkCompile.Prepare(working, copy, folder, name);
                var run = compile.RunVbsp(tools);
                if (!compile.Leaked)
                {
                    if (run.Succeeded)
                        return compile;
                    pass.RevertAll("the pass was undone: VBSP failed");
                    Log.Warn($"detail: undone, VBSP failed: {LastLines(run)}");
                    return null;
                }

                var leak = compile.LeakPath!;
                int reverted = 0;
                foreach (double reach in new[] { 16.0, 64, 256 })
                {
                    reverted = pass.RevertNear(leak, reach);
                    if (reverted > 0)
                        break;
                }
                if (reverted == 0)
                {
                    pass.RevertAll("the pass was undone: the map leaked somewhere no candidate is near");
                    Log.Warn("detail: undone, the map leaked somewhere no candidate brush is near");
                    return null;
                }
                Log.Info($"detail: round {round} leaked; {reverted} brush(es) on the leak path stay world");
            }

            if (pass.Moved.Count > 0)
            {
                pass.RevertAll($"the pass was undone: still leaking after {LeakRepairRounds} rounds");
                Log.Warn($"detail: undone, still leaking after {LeakRepairRounds} rounds");
            }
            return null;
        }

        /// <summary>Why a pass's compile must not be kept, or null when it may.</summary>
        private static string? Rejection(WorkCompile compile, List<BspSpace.BrushShape> collision, CompileMeasure before, CompileMeasure after)
        {
            if (compile.VvisRun is { Succeeded: false } vis)
                return $"VVIS failed: {LastLines(vis)}";
            int differences = BspSpace.CollisionDifferences(BspSpace.Load(compile.Bsp).CollisionShapes(CollisionMask), collision);
            if (differences > 0)
                return $"collision changed ({differences} brushes have no counterpart)";
            if (after.SampledTriangles > before.SampledTriangles * CostTolerance)
                return $"more is visible: {after.SampledTriangles:N0} triangles from a standing position against {before.SampledTriangles:N0}";
            if (after.Faces >= 65536 * 85 / 100 || after.LeafFaces >= 65536 * 85 / 100)
                return "the map would be within 15% of the face or leaf face limit";
            return null;
        }

        private static string BaselineKey(VmfDocument working, Toolchain tools)
        {
            var text = new StringBuilder(working.ToString());
            foreach (var exe in new[] { tools.Vbsp, tools.Vvis })
            {
                var info = new FileInfo(exe);
                text.Append('|').Append(info.FullName).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            }
            text.Append('|').Append(tools.GameFolder).Append('|').Append(tools.VbspFlags).Append('|').Append(tools.VvisFlags);
            text.Append('|').Append(typeof(Optimizer).Assembly.ManifestModule.ModuleVersionId);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }

        private static string LastLines(CompileRun run) =>
            string.Join(" / ", run.Output.Where(l => l.Trim().Length > 0).TakeLast(3).Select(l => l.Trim()));
    }
}
