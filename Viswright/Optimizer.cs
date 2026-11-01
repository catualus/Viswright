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
            if (options.Detail || options.Nodraw || options.Areaportals)
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

            var samples = SamplePoints.Find(BspSpace.Load(baseline.Bsp));
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

            return (detailed, nodrawn, portals);
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
