using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SourceKit.Geometry;
using SourceKit.Vmf;
using Xunit;

namespace Viswright.Tests
{
    public class WindowPictureTests
    {
        [Theory]
        [InlineData(1, 16)]
        [InlineData(16, 16)]
        [InlineData(17, 32)]
        [InlineData(300, 512)]
        [InlineData(4000, 512)]
        public void ATextureEdgeIsAPowerOfTwoFrom16To512(double texels, int size) =>
            Assert.Equal(size, WindowPictures.PowerOfTwo(texels));

        [Fact]
        public void APlaceholderHoldsEveryMipLevelOfBlack()
        {
            var vtf = WindowPictures.PlaceholderVtf(64, 32);
            // 64x32, 32x16, 16x8, 8x4, 4x2, 2x1, 1x1: 128 + 32 + 8 + 2 + 1 + 1 + 1 blocks of 8 bytes
            Assert.Equal(80 + 173 * 8, vtf.Length);
            Assert.Equal(7, vtf[56]);
            Assert.Equal(13, BitConverter.ToInt32(vtf, 52));
            Assert.All(vtf.Skip(80), b => Assert.Equal(0, b));
        }

        [Fact]
        public void TheFrontOfAShelledWindowIsCutOutAndBakedThroughTheWall()
        {
            string game = Path.Combine(Path.GetTempPath(), "viswright-pictures-" + Guid.NewGuid().ToString("N"));
            try
            {
                var pictures = new WindowPictures("vw_Test", game);
                var window = new Window(0, new Vec3(248, 96, 160), new Vec3(264, 160, 192), Passage: false);
                var (name, axes) = pictures.Front(window, new Vec3(254, 96, 160), new Vec3(258, 160, 192), high: false);
                pictures.WriteBakeFile();

                Assert.Equal("viswright/vw_test/w_248_96_160_a_front", name);
                Assert.StartsWith("[0 1 0 ", axes.U);
                Assert.StartsWith("[0 0 -1 ", axes.V);
                string vmt = File.ReadAllText(Path.Combine(game, "materials", name + ".vmt"));
                Assert.Contains("\"UnlitGeneric\"", vmt);
                Assert.Contains("\"$alphatest\" \"1\"", vmt);
                // 64 by 32 units at a texel a unit
                var vtf = File.ReadAllBytes(Path.Combine(game, "materials", name + ".vtf"));
                Assert.Equal(64, BitConverter.ToUInt16(vtf, 16));
                Assert.Equal(32, BitConverter.ToUInt16(vtf, 18));

                using var bake = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, "materials", "viswright", "vw_test", "bake.json")));
                var face = Assert.Single(bake.RootElement.GetProperty("faces").EnumerateArray());
                Assert.Equal(name, face.GetProperty("material").GetString());
                Assert.True(face.GetProperty("square").GetBoolean());
                Assert.True(face.GetProperty("alpha").GetBoolean());
                // seen looking along +x: from the wall's near face to a unit past its far face
                var clip = face.GetProperty("clip").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                Assert.Equal([1, 0, 0, 248], clip);
                Assert.Equal(265, face.GetProperty("far").GetDouble());
            }
            finally
            {
                if (Directory.Exists(game))
                    Directory.Delete(game, recursive: true);
            }
        }

        [Fact]
        public void AFlatPictureIsOpaqueAndBakedFromTheWindowPlane()
        {
            string game = Path.Combine(Path.GetTempPath(), "viswright-pictures-" + Guid.NewGuid().ToString("N"));
            try
            {
                var pictures = new WindowPictures("vw_test", game);
                var window = new Window(1, new Vec3(96, 248, 160), new Vec3(160, 264, 192), Passage: false);
                var (name, _) = pictures.Face(window, new Vec3(96, 254, 160), new Vec3(160, 258, 192), high: true);
                pictures.WriteBakeFile();

                Assert.EndsWith("_b", name);
                Assert.DoesNotContain("$alphatest", File.ReadAllText(Path.Combine(game, "materials", name + ".vmt")));
                using var bake = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, "materials", "viswright", "vw_test", "bake.json")));
                var face = Assert.Single(bake.RootElement.GetProperty("faces").EnumerateArray());
                Assert.False(face.TryGetProperty("alpha", out _));
                Assert.False(face.TryGetProperty("square", out _));
                // seen from the high side, looking along -y, from the window's middle
                var clip = face.GetProperty("clip").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                Assert.Equal([0, -1, 0, -256], clip);
            }
            finally
            {
                if (Directory.Exists(game))
                    Directory.Delete(game, recursive: true);
            }
        }

        [Fact]
        public void AShutWindowShowsAShellOfTheRoomBehindIt()
        {
            var tools = AreaportalTests.Tools();
            if (tools == null)
                return;
            using var ws = new Workspace();
            string map = ws.Write(@"maps\vw_winpic.vmf", AreaportalTests.TwoRooms(window: true));
            string folder = Path.Combine(tools.GameFolder, "materials", "viswright", "vw_winpic");
            try
            {
                var result = Optimizer.Run(new OptimizeOptions { Input = map, Areaportals = true, WindowPictures = true, Tools = tools });

                var copy = VmfDocument.Load(result.CopyPath!);
                var standIn = Assert.Single(copy.Entities, e => e.Classname == "func_brush");
                var materials = standIn.Descendants().Select(n => n.Get("material")).Where(m => m != null).ToList();
                // one side gets the shell: the opening's front card, a back wall and the walls round it
                string? front = materials.FirstOrDefault(m => m!.EndsWith("_front"));
                Assert.NotNull(front);
                string side = front!.Split('_')[^2];
                Assert.Contains($"_{side}_back", string.Join(" ", materials));
                Assert.Contains(materials, m => m!.Contains($"_{side}_floor") || m.Contains($"_{side}_ceiling") || m.Contains($"_{side}_left"));
                Assert.All(materials.Where(m => m!.StartsWith("viswright/")), m => Assert.StartsWith("viswright/vw_winpic/w_", m));

                // every picture is listed for BAKE, the cut-out ones marked so
                using var bake = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "bake.json")));
                var faces = bake.RootElement.GetProperty("faces").EnumerateArray()
                    .ToDictionary(f => f.GetProperty("material").GetString()!, f => f);
                foreach (string m in materials.Where(m => m!.StartsWith("viswright/")).Distinct()!)
                {
                    Assert.True(faces.ContainsKey(m), m);
                    Assert.True(File.Exists(Path.Combine(tools.GameFolder, "materials", m + ".vtf")), m);
                }
                foreach (var (name, face) in faces)
                {
                    bool cut = name.Contains("_slice") || name.EndsWith("_front");
                    Assert.Equal(cut, face.TryGetProperty("alpha", out var alpha) && alpha.GetBoolean());
                    Assert.Equal(cut, face.TryGetProperty("far", out _));
                }
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
        }
    }
}
