using System;
using System.IO;
using System.Linq;
using System.Text;
using SourceKit.Vmf;
using Xunit;

namespace Viswright.Tests
{
    public class SamplePointTests
    {
        /// <summary>
        /// A room with a block on its floor whose top is a displacement raised 64 units, flat: terrain
        /// at z 96 over a brush whose top is at z 32.
        /// </summary>
        private static string TerrainRoom()
        {
            string Rows(string value, int count) => string.Concat(Enumerable.Range(0, 5).Select(r => $"\t\t\t\t\"row{r}\" \"{string.Join(" ", Enumerable.Repeat(value, count))}\"\r\n"));
            string Block(string name, string rows) => $"\t\t\t{name}\r\n\t\t\t{{\r\n{rows}\t\t\t}}\r\n";
            string dispinfo = "\t\t\tdispinfo\r\n\t\t\t{\r\n\t\t\t\"power\" \"2\"\r\n\t\t\t\"startposition\" \"[96 96 32]\"\r\n" +
                "\t\t\t\"flags\" \"0\"\r\n\t\t\t\"elevation\" \"0\"\r\n\t\t\t\"subdiv\" \"0\"\r\n" +
                Block("normals", Rows("0 0 1", 5)) + Block("distances", Rows("64", 5)) + Block("offsets", Rows("0 0 0", 5)) +
                Block("offset_normals", Rows("0 0 1", 5)) + Block("alphas", Rows("0", 5)) +
                Block("triangle_tags", string.Concat(Enumerable.Range(0, 4).Select(r => $"\t\t\t\t\"row{r}\" \"{string.Join(" ", Enumerable.Repeat("9", 8))}\"\r\n"))) +
                Block("allowed_verts", "\t\t\t\t\"10\" \"-1 -1 -1 -1 -1 -1 -1 -1 -1 -1\"\r\n") + "\t\t\t}\r\n";
            string block = AreaportalTests.Box(96, 96, 16, 416, 416, 32);
            // the first side is the top
            int top = block.IndexOf("\t\t}\r\n", StringComparison.Ordinal);
            block = block.Insert(top, dispinfo);

            var world = new StringBuilder();
            world.Append(AreaportalTests.Box(0, 0, 0, 512, 512, 16));
            world.Append(AreaportalTests.Box(0, 0, 496, 512, 512, 512));
            world.Append(AreaportalTests.Box(0, 0, 16, 16, 512, 496));
            world.Append(AreaportalTests.Box(496, 0, 16, 512, 512, 496));
            world.Append(AreaportalTests.Box(16, 0, 16, 496, 16, 496));
            world.Append(AreaportalTests.Box(16, 496, 16, 496, 512, 496));
            world.Append(block);
            string start = "entity\r\n{\r\n\t\"id\" \"51\"\r\n\t\"classname\" \"info_player_start\"\r\n\t\"origin\" \"32 32 24\"\r\n}\r\n";
            return "versioninfo\r\n{\r\n\t\"editorversion\" \"400\"\r\n}\r\nworld\r\n{\r\n\t\"id\" \"1\"\r\n\t\"classname\" \"worldspawn\"\r\n" +
                "\t\"skyname\" \"sky_day01_01\"\r\n" + world + "}\r\n" + start;
        }

        [Fact]
        public void APlayerStandsOnTerrainNotInTheSpaceUnderIt()
        {
            if (AreaportalTests.Tools() is not { } tools)
                return;
            using var ws = new Workspace();
            string vmf = ws.Write("maps/terrain.vmf", TerrainRoom());
            var map = VmfDocument.Load(vmf);
            var compile = WorkCompile.Prepare(map, vmf, Path.Combine(ws.Root, "c"), "terrain");
            Assert.True(compile.RunVbsp(tools).Succeeded);
            var space = BspSpace.Load(compile.Bsp);
            Assert.NotEmpty(space.Displacements);

            var points = SamplePoints.Find(space).Points;
            var overTerrain = points.Where(p => p.X > 100 && p.X < 412 && p.Y > 100 && p.Y < 412).ToList();
            Assert.NotEmpty(overTerrain);
            // eye height above the terrain, and nothing between the terrain and the brush it was made from
            Assert.All(overTerrain, p => Assert.InRange(p.Z, 156, 164));
            // the floor around the block is still stood on
            Assert.Contains(points, p => p.X < 96 && p.Z < 120);
        }
    }
}
