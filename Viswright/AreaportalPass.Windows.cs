using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>
    /// An opening in a wall a portal can go in, framed by world brush on all four sides: a window, a
    /// transom, or a passage with no door in it, which reaches down to the floor.
    /// </summary>
    public sealed record Window(int NormalAxis, Vec3 Mins, Vec3 Maxs, bool Passage)
    {
        public Vec3 Centre => (Mins + Maxs) / 2;
    }

    public sealed partial class AreaportalPass
    {
        public const string WindowNamePrefix = "vw_apw_";

        /// <summary>
        /// Where a window's portal starts to fade shut and where it is shut, in units from the player:
        /// Valve's own, from the one func_areaportalwindow in their TF2 sources.
        /// </summary>
        public const double FadeStart = 1000, FadeEnd = 1200;

        /// <summary>What a shut window shows: black, as Valve's do.</summary>
        public const string StandInMaterial = "TOOLS/TOOLSBLACK";

        /// <summary>The most openings a doorway's rooms may need closing besides the door before the doorway is given up.</summary>
        private const int MaxOpenings = 6;

        private readonly List<(VmfNode Portal, Window Window, VmfNode? StandIn)> addedWindows = [];

        /// <summary>The window portals placed, each with the brush drawn in its place while it is shut (none for a passage).</summary>
        public IReadOnlyList<(VmfNode Portal, Window Window, VmfNode? StandIn)> AddedWindows => addedWindows;

        /// <summary>
        /// For each doorway that only divides its rooms once the windows or gaps they meet through are
        /// closed too: those openings, and the other doorways whose portals close the rest of the way round.
        /// </summary>
        public Dictionary<Doorway, (List<Window> Windows, List<Doorway> Doorways)> Rescued { get; } = new();

        /// <summary>The windows the kept doorways need, after <see cref="SealedDoorways"/>.</summary>
        public List<Window> Windows { get; } = [];

        /// <summary>
        /// The opening in a wall around <paramref name="at"/>, a point in it, for a wall facing along
        /// <paramref name="normal"/>: walked out to world brush along the wall and up and down, then
        /// checked to be a clean rectangle within the wall's thickness, as a doorway is.
        /// </summary>
        public static Window? Opening(BspSpace compiled, Vec3 at, int normal, out string reason)
        {
            reason = "";
            int width = 1 - normal;
            Vec3 Along(int axis, double d) => axis switch
            {
                0 => at with { X = at.X + d },
                1 => at with { Y = at.Y + d },
                _ => at with { Z = at.Z + d },
            };
            double? Reach(int axis, int sign)
            {
                for (double d = 1; d <= MaxReach; d += 1)
                    if (compiled.IsStructuralSolid(Along(axis, sign * d)))
                        return Math.Round(at[axis] + sign * (d - 0.5));
                return null;
            }
            if (compiled.IsStructuralSolid(at))
            {
                reason = "inside a wall";
                return null;
            }
            double? w0 = Reach(width, -1), w1 = Reach(width, +1), z0 = Reach(2, -1), z1 = Reach(2, +1);
            if (w0 == null || w1 == null || z0 == null || z1 == null)
            {
                reason = "no world brush frames it";
                return null;
            }

            Vec3 At(double a, double w, double z) => normal == 0 ? new Vec3(a, w, z) : new Vec3(w, a, z);
            double ca = at[normal], midZ = (z0.Value + z1.Value) / 2, midW = (w0.Value + w1.Value) / 2;
            // The wall's faces, on the grid: probed half a unit off it, since a point on a face can fall
            // in the leaf on either side.
            (double Lo, double Hi)? Thickness(double w, double z)
            {
                double c = Math.Floor(ca) + 0.5;
                if (!compiled.IsStructuralSolid(At(c, w, z)))
                    return null;
                double lo = c, hi = c;
                while (hi - c < 64 && compiled.IsStructuralSolid(At(hi + 1, w, z))) hi += 1;
                while (c - lo < 64 && compiled.IsStructuralSolid(At(lo - 1, w, z))) lo -= 1;
                return (lo - 0.5, hi + 0.5);
            }
            var sides = new[] { Thickness(w0.Value - 1, midZ), Thickness(w1.Value + 1, midZ), Thickness(midW, z1.Value + 1), Thickness(midW, z0.Value - 1) };
            if (sides.Any(s => s == null))
            {
                reason = "it is not in a wall";
                return null;
            }
            double aLo = sides.Max(s => s!.Value.Lo), aHi = sides.Min(s => s!.Value.Hi);
            if (aHi - aLo < 1)
            {
                reason = "its sides are not in one wall";
                return null;
            }

            double am = (aLo + aHi) / 2;
            for (double z = z0.Value + 2; z < z1.Value - 1; z += 4)
                if (!compiled.IsStructuralSolid(At(am, w0.Value - 1, z)) || !compiled.IsStructuralSolid(At(am, w1.Value + 1, z)))
                {
                    reason = "it is not a rectangle framed by world brushes";
                    return null;
                }
            for (double w = w0.Value + 2; w < w1.Value - 1; w += 4)
            {
                if (!compiled.IsStructuralSolid(At(am, w, z1.Value + 1)) || !compiled.IsStructuralSolid(At(am, w, z0.Value - 1)))
                {
                    reason = "it is not a rectangle framed by world brushes";
                    return null;
                }
                for (double z = z0.Value + 2; z < z1.Value - 1; z += 4)
                    if (compiled.IsStructuralSolid(At(am, w, z)))
                    {
                        reason = "it is not a rectangle framed by world brushes";
                        return null;
                    }
            }

            // VBSP gives an areaportal the contents of water it touches, which would make water of the
            // whole opening.
            const int Liquid = 0x20 | 0x10;
            for (double w = w0.Value + 1; w < w1.Value; w += 8)
                for (double z = z0.Value + 1; z < z1.Value; z += 8)
                    if (compiled.IsSolid(At(am, w, z), Liquid))
                    {
                        reason = "water runs through it";
                        return null;
                    }

            // A passage: the floor carries on through it on both sides, so a player can walk through.
            bool Floor(double a) => compiled.IsStructuralSolid(At(a, midW, z0.Value - 4));
            bool passage = z1.Value - z0.Value >= 64 && Floor(aLo - 16) && Floor(aHi + 16);

            // One unit into the frame on every side, as a doorway's portal is, and between the wall's faces.
            var lo = At(aLo, w0.Value - 1, z0.Value - 1);
            var hi = At(aHi, w1.Value + 1, z1.Value + 1);
            return new Window(normal, Vec3.Min(lo, hi), Vec3.Max(lo, hi), passage);
        }

        /// <summary>
        /// Tries to make <paramref name="d"/> divide its rooms by closing what joins them: the way round
        /// is followed to the first opening in a wall, which is closed - by the portal of the doorway it
        /// is, or a window portal - and the walk repeats until the rooms divide. Returns null, with the
        /// openings closed in <paramref name="closed"/> left as they were, when they cannot be divided.
        /// </summary>
        private (List<Window> Windows, List<Doorway> Doorways)? Rescue(Doorway d, int from, int to, HashSet<int> closed,
            SourceKit.Compile.PortalFile portals, BspSpace compiled, IReadOnlyList<(Doorway Doorway, Vec3 Mins, Vec3 Maxs)> doorwayBoxes,
            Func<Doorway, List<int>> doorwayCuts, Func<Window, List<int>> windowCuts)
        {
            var trial = new HashSet<int>(closed);
            trial.UnionWith(doorwayCuts(d));
            var windows = new List<Window>();
            var doorways = new List<Doorway>();
            for (int step = 0; step <= MaxOpenings; step++)
            {
                var path = OpenPath(portals, trial, from, to);
                if (path == null)
                {
                    closed.UnionWith(trial);
                    return (windows, doorways);
                }
                if (step == MaxOpenings)
                    return null;

                bool closedOne = false;
                foreach (int i in path)
                {
                    var w = portals.Portals[i].Winding;
                    var p = w.Points;
                    var normal = (p[1] - p[0]).Cross(p[2] - p[0]);
                    if (Math.Abs(normal.Z) > 0.3 * normal.Length)
                        continue;
                    var opening = Opening(compiled, w.Centre, Math.Abs(normal.X) > Math.Abs(normal.Y) ? 0 : 1, out _);
                    if (opening == null || Math.Max(opening.Maxs[1 - opening.NormalAxis] - opening.Mins[1 - opening.NormalAxis], opening.Maxs.Z - opening.Mins.Z) > 2 * MaxReach)
                        continue;

                    // Another doorway: its own portal closes it, closing with its door.
                    var doorway = doorwayBoxes.FirstOrDefault(b => b.Doorway != d && Overlaps(b.Mins, b.Maxs, opening.Mins, opening.Maxs)).Doorway;
                    if (doorway != null)
                    {
                        doorways.Add(doorway);
                        int before = trial.Count;
                        trial.UnionWith(doorwayCuts(doorway));
                        closedOne = trial.Count > before;
                        break;
                    }
                    if (doorwayBoxes.Any(b => b.Doorway == d && Overlaps(b.Mins, b.Maxs, opening.Mins, opening.Maxs)))
                        continue;

                    // A window another doorway needs already is the same window.
                    var window = Windows.FirstOrDefault(o => Overlaps(o.Mins, o.Maxs, opening.Mins, opening.Maxs)) ?? opening;
                    windows.Add(window);
                    int count = trial.Count;
                    trial.UnionWith(windowCuts(window));
                    closedOne = trial.Count > count;
                    break;
                }
                if (!closedOne)
                    return null;
            }
            return null;
        }

        /// <summary>The openings between leaves on the shortest way from one cluster to another, or null when there is none.</summary>
        private static List<int>? OpenPath(SourceKit.Compile.PortalFile portals, HashSet<int> closed, int from, int to)
        {
            var next = new List<(int Portal, int Cluster)>[portals.ClusterCount];
            for (int i = 0; i < next.Length; i++) next[i] = [];
            for (int i = 0; i < portals.Portals.Count; i++)
                if (!closed.Contains(i))
                {
                    var p = portals.Portals[i];
                    next[p.ClusterA].Add((i, p.ClusterB));
                    next[p.ClusterB].Add((i, p.ClusterA));
                }
            var path = Path(next, from, to);
            return path.Count == 0 && from != to ? null : path;
        }

        /// <summary>
        /// Adds a portal in each window. A window gets a func_areaportalwindow that fades shut between
        /// <see cref="FadeStart"/> and <see cref="FadeEnd"/>, with a black stand-in fading in over the opening
        /// as it shuts, as Valve's do; a passage gets a func_areaportal that is always open, since a
        /// player walks through it.
        /// </summary>
        public void ApplyWindows(IEnumerable<Window> windows)
        {
            long id = map.MaxId();
            foreach (var w in windows)
            {
                var portal = new VmfNode("entity");
                map.Root.Add(portal);
                long portalId = ++id;
                string name = WindowNamePrefix + Invariant(portalId);
                portal.Set("id", Invariant(portalId));
                VmfNode? standIn = null;
                if (w.Passage)
                {
                    portal.Set("classname", "func_areaportal");
                    portal.Set("targetname", name);
                    portal.Set("StartOpen", "1");
                }
                else
                {
                    standIn = new VmfNode("entity");
                    map.Root.Add(standIn);
                    standIn.Set("id", Invariant(++id));
                    standIn.Set("classname", "func_brush");
                    standIn.Set("targetname", name + "_shut");
                    standIn.Set("Solidity", "1");
                    standIn.Set("disableshadows", "1");
                    standIn.Set("vrad_brush_cast_shadows", "0");
                    standIn.Set("rendermode", "0");
                    standIn.Set("renderamt", "255");
                    // The window entity draws this brush itself, and only while it is in a leaf being
                    // drawn: so it stands a unit proud of the wall on both sides, in the leaves either
                    // side of the portal, black on its two broad faces.
                    var (lo, hi) = StandInBox(w);
                    id = AddBox(standIn, lo, hi, id, axis => axis == w.NormalAxis ? StandInMaterial : "TOOLS/TOOLSNODRAW");

                    portal.Set("classname", "func_areaportalwindow");
                    portal.Set("targetname", name);
                    portal.Set("target", name + "_shut");
                    portal.Set("FadeStartDist", Invariant(FadeStart));
                    portal.Set("FadeDist", Invariant(FadeEnd));
                    portal.Set("TranslucencyLimit", "0");
                }
                portal.Set("PortalVersion", "1");
                id = AddBox(portal, w.Mins, w.Maxs, id, _ => "TOOLS/TOOLSAREAPORTAL");
                addedWindows.Add((portal, w, standIn));
            }
        }

        /// <summary>Removes the window portals not in <paramref name="keep"/>, with their stand-ins.</summary>
        public void KeepWindows(IReadOnlySet<Window> keep)
        {
            foreach (var entry in addedWindows.Where(a => !keep.Contains(a.Window)).ToList())
            {
                map.Root.Remove(entry.Portal);
                if (entry.StandIn != null)
                    map.Root.Remove(entry.StandIn);
                addedWindows.Remove(entry);
            }
        }

        /// <summary>
        /// Takes out the door portals <paramref name="doors"/> and window portals <paramref name="windows"/>,
        /// and with them what no longer works without them: a door whose rooms needed a window or doorway
        /// taken out, and a window no door kept needs. Returns how many portals went.
        /// </summary>
        public int DropWithDependents(IEnumerable<VmfNode> doors, IEnumerable<Window> windows, string why)
        {
            var goneDoors = doors.ToHashSet();
            var goneWindows = windows.ToHashSet();
            bool more = true;
            while (more)
            {
                var goneDoorways = added.Where(a => goneDoors.Contains(a.Portal)).Select(a => a.Doorway).ToHashSet();
                var next = added.Where(a => !goneDoors.Contains(a.Portal) && Rescued.TryGetValue(a.Doorway, out var r)
                    && (r.Windows.Any(goneWindows.Contains) || r.Doorways.Any(goneDoorways.Contains))).Select(a => a.Portal).ToList();
                goneDoors.UnionWith(next);
                var keptDoorways = added.Where(a => !goneDoors.Contains(a.Portal)).Select(a => a.Doorway);
                var needed = WindowsNeededBy(keptDoorways);
                var orphans = addedWindows.Select(a => a.Window).Where(w => !needed.Contains(w) && !goneWindows.Contains(w)).ToList();
                goneWindows.UnionWith(orphans);
                more = next.Count > 0 || orphans.Count > 0;
            }
            int count = added.Count(a => goneDoors.Contains(a.Portal)) + addedWindows.Count(a => goneWindows.Contains(a.Window));
            KeepOnly(added.Select(a => a.Portal).Where(p => !goneDoors.Contains(p)).ToHashSet(), why);
            KeepWindows(addedWindows.Select(a => a.Window).Where(w => !goneWindows.Contains(w)).ToHashSet());
            return count;
        }

        /// <summary>
        /// The portals of this pass VBSP complained of by brush in <paramref name="output"/> - "areaportal
        /// brush doesn't touch two areas", "touches > 2 areas" - which stop a compile without a pointfile.
        /// </summary>
        public (HashSet<VmfNode> Doors, HashSet<Window> Windows) RefusedBy(IEnumerable<string> output)
        {
            var ids = output.Where(l => l.Contains("areaportal", StringComparison.OrdinalIgnoreCase))
                .SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l, @"[Bb]rush (\d+)").Select(m => m.Groups[1].Value))
                .ToHashSet();
            bool Named(VmfNode entity) => entity.Children("solid").Any(s => s.Get("id") is { } id && ids.Contains(id));
            return (added.Where(a => Named(a.Portal)).Select(a => a.Portal).ToHashSet(),
                addedWindows.Where(a => Named(a.Portal)).Select(a => a.Window).ToHashSet());
        }

        /// <summary>The windows the doorways in <paramref name="doorways"/> need, each once.</summary>
        public HashSet<Window> WindowsNeededBy(IEnumerable<Doorway> doorways) =>
            doorways.SelectMany(d => Rescued.TryGetValue(d, out var r) ? r.Windows : []).ToHashSet();

        /// <summary>Whether a collision brush is one of the window stand-ins, which never collide in game.</summary>
        public bool IsStandIn(BspSpace.BrushShape shape) =>
            addedWindows.Any(a => a.StandIn != null && Close(StandInBox(a.Window), shape));

        private static (Vec3 Mins, Vec3 Maxs) StandInBox(Window w) => w.NormalAxis == 0
            ? (w.Mins with { X = w.Mins.X - 1 }, w.Maxs with { X = w.Maxs.X + 1 })
            : (w.Mins with { Y = w.Mins.Y - 1 }, w.Maxs with { Y = w.Maxs.Y + 1 });

        private static bool Close((Vec3 Mins, Vec3 Maxs) box, BspSpace.BrushShape shape) =>
            (box.Mins - shape.Mins).Length < 1.5 && (box.Maxs - shape.Maxs).Length < 1.5;

    }
}
