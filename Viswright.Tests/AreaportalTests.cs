using System;
using System.IO;
using System.Linq;
using System.Text;
using SourceKit.Geometry;
using SourceKit.Vmf;
using Xunit;

namespace Viswright.Tests
{
    public class AreaportalTests
    {
        private const string ToolsFolder = @"C:\Users\Catualus\Documents\Tools++";
        private const string GameFolder = @"C:\Program Files (x86)\Steam\steamapps\common\GarrysMod\garrysmod";

        internal static Toolchain? Tools() =>
            File.Exists(Path.Combine(ToolsFolder, "vbsp++.exe")) && Directory.Exists(GameFolder)
                ? new Toolchain(Path.Combine(ToolsFolder, "vbsp++.exe"), Path.Combine(ToolsFolder, "vvis++.exe"), GameFolder)
                : null;

        private static int nextId = 100;

        internal static string Box(double x0, double y0, double z0, double x1, double y1, double z1, string material = "DEV/DEV_MEASUREWALL01A")
        {
            string P(double x, double y, double z) => $"({x} {y} {z})";
            string[] planes =
            [
                $"{P(x0, y1, z1)} {P(x1, y1, z1)} {P(x1, y0, z1)}",
                $"{P(x0, y0, z0)} {P(x1, y0, z0)} {P(x1, y1, z0)}",
                $"{P(x0, y1, z1)} {P(x0, y0, z1)} {P(x0, y0, z0)}",
                $"{P(x1, y1, z0)} {P(x1, y0, z0)} {P(x1, y0, z1)}",
                $"{P(x1, y1, z1)} {P(x0, y1, z1)} {P(x0, y1, z0)}",
                $"{P(x1, y0, z0)} {P(x0, y0, z0)} {P(x0, y0, z1)}",
            ];
            string[] axes = ["[1 0 0 0] 0.25\" \"[0 -1 0 0] 0.25", "[1 0 0 0] 0.25\" \"[0 -1 0 0] 0.25",
                "[0 1 0 0] 0.25\" \"[0 0 -1 0] 0.25", "[0 1 0 0] 0.25\" \"[0 0 -1 0] 0.25",
                "[1 0 0 0] 0.25\" \"[0 0 -1 0] 0.25", "[1 0 0 0] 0.25\" \"[0 0 -1 0] 0.25"];
            var text = new StringBuilder($"\tsolid\r\n\t{{\r\n\t\t\"id\" \"{nextId++}\"\r\n");
            for (int i = 0; i < 6; i++)
            {
                var (u, v) = (axes[i].Split("\" \"")[0], axes[i].Split("\" \"")[1]);
                text.Append($"\t\tside\r\n\t\t{{\r\n\t\t\t\"id\" \"{nextId++}\"\r\n\t\t\t\"plane\" \"{planes[i]}\"\r\n" +
                    $"\t\t\t\"material\" \"{material}\"\r\n\t\t\t\"uaxis\" \"{u}\"\r\n\t\t\t\"vaxis\" \"{v}\"\r\n" +
                    "\t\t\t\"rotation\" \"0\"\r\n\t\t\t\"lightmapscale\" \"16\"\r\n\t\t\t\"smoothing_groups\" \"0\"\r\n\t\t}\r\n");
            }
            return text.Append("\t}\r\n").ToString();
        }

        /// <summary>
        /// Two rooms side by side, sharing a 16-unit wall at x = 248..264 with a 64 by 96 doorway, and a
        /// func_door filling the doorway. <paramref name="window"/> also cuts an open hole in the wall,
        /// which joins the rooms into one area whatever the door does.
        /// </summary>
        internal static string TwoRooms(bool window = false)
        {
            var world = new StringBuilder();
            world.Append(Box(0, 0, 0, 512, 256, 16));        // floor
            world.Append(Box(0, 0, 240, 512, 256, 256));     // ceiling
            world.Append(Box(0, 0, 16, 16, 256, 240));       // outer walls
            world.Append(Box(496, 0, 16, 512, 256, 240));
            world.Append(Box(16, 0, 16, 496, 16, 240));
            world.Append(Box(16, 240, 16, 496, 256, 240));
            // The shared wall, around the doorway at y 96..160, z 16..112.
            world.Append(Box(248, 16, 16, 264, 96, 240));
            world.Append(Box(248, 160, 16, 264, 240, 240));
            if (window)
            {
                world.Append(Box(248, 96, 112, 264, 160, 160));
                world.Append(Box(248, 96, 192, 264, 160, 240));  // a hole at z 160..192
            }
            else
                world.Append(Box(248, 96, 112, 264, 160, 240));

            string door = "entity\r\n{\r\n\t\"id\" \"50\"\r\n\t\"classname\" \"func_door\"\r\n\t\"spawnflags\" \"0\"\r\n" +
                Box(252, 96, 16, 260, 160, 112) + "}\r\n";
            string start = "entity\r\n{\r\n\t\"id\" \"51\"\r\n\t\"classname\" \"info_player_start\"\r\n\t\"origin\" \"128 128 32\"\r\n}\r\n";
            string light = "entity\r\n{\r\n\t\"id\" \"52\"\r\n\t\"classname\" \"light\"\r\n\t\"origin\" \"384 128 128\"\r\n}\r\n";
            return "versioninfo\r\n{\r\n\t\"editorversion\" \"400\"\r\n}\r\nworld\r\n{\r\n\t\"id\" \"1\"\r\n\t\"classname\" \"worldspawn\"\r\n" +
                "\t\"skyname\" \"sky_day01_01\"\r\n" + world + "}\r\n" + door + start + light;
        }

        [Fact]
        public void ADoorInAFramedDoorwayGetsAPortalThatClosesWithIt()
        {
            var tools = Tools();
            if (tools == null)
                return;
            using var ws = new Workspace();
            string map = ws.Write(@"maps\vw_doorway.vmf", TwoRooms());

            var result = Optimizer.Run(new OptimizeOptions { Input = map, Areaportals = true, Tools = tools });

            Assert.Equal(1, result.Portals);
            var copy = VmfDocument.Load(result.CopyPath!);
            var portal = Assert.Single(copy.Entities, e => e.Classname == "func_areaportal");
            var door = copy.Entities.Single(e => e.Classname == "func_door");
            Assert.Equal(door.Get("targetname"), portal.Get("target"));
            Assert.StartsWith(AreaportalPass.DoorNamePrefix, door.Get("targetname"));
            Assert.Equal("0", portal.Get("StartOpen"));

            // The portal fills the doorway, a unit into the frame all round, inside the wall.
            var brush = VmfBrush.Build(portal.Child("solid")!)!;
            Assert.Equal(new SourceKit.Geometry.Vec3(254, 95, 15), brush.Mins);
            Assert.Equal(new SourceKit.Geometry.Vec3(258, 161, 113), brush.Maxs);
        }

        [Fact]
        public void AWindowTheRoomsMeetThroughGetsAFadingPortalToo()
        {
            var tools = Tools();
            if (tools == null)
                return;
            using var ws = new Workspace();
            string map = ws.Write(@"maps\vw_window.vmf", TwoRooms(window: true));

            var result = Optimizer.Run(new OptimizeOptions { Input = map, Areaportals = true, Tools = tools });

            // Found before compiling: the window above the door joins the rooms, so it is closed too.
            Assert.Contains(ws.Output, l => l.Contains("divide only once 1 window(s)"));
            Assert.True(result.Portals == 1, string.Join(" | ", ws.Output.Where(l => l.Contains("areaportal"))));
            var copy = VmfDocument.Load(result.CopyPath!);
            Assert.Single(copy.Entities, e => e.Classname == "func_areaportal");
            var window = Assert.Single(copy.Entities, e => e.Classname == "func_areaportalwindow");
            Assert.Equal(AreaportalPass.FadeEnd.ToString(System.Globalization.CultureInfo.InvariantCulture), window.Get("FadeDist"));
            var standIn = Assert.Single(copy.Entities, e => e.Classname == "func_brush");
            Assert.Equal(window.Get("target"), standIn.Get("targetname"));
            Assert.Equal("1", standIn.Get("Solidity"));
            Assert.Equal(2, standIn.Descendants().Count(n => n.Get("material") == AreaportalPass.StandInMaterial));

            // The window's portal fills the hole at z 160..192, a unit into the frame, through the wall.
            var brush = VmfBrush.Build(window.Child("solid")!)!;
            Assert.Equal(new SourceKit.Geometry.Vec3(248, 95, 159), brush.Mins);
            Assert.Equal(new SourceKit.Geometry.Vec3(264, 161, 193), brush.Maxs);
        }

        [Fact]
        public void TheBudgetKeepsTheMostValuablePortals()
        {
            var map = VmfDocument.Parse(TwoRooms());
            var pass = new AreaportalPass(map);
            var door = map.Entities.Single(e => e.Classname == "func_door");
            var doorways = Enumerable.Range(0, 3).Select(i =>
                new Doorway(door, 0, 254, 258, 95 + i * 1000, 161 + i * 1000, 15, 113, Closes: true, StartOpen: false)).ToList();
            pass.Apply(doorways);
            var portals = pass.Added.Select(a => a.Portal).ToList();
            var ranked = new[]
            {
                new AreaportalSelection.Group([portals[0]], 50),
                new AreaportalSelection.Group([portals[1], portals[2]], 80),
            };

            // One slot: the single portal worth 50 beats the pair worth 40 each but needing two.
            var keep = AreaportalSelection.Choose(ranked, 1);
            Assert.Equal([portals[0]], keep.Doors);

            // Three slots: everything fits.
            keep = AreaportalSelection.Choose(ranked, 3);
            Assert.Equal(3, keep.Doors.Count);
        }

        [Fact]
        public void AModelsTrianglesAreReadFromItsVtx()
        {
            // Header, one body part, one model, one LOD, one mesh with two strip groups of 30 and 6 indices.
            var vtx = new byte[73 + 2 * 25];
            void Put(int at, int value) => BitConverter.GetBytes(value).CopyTo(vtx, at);
            Put(0, 7); Put(28, 1); Put(32, 36);
            Put(36, 1); Put(40, 8);
            Put(44, 1); Put(48, 8);
            Put(52, 1); Put(56, 12);
            Put(64, 2); Put(68, 9);
            Put(73 + 8, 30); Put(73 + 25 + 8, 6);

            Assert.Equal(12, GameContent.VtxTriangles(vtx));
            Assert.Equal(0, GameContent.VtxTriangles(vtx[..40]));
            vtx[0] = 6;
            Assert.Equal(0, GameContent.VtxTriangles(vtx));
        }

        [Fact]
        public void AWindowTwoDoorsNeedIsPaidForOnce()
        {
            var map = VmfDocument.Parse(TwoRooms());
            var pass = new AreaportalPass(map);
            var door = map.Entities.Single(e => e.Classname == "func_door");
            pass.Apply(Enumerable.Range(0, 3).Select(i =>
                new Doorway(door, 0, 254, 258, 95 + i * 1000, 161 + i * 1000, 15, 113, Closes: true, StartOpen: false)));
            var portals = pass.Added.Select(a => a.Portal).ToList();
            var window = new Window(0, new Vec3(254, 300, 40), new Vec3(258, 340, 90), Passage: false);
            var ranked = new[]
            {
                new AreaportalSelection.Group([portals[0]], [window], 60),
                new AreaportalSelection.Group([portals[1]], [window], 60),
                new AreaportalSelection.Group([portals[2]], 50),
            };

            // Three slots: the lone door, then one door with the window; the second door would need a fourth.
            var keep = AreaportalSelection.Choose(ranked, 3);
            Assert.Equal(2, keep.Doors.Count);
            Assert.Contains(portals[2], keep.Doors);
            Assert.Equal([window], keep.Windows);

            // Four slots: once the window is in, the second door costs one slot for 60.
            keep = AreaportalSelection.Choose(ranked, 4);
            Assert.Equal(3, keep.Doors.Count);
            Assert.Single(keep.Windows);
        }

        [Fact]
        public void DoorsSharingAnIdGetDifferentNames()
        {
            // Hammer ids are not always unique; two doors named alike would each close the other's portal.
            string twins = "entity\r\n{\r\n\t\"id\" \"50\"\r\n\t\"classname\" \"func_door\"\r\n" + Box(600, 96, 16, 608, 160, 112) + "}\r\n";
            string text = TwoRooms() + twins;
            var map = VmfDocument.Parse(text);
            var doors = map.Entities.Where(e => e.Classname == "func_door").ToList();
            var pass = new AreaportalPass(map);
            pass.Apply(doors.Select((d, i) => new Doorway(d, 0, 254 + i * 350, 258 + i * 350, 95, 161, 15, 113, Closes: true, StartOpen: false)));

            Assert.NotEqual(doors[0].Get("targetname"), doors[1].Get("targetname"));
            Assert.Equal(doors.Select(d => d.Get("targetname")), pass.Added.Select(a => a.Portal.Get("target")));

            var guard = new ChangeGuard();
            pass.Declare(guard);
            var undeclared = guard.Undeclared(VmfDiff.Compare(VmfDocument.Parse(text), map));
            Assert.True(undeclared.Count == 0, string.Join("; ", undeclared));
        }

        [Fact]
        public void RemovingPortalsTakesBackTheNamesGivenToDoors()
        {
            var map = VmfDocument.Parse(TwoRooms());
            var before = VmfDocument.Parse(map.ToString());
            var pass = new AreaportalPass(map);
            var door = map.Entities.Single(e => e.Classname == "func_door");
            pass.Apply([new Doorway(door, 0, 254, 258, 95, 161, 15, 113, Closes: true, StartOpen: false)]);
            Assert.NotNull(door.Get("targetname"));

            pass.RevertAll();

            Assert.Empty(VmfDiff.Compare(before, map));
        }
    }
}
