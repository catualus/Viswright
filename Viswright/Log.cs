using System;
using System.IO;

namespace Viswright
{
    /// <summary>
    /// Output in the form Compile Pal reads.
    ///
    /// Each line starts with a short tag the host recognises: <c>vmf</c>, <c>check</c> and <c>out</c>
    /// are coloured as information, <c>warn</c> is counted as a warning and <c>error:</c> as an error.
    /// Everything goes to standard output, because that is the only stream Compile Pal shows unless it
    /// says otherwise.
    /// </summary>
    public static class Log
    {
        /// <summary>Where lines go. Swapped by tests, and silenced by the map status command.</summary>
        public static Action<string> Sink { get; set; } = Console.Out.WriteLine;

        public static void Vmf(string message) => Sink(Neutralise("vmf   " + message));
        public static void Check(string message) => Sink(Neutralise("check " + message));
        public static void Out(string message) => Sink(Neutralise("out   " + message));
        public static void Warn(string message) => Sink(Neutralise("warn  " + message));
        public static void Error(string message) => Sink(Neutralise("error: " + message));
        public static void Info(string message) => Sink(Neutralise("      " + message));

        /// <summary>The token Compile Pal treats as a command rather than a message.</summary>
        internal const string HostControlToken = "COMPILE_PAL_SET";

        /// <summary>
        /// The one line that is a command to the host rather than a message: hand the rest of this
        /// map's compile the VMF at <paramref name="path"/>. It does not go through
        /// <see cref="Neutralise"/>, which exists to stop exactly this line.
        /// </summary>
        public static void HandOff(string path) => Sink($"{HostControlToken} file \"{path}\"");

        /// <summary>
        /// Makes a message safe to print as a line of its own.
        ///
        /// Compile Pal acts on any output line that starts with its command word, and a command can
        /// re-point VBSP, VRAD or the game for the rest of the session. Every message here starts with
        /// a tag, but plenty of what follows the tag is the map's own text - material and model names,
        /// entity names, paths - which this tool did not choose. Compile Pal ends a line at a bare
        /// carriage return as well as a line feed, and the VMF reader keeps a carriage return inside a
        /// value, so a value can carry a line of its own past the tag. Control characters therefore
        /// become spaces, leaving one line, and a line that still starts with the command word, after
        /// any leading whitespace some other layer might trim, is marked so it cannot be one.
        /// </summary>
        internal static string Neutralise(string text)
        {
            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (char.IsControl(chars[i]) && chars[i] != '\t')
                    chars[i] = ' ';
            string flattened = new(chars);

            return flattened.TrimStart().StartsWith(HostControlToken, StringComparison.Ordinal)
                ? "[filtered] " + flattened.TrimStart()
                : flattened;
        }
    }
}
