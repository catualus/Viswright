using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using SourceKit.Geometry;
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
                    "render" => RenderCommand(args),
                    "view" => ViewCommand(args),
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
                { "-source", "-threads", "-dryrun", "-detail", "-nodraw", "-areaportals", "-windowpictures", "-fades", "-fadepixels", "-slim", "-luapath", "-hints", "-hinttime", "-hintsearch", "-blocksize", "-review", "-cpu", "-mode", "-accept", "-vbsp", "-vvis", "-game" };
            RefuseUnknownFlags(args, known);

            string input = Positional(args, 1) ?? throw new ArgumentException("optimize needs the VMF to read.");
            TryGetFlagValue(args, "-source", out string? source);

            Toolchain? tools = null;
            if (TryGetFlagValue(args, "-vbsp", out string? vbsp) && TryGetFlagValue(args, "-vvis", out string? vvis)
                && TryGetFlagValue(args, "-game", out string? game))
            {
                // The block size the real compile chops the map on: hints and splits depend on it.
                string vbspFlags = TryGetFlagValue(args, "-blocksize", out string? block)
                    ? (int.TryParse(block, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) && b >= 0
                        ? $"-blocksize {b}" : throw new ArgumentException($"-blocksize needs a whole number of units, not '{block}'."))
                    : "";
                // A thread count given to Viswright holds for the compilers it runs, too.
                string threadFlag = TryGetFlagValue(args, "-threads", out string? threadCount) ? $"-threads {threadCount}" : "";
                tools = new Toolchain(vbsp!, vvis!, game!, $"{vbspFlags} {threadFlag}".Trim(), threadFlag);
            }

            var result = Optimizer.Run(new OptimizeOptions
            {
                Input = input,
                Source = source,
                DryRun = HasFlag(args, "-dryrun"),
                Detail = HasFlag(args, "-detail"),
                Nodraw = HasFlag(args, "-nodraw"),
                Areaportals = HasFlag(args, "-areaportals"),
                WindowPictures = HasFlag(args, "-windowpictures"),
                Fades = HasFlag(args, "-fades"),
                FadePixels = TryGetFlagValue(args, "-fadepixels", out string? pixels)
                    ? (double.TryParse(pixels, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) && p >= 1 && p <= 64 ? p
                        : throw new ArgumentException($"-fadepixels needs a number of pixels from 1 to 64, not '{pixels}'."))
                    : 6,
                Slim = HasFlag(args, "-slim"),
                LuaPaths = AllFlagValues(args, "-luapath"),
                Hints = HasFlag(args, "-hints"),
                Review = HasFlag(args, "-review"),
                Cpu = HasFlag(args, "-cpu"),
                Accept = HasFlag(args, "-accept"),
                Mode = TryGetFlagValue(args, "-mode", out string? mode)
                    ? mode!.ToLowerInvariant() switch
                    {
                        "auto" => RunMode.Auto,
                        "review" => RunMode.Review,
                        "pinned" => RunMode.Pinned,
                        _ => throw new ArgumentException($"-mode is auto, review or pinned, not '{mode}'."),
                    }
                    : null,
                HintMinutes = TryGetFlagValue(args, "-hinttime", out string? minutes)
                    ? (double.TryParse(minutes, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0 && m <= 240 ? m
                        : throw new ArgumentException($"-hinttime needs a number of minutes from 1 to 240, not '{minutes}'."))
                    : 15,
                HintSearch = TryGetFlagValue(args, "-hintsearch", out string? search)
                    ? search!.ToLowerInvariant() switch
                    {
                        "regions" => HintSearch.Regions,
                        "tiles" => HintSearch.Tiles,
                        _ => throw new ArgumentException($"-hintsearch is regions or tiles, not '{search}'."),
                    }
                    : HintSearch.Regions,
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
            var report = VisReport.Load(bsp);
            foreach (string line in report.Lines())
                Log.Check(line);

            if (report.HighestPortalNumber > VisReport.GmodMaxPortalNumber)
            {
                Log.Error($"an areaportal is numbered {report.HighestPortalNumber}; Garry's Mod refuses to load a map with one above {VisReport.GmodMaxPortalNumber}");
                return 1;
            }
            return 0;
        }

        /// <summary>
        /// Plans of a compiled map, one per floor: the slice at eye height with world and detail brushes,
        /// or with -from, what that spot's PVS lets it see. -vmf limits the floors to places a player can
        /// reach from a spawn point.
        /// </summary>
        private static int RenderCommand(string[] args)
        {
            RefuseUnknownFlags(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "-threads", "-vmf", "-out", "-from", "-slack", "-cpu" });
            string bsp = Positional(args, 1) ?? throw new ArgumentException("render needs the BSP to draw.");
            if (!File.Exists(bsp))
                throw new FileNotFoundException($"The BSP {bsp} does not exist.", bsp);
            string output = TryGetFlagValue(args, "-out", out string? o) ? o! : Path.ChangeExtension(bsp, null) + "_render";
            Directory.CreateDirectory(output);

            var space = BspSpace.Load(bsp);
            Func<int, bool>? reachable = null;
            string prt = Path.ChangeExtension(bsp, "prt");
            if (TryGetFlagValue(args, "-vmf", out string? vmf) && File.Exists(prt))
                reachable = SamplePoints.Reachable(SourceKit.Vmf.VmfDocument.Load(vmf!), SourceKit.Compile.PortalFile.Load(prt), space);
            var samples = SamplePoints.Find(space, reachable);
            if (samples.Points.Count == 0)
                throw new InvalidOperationException("The map has nowhere a player can stand.");

            if (TryGetFlagValue(args, "-from", out string? at))
            {
                var parts = at!.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                    || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                    throw new ArgumentException($"-from needs a position as \"x y z\" or x,y,z, not '{at}'.");
                var spot = new SourceKit.Geometry.Vec3(x, y, z);
                var view = Review.PlanView.Around(samples.Points.Where(p => Math.Abs(p.Z - z) <= 160).DefaultIfEmpty(spot), z, 512, 1600);
                var canvas = Review.PlanRenderer.Base(space, view, out var open);
                Review.PlanRenderer.Pvs(canvas, open, view, space, BspVisibilityData.Load(bsp), spot);
                string file = Path.Combine(output, string.Create(CultureInfo.InvariantCulture, $"pvs-{x:0}_{y:0}_{z:0}.png"));
                canvas.SavePng(file);
                Log.Out(file);
                return 0;
            }

            Slack? slack = null;
            if (HasFlag(args, "-slack"))
            {
                slack = Slack.Measure(bsp, samples, CompileMeasure.PerPoint(WorkCompile.Existing(Path.ChangeExtension(bsp, "vmf")), samples), gpu: !HasFlag(args, "-cpu"));
                var ok = Enumerable.Range(0, slack.Values.Length).Where(i => slack.Values[i] >= 0).ToList();
                Log.Check(string.Create(CultureInfo.InvariantCulture,
                    $"slack: a standing point really sees {ok.Average(i => slack.Seen[i]):N0} of the {ok.Average(i => slack.Pvs[i]):N0} triangles its PVS lets through; " +
                    $"{slack.Average:P1} slack, {ok.Average(i => slack.SplitSlack[i]):P1} within clusters, {ok.Average(i => slack.FlowSlack[i]):P1} through portals ({slack.How})"));
            }

            int level = 0;
            foreach (double z in Review.PlanRenderer.Levels(samples))
            {
                var view = Review.PlanView.Around(samples.Points.Where(p => Math.Abs(p.Z - z) <= 96), z, 512, 1600);
                var canvas = Review.PlanRenderer.Base(space, view, out var open);
                if (slack != null)
                    Review.PlanRenderer.Heat(canvas, open, view, samples, slack.Values, Review.Rgb.Heat, alpha: 0.7);
                string file = Path.Combine(output, $"level{++level}.png");
                canvas.SavePng(file);
                Log.Out(string.Create(CultureInfo.InvariantCulture, $"{file}: eye height {z:0}, {view.Scale:0} units a pixel"));
            }
            return 0;
        }

        /// <summary>
        /// One ray-traced 3D picture of a compiled map: looking at -target from -distance away, turned -yaw
        /// degrees round it and down -pitch degrees, with everything above -cut left out so rooms show.
        /// With -vmf, doors and other brush entities are drawn, and the map's hints, areaportals and
        /// occluders are outlined.
        /// </summary>
        private static int ViewCommand(string[] args)
        {
            RefuseUnknownFlags(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "-threads", "-vmf", "-out", "-target", "-yaw", "-pitch", "-distance", "-cut", "-game" });
            string bsp = Positional(args, 1) ?? throw new ArgumentException("view needs the BSP to draw.");
            if (!File.Exists(bsp))
                throw new FileNotFoundException($"The BSP {bsp} does not exist.", bsp);
            static double Number(string[] args, string flag, double fallback) =>
                TryGetFlagValue(args, flag, out string? v) ? double.Parse(v!, NumberStyles.Float, CultureInfo.InvariantCulture) : fallback;
            static Vec3 Point(string text)
            {
                var p = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                return p.Length == 3 ? new Vec3(p[0], p[1], p[2]) : throw new ArgumentException($"A position is x,y,z, not '{text}'.");
            }

            var space = BspSpace.Load(bsp);
            var target = TryGetFlagValue(args, "-target", out string? at) ? Point(at!) : (space.Mins + space.Maxs) / 2;
            double? cut = TryGetFlagValue(args, "-cut", out string? c) ? (c == "none" ? (double?)null : double.Parse(c!, NumberStyles.Float, CultureInfo.InvariantCulture)) : target.Z + 256;
            VmfDocument? map = TryGetFlagValue(args, "-vmf", out string? vmf) ? VmfDocument.Load(vmf!) : null;
            var opacity = TryGetFlagValue(args, "-game", out string? game) ? GameContent.Open(game!).Opacity : null;

            var scene = Review.Scene.FromMap(bsp, map, opacity, cut);
            if (map != null)
            {
                foreach (var solid in map.World.Children("solid"))
                    if (VmfBrush.Build(solid) is { } brush)
                        foreach (var f in brush.Faces.Where(f => f.Material.Equals("TOOLS/TOOLSHINT", StringComparison.OrdinalIgnoreCase) && f.Winding != null))
                            scene.AddPolygon(f.Winding!.Points, new Review.Rgb(235, 40, 210), 0.12);
                foreach (var (cls, colour, alpha) in new[] { ("func_areaportal", new Review.Rgb(255, 140, 0), 0.4), ("func_areaportalwindow", new Review.Rgb(255, 140, 0), 0.4), ("func_occluder", new Review.Rgb(160, 60, 220), 0.3) })
                    foreach (var b in map.Entities.Where(e => e.Classname == cls).SelectMany(e => e.Children("solid")).Select(VmfBrush.Build).OfType<VmfBrush>())
                        scene.AddBox(b.Mins, b.Maxs, colour, alpha);
            }
            var (eye, look) = Review.Scene.Orbit(target, Number(args, "-distance", 1200), Number(args, "-yaw", 35), Number(args, "-pitch", 50));
            string output = TryGetFlagValue(args, "-out", out string? o) ? o! : Path.ChangeExtension(bsp, null) + "_view.png";
            scene.Render(eye, look, 1400, 900).SavePng(output);
            Log.Out(output);
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
                                     [-areaportals [-windowpictures]] [-detail] [-nodraw] [-fades [-fadepixels N]]
                                     [-slim [-luapath <folder>]...] [-hints [-hinttime M] [-hintsearch regions|tiles]] [-blocksize N]
                                     [-mode auto|review|pinned] [-accept]
                                     -vbsp <exe> -vvis <exe> -game <folder>
                      Optimise a copy of the map and hand it to the rest of the compile. The map
                      itself is never written. -source names the map as queued when <map.vmf> is an
                      earlier step's copy; -dryrun reports without handing anything on.
                      -areaportals puts an areaportal in the doorway of each door that has none,
                      within Garry's Mod's limit of 191, keeping those that hide the most.
                      -fades gives static props with no fade one where they shrink to N pixels
                      (default 6). -slim turns entities that do nothing an entity can do into
                      static props and func_detail, unless Lua looks their class up; when Lua
                      uses MapCreationID, only those that no map entity comes after, so map
                      entity IDs stay stable.
                      -hints puts caps over open ground at its roof heights, cuts carrying walls
                      across open space, and boxes round buildings under a cap, accepting one at a
                      time by what the whole map sees, searching for at most M minutes (15).
                      -hintsearch tiles uses the earlier search instead: a hint over each 1,024-unit
                      square at a few heights, then the ends of walls, each judged near itself.
                      -threads N also limits the compilers Viswright runs.
                      With no -mode, a map's first run is a review: everything is searched and
                      measured, a review and <map>.viswright.json are written beside it, and the
                      compile uses the map as it is. Once every item is accepted or denied - in
                      that file, or by editing the review VMF in Hammer, or with -accept, which
                      accepts all still waiting - runs are pinned: the accepted items are applied
                      with no search, checked by one compile. -mode auto searches and applies as
                      before reviews existed.
                      -blocksize is the block size the real VBSP compile uses. -review writes a
                      review beside the copy: a Hammer VMF of every item, plans and a page,
                      with PVS slack measured by casting rays on the GPU (-cpu: on the CPU).
                      -detail makes small and complex world brushes func_detail; -nodraw makes
                      faces hidden against opaque brushes nodraw. Both are checked by compiling
                      with the compilers named, and undone if the map leaks, its collision
                      changes, or more becomes visible.

                  viswright audit <map.vmf>
                      Report what could be optimised, changing nothing.

                  viswright render <map.bsp> [-vmf <map.vmf>] [-from x,y,z] [-slack [-cpu]] [-out <folder>]
                      Draw a plan of each floor of a compiled map, or with -from, what that spot's
                      PVS lets it see. -slack colours each place by how much of what its PVS lets
                      through cannot be seen from there, casting rays on the GPU (-cpu: on the CPU).

                  viswright view <map.bsp> [-vmf <map.vmf>] [-game <folder>] [-target x,y,z] [-yaw D] [-pitch D]
                                 [-distance N] [-cut z|none] [-out <file.png>]
                      Draw the compiled map in 3D, ray-traced, cut away above -cut so rooms show;
                      with -vmf, doors are drawn and hints, areaportals and occluders outlined.

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
            { "-source", "-threads", "-vbsp", "-vvis", "-game", "-fadepixels", "-luapath", "-hinttime", "-hintsearch", "-blocksize", "-vmf", "-out", "-from", "-mode" };

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
