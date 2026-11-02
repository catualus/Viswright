using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;
using Viswright.Review;

namespace Viswright
{
    /// <summary>How a run goes.</summary>
    public enum RunMode
    {
        /// <summary>Search, measure, and apply whatever proves itself.</summary>
        Auto,

        /// <summary>Search and measure, write the review and the decisions file, and compile the map as it is.</summary>
        Review,

        /// <summary>Apply the accepted items and nothing else, checked by one compile.</summary>
        Pinned,
    }

    public static partial class Optimizer
    {
        /// <summary>
        /// A pinned run: the accepted items, put back exactly as they were decided, with no search. One VBSP
        /// compile checks them - for a leak, an areaportal that divides no areas, a model VBSP refuses as a
        /// static prop, and changed collision - and an item that no longer fits the map is left out and
        /// reported, never forced. The same map and the same decisions give the same copy every time.
        /// </summary>
        private static (int Detailed, int Nodrawn, int Portals) RunPinned(OptimizeOptions options, VmfDocument working, string copy,
            ChangeGuard guard, DecisionFile decisions)
        {
            var tools = options.Tools ?? throw new ArgumentException("A pinned run needs -vbsp, -vvis and -game to check the items.");
            tools.Validate();
            string name = Path.GetFileNameWithoutExtension(copy);
            string work = Path.Combine(Path.GetDirectoryName(copy)!, "work");

            // The map as it is, for collision to be compared with: cached from the review when the map is unchanged.
            var baseline = Baseline(working, copy, Path.Combine(work, "baseline"), name, tools);
            if (baseline == null)
                return (0, 0, 0);
            var collision = BspSpace.Load(baseline.Bsp).CollisionShapes(CollisionMask);

            var accepted = decisions.Items.Where(i => i.Decision == Decision.Accept && !i.Stale).ToList();
            bool Accepted(string id) => decisions.Accepted(id);
            var left = new List<(string Id, string Why)>();
            Log.Info($"pinned: {accepted.Count} accepted item(s) in {decisions.Path}; nothing is searched");

            // Areaportals: each doorway as decided, in the door its id names.
            AreaportalPass? portalPass = null;
            if (options.Areaportals)
            {
                portalPass = new AreaportalPass(working);
                var doors = working.Entities.Where(e => Audit.DoorClasses.Contains(e.Classname ?? "", StringComparer.OrdinalIgnoreCase)).ToList();
                int existing = working.Entities.Count(e => Audit.PortalClasses.Contains(e.Classname ?? "", StringComparer.OrdinalIgnoreCase));
                var doorways = new List<Doorway>();
                var windows = new List<Window>();
                foreach (var item in accepted.Where(i => i.Pass == "areaportals"))
                {
                    if (item.Id.StartsWith("window:", StringComparison.Ordinal))
                    {
                        if (!item.Data.ContainsKey("x0"))
                            left.Add((item.Id, "it has no opening"));
                        else if (existing + doorways.Count + windows.Count >= Audit.PortalBudget)
                            left.Add((item.Id, $"Garry's Mod's {Audit.PortalBudget} areaportals are used up"));
                        else
                            windows.Add(DecisionFile.Window(item.Data));
                        continue;
                    }
                    var door = FindDoor(doors, item);
                    if (door == null || item.Data.Count == 0)
                        left.Add((item.Id, "its door is no longer in the map"));
                    else if (existing + doorways.Count + windows.Count >= Audit.PortalBudget)
                        left.Add((item.Id, $"Garry's Mod's {Audit.PortalBudget} areaportals are used up"));
                    else
                        doorways.Add(DecisionFile.Doorway(door, item.Data));
                }
                portalPass.Apply(doorways);
                portalPass.ApplyWindows(windows);
            }

            // Detail: the world brushes with the decided bounds.
            DetailPass? detailPass = null;
            if (options.Detail)
            {
                detailPass = new DetailPass(working);
                var solids = working.World.Children("solid").Select(s => (Solid: s, Brush: VmfBrush.Build(s))).Where(s => s.Brush != null).ToList();
                var picked = new List<(VmfNode, VmfBrush)>();
                foreach (var item in accepted.Where(i => i.Pass == "detail"))
                {
                    var match = solids.FirstOrDefault(s => Close(s.Brush!.Mins, item.Mins) && Close(s.Brush!.Maxs, item.Maxs));
                    if (match.Solid == null || picked.Any(p => ReferenceEquals(p.Item1, match.Solid)))
                        left.Add((item.Id, "no world brush has its bounds any more"));
                    else
                        picked.Add((match.Solid, match.Brush!));
                }
                detailPass.Apply(picked);
            }

            // Hints: a slab over each decided square at the decided height.
            HintPass? hintPass = null;
            var wallSolids = new List<VmfNode>();
            if (options.Hints)
            {
                hintPass = new HintPass(working);
                foreach (var item in accepted.Where(i => i.Pass == "hints"))
                {
                    var d = item.Data;
                    if (d.ContainsKey("wallAxis"))
                    {
                        var solid = WallHints.Brush(WallHint.FromData(d), working.MaxId());
                        working.World.Add(solid);
                        wallSolids.Add(solid);
                        continue;
                    }
                    if (!d.ContainsKey("x") || !d.ContainsKey("height"))
                    {
                        left.Add((item.Id, "it has no square and height"));
                        continue;
                    }
                    hintPass.Place(new HintSquare((int)d["x"], (int)d["y"], d["floor"], d["ceiling"], []), d["height"]);
                }
            }

            // The passes that decide from the map alone run as they always do; what was not accepted goes back.
            var content = GameContent.Open(tools.GameFolder);
            NodrawPass? nodrawPass = null;
            if (options.Nodraw)
            {
                nodrawPass = new NodrawPass();
                nodrawPass.Apply(working, content.Opacity);
                nodrawPass.RevertWhere(c => !Accepted("nodraw:" + ReviewLog.Anchor(c)));
            }
            PropFadePass? fadePass = null;
            if (options.Fades)
            {
                fadePass = new PropFadePass(options.FadePixels);
                fadePass.Apply(working, content, _ => false);
                fadePass.RevertWhere(p => !Accepted("fade:" + ReviewLog.Anchor(Centre(p))));
            }
            SlimmingPass? slimPass = null;
            if (options.Slim)
            {
                slimPass = new SlimmingPass();
                slimPass.Apply(working, content, LuaGuard.Scan(LuaGuard.GameFolders(tools.GameFolder).Concat(options.LuaPaths)));
                slimPass.RevertWhere(e => !Accepted("slim:" + ReviewLog.Anchor(Centre(e))));
            }

            // One compile checks the lot; whatever it shows does not fit is taken out and the map compiled again.
            WorkCompile? compile = null;
            for (int round = 1; round <= 12; round++)
            {
                compile = WorkCompile.Prepare(working, copy, Path.Combine(work, "pinned"), name);
                var run = compile.RunVbsp(tools);
                if (compile.Leaked)
                {
                    var leak = compile.LeakPath!;
                    int dropped = 0;
                    if (portalPass != null)
                    {
                        var near = portalPass.Added.Where(a => leak.Points.Any(p => Distance(p, a.Doorway) < 64)).Select(a => a.Portal).ToHashSet();
                        var nearWindows = portalPass.AddedWindows.Where(a => leak.Points.Any(p => Distance(p, a.Window.Mins, a.Window.Maxs) < 64)).Select(a => a.Window).ToHashSet();
                        dropped += near.Count + nearWindows.Count;
                        portalPass.KeepOnly(portalPass.Added.Select(a => a.Portal).Where(p => !near.Contains(p)).ToHashSet(),
                            "a leak ran through its doorway: the portal no longer seals it");
                        portalPass.KeepWindows(portalPass.AddedWindows.Select(a => a.Window).Where(w => !nearWindows.Contains(w)).ToHashSet());
                    }
                    if (detailPass != null)
                        dropped += detailPass.RevertNear(leak, 64);
                    if (dropped == 0)
                    {
                        UndoAll();
                        Log.Warn("pinned: the map leaked somewhere no accepted item is near; compiling it as it is");
                        return (0, 0, 0);
                    }
                    Log.Info($"pinned: {dropped} item(s) on a leak path left out");
                    continue;
                }
                if (!run.Succeeded && portalPass?.RefusedBy(run.Output) is var (refusedDoors, refusedWindows) && refusedDoors.Count + refusedWindows.Count > 0)
                {
                    int removed = portalPass.DropWithDependents(refusedDoors, refusedWindows, "VBSP no longer puts it, or a window it needs, between two areas");
                    foreach (var w in refusedWindows)
                        left.Add(("window:" + ReviewLog.Anchor(w.Mins), "VBSP no longer puts it between two areas"));
                    Log.Info($"pinned: VBSP refused {refusedDoors.Count + refusedWindows.Count} portal(s); {removed} left out with those needing them");
                    continue;
                }
                if (!run.Succeeded)
                {
                    UndoAll();
                    Log.Warn($"pinned: VBSP failed with the accepted items in ({LastLines(run)}); compiling the map as it is");
                    return (0, 0, 0);
                }
                int retry = 0;
                if (portalPass is { Added.Count: > 0 })
                {
                    var placed = BspVisibilityData.Load(compile.Bsp);
                    var invalid = AreaportalSelection.Invalid(portalPass, placed);
                    var invalidWindows = AreaportalSelection.InvalidWindows(portalPass, placed);
                    if (invalid.Count > 0)
                    {
                        portalPass.KeepOnly(portalPass.Added.Select(a => a.Portal).Except(invalid).ToHashSet(),
                            "VBSP no longer puts it between two areas");
                        retry += invalid.Count;
                    }
                    if (invalidWindows.Count > 0)
                    {
                        portalPass.KeepWindows(portalPass.AddedWindows.Select(a => a.Window).Except(invalidWindows).ToHashSet());
                        foreach (var w in invalidWindows)
                            left.Add(("window:" + ReviewLog.Anchor(w.Mins), "VBSP no longer puts it between two areas"));
                        retry += invalidWindows.Count;
                    }
                }
                if (slimPass is { Converted.Count: > 0 })
                {
                    var refused = RefusedModels(run);
                    if (refused.Count > 0)
                        retry += slimPass.RevertModels(refused);
                }
                if (retry == 0)
                    break;
            }

            if (compile == null || BspSpace.CollisionDifferences(BspSpace.Load(compile.Bsp).CollisionShapes(CollisionMask)
                    .Where(s => portalPass == null || !portalPass.IsStandIn(s)).ToList(), collision) is > 0 and var changed && changed > 0)
            {
                UndoAll();
                Log.Warn("pinned: collision changed with the accepted items in; compiling the map as it is");
                return (0, 0, 0);
            }

            foreach (var (_, door, why) in portalPass?.Dropped ?? [])
                left.Add(("areaportal at " + ReviewLog.Anchor(ReviewLog.Bounds(door).Mins), why));
            portalPass?.Declare(guard);
            detailPass?.Declare(guard);
            hintPass?.Declare(guard);
            nodrawPass?.Declare(guard);
            fadePass?.Declare(guard);
            slimPass?.Declare(guard);

            var wallPaths = wallSolids.Select(s => $"world#{working.World.Id}/solid#{s.Id}").ToHashSet();
            guard.Allow("hints", change => change.Kind == VmfChangeKind.Added && wallPaths.Contains(change.Path));
            int portals = (portalPass?.Added.Count ?? 0) + (portalPass?.AddedWindows.Count ?? 0), detailed = detailPass?.Moved.Count ?? 0, hints = (hintPass?.Placed.Count ?? 0) + wallSolids.Count;
            int nodrawn = nodrawPass?.Changed.Count ?? 0, faded = fadePass?.Changed.Count ?? 0, slimmed = slimPass?.Converted.Count ?? 0;
            Log.Check($"pinned: applied {portals} areaportal(s), {detailed} detail brush(es), {hints} hint(s), {nodrawn} nodraw face(s), " +
                $"{faded} fade(s), {slimmed} entit(ies) made static");
            foreach (var (id, why) in left.Take(12))
                Log.Warn($"pinned: {id} left out: {why}");
            if (left.Count > 12)
                Log.Warn($"pinned: {left.Count - 12} more left out");
            int stale = decisions.Items.Count(i => i.Stale && i.Decision == Decision.Accept);
            if (stale > 0)
                Log.Info($"pinned: {stale} accepted item(s) were not found by the last review, the map having changed; run a review to see them again");

            WriteAdopt(working, Path.Combine(Path.GetDirectoryName(copy)!, name + "_adopt.vmf"),
                (portalPass?.Added.SelectMany(a => a.NamedDoor ? new[] { a.Portal, a.Door } : [a.Portal]) ?? [])
                    .Concat(portalPass?.AddedWindows.SelectMany(a => a.StandIn == null ? [a.Portal] : new[] { a.Portal, a.StandIn }) ?? [])
                    .Concat(detailPass?.EntityNode is { } detailEntity ? [detailEntity] : [])
                    .Concat(hintPass?.Placed.Values.Select(p => p.Solid) ?? [])
                    .Concat(wallSolids)
                    .Concat(fadePass?.Changed.Select(c => c.Prop) ?? [])
                    .Concat(slimPass?.Converted.Select(c => c.Entity) ?? []));
            return (detailed, nodrawn, portals);

            void UndoAll()
            {
                portalPass?.RevertAll();
                detailPass?.RevertAll();
                hintPass?.RevertAll();
                foreach (var s in wallSolids) working.World.Remove(s);
                wallSolids.Clear();
                nodrawPass?.RevertAll();
                fadePass?.RevertAll();
                slimPass?.RevertAll();
            }
        }

        /// <summary>The door an areaportal item names: by its own name, or for a door that had none, by where it stands.</summary>
        private static VmfNode? FindDoor(List<VmfNode> doors, DecidedItem item)
        {
            string key = item.Id["areaportal:".Length..].Split('~')[0];
            var named = key.StartsWith("door@", StringComparison.Ordinal)
                ? doors.Where(d => ReviewLog.Anchor(ReviewLog.Bounds(d).Mins) == key["door@".Length..]).ToList()
                : doors.Where(d => string.Equals(d.Get("targetname"), key, StringComparison.OrdinalIgnoreCase)).ToList();
            // Doors can share a name (double doors); the one in the doorway is the one meant.
            var lo = item.Mins - new Vec3(16, 16, 16);
            var hi = item.Maxs + new Vec3(16, 16, 16);
            return named.FirstOrDefault(d => ReviewLog.Bounds(d) is var (dlo, dhi) && dlo.X <= hi.X && dhi.X >= lo.X && dlo.Y <= hi.Y && dhi.Y >= lo.Y && dlo.Z <= hi.Z && dhi.Z >= lo.Z)
                ?? named.FirstOrDefault();
        }

        private static bool Close(Vec3 a, Vec3 b) => Math.Abs(a.X - b.X) < 0.75 && Math.Abs(a.Y - b.Y) < 0.75 && Math.Abs(a.Z - b.Z) < 0.75;

        private static Vec3 Centre(VmfNode entity)
        {
            var (lo, hi) = ReviewLog.Bounds(entity);
            return (lo + hi) / 2;
        }

        /// <summary>
        /// The map with the applied changes in a "Viswright accepted" visgroup, for the mapper to open in
        /// Hammer and save over their own map if they want the changes to be theirs. Viswright never does that.
        /// </summary>
        private static void WriteAdopt(VmfDocument working, string path, IEnumerable<VmfNode> changed)
        {
            var doc = VmfDocument.Parse(working.ToString(), working.HasByteOrderMark);
            var byId = changed.Select(n => n.Id).OfType<long>().ToHashSet();
            if (byId.Count == 0)
                return;
            var visgroups = doc.Root.Child("visgroups");
            if (visgroups == null)
            {
                visgroups = new VmfNode("visgroups");
                doc.Root.InsertBefore(doc.World, visgroups);
            }
            int groupId = doc.Root.Descendants().Where(n => n.Name.Equals("visgroup", StringComparison.OrdinalIgnoreCase))
                .Select(n => int.TryParse(n.Get("visgroupid"), out int v) ? v : 0).DefaultIfEmpty(0).Max() + 1;
            var group = new VmfNode("visgroup");
            group.Set("name", "Viswright accepted");
            group.Set("visgroupid", groupId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            group.Set("color", "40 170 90");
            visgroups.Add(group);
            foreach (var node in doc.World.Children("solid").Concat(doc.Entities))
                if (node.Id is { } id && byId.Contains(id))
                    ReviewVmf.Join(node, group, shown: true);
            doc.Save(path);
            Log.Info($"adopt: {path} is your map with the accepted changes in a \"Viswright accepted\" visgroup; save it over your map in Hammer to make them yours");
        }
    }
}
