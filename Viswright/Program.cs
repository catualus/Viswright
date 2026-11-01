using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>
    /// Command line entry point. Compile Pal runs <c>optimize</c> as the step before VBSP; the other
    /// commands are for running by hand.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Usage();
                return 1;
            }

            using var interrupt = HandleCtrlC();

            try
            {
                if (TryGetFlagValue(args, "-threads", out string? threads))
                {
                    if (!int.TryParse(threads, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 1)
                        throw new ArgumentException($"-threads needs a whole number of at least 1, not '{threads}'.");
                    SourceKitConcurrency.MaxThreads = count;
                }

                return args[0].ToLowerInvariant() switch
                {
                    "optimize" => Optimize(args),
                    "diff" => Diff(args),
                    "status" => Status(args),
                    "help" or "-help" or "--help" or "/?" => Usage(),
                    _ => Unknown(args[0]),
                };
            }
            catch (OperationCanceledException)
            {
                // Nothing is handed on when a run is stopped, so the compile is left on its own map.
                Log.Error("cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                Log.Error(IsExpected(ex) ? ex.Message : ex.ToString());
                return 1;
            }
        }

        private static int Optimize(string[] args)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "-source", "-threads", "-dryrun" };
            RefuseUnknownFlags(args, known);

            string input = Positional(args, 1) ?? throw new ArgumentException("optimize needs the VMF to read.");
            TryGetFlagValue(args, "-source", out string? source);

            var result = Optimizer.Run(new OptimizeOptions
            {
                Input = input,
                Source = source,
                DryRun = HasFlag(args, "-dryrun"),
            });

            // Last, and only once everything above has succeeded: from this line on, Compile Pal
            // compiles the copy.
            if (result.CopyPath != null)
                Log.HandOff(result.CopyPath);
            return 0;
        }

        private static int Diff(string[] args)
        {
            string a = Positional(args, 1) ?? throw new ArgumentException("diff needs two VMFs.");
            string b = Positional(args, 2) ?? throw new ArgumentException("diff needs two VMFs.");

            var changes = VmfDiff.Compare(VmfDocument.Load(a), VmfDocument.Load(b));
            foreach (var change in changes)
                Console.Out.WriteLine(change);
            Console.Out.WriteLine($"{changes.Count} change(s)");
            return 0;
        }

        /// <summary>
        /// The one line of JSON Compile Pal shows on the map's card before a compile. Offline, fast, and
        /// never an error: a status that cannot be worked out is simply not shown.
        /// </summary>
        private static int Status(string[] args)
        {
            Log.Sink = _ => { };
            bool enabled = Environment.GetEnvironmentVariable("COMPILE_PAL_STEP_ENABLED") != "false";

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("label", "Viswright");
                json.WriteString("detail", enabled
                    ? "No optimisation passes yet: the map is copied and compiled as it is."
                    : "Viswright is not ticked for this map.");
                json.WriteString("severity", "info");
                json.WriteEndObject();
            }
            Console.Out.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
            return 0;
        }

        private static int Usage()
        {
            Console.Out.WriteLine("""
                viswright - automatic visibility optimisation for Source maps

                  viswright optimize <map.vmf> [-source <queued.vmf>] [-dryrun] [-threads N]
                      Optimise a copy of the map and hand it to the rest of the compile. The map
                      itself is never written. -source names the map as queued when <map.vmf> is an
                      earlier step's copy; -dryrun reports without handing anything on.

                  viswright diff <a.vmf> <b.vmf>
                      List what differs between two maps, block by block.

                  viswright status <map.vmf>
                      One line of JSON for Compile Pal's map card.
                """);
            return 1;
        }

        private static int Unknown(string command)
        {
            Log.Error($"unknown command '{command}'. Run viswright help for the list.");
            return 1;
        }

        private static bool IsExpected(Exception ex) =>
            ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException
                or NotSupportedException or InvalidOperationException;

        private static string? Positional(string[] args, int index)
        {
            int seen = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith('-'))
                {
                    if (TakesValue(args[i]))
                        i++;
                    continue;
                }
                if (seen++ == index)
                    return args[i];
            }
            return null;
        }

        private static bool TakesValue(string flag) =>
            flag.Equals("-source", StringComparison.OrdinalIgnoreCase) || flag.Equals("-threads", StringComparison.OrdinalIgnoreCase);

        private static bool HasFlag(string[] args, string flag) =>
            Array.Exists(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        private static bool TryGetFlagValue(string[] args, string flag, out string? value)
        {
            value = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                {
                    value = args[i + 1];
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// A misspelt flag is refused rather than ignored: a pass someone thinks they turned on and did
        /// not is worse than a compile that stops and says so.
        /// </summary>
        private static void RefuseUnknownFlags(string[] args, HashSet<string> known)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith('-'))
                    continue;
                if (!known.Contains(args[i]))
                    throw new ArgumentException($"unknown option '{args[i]}'.");
                if (TakesValue(args[i]))
                    i++;
            }
        }

        private static IDisposable HandleCtrlC()
        {
            var source = new CancellationTokenSource();
            SourceKitConcurrency.CancellationToken = source.Token;

            ConsoleCancelEventHandler handler = (_, e) =>
            {
                if (source.IsCancellationRequested)
                    return;     // second press: let it end the process
                e.Cancel = true;
                Console.Error.WriteLine("      stopping. Press Ctrl+C again to quit now.");
                source.Cancel();
            };
            Console.CancelKeyPress += handler;

            return new Unsubscribe(() =>
            {
                Console.CancelKeyPress -= handler;
                SourceKitConcurrency.CancellationToken = CancellationToken.None;
                source.Dispose();
            });
        }

        private sealed class Unsubscribe(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }
}
