using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    }

    /// <summary>What a run did.</summary>
    public sealed record OptimizeResult(string? CopyPath, IReadOnlyList<VmfChange> Changes);

    /// <summary>
    /// The optimiser: read the map, run the enabled passes on it, check that only declared changes were
    /// made, and write the copy the rest of the compile uses.
    ///
    /// No passes exist yet. A run today proves the plumbing - the copy is the map, byte for byte apart
    /// from instance paths re-pointed at the same files, and VBSP compiles it in place of the original.
    /// </summary>
    public static class Optimizer
    {
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
                return new OptimizeResult(null, changes);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            working.Save(copy);
            Log.Out(copy);
            return new OptimizeResult(copy, changes);
        }
    }
}
