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

        /// <summary>The compilers every pass is checked with. Needed when any pass is on.</summary>
        public Toolchain? Tools { get; init; }
    }

    /// <summary>What a run did.</summary>
    public sealed record OptimizeResult(string? CopyPath, IReadOnlyList<VmfChange> Changes, int Detailed = 0, int Nodrawn = 0);

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

        private const int LeakRepairRounds = 12;

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

            int detailed = 0, nodrawn = 0;
            if (options.Detail || options.Nodraw)
                (detailed, nodrawn) = RunPasses(options, working, copy, guard);

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
                return new OptimizeResult(null, changes, detailed, nodrawn);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            working.Save(copy);
            Log.Out(copy);
            return new OptimizeResult(copy, changes, detailed, nodrawn);
        }

        private static (int Detailed, int Nodrawn) RunPasses(OptimizeOptions options, VmfDocument working, string copy, ChangeGuard guard)
        {
            var tools = options.Tools ?? throw new ArgumentException("The passes need -vbsp, -vvis and -game to check their changes.");
            tools.Validate();

            string name = Path.GetFileNameWithoutExtension(copy);
            string work = Path.Combine(Path.GetDirectoryName(copy)!, "work");

            var baseline = Baseline(working, copy, Path.Combine(work, "baseline"), name, tools);
            if (baseline == null)
                return (0, 0);

            var samples = SamplePoints.Find(BspSpace.Load(baseline.Bsp));
            var before = CompileMeasure.Of(baseline, samples);
            var collision = BspSpace.Load(baseline.Bsp).CollisionShapes(-1);
            Log.Check($"before: {before.Describe()}");

            int detailed = 0, nodrawn = 0;
            CompileMeasure current = before;

            if (options.Detail)
            {
                var pass = new DetailPass(working);
                var candidates = DetailPass.Candidates(working, BspSpace.Load(baseline.Bsp));
                pass.Apply(candidates);
                Log.Info($"detail: {candidates.Count} world brush(es) are candidates");

                var compile = CompileUntilSealed(pass, working, copy, Path.Combine(work, "detail"), name, tools);
                if (compile != null && pass.Moved.Count > 0)
                {
                    compile.RunVvis(tools);
                    var after = CompileMeasure.Of(compile, samples);
                    string? reason = Rejection(compile, collision, before, after);
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
                int found = pass.Apply(working, MaterialOpacity.ForGame(tools.GameFolder));
                if (found > 0)
                {
                    var compile = WorkCompile.Prepare(working, copy, Path.Combine(work, "nodraw"), name);
                    var run = compile.RunVbsp(tools);
                    string? reason = compile.Leaked ? "the map leaked"
                        : !run.Succeeded ? $"VBSP failed: {LastLines(run)}"
                        : BspSpace.CollisionDifferences(BspSpace.Load(compile.Bsp).CollisionShapes(-1), collision) is > 0 and var n ? $"collision changed ({n} brushes)"
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

            return (detailed, nodrawn);
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
            int differences = BspSpace.CollisionDifferences(BspSpace.Load(compile.Bsp).CollisionShapes(-1), collision);
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
