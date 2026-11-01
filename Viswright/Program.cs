using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
                    "audit" => AuditCommand(args),
                    "report" => Report(args),
                    "diff" => Diff(args),
                    "collision" => CollisionCommand(args),
                    "model" => ModelCommand(args),
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
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "-source", "-threads", "-dryrun", "-detail", "-nodraw", "-areaportals", "-fades", "-fadepixels", "-slim", "-luapath", "-vbsp", "-vvis", "-game" };
            RefuseUnknownFlags(args, known);

            string input = Positional(args, 1) ?? throw new ArgumentException("optimize needs the VMF to read.");
            TryGetFlagValue(args, "-source", out string? source);

            Toolchain? tools = null;
            if (TryGetFlagValue(args, "-vbsp", out string? vbsp) && TryGetFlagValue(args, "-vvis", out string? vvis)
                && TryGetFlagValue(args, "-game", out string? game))
                tools = new Toolchain(vbsp!, vvis!, game!);

            var result = Optimizer.Run(new OptimizeOptions
            {
                Input = input,
                Source = source,
                DryRun = HasFlag(args, "-dryrun"),
                Detail = HasFlag(args, "-detail"),
                Nodraw = HasFlag(args, "-nodraw"),
                Areaportals = HasFlag(args, "-areaportals"),
                Fades = HasFlag(args, "-fades"),
                FadePixels = TryGetFlagValue(args, "-fadepixels", out string? pixels)
                    ? (double.TryParse(pixels, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) && p >= 1 && p <= 64 ? p
                        : throw new ArgumentException($"-fadepixels needs a number of pixels from 1 to 64, not '{pixels}'."))
                    : 6,
                Slim = HasFlag(args, "-slim"),
                LuaPaths = AllFlagValues(args, "-luapath"),
                Tools = tools,
            });

            // Last, and only once everything above has succeeded: from this line on, Compile Pal
            // compiles the copy.
            if (result.CopyPath != null)
                Log.HandOff(result.CopyPath);
            return 0;
        }

        private static int AuditCommand(string[] args)
        {
            RefuseUnknownFlags(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "-threads" });
            string map = Positional(args, 1) ?? throw new ArgumentException("audit needs the VMF to read.");
            var findings = Audit.Run(VmfDocument.Load(map));
            Audit.Print(findings);
            return findings.Any(f => f.Level == FindingLevel.Error) ? 1 : 0;
        }

        /// <summary>
        /// Measures a compiled map's visibility. Runs after VVIS, so it sees the visibility the map will
        /// ship with. An areaportal numbered past what Garry's Mod loads is an error here, because the
        /// map it describes will not load.
        /// </summary>
        private static int Report(string[] args)
        {
            RefuseUnknownFlags(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "-threads" });
            string bsp = Positional(args, 1) ?? throw new ArgumentException("report needs the BSP to read.");
            if (!File.Exists(bsp))
                throw new FileNotFoundException($"The BSP {bsp} does not exist. Run VBSP and VVIS first.", bsp);

            Log.Vmf(Path.GetFileName(bsp));
            var report = VisReport.Build(BspVisibilityData.Load(bsp));
            foreach (string line in report.Lines())
                Log.Check(line);

            if (report.HighestPortalNumber > VisReport.GmodMaxPortalNumber)
            {
                Log.Error($"an areaportal is numbered {report.HighestPortalNumber}; Garry's Mod refuses to load a map with one above {VisReport.GmodMaxPortalNumber}");
                return 1;
            }
            return 0;
        }

        /// <summary>What Viswright reads from a model: for finding out why a prop was or was not changed.</summary>
        private static int ModelCommand(string[] args)
        {
            string path = Positional(args, 1) ?? throw new ArgumentException("model needs a model path, such as models/props_c17/door01_left.mdl.");
            if (!TryGetFlagValue(args, "-game", out string? game))
                throw new ArgumentException("model needs -game <folder>.");
            var model = GameContent.Open(game!).Model(path) ?? throw new FileNotFoundException($"{path} was not found in the game's content.");
            Console.Out.WriteLine($"version {model.Version}, flags 0x{model.Flags:X}, static prop {model.StaticProp}, usable as prop_static {model.UsableAsStaticProp}");
            Console.Out.WriteLine($"hull ({model.HullMin}) - ({model.HullMax})");
            Console.Out.WriteLine($"textures: {string.Join(", ", model.TextureNames)} in {string.Join(", ", model.TextureFolders)}");
            Console.Out.WriteLine($"keyvalues: {(model.KeyValues.Length == 0 ? "(none)" : model.KeyValues)}");
            return 0;
        }

        /// <summary>Lists the collision brushes two compiles do not share; for finding out why a pass was undone.</summary>
        private static int CollisionCommand(string[] args)
        {
            string a = Positional(args, 1) ?? throw new ArgumentException("collision needs two BSPs.");
            string b = Positional(args, 2) ?? throw new ArgumentException("collision needs two BSPs.");
            var left = BspSpace.Load(a).CollisionShapes(-1);
            var right = BspSpace.Load(b).CollisionShapes(-1);
            Console.Out.WriteLine($"{left.Count:N0} and {right.Count:N0} brushes; {BspSpace.CollisionDifferences(left, right)} without a counterpart");
            foreach (var s in left.Where(x => BspSpace.CollisionDifferences([x], right) == right.Count + 1).Take(10))
                Console.Out.WriteLine($"  only in the first: contents {s.Contents:X} ({s.Mins}) - ({s.Maxs}) volume {s.Volume:0.#}");
            foreach (var s in right.Where(x => BspSpace.CollisionDifferences([x], left) == left.Count + 1).Take(10))
                Console.Out.WriteLine($"  only in the second: contents {s.Contents:X} ({s.Mins}) - ({s.Maxs}) volume {s.Volume:0.#}");
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

            // The areaportal budget is the one thing worth saying before a compile: a map past it will
            // not load, and nothing in the compile itself says so. Counted by scanning lines rather
            // than parsing, because this runs for every queued map in front of someone waiting.
            int? portals = null;
            string? map = Positional(args, 1);
            if (map != null && File.Exists(map))
            {
                try
                {
                    portals = CountTopLevelPortals(map);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            // Compile Pal shows the step name in front of the label, so the label is only what follows it.
            string label = portals is { } p ? $"{p}/{Audit.PortalBudget} areaportals" : "ready";
            string detail = !enabled
                ? "Viswright is not ticked for this map."
                : portals > Audit.PortalBudget
                    ? $"The map has {portals} areaportals and areaportal windows. Garry's Mod refuses to load a map with more than {Audit.PortalBudget}, so the compile would stop."
                    : "No optimisation passes yet: the map is audited, copied and compiled as it is.";
            string severity = portals > Audit.PortalBudget ? (enabled ? "blocking" : "warn")
                : portals > Audit.PortalBudget - 20 ? "warn" : "info";

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("label", label);
                json.WriteString("detail", detail);
                json.WriteString("severity", severity);
                json.WriteEndObject();
            }
            Console.Out.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
            return 0;
        }

        /// <summary>
        /// Areaportal entities VBSP will compile: only those in a top-level entity block. Hammer writes
        /// an object hidden with Ctrl+H inside a "hidden" block, which VBSP does not read at all, so a
        /// plain text search over-counts any map with hidden portals.
        /// </summary>
        internal static int CountTopLevelPortals(string map)
        {
            int count = 0, depth = 0;
            bool inEntity = false;
            string? pending = null;
            foreach (string raw in File.ReadLines(map))
            {
                string line = raw.Trim();
                if (line == "{")
                {
                    if (depth == 0)
                        inEntity = string.Equals(pending, "entity", StringComparison.OrdinalIgnoreCase);
                    depth++;
                }
                else if (line == "}")
                    depth--;
                else if (line.StartsWith('"'))
                {
                    if (inEntity && depth == 1
                        && (line.Equals("\"classname\" \"func_areaportal\"", StringComparison.OrdinalIgnoreCase)
                            || line.Equals("\"classname\" \"func_areaportalwindow\"", StringComparison.OrdinalIgnoreCase)))
                        count++;
                }
                else if (line.Length > 0)
                    pending = line;
            }
            return count;
        }

        private static int Usage()
        {
            Console.Out.WriteLine("""
                viswright - automatic visibility optimisation for Source maps

                  viswright optimize <map.vmf> [-source <queued.vmf>] [-dryrun] [-threads N]
                                     [-areaportals] [-detail] [-nodraw] [-fades [-fadepixels N]]
                                     [-slim [-luapath <folder>]...] -vbsp <exe> -vvis <exe> -game <folder>
                      Optimise a copy of the map and hand it to the rest of the compile. The map
                      itself is never written. -source names the map as queued when <map.vmf> is an
                      earlier step's copy; -dryrun reports without handing anything on.
                      -areaportals puts an areaportal in the doorway of each door that has none,
                      within Garry's Mod's limit of 191, keeping those that hide the most.
                      -fades gives static props with no fade one where they shrink to N pixels
                      (default 6). -slim turns entities that do nothing an entity can do into
                      static props and func_detail, unless Lua looks their class up.
                      -detail makes small and complex world brushes func_detail; -nodraw makes
                      faces hidden against opaque brushes nodraw. Both are checked by compiling
                      with the compilers named, and undone if the map leaks, its collision
                      changes, or more becomes visible.

                  viswright audit <map.vmf>
                      Report what could be optimised, changing nothing.

                  viswright report <map.bsp>
                      Measure a compiled map's visibility: clusters and triangles each cluster
                      can see, and areaportal and lump limits. Run after VVIS.

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

        private static readonly HashSet<string> ValueFlags = new(StringComparer.OrdinalIgnoreCase)
            { "-source", "-threads", "-vbsp", "-vvis", "-game", "-fadepixels", "-luapath" };

        private static List<string> AllFlagValues(string[] args, string flag)
        {
            var values = new List<string>();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                    values.Add(args[i + 1]);
            return values;
        }

        private static bool TakesValue(string flag) => ValueFlags.Contains(flag);

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
