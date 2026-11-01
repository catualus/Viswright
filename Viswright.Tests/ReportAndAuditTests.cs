using System;
using System.IO;
using System.Linq;
using System.Text;
using SourceKit.Vmf;
using Xunit;

namespace Viswright.Tests
{
    public class ReportAndAuditTests
    {
        [Fact]
        public void EachClusterCountsTheTrianglesItCanSeeOnce()
        {
            using var ws = new Workspace();
            string bsp = Path.Combine(ws.Root, "maps", "tiny.bsp");
            TinyBsp.Write(bsp);

            var report = VisReport.Build(BspVisibilityData.Load(bsp));

            // Faces of 2, 1 and 4 triangles. Cluster 0 holds face 0, cluster 1 faces 0 and 1, cluster 2
            // face 2; face 0 is in two clusters and counts once.
            Assert.Equal(7, report.WorldTriangles);
            // Cluster 0 sees 0 and 1 (2+1), 1 sees all (7), 2 sees 1 and 2 (2+1+4).
            Assert.Equal(7, report.MaxTriangles);
            Assert.Equal((3 + 7 + 7) / 3.0, report.MeanTriangles, 6);
            Assert.Equal(2 + 3 + 2, report.TotalVisible);
            Assert.Equal(2, report.AverageVisible);
            Assert.Equal([0, 1, 2], report.Worst.Select(c => c.Cluster).Take(3).OrderBy(c => c));
        }

        [Fact]
        public void AReportOnAMapWithoutVisibilityIsRefused()
        {
            using var ws = new Workspace();
            string bsp = Path.Combine(ws.Root, "maps", "novis.bsp");
            TinyBsp.Write(bsp, visibility: []);

            Assert.Equal(1, Program.Main(["report", bsp]));
            Assert.Contains(ws.Output, l => l.Contains("run VVIS"));
        }

        [Fact]
        public void AnAreaportalNumberedPastWhatGmodLoadsFailsTheReport()
        {
            using var ws = new Workspace();
            string bsp = Path.Combine(ws.Root, "maps", "portals.bsp");
            TinyBsp.Write(bsp, entities: "{\n\"classname\" \"func_areaportal\"\n\"portalnumber\" \"192\"\n}\n");

            Assert.Equal(1, Program.Main(["report", bsp]));
            Assert.Contains(ws.Output, l => l.StartsWith("error: ") && l.Contains("192"));
        }

        private static VmfDocument Map(string entities) =>
            VmfDocument.Parse(Workspace.Room.Replace("func_instance", "info_player_start") + entities);

        private static string Entity(int id, string classname, params (string Key, string Value)[] keys)
        {
            var text = new StringBuilder($"entity\r\n{{\r\n\t\"id\" \"{id}\"\r\n\t\"classname\" \"{classname}\"\r\n");
            foreach (var (k, v) in keys)
                text.Append($"\t\"{k}\" \"{v}\"\r\n");
            return text.Append("}\r\n").ToString();
        }

        [Fact]
        public void TheAuditFindsDoorsWithoutPortalsAndPropsWithoutFades()
        {
            var map = Map(
                Entity(30, "prop_door_rotating", ("targetname", "door_a")) +
                Entity(31, "prop_door_rotating", ("targetname", "door_b")) +
                Entity(32, "func_door") +
                Entity(33, "func_areaportal", ("target", "door_a")) +
                Entity(34, "prop_static", ("fademaxdist", "0")) +
                Entity(35, "prop_static", ("fademaxdist", "1500")));

            var findings = Audit.Run(map);

            Assert.Equal(2, findings.Single(f => f.Code == "door-without-portal").Count);
            Assert.Equal(1, findings.Single(f => f.Code == "prop-without-fade").Count);
            var budget = findings.Single(f => f.Code == "portal-budget");
            Assert.Equal(FindingLevel.Info, budget.Level);
            Assert.Contains("190 left", budget.Message);
        }

        [Fact]
        public void MoreAreaportalsThanGmodLoadsIsAnError()
        {
            string portals = string.Concat(Enumerable.Range(0, 192).Select(i => Entity(100 + i, i % 2 == 0 ? "func_areaportal" : "func_areaportalwindow")));

            var budget = Audit.Run(Map(portals)).Single(f => f.Code == "portal-budget");

            Assert.Equal(FindingLevel.Error, budget.Level);
        }

        [Fact]
        public void OptimizeStopsOnAMapThatWouldNotLoad()
        {
            using var ws = new Workspace();
            string portals = string.Concat(Enumerable.Range(0, 192).Select(i => Entity(100 + i, "func_areaportal")));
            string map = ws.Write(@"maps\rp_test.vmf", Workspace.Room.Replace("func_instance", "info_player_start") + portals);

            Assert.Equal(1, Program.Main(["optimize", map]));
            Assert.DoesNotContain(ws.Output, l => l.StartsWith("COMPILE_PAL_SET", StringComparison.Ordinal));
        }

        [Fact]
        public void TheMapCardCountsOnlyPortalsVbspCompiles()
        {
            using var ws = new Workspace();
            string hidden = "hidden\r\n{\r\n" + Entity(50, "func_areaportal") + "}\r\n";
            string map = ws.Write(@"maps\rp_test.vmf", Workspace.Room.Replace("func_instance", "info_player_start")
                + Entity(51, "func_areaportal") + Entity(52, "func_areaportalwindow") + hidden);

            Assert.Equal(2, Program.CountTopLevelPortals(map));
            Assert.Equal(2, Audit.Run(VmfDocument.Load(map)).Single(f => f.Code == "portal-budget").Count);
        }

        [Fact]
        public void NamedLightsCountAsIdleOnlyWhenNothingFiresAtThem()
        {
            var map = Map(
                Entity(40, "light", ("targetname", "lamp_switched")) +
                Entity(41, "light", ("targetname", "lamp_idle")) +
                "entity\r\n{\r\n\t\"id\" \"42\"\r\n\t\"classname\" \"func_button\"\r\n\tconnections\r\n\t{\r\n" +
                "\t\t\"OnPressed\" \"lamp_switched\u001bToggle\u001b\u001b0\u001b-1\"\r\n\t}\r\n}\r\n");

            Assert.Equal(1, Audit.Run(map).Single(f => f.Code == "untargeted-named-light").Count);
        }

        [Fact]
        public void SmallAndComplexWorldBrushesLookLikeDetail()
        {
            // The fixture's room brush is a 64-unit cube.
            var map = Map("");
            Assert.True(Audit.LooksLikeDetail(map.World.Child("solid")!));

            var nodraw = Map("");
            foreach (var side in nodraw.World.Child("solid")!.Children("side"))
                side.Set("material", "TOOLS/TOOLSNODRAW");
            Assert.False(Audit.LooksLikeDetail(nodraw.World.Child("solid")!));
        }
    }
}
