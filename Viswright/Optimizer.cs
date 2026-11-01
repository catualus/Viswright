using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SourceKit.Vmf;

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
    public static class Optimizer
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

            int detailed = 0, nodrawn = 0, portals = 0;
            if (options.Detail || options.Nodraw || options.Areaportals || options.Fades || options.Slim || options.Hints)
                (detailed, nodrawn, portals) = RunPasses(options, working, copy, guard);

            var changes = VmfDiff.Compare(original, working);
            var undeclared = guard.Undeclared(changes);
            if (undeclared.Count > 0)
                throw new InvalidOperationException(
                    "The optimised map differs from the original in ways no pass declared, so it was not used:"
                    + Environment.NewLine + string.Join(Environment.NewLine, undeclared.Take(20).Select(c => "  " + c)));

            Log.Check($"{changes.Count} change(s), all declared");

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

        private static (int Detailed, int Nodrawn, int Portals) RunPasses(OptimizeOptions options, VmfDocument working, string copy, ChangeGuard guard)
        {
            var tools = options.Tools ?? throw new ArgumentException("The passes need -vbsp, -vvis and -game to check their changes.");
            tools.Validate();

            string name = Path.GetFileNameWithoutExtension(copy);
            string work = Path.Combine(Path.GetDirectoryName(copy)!, "work");

            var baseline = Baseline(working, copy, Path.Combine(work, "baseline"), name, tools);
            if (baseline == null)
                return (0, 0, 0);

            var baselineSpace = BspSpace.Load(baseline.Bsp);
            var reachable = SamplePoints.Reachable(working, SourceKit.Compile.PortalFile.Load(baseline.Prt), baselineSpace);
            var samples = SamplePoints.Find(baselineSpace, reachable);
            if (reachable == null)
                Log.Info("measure: the map has no spawn point, so every floor counts as somewhere a player stands");
            var before = CompileMeasure.Of(baseline, samples);
            var collision = BspSpace.Load(baseline.Bsp).CollisionShapes(CollisionMask);
            Log.Check($"before: {before.Describe()}");

            int detailed = 0, nodrawn = 0, portals = 0;
            CompileMeasure current = before;
            var content = GameContent.Open(tools.GameFolder);

            // Before detail, so the detail pass sees the new portals and leaves their frames as world.
            // Each pass is judged against the map as the passes before it left it.
            WorkCompile reference = baseline;
            if (options.Areaportals)
            {
                portals = RunAreaportals(working, copy, Path.Combine(work, "areaportals"), name, tools, baseline, samples, collision, content, guard,
                    out var portalCompile);
                if (portalCompile is { VvisRun.Succeeded: true })
                {
                    reference = portalCompile;
                    current = CompileMeasure.Of(portalCompile, samples);
                }
            }

            if (options.Detail)
            {
                var pass = new DetailPass(working);
                var candidates = DetailPass.Candidates(working, BspSpace.Load(reference.Bsp));
                pass.Apply(candidates);
                Log.Info($"detail: {candidates.Count} world brush(es) are candidates");

                var compile = CompileUntilSealed(pass, working, copy, Path.Combine(work, "detail"), name, tools);
                if (compile != null && pass.Moved.Count > 0)
                {
                    compile.RunVvis(tools);
                    var after = CompileMeasure.Of(compile, samples);
                    string? reason = Rejection(compile, collision, current, after);
                    if (reason != null)
                    {
                        pass.RevertAll();
                        Log.Warn($"detail: undone, {reason}");
                    }
                    else
                    {
                        detailed = pass.Moved.Count;
                        pass.Declare(guard);
                        current = after;
                        Log.Check($"detail: {detailed} brush(es) made func_detail; after: {after.Describe()}");
                    }
                }
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
                    if (reason != null)
                    {
                        pass.RevertAll();
                        Log.Warn($"nodraw: undone, {reason}");
                    }
                    else
                    {
                        nodrawn = found;
                        pass.Declare(guard);
                        int faces = BspVisibilityData.Load(compile.Bsp).Faces.Count;
                        Log.Check($"nodraw: {found} hidden face(s) made nodraw; faces {current.Faces:N0} -> {faces:N0}");
                        foreach (var (side, centre) in pass.Changed.Zip(pass.Centres).Take(5))
                            Log.Info($"  e.g. side {side.Side.Id} ({side.Before}) at ({centre})");
                    }
                }
                else
                    Log.Check("nodraw: no face is fully hidden");
            }

            if (options.Fades)
                RunFades(options, working, reference, content, guard, Path.Combine(work, "fades.txt"));
            if (options.Slim)
                RunSlimming(options, working, copy, Path.Combine(work, "slim"), name, tools, collision, content, guard);
            // Last: it is measured against the map with every other change made.
            if (options.Hints)
                RunHints(options, working, copy, Path.Combine(work, "hints"), name, tools, collision, samples, guard);

            return (detailed, nodrawn, portals);
        }

        private static void RunFades(OptimizeOptions options, VmfDocument working, WorkCompile reference, GameContent content,
            ChangeGuard guard, string listFile)
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

        private static void RunSlimming(OptimizeOptions options, VmfDocument working, string copy, string folder, string name,
            Toolchain tools, List<BspSpace.BrushShape> collision, GameContent content, ChangeGuard guard)
        {
            var lua = LuaGuard.Scan(LuaGuard.GameFolders(tools.GameFolder).Concat(options.LuaPaths));
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
                Log.Check("slim: no entity can be made static");
                return;
            }

            // The map as the passes before this one left it, to count against.
            pass.RevertAll();
            var before = WorkCompile.Prepare(working, copy, folder + "-before", name);
            if (!before.RunVbsp(tools).Succeeded || before.Leaked)
            {
                Log.Warn("slim: skipped, the map does not compile cleanly before it");
                return;
            }
            var countsBefore = EntityCounts(before.Bsp);
            pass = new SlimmingPass();
            count = pass.Apply(working, content, lua);

            // VBSP refuses a static prop whose model was not built for it, and deletes the prop.
            var compile = WorkCompile.Prepare(working, copy, folder, name);
            var run = compile.RunVbsp(tools);
            var refused = run.Output
                .Where(l => l.Contains("staticprop", StringComparison.OrdinalIgnoreCase) || l.Contains("static prop", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("prop_static", StringComparison.OrdinalIgnoreCase))
                .SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l, @"models[/\\][^""\s!]+?\.mdl", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Select(m => m.Value.Replace('\\', '/').ToLowerInvariant()).ToHashSet();
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
                pass.RevertAll();
                Log.Warn($"slim: undone, the counts do not add up: entities {countsBefore.Entities} -> {countsAfter.Entities}, " +
                    $"static props {countsBefore.StaticProps} -> {countsAfter.StaticProps}, for {props} prop(s) and {brushes} brush(es) converted");
                return;
            }

            pass.Declare(guard);
            Log.Check($"slim: {props} prop_dynamic made prop_static, {brushes} func_brush made func_detail: {pass.Converted.Count} fewer entities" +
                (back > 0 ? $" ({back} put back: VBSP would not take their model as static)" : ""));
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
        private static void RunHints(OptimizeOptions options, VmfDocument working, string copy, string folder, string name, Toolchain tools,
            List<BspSpace.BrushShape> collision, SamplePoints samples, ChangeGuard guard)
        {
            string vvis = Path.GetFileName(tools.Vvis);
            if (!vvis.Contains("++", StringComparison.Ordinal) && !vvis.Contains("plusplus", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("hints: skipped, the search compiles the map several times and needs VVIS++ to do it in reasonable time");
                return;
            }
            var budget = TimeSpan.FromMinutes(options.HintMinutes);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var reference = WorkCompile.Prepare(working, copy, folder + "-reference", name);
            if (!reference.RunVbsp(tools).Succeeded || reference.Leaked || !reference.RunVvis(tools).Succeeded)
            {
                Log.Warn("hints: skipped, the map does not compile cleanly before them");
                return;
            }
            var before = CompileMeasure.Of(reference, samples);
            var referenceCost = CompileMeasure.PerPoint(reference, samples);
            var squares = HintPass.Squares(samples, BspSpace.Load(reference.Bsp), _ => false);
            Log.Info($"hints: {squares.Count} square(s) of open ground to try, {budget.TotalMinutes:0} minute(s) to search");
            if (squares.Count == 0)
                return;

            var pass = new HintPass(working);
            var best = new Dictionary<HintSquare, (double Height, double Gain)>();
            TimeSpan slowest = reference.VbspRun!.Elapsed + reference.VvisRun!.Elapsed;
            for (int round = 0; ; round++)
            {
                var tries = squares.Select(s => (Square: s, Height: s.Heights().Skip(round).FirstOrDefault(double.NaN)))
                    .Where(t => !double.IsNaN(t.Height)).ToList();
                if (tries.Count == 0)
                    break;
                // Leave time for the compile that proves the squares chosen.
                if (clock.Elapsed + slowest * 2 > budget)
                {
                    Log.Info($"hints: time is up after {round} height(s)");
                    break;
                }
                foreach (var (square, height) in tries)
                    pass.Place(square, height);
                var compile = Compile(working, copy, folder, name, tools);
                pass.RevertAll();
                if (compile == null)
                {
                    Log.Warn($"hints: height {round + 1} did not compile cleanly; skipped");
                    continue;
                }
                slowest = TimeSpan.FromTicks(Math.Max(slowest.Ticks, (compile.VbspRun!.Elapsed + compile.VvisRun!.Elapsed).Ticks));
                var cost = CompileMeasure.PerPoint(compile, samples);
                int better = 0;
                foreach (var (square, height) in tries)
                {
                    double gain = Gain(square, referenceCost, cost);
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
                Log.Check("hints: none kept, no square saw less with a hint");
                return;
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
                    pass.Remove(square);
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
                pass.RevertAll();
                Log.Warn($"hints: undone, {reason}");
                return;
            }

            pass.Declare(guard);
            string list = Path.Combine(folder, "hints.txt");
            File.WriteAllLines(list, pass.Placed.OrderBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Y)
                .Select(kv => $"{kv.Key.Mins.X:0} {kv.Key.Mins.Y:0} to {kv.Key.Maxs.X:0} {kv.Key.Maxs.Y:0} at z {kv.Value.Height:0}: {best[kv.Key].Gain:P1} less seen there"));
            Log.Check($"hints: {pass.Placed.Count} kept; triangles visible from a standing position {before.SampledTriangles:N0} -> {after!.SampledTriangles:N0} " +
                $"({1 - after.SampledTriangles / before.SampledTriangles:P1} less), portals {before.Portals:N0} -> {after.Portals:N0}, " +
                $"vvis {before.VvisTime.TotalSeconds:0}s -> {after.VvisTime.TotalSeconds:0}s; list in {list}");
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

            var pass = new AreaportalPass(working);
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
                    pass.RevertAll();
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
                        pass.RevertAll();
                        Log.Warn("areaportals: undone, the map leaked somewhere no new portal is near");
                        return 0;
                    }
                    pass.KeepOnly(pass.Added.Select(a => a.Portal).Where(p => !near.Contains(p)).ToHashSet());
                    Log.Info($"areaportals: round {round} leaked; {near.Count} portal(s) on the leak path removed");
                    continue;
                }

                vis = BspVisibilityData.Load(compile.Bsp);
                var invalid = AreaportalSelection.Invalid(pass, vis);
                if (invalid.Count == 0)
                    break;
                pass.KeepOnly(pass.Added.Select(a => a.Portal).Except(invalid).ToHashSet());
                Log.Info($"areaportals: round {round}: {invalid.Count} portal(s) do not divide two areas (an open window or gap joins them) and are removed");
                vis = null;
            }

            if (compile == null || vis == null || pass.Added.Count == 0)
            {
                pass.RevertAll();
                Log.Warn("areaportals: none could be placed");
                return 0;
            }

            var ranked = AreaportalSelection.Rank(pass, vis, BspSpace.Load(compile.Bsp), samples);
            var keep = AreaportalSelection.Choose(pass, ranked, slots);
            int dropped = pass.Added.Count - keep.Count;
            pass.KeepOnly(keep);
            if (dropped > 0)
                Log.Info($"areaportals: {dropped} portal(s) worth least left out to stay within the budget");

            // Leaving portals out reopens their doorways, which can join the two sides of one that is kept.
            // The flood finds those before compiling; anything it misses shows up as a leak below.
            var stillSealed = pass.SealedDoorways(pass.Added.Select(a => a.Doorway).ToList(),
                SourceKit.Compile.PortalFile.Load(baseline.Prt), space).ToHashSet();
            int unsealed = pass.Added.Count(a => !stillSealed.Contains(a.Doorway));
            if (unsealed > 0)
            {
                pass.KeepOnly(pass.Added.Where(a => stillSealed.Contains(a.Doorway)).Select(a => a.Portal).ToHashSet());
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
                        pass.RevertAll();
                        Log.Warn("areaportals: undone, the chosen set leaks somewhere no new portal is near");
                        return 0;
                    }
                    pass.KeepOnly(pass.Added.Select(a => a.Portal).Where(p => !near.Contains(p)).ToHashSet());
                    Log.Info($"areaportals: the chosen set leaked; {near.Count} portal(s) on the leak path removed");
                    continue;
                }
                if (!run.Succeeded)
                {
                    pass.RevertAll();
                    Log.Warn($"areaportals: undone, VBSP failed: {LastLines(run)}");
                    return 0;
                }
                vis = BspVisibilityData.Load(compile.Bsp);
                var invalid = AreaportalSelection.Invalid(pass, vis);
                if (invalid.Count == 0)
                    break;
                pass.KeepOnly(pass.Added.Select(a => a.Portal).Except(invalid).ToHashSet());
            }
            if (pass.Added.Count == 0)
            {
                Log.Warn("areaportals: none could be placed");
                return 0;
            }

            int differences = BspSpace.CollisionDifferences(BspSpace.Load(compile!.Bsp).CollisionShapes(CollisionMask), collision);
            if (differences > 0 || vis!.HighestPortalNumber > Audit.PortalBudget)
            {
                pass.RevertAll();
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
                    pass.RevertAll();
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
                    pass.RevertAll();
                    Log.Warn("detail: undone, the map leaked somewhere no candidate brush is near");
                    return null;
                }
                Log.Info($"detail: round {round} leaked; {reverted} brush(es) on the leak path stay world");
            }

            if (pass.Moved.Count > 0)
            {
                pass.RevertAll();
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
