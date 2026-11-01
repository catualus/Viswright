using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Viswright
{
    /// <summary>
    /// What a server's Lua could notice if the map changed under it.
    ///
    /// Garry's Mod gamemodes and addons find map entities by class (<c>ents.FindByClass("func_brush")</c>,
    /// <c>ent:GetClass() == "prop_dynamic"</c>). An entity turned into a static prop or into func_detail
    /// is no longer an entity at all, so Lua that looks for its class finds one fewer. Any class the
    /// scanned Lua mentions in a lookup is protected, and the passes leave entities of it alone.
    /// </summary>
    public sealed class LuaGuard
    {
        private static readonly Regex ClassLookup = new(
            // Not FindByClassAndParent: it only finds children of one entity, and nothing this tool
            // changes has a parent.
            @"(?:FindByClass\s*\(\s*|GetClass\s*\(\s*\)\s*[=~]=\s*|ClassName\s*[=~]=\s*)[""']([A-Za-z0-9_*]+)[""']",
            RegexOptions.Compiled);

        /// <summary>Classes some scanned Lua looks entities up by, with a file that does it.</summary>
        public Dictionary<string, string> Classes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int FilesScanned { get; private set; }

        public static LuaGuard Scan(IEnumerable<string> folders)
        {
            var guard = new LuaGuard();
            foreach (string folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.lua", SearchOption.AllDirectories))
                {
                    if (guard.FilesScanned >= 50000)
                        return guard;
                    guard.FilesScanned++;
                    string text;
                    try { text = File.ReadAllText(file); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    foreach (Match m in ClassLookup.Matches(text))
                        guard.Classes.TryAdd(m.Groups[1].Value, file);
                }
            }
            return guard;
        }

        /// <summary>The Lua folders of a Garry's Mod install: its gamemodes, and each addon's lua folder.</summary>
        public static IEnumerable<string> GameFolders(string gameFolder)
        {
            yield return Path.Combine(gameFolder, "gamemodes");
            yield return Path.Combine(gameFolder, "lua");
            string addons = Path.Combine(gameFolder, "addons");
            if (Directory.Exists(addons))
                foreach (string addon in Directory.EnumerateDirectories(addons))
                    yield return addon;
        }

        /// <summary>Whether Lua looks up entities of <paramref name="classname"/>, directly or by a wildcard.</summary>
        public bool Protects(string classname, out string? where) => Protects(classname, out where, out _);

        public bool Protects(string classname, out string? where, out string? lookup)
        {
            lookup = null;
            foreach (var (pattern, file) in Classes)
            {
                bool matches = pattern.EndsWith('*')
                    ? classname.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(pattern, classname, StringComparison.OrdinalIgnoreCase);
                if (matches)
                {
                    where = file;
                    lookup = pattern;
                    return true;
                }
            }
            where = null;
            return false;
        }
    }
}
