using System;
using System.IO;
using System.Linq;
using System.Text;
using SourceKit.Geometry;
using SourceKit.Vmf;
using Xunit;

namespace Viswright.Tests
{
    public class HintTests
    {
        private static string Entity(int id, string classname, string origin) =>
            $"entity\r\n{{\r\n\t\"id\" \"{id}\"\r\n\t\"classname\" \"{classname}\"\r\n\t\"origin\" \"{origin}\"\r\n}}\r\n";

        /// <summary>
        /// A 1024-unit courtyard open to the sky, with the player start, and beside it a sealed room
        /// holding only a light, which no player can reach.
        /// </summary>
        internal static string Courtyard()
        {
            var world = new StringBuilder();
            world.Append(AreaportalTests.Box(0, 0, 0, 1024, 1024, 16));
            world.Append(AreaportalTests.Box(0, 0, 1008, 1024, 1024, 1024, "TOOLS/TOOLSSKYBOX"));
            world.Append(AreaportalTests.Box(0, 0, 16, 16, 1024, 1008));
            world.Append(AreaportalTests.Box(1008, 0, 16, 1024, 1024, 1008));
            world.Append(AreaportalTests.Box(16, 0, 16, 1008, 16, 1008));
            world.Append(AreaportalTests.Box(16, 1008, 16, 1008, 1024, 1008));

            world.Append(AreaportalTests.Box(2048, 0, 0, 2304, 256, 16));
            world.Append(AreaportalTests.Box(2048, 0, 240, 2304, 256, 256));
            world.Append(AreaportalTests.Box(2048, 0, 16, 2064, 256, 240));
            world.Append(AreaportalTests.Box(2288, 0, 16, 2304, 256, 240));
            world.Append(AreaportalTests.Box(2064, 0, 16, 2288, 16, 240));
            world.Append(AreaportalTests.Box(2064, 240, 16, 2288, 256, 240));

            return "versioninfo\r\n{\r\n\t\"editorversion\" \"400\"\r\n}\r\nworld\r\n{\r\n\t\"id\" \"1\"\r\n\t\"classname\" \"worldspawn\"\r\n" +
                "\t\"skyname\" \"sky_day01_01\"\r\n" + world + "}\r\n" +
                Entity(51, "info_player_start", "512 512 32") + Entity(52, "light", "2176 128 128");
        }

        [Fact]
        public void AHintIsHintOnTopAndSkipElsewhereAndInsideItsSquare()
        {
            var map = VmfDocument.Parse(Courtyard());
            var before = VmfDocument.Parse(map.ToString());
            var pass = new HintPass(map);
            var square = new HintSquare(0, 0, 16, 1008, [0]);
            pass.Place(square, 400);

            var solid = pass.Placed[square].Solid;
            var materials = solid.Children("side").Select(s => s.Get("material")).ToList();
            Assert.Equal(HintPass.HintMaterial, materials[0]);
            Assert.All(materials.Skip(1), m => Assert.Equal(HintPass.SkipMaterial, m));
            var brush = VmfBrush.Build(solid)!;
            Assert.Equal(400, brush.Maxs.Z, 3);
            Assert.True(brush.Mins.X > 0 && brush.Maxs.X < HintPass.SquareSize);

            var guard = new ChangeGuard();
            pass.Declare(guard);
            Assert.Empty(guard.Undeclared(VmfDiff.Compare(before, map)));

            // Moving a hint replaces it rather than stacking a second.
            pass.Place(square, 592);
            Assert.Single(map.World.Children("solid"), s => s.Children("side").Any(x => x.Get("material") == HintPass.HintMaterial));

            pass.RevertAll();
            Assert.Empty(VmfDiff.Compare(before, map));
        }

        [Fact]
        public void AWallEndHintHasItsHintFaceOnThePlane()
        {
            var hint = new WallHint(1, 600, 516, 1008, 16, 400, []);
            var brush = VmfBrush.Build(WallHints.Brush(hint, 100))!;
            var face = brush.Faces.Single(f => f.Material == HintPass.HintMaterial);
            Assert.Equal(1, Math.Abs(face.Plane.Normal.Y), 3);
            Assert.Equal(600, face.Winding!.Centre.Y, 3);
            Assert.All(brush.Faces.Where(f => f != face), f => Assert.Equal(HintPass.SkipMaterial, f.Material));
            Assert.Equal(hint with { Points = [] }, WallHint.FromData(hint.Data) with { Points = [] });
        }

        [Fact]
        public void AWallEndingInTheCourtyardIsCarriedAcrossIt()
        {
            if (AreaportalTests.Tools() is not { } tools)
                return;
            using var space = new Workspace();
            // A wall from the south side to y = 600, leaving a gap to the north wall.
            string text = Courtyard();
            text = text.Insert(text.IndexOf("}\r\nentity", StringComparison.Ordinal), AreaportalTests.Box(500, 16, 16, 516, 600, 1008));
            string vmf = space.Write("maps/courtyard.vmf", text);
            var map = VmfDocument.Load(vmf);
            var compile = WorkCompile.Prepare(map, vmf, Path.Combine(space.Root, "c"), "courtyard");
            Assert.True(compile.RunVbsp(tools).Succeeded);
            var bsp = BspSpace.Load(compile.Bsp);
            var samples = SamplePoints.Find(bsp, SamplePoints.Reachable(map, SourceKit.Compile.PortalFile.Load(compile.Prt), bsp));

            var found = WallHints.Find(map, bsp, samples);
            // The wall's end at y = 600, carried west to x = 16 and east to x = 1008.
            Assert.Contains(found, h => h.Axis == 1 && Math.Abs(h.At - 600) < 1 && h.From < 40 && h.To > 495 && h.To < 505);
            Assert.Contains(found, h => h.Axis == 1 && Math.Abs(h.At - 600) < 1 && h.From > 510 && h.To > 990);
        }

        [Fact]
        public void HeightsStayBelowTheSky()
        {
            var square = new HintSquare(0, 0, 0, 600, [0]);
            Assert.Equal([192.0, 384.0], square.Heights());
        }

        [Fact]
        public void OnlyRoomsAPlayerCanReachAreMeasuredAndAHintChangesNoCollision()
        {
            if (AreaportalTests.Tools() is not { } tools)
                return;
            using var space = new Workspace();
            string vmf = space.Write("maps/courtyard.vmf", Courtyard());
            var map = VmfDocument.Load(vmf);

            var plain = WorkCompile.Prepare(map, vmf, Path.Combine(space.Root, "plain"), "courtyard");
            Assert.True(plain.RunVbsp(tools).Succeeded);
            Assert.False(plain.Leaked);
            var compiled = BspSpace.Load(plain.Bsp);

            var all = SamplePoints.Find(compiled);
            var reachable = SamplePoints.Find(compiled, SamplePoints.Reachable(map, SourceKit.Compile.PortalFile.Load(plain.Prt), compiled));
            Assert.Contains(all.Points, p => p.X > 2048);
            Assert.NotEmpty(reachable.Points);
            Assert.All(reachable.Points, p => Assert.True(p.X < 1024));

            var squares = HintPass.Squares(reachable, compiled, _ => false);
            var square = Assert.Single(squares);
            // Points stand within one 16-unit step of the floor.
            Assert.InRange(square.Floor, 16, 32);

            var pass = new HintPass(map);
            pass.Place(square, square.Heights().First());
            var hinted = WorkCompile.Prepare(map, vmf, Path.Combine(space.Root, "hinted"), "courtyard");
            Assert.True(hinted.RunVbsp(tools).Succeeded);
            Assert.False(hinted.Leaked);
            Assert.Equal(0, BspSpace.CollisionDifferences(
                BspSpace.Load(hinted.Bsp).CollisionShapes(~0x8000), compiled.CollisionShapes(~0x8000)));
            // The hint cut the courtyard: more leaves than before.
            Assert.True(BspVisibilityData.Load(hinted.Bsp).Leaves.Count > BspVisibilityData.Load(plain.Bsp).Leaves.Count);
        }

        /// <summary>
        /// VVIS failing on a set of hints loses only the hints at fault: one that fails on its own, and
        /// one of a pair that fails together.
        /// </summary>
        [Fact]
        public void AFailingCompileLeavesOutOnlyTheHintsAtFault()
        {
            var placed = Enumerable.Range(0, 40).ToHashSet();
            var ok = WorkCompile.Existing("ok.vmf");
            int compiles = 0;
            WorkCompile? Compile()
            {
                compiles++;
                return placed.Contains(7) || (placed.Contains(3) && placed.Contains(30)) ? null : ok;
            }
            var dropped = new System.Collections.Generic.List<int>();
            var result = Optimizer.CompileLeavingOutFailures(placed.ToList(), i => placed.Add(i), i => placed.Remove(i), Compile,
                i => i.ToString(), dropped, Optimizer.FailureCompiles(placed.Count));

            Assert.Same(ok, result);
            Assert.Contains(7, dropped);
            Assert.Single(dropped, d => d is 3 or 30);
            Assert.Equal(2, dropped.Count);
            Assert.Equal(38, placed.Count);
            Assert.True(compiles <= Optimizer.FailureCompiles(40), $"{compiles} compiles");
        }
    }
}
