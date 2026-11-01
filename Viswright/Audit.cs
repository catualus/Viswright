using System;
using System.Collections.Generic;
using System.Linq;
using SourceKit.Vmf;

namespace Viswright
{
    public enum FindingLevel { Info, Warning, Error }

    /// <summary>One thing the audit noticed, with how many objects it applies to.</summary>
    public sealed record Finding(FindingLevel Level, string Code, int Count, string Message);

    /// <summary>
    /// What a reviewer would point out about a map's optimisation, read from the VMF and changing
    /// nothing. It runs on every compile, whether or not any pass is enabled, and it is what the passes
    /// will act on: a door without a portal here is a portal pass C may add.
    /// </summary>
    public static class Audit
    {
        public static readonly string[] DoorClasses = ["func_door", "func_door_rotating", "prop_door_rotating"];
        public static readonly string[] PortalClasses = ["func_areaportal", "func_areaportalwindow"];

        /// <summary>The highest areaportal number Garry's Mod will load; measured, see <see cref="VisReport.GmodMaxPortalNumber"/>.</summary>
        public const int PortalBudget = VisReport.GmodMaxPortalNumber;

        public static IReadOnlyList<Finding> Run(VmfDocument map)
        {
            var findings = new List<Finding>();
            var entities = map.Entities.ToList();

            bool Is(VmfNode e, params string[] classes) =>
                classes.Contains(e.Classname ?? "", StringComparer.OrdinalIgnoreCase);

            // Every name any output fires at. An output's value is "target,input,param,delay,times",
            // separated by commas or, in newer Hammer, the escape character.
            var targeted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entities)
                foreach (var connections in e.Children("connections"))
                    foreach (var output in connections.KeyValues)
                    {
                        string target = output.Value.Split(',', '\u001b')[0].Trim();
                        if (target.Length > 0)
                            targeted.Add(target);
                    }

            // Areaportal budget.
            int portals = entities.Count(e => Is(e, PortalClasses));
            var budgetLevel = portals > PortalBudget ? FindingLevel.Error
                : portals > PortalBudget - 20 ? FindingLevel.Warning : FindingLevel.Info;
            findings.Add(new Finding(budgetLevel, "portal-budget", portals,
                $"{portals} areaportals and areaportal windows of the {PortalBudget} Garry's Mod loads; {Math.Max(0, PortalBudget - portals)} left"));

            // Doors no areaportal closes with.
            var portalTargets = new HashSet<string>(
                entities.Where(e => Is(e, "func_areaportal")).Select(e => e.Get("target") ?? "").Where(t => t.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            int doors = entities.Count(e => Is(e, DoorClasses));
            int unportalled = entities.Count(e => Is(e, DoorClasses) && !portalTargets.Contains(e.Get("targetname") ?? ""));
            if (unportalled > 0)
                findings.Add(new Finding(FindingLevel.Info, "door-without-portal", unportalled,
                    $"{unportalled} of {doors} doors have no areaportal that closes with them"));

            // Static props that never fade.
            int props = entities.Count(e => Is(e, "prop_static"));
            int unfaded = entities.Count(e => Is(e, "prop_static") && !(double.TryParse(e.Get("fademaxdist"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double max) && max > 0));
            if (unfaded > 0)
                findings.Add(new Finding(FindingLevel.Info, "prop-without-fade", unfaded,
                    $"{unfaded} of {props} static props never fade"));

            // Visclusters, which vbsp++ itself warns make a map slower.
            int visclusters = entities.Count(e => Is(e, "func_viscluster"));
            if (visclusters > 0)
                findings.Add(new Finding(FindingLevel.Warning, "viscluster", visclusters,
                    $"{visclusters} func_viscluster: they merge clusters into coarser visibility, and VVIS++ does not need them"));

            // World brushes shaped like detail.
            int detailLike = map.World.Children("solid").Count(LooksLikeDetail);
            if (detailLike > 0)
                findings.Add(new Finding(FindingLevel.Info, "world-detail-candidate", detailLike,
                    $"{detailLike} world brushes are small or complex enough to be func_detail"));

            // Hints.
            int hints = map.Root.Descendants().Count(n => n.Name.Equals("side", StringComparison.OrdinalIgnoreCase)
                && string.Equals(n.Get("material"), "TOOLS/TOOLSHINT", StringComparison.OrdinalIgnoreCase));
            findings.Add(new Finding(FindingLevel.Info, "hint-faces", hints, $"{hints} hint faces"));

            // Named lights nothing switches: each one still costs its own lightmap style.
            int idleLights = entities.Count(e => Is(e, "light", "light_spot") && e.Get("targetname") is { Length: > 0 } name
                && !targeted.Contains(name));
            if (idleLights > 0)
                findings.Add(new Finding(FindingLevel.Info, "untargeted-named-light", idleLights,
                    $"{idleLights} named lights no output targets; each keeps a switchable lightmap style (Lua may still use them)"));

            // prop_dynamic that nothing names, parents or fires.
            int idleDynamic = entities.Count(e => Is(e, "prop_dynamic", "prop_dynamic_override")
                && string.IsNullOrEmpty(e.Get("targetname")) && string.IsNullOrEmpty(e.Get("parentname"))
                && !e.Children("connections").Any(c => c.KeyValues.Any()));
            if (idleDynamic > 0)
                findings.Add(new Finding(FindingLevel.Info, "static-candidate", idleDynamic,
                    $"{idleDynamic} prop_dynamic have no name, parent or outputs, so may not need to be entities"));

            return findings;
        }

        private static readonly HashSet<string> NonVisibleTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "TOOLS/TOOLSHINT", "TOOLS/TOOLSSKIP", "TOOLS/TOOLSAREAPORTAL", "TOOLS/TOOLSOCCLUDER", "TOOLS/TOOLSCLIP",
            "TOOLS/TOOLSPLAYERCLIP", "TOOLS/TOOLSNPCCLIP", "TOOLS/TOOLSTRIGGER", "TOOLS/TOOLSSKYBOX", "TOOLS/TOOLSSKYBOX2D",
            "TOOLS/TOOLSINVISIBLELADDER", "TOOLS/TOOLSBLOCKLIGHT",
        };

        /// <summary>
        /// The shape test pass A starts from: small (64 units or less on its longest side) or complex
        /// (a non-axial side or more than six sides) with a small footprint, and not a tool brush or a
        /// displacement. Whether converting it is safe is the pass's job, decided by compiling.
        /// </summary>
        internal static bool LooksLikeDetail(VmfNode solid)
        {
            var sides = solid.Children("side").ToList();
            if (sides.Any(s => s.Child("dispinfo") != null))
                return false;
            if (sides.All(s => (s.Get("material") ?? "").StartsWith("TOOLS/", StringComparison.OrdinalIgnoreCase)))
                return false;
            if (sides.Any(s => NonVisibleTools.Contains(s.Get("material") ?? "")))
                return false;

            var brush = VmfBrush.Build(solid);
            if (brush == null)
                return false;

            double longest = Math.Max(brush.Size.X, Math.Max(brush.Size.Y, brush.Size.Z));
            if (longest <= 64)
                return true;

            bool complex = !brush.IsAxialBox;
            double secondLongest = new[] { brush.Size.X, brush.Size.Y, brush.Size.Z }.OrderByDescending(v => v).ElementAt(1);
            return complex && secondLongest <= 128;
        }

        public static void Print(IEnumerable<Finding> findings)
        {
            foreach (var f in findings)
            {
                switch (f.Level)
                {
                    case FindingLevel.Error: Log.Error(f.Message); break;
                    case FindingLevel.Warning: Log.Warn(f.Message); break;
                    default: Log.Check(f.Message); break;
                }
            }
        }
    }
}
