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
        public void LuaThatFindsMapEntitiesByNumberIsNoted()
        {
            string folder = Path.Combine(Path.GetTempPath(), "viswright-lua-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                // DarkRP's doors, a keypad addon's links, and a comparison with a saved ID all find
                // entities by number.
                File.WriteAllText(Path.Combine(folder, "a_doors.lua"),
                    "function meta:doorIndex()\n    return self:CreatedByMap() and self:MapCreationID() or nil\nend\n");
                File.WriteAllText(Path.Combine(folder, "b_keypads.lua"), "local ent = ents.GetMapCreatedEntity(num)\n");
                File.WriteAllText(Path.Combine(folder, "c_compare.lua"), "if saved.MapCreationID == ent:MapCreationID() then end\n");
                // The game's own duplicator only copies the ID into what it saves, for addons to read.
                File.WriteAllText(Path.Combine(folder, "d_duplicator.lua"),
                    "if ( ent:CreatedByMap() ) then\n\tdata.MapCreationID = ent:MapCreationID()\nend\n");
                File.WriteAllText(Path.Combine(folder, "e_other.lua"), "print(ent:EntIndex())\n");

                var lua = LuaGuard.Scan([folder]);

                Assert.True(lua.UsesMapIds);
                Assert.Equal(["a_doors.lua", "b_keypads.lua", "c_compare.lua"], lua.MapIdFiles.Select(f => Path.GetFileName(f)).Order());
                Assert.Empty(lua.Classes);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
            Assert.False(new LuaGuard().UsesMapIds);
        }

        /// <summary>
        /// Four candidates around a door: the first comes before the door, the rest only before classes
        /// VBSP leaves out of the entity list (a prop_static, an unnamed overlay, a cubemap).
        /// </summary>
        private static string AroundADoor() =>
            Entity(20, "prop_dynamic", ("model", "models/bench.mdl")) +
            Entity(21, "prop_door_rotating", ("model", "models/door.mdl")) +
            Entity(22, "prop_dynamic", ("model", "models/bench.mdl")) +
            Entity(23, "prop_static", ("model", "models/bench.mdl")) +
            Entity(24, "info_overlay", ("material", "decals/x")) +
            Entity(25, "prop_dynamic", ("model", "models/bench.mdl")) +
            Entity(26, "func_brush", ("vrad_brush_cast_shadows", "1")) +
            Entity(27, "env_cubemap");

        private static LuaGuard MapIdLua() => new() { MapIdFiles = { "sv_doors.lua" } };

        [Fact]
        public void WithLuaUsingMapCreationIdOnlyEntitiesNothingStaysAfterAreConverted()
        {
            var content = Content(("models/bench.mdl", Mdl(new Vec3(-32, -8, 0), new Vec3(32, 8, 32), StaticPropFlag)));

            // Without it, every candidate goes.
            var free = Map(AroundADoor());
            Assert.Equal(4, new SlimmingPass().Apply(free, content, new LuaGuard()));

            var map = Map(AroundADoor());
            var pass = new SlimmingPass();
            Assert.Equal(3, pass.Apply(map, content, MapIdLua()));

            Assert.True(pass.KeepsMapIds);
            Assert.Equal(1, pass.KeptForMapIds);
            Assert.Equal(["prop_dynamic", "prop_door_rotating", "prop_static", "prop_static", "info_overlay", "prop_static", "func_detail", "env_cubemap"],
                map.Entities.Select(e => e.Classname));
            // The one kept is in the review, saying why.
            var (kept, why) = Assert.Single(pass.SkippedEntities);
            Assert.Equal(20L, kept.Id);
            Assert.Contains("MapCreationID", why);
            Assert.Contains("sv_doors.lua", why);
        }

        [Fact]
        public void ANamedOverlayOrAnInstanceStaysAfterTheCandidates()
        {
            var content = Content(("models/bench.mdl", Mdl(new Vec3(-32, -8, 0), new Vec3(32, 8, 32), StaticPropFlag)));

            // VBSP keeps a named overlay as info_overlay_accessor.
            var named = Map(Entity(20, "prop_dynamic", ("model", "models/bench.mdl")) + Entity(21, "info_overlay", ("targetname", "sign")));
            Assert.Equal(0, new SlimmingPass().Apply(named, content, MapIdLua()));

            // VBSP adds an instance's entities after all of the map's own, wherever the func_instance is.
            var instanced = Map(Entity(19, "func_instance", ("file", "instances/door.vmf")) + Entity(20, "prop_dynamic", ("model", "models/bench.mdl")));
            Assert.Equal(0, new SlimmingPass().Apply(instanced, content, MapIdLua()));
            Assert.Equal(1, new SlimmingPass().Apply(Map(Entity(19, "func_instance", ("file", "instances/door.vmf")) + Entity(20, "prop_dynamic", ("model", "models/bench.mdl"))),
                content, new LuaGuard()));
        }

        [Fact]
        public void EntitiesThePassesAddAfterTheMapsOwnDoNotHoldItsEndBack()
        {
            var content = Content(("models/bench.mdl", Mdl(new Vec3(-32, -8, 0), new Vec3(32, 8, 32), StaticPropFlag)));
            VmfDocument WithPortal(out HashSet<VmfNode> own)
            {
                var doc = Map(Entity(20, "prop_door_rotating") + Entity(21, "prop_dynamic", ("model", "models/bench.mdl")));
                own = doc.Entities.ToHashSet();
                // The areaportal pass adds its portals at the end of the VMF, as it does.
                var portal = new VmfNode("entity");
                doc.Root.Add(portal);
                portal.Set("id", "900");
                portal.Set("classname", "func_areaportal");
                return doc;
            }

            var map = WithPortal(out var own);
            Assert.Equal(1, new SlimmingPass().Apply(map, content, MapIdLua(), own));
            Assert.Equal("prop_static", map.Entities.ElementAt(1).Classname);

            // Counted as one of the map's own, the portal would hold the prop back.
            Assert.Equal(0, new SlimmingPass().Apply(WithPortal(out _), content, MapIdLua()));
        }

        [Fact]
        public void PuttingAnEntityBackPutsBackTheOnesBeforeIt()
        {
            var content = Content(("models/bench.mdl", Mdl(new Vec3(-32, -8, 0), new Vec3(32, 8, 32), StaticPropFlag)));
            var map = Map(AroundADoor());
            var pass = new SlimmingPass();
            pass.Apply(map, content, MapIdLua());

            // Denied in the review: 25 stays an entity, so 22 before it would move it.
            Assert.Equal(1, pass.RevertWhere(e => e.Id == 25));
            Assert.Equal([26L], pass.Converted.Select(c => c.Entity.Id!.Value));
            Assert.Equal(2, pass.KeptForMapIds);

            // VBSP refusing a model works the same way: the prop stays, and the func_brush before it with it.
            var again = Map(Entity(21, "prop_door_rotating") + Entity(30, "func_brush", ("vrad_brush_cast_shadows", "1"))
                + Entity(31, "prop_dynamic", ("model", "models/bench.mdl")));
            var other = new SlimmingPass();
            Assert.Equal(2, other.Apply(again, content, MapIdLua()));
            Assert.Equal(1, other.RevertModels(new HashSet<string> { "models/bench.mdl" }));
            Assert.Empty(other.Converted);
            Assert.Equal(1, other.KeptForMapIds);
            Assert.Equal(["prop_door_rotating", "func_brush", "prop_dynamic"], again.Entities.Select(e => e.Classname));
        }

        [Fact]
        public void TheCompiledEntityListShowsWhetherAnyMapEntityMoved()
        {
            var before = SlimmingPass.EntityListOf(
                "{\n\"world_maxs\" \"64 64 64\"\n\"classname\" \"worldspawn\"\n\"hammerid\" \"1\"\n}\n" +
                "{\n\"classname\" \"prop_door_rotating\"\n\"hammerid\" \"21\"\n\"message\" \"a {brace} in a value\"\n}\n" +
                "{\n\"classname\" \"prop_dynamic\"\n\"hammerid\" \"22\"\n}\n" +
                "{\n\"hammerid\" \"25\"\n\"classname\" \"prop_dynamic\"\n}\n");
            Assert.Equal([("1", "worldspawn"), ("21", "prop_door_rotating"), ("22", "prop_dynamic"), ("25", "prop_dynamic")], before);

            // The end of the list converted, a portal added after: nothing moved.
            var after = new List<(string?, string?)> { ("1", "worldspawn"), ("21", "prop_door_rotating"), ("900", "func_areaportal") };
            Assert.Null(SlimmingPass.Renumbered(before, after, new HashSet<string> { "22", "25" }));

            // 22 converted with 25 after it: 25 moves down a place.
            var moved = SlimmingPass.Renumbered(before, [("1", "worldspawn"), ("21", "prop_door_rotating"), ("25", "prop_dynamic")], new HashSet<string> { "22" });
            Assert.NotNull(moved);
            Assert.Contains("prop_dynamic 25", moved);

            // A list that could not be read proves nothing.
            Assert.NotNull(SlimmingPass.Renumbered([], after, new HashSet<string>()));
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
