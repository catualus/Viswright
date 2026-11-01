using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SourceKit.Geometry;
using SourceKit.Vmf;
using Xunit;

namespace Viswright.Tests
{
    public class FadeAndSlimTests
    {
        /// <summary>
        /// A minimal MDL header: version 48, a hull from <paramref name="min"/> to <paramref name="max"/>,
        /// and the flags word; no texture table.
        /// </summary>
        private static byte[] Mdl(Vec3 min, Vec3 max, int flags, string? keyValues = null)
        {
            var bytes = new byte[400 + (keyValues?.Length ?? 0) + 1];
            if (keyValues != null)
            {
                Encoding.ASCII.GetBytes(keyValues).CopyTo(bytes, 400);
                BitConverter.GetBytes(400).CopyTo(bytes, 312);
                BitConverter.GetBytes(keyValues.Length + 1).CopyTo(bytes, 316);
            }
            BitConverter.GetBytes(0x54534449).CopyTo(bytes, 0);
            BitConverter.GetBytes(48).CopyTo(bytes, 4);
            int at = 104;
            foreach (double v in new[] { min.X, min.Y, min.Z, max.X, max.Y, max.Z })
            {
                BitConverter.GetBytes((float)v).CopyTo(bytes, at);
                at += 4;
            }
            BitConverter.GetBytes(flags).CopyTo(bytes, 152);
            return bytes;
        }

        private static GameContent Content(params (string Path, byte[] Bytes)[] files)
        {
            var map = files.ToDictionary(f => f.Path, f => f.Bytes, StringComparer.OrdinalIgnoreCase);
            return new GameContent(path => map.TryGetValue(path, out var b) ? b : null);
        }

        private static string Entity(int id, string classname, params (string Key, string Value)[] keys)
        {
            var text = new StringBuilder($"entity\r\n{{\r\n\t\"id\" \"{id}\"\r\n\t\"classname\" \"{classname}\"\r\n");
            foreach (var (k, v) in keys)
                text.Append($"\t\"{k}\" \"{v}\"\r\n");
            return text.Append("}\r\n").ToString();
        }

        private static VmfDocument Map(string entities) =>
            VmfDocument.Parse("world\r\n{\r\n\t\"id\" \"1\"\r\n\t\"classname\" \"worldspawn\"\r\n}\r\n" + entities);

        private const int StaticPropFlag = 0x10, NoForcedFade = 0x800;

        [Fact]
        public void APropWithNoFadeFadesWhereItShrinksToAFewPixels()
        {
            // A 32-unit cube: radius 27.7, so 6 pixels at 960 pixels of focal length is 8,868 units, to the nearest 64.
            var content = Content(("models/box.mdl", Mdl(new Vec3(-16, -16, -16), new Vec3(16, 16, 16), StaticPropFlag)));
            var map = Map(Entity(10, "prop_static", ("model", "models/box.mdl"), ("origin", "0 0 0"), ("fademaxdist", "0")));

            var pass = new PropFadePass(6);
            Assert.Equal(1, pass.Apply(map, content, _ => false));

            var prop = map.Entities.Single();
            Assert.Equal("8896", prop.Get("fademaxdist"));
            Assert.Equal("7562", prop.Get("fademindist"));
        }

        [Fact]
        public void AFadeBeyondTheMapIsNotWritten()
        {
            var content = Content(("models/box.mdl", Mdl(new Vec3(-16, -16, -16), new Vec3(16, 16, 16), StaticPropFlag)));
            var map = Map(Entity(10, "prop_static", ("model", "models/box.mdl"), ("origin", "0 0 0")));

            // 8,896 units is further than a map 4,096 units across can show.
            var pass = new PropFadePass(6, longestView: 4096);
            Assert.Equal(0, pass.Apply(map, content, _ => false));
            Assert.Equal(1, pass.Skipped["it would never fade within the map at this many pixels"]);
        }

        [Fact]
        public void PropsThatAlreadyFadeOrShouldNotAreLeftAlone()
        {
            var content = Content(
                ("models/box.mdl", Mdl(new Vec3(-16, -16, -16), new Vec3(16, 16, 16), StaticPropFlag)),
                ("models/keep.mdl", Mdl(new Vec3(-16, -16, -16), new Vec3(16, 16, 16), StaticPropFlag | NoForcedFade)),
                ("models/tower.mdl", Mdl(new Vec3(-64, -64, 0), new Vec3(64, 64, 600), StaticPropFlag)));
            var map = Map(
                Entity(10, "prop_static", ("model", "models/box.mdl"), ("origin", "0 0 0"), ("fademaxdist", "1500")) +
                Entity(11, "prop_static", ("model", "models/keep.mdl"), ("origin", "0 0 0")) +
                Entity(12, "prop_static", ("model", "models/tower.mdl"), ("origin", "0 0 0")) +
                Entity(13, "prop_static", ("model", "models/box.mdl"), ("origin", "9000 0 0")) +
                Entity(14, "prop_static", ("model", "models/missing.mdl"), ("origin", "0 0 0")));

            var pass = new PropFadePass(6);
            Assert.Equal(0, pass.Apply(map, content, p => p.X > 8000));

            Assert.Equal("1500", map.Entities.First().Get("fademaxdist"));
            Assert.Equal(1, pass.Skipped["the model is marked never to fade"]);
            Assert.Equal(1, pass.Skipped["too big to fade (a landmark)"]);
            Assert.Equal(1, pass.Skipped["in the 3D skybox"]);
            Assert.Equal(1, pass.Skipped["the model was not found"]);
        }

        [Fact]
        public void FadeChangesAreDeclaredAndRevertCleanly()
        {
            var content = Content(("models/box.mdl", Mdl(new Vec3(-16, -16, -16), new Vec3(16, 16, 16), StaticPropFlag)));
            string text = Entity(10, "prop_static", ("model", "models/box.mdl"), ("origin", "0 0 0"));
            var map = Map(text);
            var pass = new PropFadePass(6);
            pass.Apply(map, content, _ => false);

            var guard = new ChangeGuard();
            pass.Declare(guard);
            Assert.Empty(guard.Undeclared(VmfDiff.Compare(Map(text), map)));

            pass.RevertAll();
            Assert.Empty(VmfDiff.Compare(Map(text), map));
        }

        [Fact]
        public void APlainPropDynamicBecomesStaticButAnythingSetKeepsItAnEntity()
        {
            var content = Content(
                ("models/bench.mdl", Mdl(new Vec3(-32, -8, 0), new Vec3(32, 8, 32), StaticPropFlag)),
                ("models/anim.mdl", Mdl(new Vec3(-8, -8, 0), new Vec3(8, 8, 64), 0)));
            var map = Map(
                Entity(20, "prop_dynamic", ("model", "models/bench.mdl"), ("solid", "6"), ("rendercolor", "255 255 255")) +
                Entity(21, "prop_dynamic", ("model", "models/bench.mdl"), ("targetname", "lever")) +
                Entity(22, "prop_dynamic", ("model", "models/bench.mdl"), ("gmod_allowphysgun", "1")) +
                Entity(23, "prop_dynamic", ("model", "models/bench.mdl"), ("SetBodyGroup", "2")) +
                Entity(24, "prop_dynamic", ("model", "models/anim.mdl")) +
                "entity\r\n{\r\n\t\"id\" \"25\"\r\n\t\"classname\" \"prop_dynamic\"\r\n\t\"model\" \"models/bench.mdl\"\r\n\tconnections\r\n\t{\r\n\t\t\"OnUser1\" \"x,Kill,,0,-1\"\r\n\t}\r\n}\r\n");

            var pass = new SlimmingPass();
            Assert.Equal(1, pass.Apply(map, content, new LuaGuard()));

            Assert.Equal(["prop_static", "prop_dynamic", "prop_dynamic", "prop_dynamic", "prop_dynamic", "prop_dynamic"],
                map.Entities.Select(e => e.Classname));
            // As a prop_dynamic it cast no shadow into the lightmaps; as a prop_static it must not start to.
            Assert.Equal("1", map.Entities.First().Get("disableshadows"));
            Assert.Equal(1, pass.Skipped["the model is not compiled for static use"]);
        }

        [Fact]
        public void APhysicsPropModelStaysDynamicUnlessItAllowsStatic()
        {
            // VBSP deletes a prop_static whose model has prop_data without allowstatic, even if it is
            // compiled with $staticprop.
            var content = Content(
                ("models/crate.mdl", Mdl(new Vec3(-16, -16, 0), new Vec3(16, 16, 32), StaticPropFlag,
                    "\"prop_data\" { \"base\" \"Wooden.Small\" }")),
                ("models/table.mdl", Mdl(new Vec3(-16, -16, 0), new Vec3(16, 16, 32), StaticPropFlag,
                    "prop_data { \"base\" \"Wooden.Large\" \"allowstatic\" \"1\" }")));
            var map = Map(Entity(40, "prop_dynamic", ("model", "models/crate.mdl")) + Entity(41, "prop_dynamic", ("model", "models/table.mdl")));

            var pass = new SlimmingPass();
            pass.Apply(map, content, new LuaGuard());

            Assert.Equal(["prop_dynamic", "prop_static"], map.Entities.Select(e => e.Classname));
            Assert.Equal(1, pass.Skipped["the model is a physics prop (prop_data without allowstatic)"]);
        }

        [Fact]
        public void AFuncBrushBecomesDetailOnlyWhenItsLightingStaysTheSame()
        {
            var map = Map(
                Entity(30, "func_brush", ("vrad_brush_cast_shadows", "1"), ("Solidity", "2")) +
                Entity(31, "func_brush", ("Solidity", "2")) +
                Entity(32, "func_brush", ("vrad_brush_cast_shadows", "1"), ("Solidity", "1")));

            var pass = new SlimmingPass();
            pass.Apply(map, Content(), new LuaGuard());

            Assert.Equal(["func_detail", "func_brush", "func_brush"], map.Entities.Select(e => e.Classname));
            Assert.Equal(1, pass.LightingChanges);
        }

        [Fact]
        public void LuaThatLooksAClassUpProtectsIt()
        {
            string folder = Path.Combine(Path.GetTempPath(), "viswright-lua-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "lua", "autorun"));
            try
            {
                File.WriteAllText(Path.Combine(folder, "lua", "autorun", "doors.lua"),
                    "for _, e in ipairs(ents.FindByClass(\"func_brush\")) do end\nif ent:GetClass() == 'prop_dynamic' then end\n" +
                    // Only finds one entity's children, which nothing the passes change has.
                    "local kids = ents.FindByClassAndParent(\"prop_physics\", self)\n");
                var lua = LuaGuard.Scan([folder]);

                Assert.True(lua.Protects("func_brush", out _));
                Assert.True(lua.Protects("prop_dynamic", out _));
                Assert.False(lua.Protects("prop_physics", out _));

                var map = Map(Entity(30, "func_brush", ("vrad_brush_cast_shadows", "1")));
                var pass = new SlimmingPass();
                Assert.Equal(0, pass.Apply(map, Content(), lua));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public void AWildcardLookupProtectsEveryClassItCovers()
        {
            string folder = Path.Combine(Path.GetTempPath(), "viswright-lua-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "x.lua"), "ents.FindByClass(\"prop_*\")");
                var lua = LuaGuard.Scan([folder]);
                Assert.True(lua.Protects("prop_dynamic_override", out _));
                Assert.False(lua.Protects("func_brush", out _));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public void SlimmingIsDeclaredAndRevertsCleanly()
        {
            var content = Content(("models/bench.mdl", Mdl(new Vec3(-32, -8, 0), new Vec3(32, 8, 32), StaticPropFlag)));
            string text = Entity(20, "prop_dynamic", ("model", "models/bench.mdl")) + Entity(30, "func_brush", ("vrad_brush_cast_shadows", "1"));
            var map = Map(text);
            var pass = new SlimmingPass();
            pass.Apply(map, content, new LuaGuard());

            var guard = new ChangeGuard();
            pass.Declare(guard);
            Assert.Empty(guard.Undeclared(VmfDiff.Compare(Map(text), map)));

            Assert.Equal(1, pass.RevertModels(new HashSet<string> { "models/bench.mdl" }));
            Assert.Equal("prop_dynamic", map.Entities.First().Classname);
            pass.RevertAll();
            Assert.Empty(VmfDiff.Compare(Map(text), map));
        }
    }
}
