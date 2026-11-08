using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SourceKit.Vmf;
using Xunit;

namespace Viswright.Tests
{
    public class OptimizeTests
    {
        [Fact]
        public void TheCopyIsTheMapWithOnlyItsInstancePathsMoved()
        {
            using var ws = new Workspace();
            string map = ws.Write(@"maps\rp_test.vmf", Workspace.Room);
            ws.Write(@"maps\instances\door.vmf", "versioninfo\n{\n}\n");
            byte[] before = File.ReadAllBytes(map);

            var result = Optimizer.Run(new OptimizeOptions { Input = map });

            Assert.Equal(before, File.ReadAllBytes(map));
            Assert.Equal("rp_test.vmf", Path.GetFileName(result.CopyPath));
            var change = Assert.Single(result.Changes);
            Assert.Equal("entity#20/file", change.Path);

            // The rewritten path reaches the same instance from the copy's folder, the way VBSP joins it.
            var copy = VmfDocument.Load(result.CopyPath!);
            string file = copy.Entities.Single().Get("file")!;
            // VBSP reads a backslash in a value as an escape, so the path must use forward slashes.
            Assert.DoesNotContain('\\', file);
            Assert.Equal(Path.Combine(ws.Root, "maps", "instances", "door.vmf"),
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(result.CopyPath)!, file)));
        }

        [Fact]
        public void AMapWithoutInstancesIsCopiedByteForByte()
        {
            using var ws = new Workspace();
            string map = ws.Write(@"maps\rp_test.vmf", Workspace.Room.Replace("func_instance", "info_player_start"));

            var result = Optimizer.Run(new OptimizeOptions { Input = map });

            Assert.Empty(result.Changes);
            Assert.Equal(File.ReadAllBytes(map), File.ReadAllBytes(result.CopyPath!));
        }

        [Fact]
        public void TheCopyIsNamedAfterTheQueuedMapNotTheFileRead()
        {
            using var ws = new Workspace();
            string queued = ws.Write(@"maps\rp_test.vmf", Workspace.Room);
            string earlier = ws.Write(@"vmffix\rp_test.vmf", Workspace.Room);

            var fromQueued = Optimizer.Run(new OptimizeOptions { Input = queued });
            var fromEarlier = Optimizer.Run(new OptimizeOptions { Input = earlier, Source = queued });

            Assert.Equal(fromQueued.CopyPath, fromEarlier.CopyPath);
        }

        [Fact]
        public void ADryRunWritesNothing()
        {
            using var ws = new Workspace();
            string map = ws.Write(@"maps\rp_test.vmf", Workspace.Room);

            var result = Optimizer.Run(new OptimizeOptions { Input = map, DryRun = true });

            Assert.Null(result.CopyPath);
            Assert.False(Directory.Exists(Handoff.Root));
        }

        [Fact]
        public void TheGuardRefusesAChangeNoPassDeclared()
        {
            var guard = ChangeGuard.ForRelocation();
            var changes = new[]
            {
                new VmfChange(VmfChangeKind.ValueChanged, "entity#20/file", "a", "b"),
                new VmfChange(VmfChangeKind.ValueChanged, "world#1/solid#2/side#10/material", "A", "B"),
                new VmfChange(VmfChangeKind.ValueChanged, "world#1/file", "a", "b"),
            };

            var undeclared = guard.Undeclared(changes);

            Assert.Equal(2, undeclared.Count);
            Assert.DoesNotContain(undeclared, c => c.Path == "entity#20/file");
        }

        [Fact]
        public void TheHandOffIsTheLastLineAndOnlyOnSuccess()
        {
            using var ws = new Workspace();
            string map = ws.Write(@"maps\rp_test.vmf", Workspace.Room);

            Assert.Equal(0, Program.Main(["optimize", map]));
            Assert.StartsWith("COMPILE_PAL_SET file \"", ws.Output.Last());
            Assert.Single(ws.Output, line => line.StartsWith("COMPILE_PAL_SET", StringComparison.Ordinal));

            ws.Output.Clear();
            Assert.Equal(1, Program.Main(["optimize", Path.Combine(ws.Root, "maps", "missing.vmf")]));
            Assert.DoesNotContain(ws.Output, line => line.StartsWith("COMPILE_PAL_SET", StringComparison.Ordinal));
            Assert.Contains(ws.Output, line => line.StartsWith("error: ", StringComparison.Ordinal));
        }

        [Fact]
        public void AMalformedMapStopsTheStepWithoutHandingOn()
        {
            using var ws = new Workspace();
            string map = ws.Write(@"maps\rp_test.vmf", "world\n{\n");

            Assert.Equal(1, Program.Main(["optimize", map]));
            Assert.DoesNotContain(ws.Output, line => line.StartsWith("COMPILE_PAL_SET", StringComparison.Ordinal));
        }

        [Fact]
        public void AMisspeltOptionIsRefused()
        {
            using var ws = new Workspace();
            string map = ws.Write(@"maps\rp_test.vmf", Workspace.Room);

            Assert.Equal(1, Program.Main(["optimize", map, "-dryrnu"]));
            Assert.Contains(ws.Output, line => line.Contains("unknown option '-dryrnu'"));
        }

        [Fact]
        public void TextFromTheMapCannotIssueAHostCommand()
        {
            Assert.Equal("[filtered] COMPILE_PAL_SET file x", Log.Neutralise("COMPILE_PAL_SET file x"));
            Assert.Equal("[filtered] COMPILE_PAL_SET file x", Log.Neutralise("   COMPILE_PAL_SET file x"));
            Assert.Equal("vmf   a\tb", Log.Neutralise("vmf   a\tb"));
        }

        /// <summary>
        /// Compile Pal splits a step's output at a bare carriage return as well as a line feed, and acts
        /// on any piece that starts with its command word. A value from the map - here a material name
        /// with a carriage return in it, which the VMF reader keeps - must not be able to start one.
        /// </summary>
        [Fact]
        public void AControlCharacterInMapTextCannotStartAHostCommand()
        {
            using var ws = new Workspace();
            string material = "BRICK/WALL\rCOMPILE_PAL_SET vrad_exe \"C:\\elsewhere\\vrad.exe\"\rmore";
            Log.Info($"  e.g. side 10 ({material}) at (0 0 0)");
            Log.Warn("a\nCOMPILE_PAL_SET file x");
            Log.Out("a\r\nCOMPILE_PAL_SET file x\u0085y\0z");

            Assert.Equal(3, ws.Output.Count);
            foreach (string line in ws.Output)
            {
                Assert.False(line.Any(char.IsControl), line);
                // What Compile Pal makes of the line: Logger splits CRLF, CR and LF alike.
                foreach (string piece in line.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                    Assert.False(piece.StartsWith("COMPILE_PAL_SET", StringComparison.Ordinal), piece);
            }
            Assert.Contains("BRICK/WALL COMPILE_PAL_SET vrad_exe", ws.Output[0]);
        }

        [Fact]
        public void TheHandOffLineGoesOutAsItIs()
        {
            using var ws = new Workspace();
            string copy = Path.Combine(ws.Root, "handoff", "rp_test-1234abcd", "rp_test.vmf");

            Log.HandOff(copy);

            Assert.Equal($"COMPILE_PAL_SET file \"{copy}\"", Assert.Single(ws.Output));
        }

        [Fact]
        public void StatusIsOneLineOfJson()
        {
            using var ws = new Workspace();
            var original = Console.Out;
            var captured = new StringWriter();
            Console.SetOut(captured);
            try
            {
                Assert.Equal(0, Program.Main(["status", "rp_test.vmf"]));
            }
            finally
            {
                Console.SetOut(original);
            }

            string[] lines = captured.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var json = JsonDocument.Parse(Assert.Single(lines)).RootElement;
            Assert.Equal("ready", json.GetProperty("label").GetString());
            Assert.Equal("info", json.GetProperty("severity").GetString());
        }
    }
}
