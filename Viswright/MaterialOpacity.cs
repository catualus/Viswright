using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Viswright
{
    /// <summary>
    /// Whether a material hides what is behind it.
    ///
    /// A face can only be proven hidden by a brush you cannot see through. That is decided from the
    /// material's VMT: a shader that draws something solid, with none of the keys that let the world
    /// show through. A material that cannot be found, or cannot be read, counts as see-through - an
    /// unknown is never allowed to hide a face.
    /// </summary>
    public sealed class MaterialOpacity
    {
        private static readonly HashSet<string> OpaqueShaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "LightmappedGeneric", "WorldVertexTransition", "LightmappedTwoTexture", "Lightmapped_4WayBlend",
            "LightmappedReflective", "VertexLitGeneric", "UnlitGeneric", "WorldTwoTextureBlend",
        };

        private static readonly string[] SeeThroughKeys = ["$translucent", "$alphatest", "$additive", "$vertexalpha", "$distancealpha"];

        private readonly Func<string, byte[]?> read;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> cache = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="read">Reads a file by its game path, such as "materials/brick/wall01.vmt", or returns null.</param>
        public MaterialOpacity(Func<string, byte[]?> read) => this.read = read;

        /// <summary>Whether a brush face with <paramref name="material"/> (as a VMF names it) is opaque.</summary>
        public bool IsOpaque(string material)
        {
            return cache.GetOrAdd(material, m => Decide(m, depth: 0));
        }

        private bool Decide(string material, int depth)
        {
            if (depth > 4 || material.StartsWith("TOOLS/", StringComparison.OrdinalIgnoreCase))
                return false;

            string path = "materials/" + material.Replace('\\', '/').ToLowerInvariant();
            if (!path.EndsWith(".vmt", StringComparison.Ordinal))
                path += ".vmt";
            var bytes = read(path);
            if (bytes == null)
                return false;

            var (shader, keys) = Parse(Encoding.Latin1.GetString(bytes));
            if (shader.Equals("patch", StringComparison.OrdinalIgnoreCase))
            {
                // A patch material is its include with some keys changed; the changes win.
                if (!keys.TryGetValue("include", out string? include))
                    return false;
                foreach (var key in SeeThroughKeys)
                    if (IsOn(keys, key))
                        return false;
                if (keys.TryGetValue("$alpha", out string? patchAlpha) && Number(patchAlpha) < 1)
                    return false;
                string included = include.Replace('\\', '/');
                if (included.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                    included = included["materials/".Length..];
                return Decide(included, depth + 1);
            }

            if (!OpaqueShaders.Contains(shader))
                return false;
            foreach (var key in SeeThroughKeys)
                if (IsOn(keys, key))
                    return false;
            if (keys.TryGetValue("$alpha", out string? alpha) && Number(alpha) < 1)
                return false;
            return true;
        }

        private static bool IsOn(Dictionary<string, string> keys, string key) =>
            keys.TryGetValue(key, out string? value) && Number(value) != 0;

        private static double Number(string value) =>
            double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 1;

        /// <summary>The shader name and the top-level keys (nested blocks such as proxies are skipped).</summary>
        internal static (string Shader, Dictionary<string, string> Keys) Parse(string text)
        {
            var tokens = Tokens(text);
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (tokens.Count == 0)
                return ("", keys);

            string shader = tokens[0];
            int depth = 0;
            for (int i = 1; i < tokens.Count; i++)
            {
                if (tokens[i] == "{") { depth++; continue; }
                if (tokens[i] == "}") { depth--; continue; }
                if (depth == 1 && i + 1 < tokens.Count && tokens[i + 1] != "{" && tokens[i + 1] != "}")
                {
                    keys[tokens[i]] = tokens[i + 1];
                    i++;
                }
            }
            return (shader, keys);
        }

        private static List<string> Tokens(string text)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (c == '{' || c == '}') { tokens.Add(c.ToString()); i++; continue; }
                if (c == '"')
                {
                    int end = text.IndexOf('"', i + 1);
                    if (end < 0) end = text.Length;
                    tokens.Add(text[(i + 1)..end]);
                    i = end + 1;
                    continue;
                }
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"') i++;
                tokens.Add(text[start..i]);
            }
            return tokens;
        }
    }
}
