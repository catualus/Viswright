using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Viswright
{
    /// <summary>
    /// Where the optimised copy of a map is written.
    ///
    /// Never beside the map: the folder holding the mapper's VMF is theirs, and nothing this tool makes
    /// belongs in it. The copy keeps the map's own file name, because the compilers name the BSP after
    /// the VMF and the BSP has to reach the game under the name the map has always had. One folder per
    /// map, keyed on the map's full path, so two maps that share a name never share a copy.
    /// </summary>
    public static class Handoff
    {
        /// <summary>The folder every copy goes under, unless a test points it somewhere else.</summary>
        public static string Root { get; set; } = Path.Combine(Path.GetTempPath(), "CompilePal", "Viswright");

        public static string CopyPath(string sourceMapFile)
        {
            string name = Path.GetFileNameWithoutExtension(sourceMapFile);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourceMapFile).ToLowerInvariant()));
            return Path.Combine(Root, $"{name}-{Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}", name + ".vmf");
        }
    }
}
