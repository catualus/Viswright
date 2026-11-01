using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SourceKit.Geometry;
using SourceKit.Vmf;
using Xunit;

namespace Viswright.Tests
{
    public class PassTests
    {
        private static Winding Square(double x0, double y0, double x1, double y1, double z = 0) =>
            new([new Vec3(x0, y0, z), new Vec3(x1, y0, z), new Vec3(x1, y1, z), new Vec3(x0, y1, z)]);

        private static readonly Vec3 Up = new(0, 0, 1);

        [Fact]
        public void AFaceCoveredByTwoHalvesIsCovered()
        {
            Assert.True(NodrawPass.IsCovered(Square(0, 0, 64, 64), Up, [Square(0, 0, 32, 64), Square(32, 0, 64, 64)]));
        }

        [Fact]
        public void AFaceWithAGapIsNot()
        {
            // An 8-unit strip in the middle is open.
            Assert.False(NodrawPass.IsCovered(Square(0, 0, 64, 64), Up, [Square(0, 0, 28, 64), Square(36, 0, 64, 64)]));
        }

        [Fact]
        public void ALargerCoverCoversASmallerFace()
        {
            Assert.True(NodrawPass.IsCovered(Square(16, 16, 32, 32), Up, [Square(0, 0, 64, 64)]));
            Assert.False(NodrawPass.IsCovered(Square(0, 0, 64, 64), Up, [Square(16, 16, 32, 32)]));
        }

        private static Dictionary<string, string> Files(params (string Path, string Text)[] files) =>
            files.ToDictionary(f => f.Path, f => f.Text);

        private static MaterialOpacity Opacity(Dictionary<string, string> files) =>
            new(path => files.TryGetValue(path, out var text) ? Encoding.UTF8.GetBytes(text) : null);

        [Fact]
        public void OpacityComesFromTheVmt()
        {
            var opacity = Opacity(Files(
                ("materials/brick/wall.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"brick/wall\"\n}\n"),
                ("materials/glass/window.vmt", "LightmappedGeneric\n{\n$basetexture glass/window\n$translucent 1\n}\n"),
                ("materials/fence/chain.vmt", "\"LightmappedGeneric\" { \"$alphatest\" \"1\" }"),
                ("materials/brick/wall_patched.vmt", "patch\n{\ninclude \"materials/brick/wall.vmt\"\ninsert\n{\n$surfaceprop brick\n}\n}\n"),
                ("materials/water/river.vmt", "Water\n{\n}\n"),
                ("materials/brick/faded.vmt", "LightmappedGeneric\n{\n$alpha 0.5\n}\n")));

            Assert.True(opacity.IsOpaque("BRICK/WALL"));
            Assert.True(opacity.IsOpaque("brick/wall_patched"));
            Assert.False(opacity.IsOpaque("glass/window"));
            Assert.False(opacity.IsOpaque("fence/chain"));
            Assert.False(opacity.IsOpaque("water/river"));
            Assert.False(opacity.IsOpaque("brick/faded"));
            Assert.False(opacity.IsOpaque("missing/material"));
            Assert.False(opacity.IsOpaque("TOOLS/TOOLSNODRAW"));
        }

        private static string Box(int id, int x0, int y0, int z0, int x1, int y1, int z1, string material = "BRICK/WALL")
        {
            string[] planes =
            [
                $"({x0} {y1} {z1}) ({x1} {y1} {z1}) ({x1} {y0} {z1})",
                $"({x0} {y0} {z0}) ({x1} {y0} {z0}) ({x1} {y1} {z0})",
                $"({x0} {y1} {z1}) ({x0} {y0} {z1}) ({x0} {y0} {z0})",
                $"({x1} {y1} {z0}) ({x1} {y0} {z0}) ({x1} {y0} {z1})",
                $"({x1} {y1} {z1}) ({x0} {y1} {z1}) ({x0} {y1} {z0})",
                $"({x1} {y0} {z0}) ({x0} {y0} {z0}) ({x0} {y0} {z1})",
            ];
            var text = new StringBuilder($"\tsolid\r\n\t{{\r\n\t\t\"id\" \"{id}\"\r\n");
            for (int i = 0; i < 6; i++)
                text.Append($"\t\tside\r\n\t\t{{\r\n\t\t\t\"id\" \"{id * 10 + i}\"\r\n\t\t\t\"plane\" \"{planes[i]}\"\r\n\t\t\t\"material\" \"{material}\"\r\n\t\t}}\r\n");
            return text.Append("\t}\r\n").ToString();
        }

        private static VmfDocument Map(string world, string entities = "") =>
            VmfDocument.Parse("world\r\n{\r\n\t\"id\" \"1\"\r\n\t\"classname\" \"worldspawn\"\r\n" + world + "}\r\n" + entities);

        [Fact]
        public void AWorldFaceAgainstADetailBrushBecomesNodraw()
        {
            // A wall's top face, with a detail slab of the same footprint sitting on it.
            var map = Map(Box(2, 0, 0, 0, 64, 64, 64),
                "entity\r\n{\r\n\t\"id\" \"5\"\r\n\t\"classname\" \"func_detail\"\r\n" + Box(3, 0, 0, 64, 64, 64, 72) + "}\r\n");
            var opacity = Opacity(Files(("materials/brick/wall.vmt", "LightmappedGeneric\n{\n}\n")));

            var pass = new NodrawPass();
            pass.Apply(map, opacity);

            // The wall's top and the slab's bottom hide each other.
            Assert.Equal(new long?[] { 20, 31 }, pass.Changed.Select(c => c.Side.Id).OrderBy(i => i));
        }

        [Fact]
        public void TwoWorldBrushesAreLeftToVbsp()
        {
            var map = Map(Box(2, 0, 0, 0, 64, 64, 64) + Box(3, 0, 0, 64, 64, 64, 72));
            var opacity = Opacity(Files(("materials/brick/wall.vmt", "LightmappedGeneric\n{\n}\n")));

            var pass = new NodrawPass();
            Assert.Equal(0, pass.Apply(map, opacity));
        }

        [Fact]
        public void GlassCoversNothing()
        {
            var map = Map(Box(2, 0, 0, 0, 64, 64, 64),
                "entity\r\n{\r\n\t\"id\" \"5\"\r\n\t\"classname\" \"func_detail\"\r\n" + Box(3, 0, 0, 64, 64, 64, 72, "GLASS/WINDOW") + "}\r\n");
            var opacity = Opacity(Files(
                ("materials/brick/wall.vmt", "LightmappedGeneric\n{\n}\n"),
                ("materials/glass/window.vmt", "LightmappedGeneric\n{\n$translucent 1\n}\n")));

            var pass = new NodrawPass();
            pass.Apply(map, opacity);

            // The glass's own bottom face is still hidden by the opaque wall, but nothing behind the glass is.
            Assert.DoesNotContain(pass.Changed, c => c.Side.Id == 20);
        }

        [Fact]
        public void DetailCandidatesSkipPortalFramesAndIgnoredBrushes()
        {
            string ignored = Box(4, 500, 0, 0, 532, 32, 32).Replace("\t}\r\n", "\t\teditor\r\n\t\t{\r\n\t\t\t\"visgroupid\" \"7\"\r\n\t\t}\r\n\t}\r\n");
            var map = VmfDocument.Parse(
                "visgroups\r\n{\r\n\tvisgroup\r\n\t{\r\n\t\t\"name\" \"Viswright_Ignore\"\r\n\t\t\"visgroupid\" \"7\"\r\n\t}\r\n}\r\n" +
                "world\r\n{\r\n\t\"id\" \"1\"\r\n\t\"classname\" \"worldspawn\"\r\n" +
                Box(2, 0, 0, 0, 32, 32, 32) +          // beside the areaportal: a frame
                Box(3, 200, 0, 0, 232, 32, 32) +       // a free-standing small brush: a candidate
                ignored +
                Box(6, 300, 0, 0, 332, 32, 32, "NATURE/WATER_CANAL") +
                "}\r\n" +
                "entity\r\n{\r\n\t\"id\" \"9\"\r\n\t\"classname\" \"func_areaportal\"\r\n" + Box(8, 32, 0, 0, 48, 32, 32, "TOOLS/TOOLSAREAPORTAL") + "}\r\n");

            var candidates = DetailPass.Candidates(map);

            Assert.Equal(new long?[] { 3 }, candidates.Select(c => c.Solid.Id));
        }

        [Fact]
        public void NoCandidatesAddsNoEntity()
        {
            var map = Map("");
            var pass = new DetailPass(map);
            pass.Apply([]);
            Assert.Empty(map.Entities);
        }

        [Fact]
        public void RevertingPutsTheMapBack()
        {
            var map = Map(Box(2, 0, 0, 0, 32, 32, 32) + Box(3, 100, 0, 0, 132, 32, 32));
            var before = VmfDocument.Parse(map.ToString());
            var pass = new DetailPass(map);
            pass.Apply(DetailPass.Candidates(map));
            Assert.Equal(2, pass.Moved.Count);

            pass.RevertAll();

            Assert.Empty(VmfDiff.Compare(before, map));
        }
    }
}
