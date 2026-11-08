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
    ///
    /// VBSP leaves a prop_static or func_detail out of the BSP's entity list, and Garry's Mod numbers map
    /// entities by their place in that list (MapCreationID). When the server's Lua finds entities by that
    /// number - DarkRP's saved doors, keypad links - an entity is only converted if no entity that stays in
    /// the list comes after it, so every one that stays keeps its number.
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

        /// <summary>
        /// Classes VBSP leaves out of the BSP's entity list: what they hold goes to a lump of its own or into
        /// the world. Taken from what vbsp++ wrote for the downtown maps and ten of Valve's TF2 maps. A class
        /// not listed is taken to stay, which can only keep more entities as they are; func_ladder, for one,
        /// stays as info_ladder. An info_overlay is left out only without a name: a named one stays as
        /// info_overlay_accessor.
        /// </summary>
        private static readonly HashSet<string> LeftOutOfEntityList = new(StringComparer.OrdinalIgnoreCase)
        {
            "prop_static", "func_detail", "env_cubemap", "info_lighting", "func_viscluster", "info_no_dynamic_shadow", "func_instance",
        };

        private readonly List<(VmfNode Entity, string Before)> converted = [];
        private readonly Dictionary<VmfNode, string?> shadowsBefore = new();
        private VmfDocument? map;
        private IReadOnlySet<VmfNode>? mapEntities;
        private string? mapIdFile;

        public IReadOnlyList<(VmfNode Entity, string Before)> Converted => converted;

        /// <summary>Set when Lua finds map entities by MapCreationID, so every map entity that stays in the entity list must keep its place.</summary>
        public bool KeepsMapIds { get; private set; }

        /// <summary>Entities that qualified but stay as they are, because converting them would move a map entity after them to a new MapCreationID.</summary>
        public int KeptForMapIds { get; private set; }

        /// <summary>func_brush left alone only because converting would change how it is lit.</summary>
        public int LightingChanges { get; private set; }

        public Dictionary<string, int> Skipped { get; } = new();

        /// <summary>Each class Lua protected, with the lookup and a file that does it.</summary>
        public List<(string Class, string Lookup, string File)> Protected { get; } = [];

        /// <summary>Each entity passed over, and why, for the review.</summary>
        public List<(VmfNode Entity, string Reason)> SkippedEntities { get; } = [];

        /// <summary>
        /// Converts every entity that qualifies. Returns how many. <paramref name="mapEntities"/> are the
        /// entities the map came with, when other passes have added their own after them: those are made
        /// again on every compile, so nothing saved can rely on their place. Null counts every entity.
        /// </summary>
        public int Apply(VmfDocument map, GameContent content, LuaGuard lua, IReadOnlySet<VmfNode>? mapEntities = null)
        {
            this.map = map;
            this.mapEntities = mapEntities;
            KeepsMapIds = lua.UsesMapIds;
            mapIdFile = lua.MapIdFiles.FirstOrDefault();

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
            KeepMapIds();
            return converted.Count;
        }

        /// <summary>
        /// When Lua finds map entities by MapCreationID, puts back every converted entity that a map entity
        /// staying in the entity list comes after. VBSP writes the list in the VMF's order, leaving out the
        /// classes it takes elsewhere, so only a run of conversions at the very end of the map takes nothing
        /// from anyone's place. Returns how many went back.
        /// </summary>
        private int KeepMapIds()
        {
            if (!KeepsMapIds || map == null || converted.Count == 0)
                return 0;
            var mine = converted.Select(c => c.Entity).ToHashSet();
            var entities = map.Entities.Where(e => mapEntities == null || mapEntities.Contains(e)).ToList();
            // VBSP puts an instance's entities after all of the map's own, so with an instance in the map
            // no entity of the map's own is last.
            bool staysAfter = entities.Any(e => string.Equals(e.Classname, "func_instance", StringComparison.OrdinalIgnoreCase));
            var back = new List<VmfNode>();
            for (int i = entities.Count - 1; i >= 0; i--)
            {
                if (mine.Contains(entities[i]))
                {
                    if (staysAfter)
                        back.Add(entities[i]);
                }
                else if (InEntityList(entities[i]))
                    staysAfter = true;
            }
            string reason = $"kept as an entity: making it static would give the map entities after it new MapCreationIDs, which Lua finds them by ({mapIdFile})";
            foreach (var entity in back)
            {
                var entry = converted.First(c => ReferenceEquals(c.Entity, entity));
                Restore(entry.Entity, entry.Before);
                converted.Remove(entry);
                Skip(reason, entity);
            }
            KeptForMapIds += back.Count;
            return back.Count;
        }

        /// <summary>Whether VBSP keeps <paramref name="entity"/>, as it is now, in the BSP's entity list.</summary>
        internal static bool InEntityList(VmfNode entity)
        {
            string cls = entity.Classname ?? "";
            if (string.Equals(cls, "info_overlay", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrEmpty(entity.Get("targetname"));
            return !LeftOutOfEntityList.Contains(cls);
        }

        /// <summary>The hammerid and classname of each entity in a BSP's entity list, in order.</summary>
        public static List<(string? Id, string? Classname)> EntityList(string bsp)
        {
            var bytes = System.IO.File.ReadAllBytes(bsp);
            int offset = BitConverter.ToInt32(bytes, 8), length = BitConverter.ToInt32(bytes, 12);
            return EntityListOf(System.Text.Encoding.Latin1.GetString(bytes, offset, length));
        }

        internal static List<(string? Id, string? Classname)> EntityListOf(string lump)
        {
            var list = new List<(string? Id, string? Classname)>();
            string? key = null, id = null, classname = null;
            bool open = false;
            for (int i = 0; i < lump.Length; i++)
            {
                char c = lump[i];
                if (c == '"')
                {
                    int end = lump.IndexOf('"', i + 1);
                    if (end < 0)
                        break;
                    string token = lump[(i + 1)..end];
                    i = end;
                    if (!open)
                        continue;
                    if (key == null)
                        key = token;
                    else
                    {
                        if (key.Equals("hammerid", StringComparison.OrdinalIgnoreCase)) id ??= token;
                        else if (key.Equals("classname", StringComparison.OrdinalIgnoreCase)) classname ??= token;
                        key = null;
                    }
                }
                else if (c == '{')
                {
                    open = true;
                    key = id = classname = null;
                }
                else if (c == '}' && open)
                {
                    list.Add((id, classname));
                    open = false;
                }
            }
            return list;
        }

        /// <summary>
        /// The first entity of <paramref name="before"/>, other than those in <paramref name="converted"/>
        /// (by hammerid), that <paramref name="after"/> does not have at the same place, described; null
        /// when every one kept its place and so its MapCreationID. Entities added after them are fine.
        /// </summary>
        public static string? Renumbered(IReadOnlyList<(string? Id, string? Classname)> before, IReadOnlyList<(string? Id, string? Classname)> after,
            IReadOnlySet<string> converted)
        {
            // Every map has its world first; a list without it was not read, and proves nothing.
            if (before.Count == 0)
                return "the map's entity list could not be read, so it cannot be shown that no MapCreationID changes";
            for (int i = 0; i < before.Count; i++)
            {
                if (before[i].Id is { } id && converted.Contains(id))
                    continue;
                if (i >= after.Count || after[i] != before[i])
                    return $"{before[i].Classname} {before[i].Id} would move from place {i} in the entity list, changing its MapCreationID";
            }
            return null;
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
            // An entity put back stays in the entity list, so the ones converted before it would now move it.
            if (back.Count > 0)
                KeepMapIds();
            return back.Count;
        }

        public void RevertAll()
        {
            foreach (var (entity, before) in converted)
                Restore(entity, before);
            converted.Clear();
        }

        /// <summary>
        /// Puts back the converted entities <paramref name="back"/> picks, and when map entities must keep
        /// their places, any converted before one of those. Returns how many <paramref name="back"/> picked.
        /// </summary>
        public int RevertWhere(Func<VmfNode, bool> back)
        {
            var picked = converted.Where(c => back(c.Entity)).ToList();
            foreach (var (entity, before) in picked)
            {
                Restore(entity, before);
                converted.Remove((entity, before));
            }
            if (picked.Count > 0)
                KeepMapIds();
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
