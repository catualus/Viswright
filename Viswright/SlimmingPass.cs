using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>
    /// Pass F: entities that never do anything an entity can do stop being entities.
    ///
    /// Every entity costs an edict (Garry's Mod has 8192 for the map and everything players spawn) and is
    /// sent to every client. A prop_dynamic nothing names, parents, fires or animates is drawn exactly
    /// like a prop_static, which costs neither; a func_brush the same is drawn like func_detail.
    /// Eligibility is strict - any key the static form does not support, set to anything but its
    /// default, keeps the entity as it is - and Lua that looks entities up by class protects the class.
    /// </summary>
    public sealed class SlimmingPass
    {
        private static readonly string[] DynamicClasses = ["prop_dynamic", "prop_dynamic_override"];

        /// <summary>Keys a static prop cannot carry, with the value each must have for the prop to qualify.</summary>
        private static readonly (string Key, string[] Allowed)[] PropDefaults =
        [
            ("targetname", [""]), ("parentname", [""]), ("DefaultAnim", [""]), ("RandomAnimation", ["", "0"]),
            ("SetBodyGroup", ["", "0"]), ("StartDisabled", ["", "0"]), ("health", ["", "0"]),
            ("gmod_allowphysgun", ["", "0"]), ("rendermode", ["", "0"]), ("renderfx", ["", "0"]),
            ("renderamt", ["", "255"]), ("rendercolor", ["", "255 255 255"]), ("modelscale", ["", "1", "1.0"]),
            ("spawnflags", ["", "0"]), ("solid", ["", "0", "2", "6"]), ("lightingorigin", [""]),
        ];

        private static readonly (string Key, string[] Allowed)[] BrushDefaults =
        [
            ("targetname", [""]), ("parentname", [""]), ("StartDisabled", ["", "0"]), ("rendermode", ["", "0"]),
            ("renderfx", ["", "0"]), ("renderamt", ["", "255"]), ("rendercolor", ["", "255 255 255"]),
            ("spawnflags", ["", "0", "2"]), ("_minlight", ["", "0"]),
            ("gmod_allowphysgun", ["", "0"]), ("InputFilter", ["", "0"]), ("invert_exclusion", ["", "0"]),
            ("excludednpc", [""]),
        ];

        private readonly List<(VmfNode Entity, string Before)> converted = [];
        private readonly Dictionary<VmfNode, string?> shadowsBefore = new();

        public IReadOnlyList<(VmfNode Entity, string Before)> Converted => converted;

        /// <summary>func_brush left alone only because converting would change how it is lit.</summary>
        public int LightingChanges { get; private set; }

        public Dictionary<string, int> Skipped { get; } = new();

        /// <summary>Each class Lua protected, with the lookup and a file that does it.</summary>
        public List<(string Class, string Lookup, string File)> Protected { get; } = [];

        /// <summary>Each entity passed over, and why, for the review.</summary>
        public List<(VmfNode Entity, string Reason)> SkippedEntities { get; } = [];

        public int Apply(VmfDocument map, GameContent content, LuaGuard lua)
        {
            bool Guarded(string cls)
            {
                if (!lua.Protects(cls, out string? file, out string? lookup)) return false;
                Protected.Add((cls, lookup!, file!));
                return true;
            }
            bool propsProtected = DynamicClasses.Select(Guarded).ToList().Any(p => p);
            bool brushesProtected = Guarded("func_brush");
            if (propsProtected) Skip("Lua looks up prop_dynamic by class", 1);
            if (brushesProtected) Skip("Lua looks up func_brush by class", 1);

            foreach (var e in map.Entities.ToList())
            {
                string cls = e.Classname ?? "";
                if (!propsProtected && DynamicClasses.Contains(cls, StringComparer.OrdinalIgnoreCase))
                {
                    if (!Plain(e, PropDefaults)) continue;
                    string? path = e.Get("model");
                    var model = path == null ? null : content.Model(path);
                    if (model == null) { Skip("the model was not found", e); continue; }
                    // VBSP deletes a static prop whose model was not compiled with $staticprop, or whose
                    // prop_data makes it a physics prop without allowing static use.
                    if (!model.StaticProp) { Skip("the model is not compiled for static use", e); continue; }
                    if (!model.UsableAsStaticProp) { Skip("the model is a physics prop (prop_data without allowstatic)", e); continue; }
                    converted.Add((e, cls));
                    e.Set("classname", "prop_static");
                    // A prop_dynamic casts no shadow into the lightmaps; a prop_static does unless told not
                    // to. Telling it not to keeps the world's lighting exactly as it was.
                    shadowsBefore[e] = e.Get("disableshadows");
                    e.Set("disableshadows", "1");
                }
                else if (!brushesProtected && string.Equals(cls, "func_brush", StringComparison.OrdinalIgnoreCase))
                {
                    // Solidity 1 is never solid; as func_detail it would block players.
                    if (e.Get("Solidity")?.Trim() == "1") { Skip("the func_brush is never solid", e); continue; }
                    if (!Plain(e, BrushDefaults)) continue;
                    if (e.Children("solid").SelectMany(s => s.Children("side")).Any(s => s.Child("dispinfo") != null)) continue;
                    // func_brush casts no lightmap shadow unless told to; func_detail always does.
                    if (e.Get("vrad_brush_cast_shadows") != "1")
                    {
                        LightingChanges++;
                        SkippedEntities.Add((e, "as func_detail it would cast a lightmap shadow it does not cast now"));
                        continue;
                    }
                    converted.Add((e, cls));
                    e.Set("classname", "func_detail");
                }
            }
            return converted.Count;
        }

        /// <summary>Whether the entity has no outputs and every listed key at a default value.</summary>
        private bool Plain(VmfNode e, (string Key, string[] Allowed)[] defaults)
        {
            if (e.Children("connections").Any(c => c.KeyValues.Any()))
                return false;
            foreach (var (key, allowed) in defaults)
            {
                string value = (e.Get(key) ?? "").Trim();
                if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    Skip($"{key} is set", e);
                    return false;
                }
            }
            return true;
        }

        private void Skip(string reason, int count = 1) => Skipped[reason] = Skipped.GetValueOrDefault(reason) + count;

        private void Skip(string reason, VmfNode entity)
        {
            Skip(reason);
            SkippedEntities.Add((entity, reason));
        }

        /// <summary>Puts back the entities whose model VBSP refused as a static prop.</summary>
        public int RevertModels(IReadOnlySet<string> refused)
        {
            var back = converted.Where(c => c.Before.StartsWith("prop_", StringComparison.OrdinalIgnoreCase)
                && refused.Contains((c.Entity.Get("model") ?? "").Replace('\\', '/').ToLowerInvariant())).ToList();
            foreach (var (entity, before) in back)
            {
                Restore(entity, before);
                converted.Remove((entity, before));
                SkippedEntities.Add((entity, "VBSP would not take its model as a static prop"));
            }
            return back.Count;
        }

        public void RevertAll()
        {
            foreach (var (entity, before) in converted)
                Restore(entity, before);
            converted.Clear();
        }

        /// <summary>Puts back the converted entities <paramref name="back"/> picks. Returns how many.</summary>
        public int RevertWhere(Func<VmfNode, bool> back)
        {
            var picked = converted.Where(c => back(c.Entity)).ToList();
            foreach (var (entity, before) in picked)
            {
                Restore(entity, before);
                converted.Remove((entity, before));
            }
            return picked.Count;
        }

        private void Restore(VmfNode entity, string classname)
        {
            entity.Set("classname", classname);
            if (!shadowsBefore.Remove(entity, out string? shadows))
                return;
            if (shadows != null)
                entity.Set("disableshadows", shadows);
            else if (entity.GetPair("disableshadows") is { } pair)
                entity.Remove(pair);
        }

        /// <summary>What the pass may change: the classname of the entities it converted.</summary>
        public void Declare(ChangeGuard guard)
        {
            var ids = converted.Select(c => c.Entity.Id).OfType<long>().Select(id => id.ToString(CultureInfo.InvariantCulture)).ToHashSet();
            guard.Allow("slimming", change =>
            {
                var parts = change.Path.Split('/');
                if (parts.Length != 2 || !parts[0].StartsWith("entity#", StringComparison.Ordinal)
                    || !ids.Contains(parts[0]["entity#".Length..].Split('~')[0]))
                    return false;
                return (parts[1] == "classname" && change.Kind == VmfChangeKind.ValueChanged && change.After is "prop_static" or "func_detail")
                    || (parts[1] == "disableshadows" && change.Kind != VmfChangeKind.Removed && change.After == "1");
            });
        }
    }
}
