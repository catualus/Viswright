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

        /// <summary>
        /// The one line that is a command to the host rather than a message: hand the rest of this
        /// map's compile the VMF at <paramref name="path"/>.
        /// </summary>
        public static void HandOff(string path) => Sink($"COMPILE_PAL_SET file \"{path}\"");

        /// <summary>
        /// Compile Pal acts on any output line that starts with its command word. Every message here
        /// starts with a tag, so none can, but a line built from a map's own text must never be able
        /// to - a map path or entity name is not something this tool chose.
        /// </summary>
        internal static string Neutralise(string line) =>
            line.StartsWith("COMPILE_PAL_SET", StringComparison.Ordinal) ? "      " + line : line;
    }
}
