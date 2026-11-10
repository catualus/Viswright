using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SourceKit.Compile;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>The compilers a run checks its changes with, and how to call them.</summary>
    public sealed record Toolchain(string Vbsp, string Vvis, string GameFolder, string VbspFlags = "", string VvisFlags = "")
    {
        /// <summary>
        /// The same tools for the trial compiles of a search, <paramref name="lanes"/> running at once:
        /// the machine's threads shared between them, and, with VBSP++, no collision models or default
        /// cubemaps, which a trial is not judged on. The compiles a search keeps are made with the
        /// tools as they are.
        /// </summary>
        public Toolchain ForTrials(int lanes)
        {
            static bool PlusPlus(string exe) => Path.GetFileName(exe).Contains("++", StringComparison.Ordinal);
            string threads = $" -threads {Math.Max(1, Environment.ProcessorCount / lanes)}";
            return this with
            {
                VbspFlags = (VbspFlags + (PlusPlus(Vbsp) ? threads + " -nophysics -nodefaultcubemap" : "")).Trim(),
                VvisFlags = (VvisFlags + (PlusPlus(Vvis) ? threads : "")).Trim(),
            };
        }

        public void Validate()
        {
            if (!File.Exists(Vbsp)) throw new FileNotFoundException($"VBSP not found at {Vbsp}.", Vbsp);
            if (!File.Exists(Vvis)) throw new FileNotFoundException($"VVIS not found at {Vvis}.", Vvis);
            if (!Directory.Exists(GameFolder)) throw new DirectoryNotFoundException($"The game folder {GameFolder} does not exist.");
        }
    }

    /// <summary>What a compiler run produced.</summary>
    public sealed record CompileRun(int ExitCode, IReadOnlyList<string> Output, TimeSpan Elapsed)
    {
        public bool Succeeded => ExitCode == 0;
    }

    /// <summary>A VBSP (and optionally VVIS) compile of one version of the map, in a folder of its own.</summary>
    public sealed class WorkCompile
    {
        public string Vmf { get; }
        public string Bsp => Path.ChangeExtension(Vmf, "bsp");
        public string Prt => Path.ChangeExtension(Vmf, "prt");
        public string Lin => Path.ChangeExtension(Vmf, "lin");

        /// <summary>Time spent waiting on compilers since the process started, for the timing summary.</summary>
        public static TimeSpan CompilerTime => TimeSpan.FromTicks(Interlocked.Read(ref compilerTicks));
        public static int CompilerRuns => compilerRuns;
        private static long compilerTicks;
        private static int compilerRuns;

        public CompileRun? VbspRun { get; private set; }
        public CompileRun? VvisRun { get; private set; }

        /// <summary>Whether VBSP reported a leak; it writes the pointfile only then.</summary>
        public bool Leaked => File.Exists(Lin);

        public PointFile? LeakPath => Leaked ? PointFile.Load(Lin) : null;

        private WorkCompile(string vmf) => Vmf = vmf;

        /// <summary>A compile already on disk, kept from an earlier run.</summary>
        public static WorkCompile Existing(string vmf) => new(vmf);

        /// <summary>How long a compile kept from an earlier run took, for anything that compares times with it.</summary>
        public void Remember(TimeSpan vbsp, TimeSpan vvis)
        {
            VbspRun ??= new CompileRun(0, [], vbsp);
            VvisRun ??= new CompileRun(0, [], vvis);
        }

        /// <summary>
        /// Writes <paramref name="map"/> (read from <paramref name="loadedFrom"/>) to
        /// <paramref name="folder"/> with its instance paths re-pointed, ready to compile.
        /// </summary>
        public static WorkCompile Prepare(VmfDocument map, string loadedFrom, string folder, string name)
        {
            Directory.CreateDirectory(folder);
            foreach (var stale in Directory.EnumerateFiles(folder))
                File.Delete(stale);

            string vmf = Path.Combine(folder, name + ".vmf");
            // With no instance to re-point, the map is written as it is: copying a large map costs more
            // than a second, every compile.
            if (!map.Entities.Any(e => string.Equals(e.Classname, "func_instance", StringComparison.OrdinalIgnoreCase)))
            {
                map.Save(vmf);
                return new WorkCompile(vmf);
            }
            // A copy, so moving this one does not re-point the caller's document.
            var copy = VmfDocument.Parse(map.ToString(), map.HasByteOrderMark);
            VmfRelocation.RetargetInstances(copy, loadedFrom, vmf);
            copy.Save(vmf);
            return new WorkCompile(vmf);
        }

        public CompileRun RunVbsp(Toolchain tools)
        {
            VbspRun = Run(tools.Vbsp, $"{tools.VbspFlags} -game \"{tools.GameFolder}\" \"{Vmf}\"");
            return VbspRun;
        }

        public CompileRun RunVvis(Toolchain tools)
        {
            VvisRun = Run(tools.Vvis, $"{tools.VvisFlags} -game \"{tools.GameFolder}\" \"{Vmf}\"");
            return VvisRun;
        }

        /// <summary>
        /// Runs a compiler and collects what it prints. Cancelling kills it: a half-finished compile in
        /// a work folder is worth nothing and the folder is cleared before the next one.
        /// </summary>
        private CompileRun Run(string exe, string arguments)
        {
            var start = new ProcessStartInfo(exe, arguments.Trim())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Vmf)!,
            };

            var output = new List<string>();
            var gate = new object();
            var watch = Stopwatch.StartNew();
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) output.Add(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) output.Add(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (SourceKitConcurrency.CancellationToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
                process.WaitForExit();
            SourceKitConcurrency.ThrowIfCancelled();

            Interlocked.Add(ref compilerTicks, watch.Elapsed.Ticks);
            Interlocked.Increment(ref compilerRuns);
            lock (gate)
                return new CompileRun(process.ExitCode, output.ToList(), watch.Elapsed);
        }

        /// <summary>
        /// Deletes the compile's folder. Only for a compile that has a folder to itself, such as one
        /// trial of a search, once what it was for has been read from it: a big map's VMF, BSP and
        /// portal file come to some 50 MB, and a search makes hundreds.
        /// </summary>
        public void Discard() => DeleteFolder(Path.GetDirectoryName(Vmf)!);

        /// <summary>
        /// Deletes a folder and everything in it, if it can. A file something else still holds open is
        /// left for the next run to clear rather than failing this one.
        /// </summary>
        internal static void DeleteFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
