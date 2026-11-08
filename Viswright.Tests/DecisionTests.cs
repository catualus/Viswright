using System.Collections.Generic;
using System.IO;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;
using Viswright.Review;
using Xunit;

namespace Viswright.Tests
{
    public class DecisionTests
    {
        private static ReviewItem Item(string id, string pass, ItemStatus status, Dictionary<string, double>? data = null) =>
            new(id, pass, status, "because", new Vec3(0, 0, 0), new Vec3(8, 8, 8)) { Data = data ?? new() };

        [Fact]
        public void ARunsItemsComeInWaitingAndTheMappersDecisionsStand()
        {
            using var space = new Workspace();
            string path = Path.Combine(space.Root, "maps", "x.viswright.json");
            var file = DecisionFile.Load(path);
            file.Merge([
                Item("areaportal:a", "areaportals", ItemStatus.Applied, new() { ["axis"] = 0 }),
                Item("hint:1_2", "hints", ItemStatus.Rejected, new() { ["height"] = 300 }),
                Item("detail:x", "detail", ItemStatus.Rejected),
            ]);
            Assert.Equal(["areaportal:a", "hint:1_2"], file.Items.Select(i => i.Id));
            Assert.Equal(1, file.Pending);
            Assert.Equal(Decision.Deny, file["hint:1_2"]!.Decision);
            file.Set("hint:1_2", i => i with { Decision = Decision.Accept });
            file.ReviewFolder = Path.Combine(space.Root, "review");
            file.Save();

            // The next review finds the portal again and not the hint: the hint is kept, marked stale.
            var again = DecisionFile.Load(path);
            Assert.Equal(Path.Combine(space.Root, "review"), again.ReviewFolder);
            Assert.True(again.Accepted("hint:1_2"));
            again.Merge([Item("areaportal:a", "areaportals", ItemStatus.Applied, new() { ["axis"] = 0 })]);
            Assert.True(again["hint:1_2"]!.Stale);
            Assert.False(again.Accepted("hint:1_2"));
            Assert.Equal(1, again.AcceptPending());
            Assert.True(again.Accepted("areaportal:a"));
            Assert.Equal(0, again.Pending);
        }

        [Fact]
        public void DeletingAChangeInHammerDeniesItAndAMarkerInAcceptAcceptsIt()
        {
            using var space = new Workspace();
            string review = Path.Combine(space.Root, "review");
            Directory.CreateDirectory(review);
            var file = DecisionFile.Load(Path.Combine(space.Root, "maps", "x.viswright.json"));
            file.Merge([
                Item("areaportal:kept", "areaportals", ItemStatus.Applied),
                Item("areaportal:gone", "areaportals", ItemStatus.Applied),
                Item("hint:0_0", "hints", ItemStatus.Rejected, new() { ["height"] = 300 }),
            ]);
            file.Save();

            File.WriteAllText(Path.Combine(review, "items.json"), """
                { "items": [
                  { "id": "areaportal:kept", "pass": "areaportals", "nodes": [20] },
                  { "id": "areaportal:gone", "pass": "areaportals", "nodes": [30] },
                  { "id": "hint:0_0", "pass": "hints", "marker": "vw_3_hints" }
                ] }
                """);
            string vmf = Path.Combine(review, "x_review.vmf");
            File.WriteAllText(vmf, "x");
            File.WriteAllText(Path.Combine(review, RoundTrip.StampFile), File.GetLastWriteTimeUtc(vmf).Ticks.ToString());

            // The mapper deletes portal 30 and moves the hint marker into Viswright/Accept, then saves.
            File.WriteAllText(vmf, """
                visgroups
                {
                	visgroup
                	{
                		"name" "Viswright"
                		"visgroupid" "5"
                		visgroup
                		{
                			"name" "Accept"
                			"visgroupid" "11"
                		}
                	}
                }
                world
                {
                	"id" "1"
                }
                entity
                {
                	"id" "20"
                	"classname" "func_areaportal"
                }
                hidden
                {
                	entity
                	{
                		"id" "40"
                		"classname" "func_illusionary"
                		"targetname" "vw_3_hints"
                		editor
                		{
                			"visgroupid" "11"
                		}
                	}
                }
                """);
            File.SetLastWriteTimeUtc(vmf, File.GetLastWriteTimeUtc(vmf).AddMinutes(1));

            Assert.Equal(2, RoundTrip.Apply(file, review));
            Assert.Equal(Decision.Pending, file["areaportal:kept"]!.Decision);
            Assert.Equal(Decision.Deny, file["areaportal:gone"]!.Decision);
            Assert.Equal(Decision.Accept, file["hint:0_0"]!.Decision);

            // Read once: the same edits are not taken again.
            Assert.Equal(0, RoundTrip.Apply(file, review));
        }

        /// <summary>A review folder whose VMF the mapper has saved as <paramref name="edited"/> since it was written.</summary>
        private static string EditedReview(Workspace space, string itemsJson, string edited)
        {
            string review = Path.Combine(space.Root, "review");
            Directory.CreateDirectory(review);
            File.WriteAllText(Path.Combine(review, "items.json"), itemsJson);
            string vmf = Path.Combine(review, "x_review.vmf");
            File.WriteAllText(vmf, "x");
            File.WriteAllText(Path.Combine(review, RoundTrip.StampFile), File.GetLastWriteTimeUtc(vmf).Ticks.ToString());
            File.WriteAllText(vmf, edited);
            File.SetLastWriteTimeUtc(vmf, File.GetLastWriteTimeUtc(vmf).AddMinutes(1));
            return review;
        }

        private static string BrushEntity(int id, string classname, double x0, double y0, double z0, double x1, double y1, double z1) =>
            $"entity\r\n{{\r\n\t\"id\" \"{id}\"\r\n\t\"classname\" \"{classname}\"\r\n" + AreaportalTests.Box(x0, y0, z0, x1, y1, z1, "TOOLS/TOOLSAREAPORTAL") + "}\r\n";

        private static Dictionary<string, double> DoorwayData() => new()
        {
            ["axis"] = 0, ["a0"] = 0, ["a1"] = 8, ["w0"] = 0, ["w1"] = 8, ["z0"] = 0, ["z1"] = 8, ["closes"] = 1, ["startOpen"] = 0,
        };

        private static Dictionary<string, double> WindowData() => new()
        {
            ["axis"] = 0, ["x0"] = 0, ["y0"] = 0, ["z0"] = 0, ["x1"] = 8, ["y1"] = 8, ["z1"] = 8, ["passage"] = 0,
        };

        [Fact]
        public void DeletingThePortalDeniesItEvenWithTheDoorItNamedOrTheWindowsStandInLeft()
        {
            using var space = new Workspace();
            var file = DecisionFile.Load(Path.Combine(space.Root, "maps", "x.viswright.json"));
            file.Merge([
                Item("areaportal:func_door", "areaportals", ItemStatus.Applied, DoorwayData()),
                Item("areaportal:prop_door", "areaportals", ItemStatus.Applied, DoorwayData()),
                Item("window:0_0_0", "areaportals", ItemStatus.Applied, WindowData()),
                Item("areaportal:door_gone", "areaportals", ItemStatus.Applied, DoorwayData()),
            ]);
            file.Save();

            // Portals 20, 30 and 40 deleted; the func_door 21 the first one named, the prop door 31 and the
            // window's stand-in 41 left. For the last item only its door 51 was deleted: its portal 50 stays.
            string review = EditedReview(space, """
                { "items": [
                  { "id": "areaportal:func_door", "pass": "areaportals", "nodes": [20, 21] },
                  { "id": "areaportal:prop_door", "pass": "areaportals", "nodes": [30, 31] },
                  { "id": "window:0_0_0", "pass": "areaportals", "nodes": [40, 41] },
                  { "id": "areaportal:door_gone", "pass": "areaportals", "nodes": [50, 51] }
                ] }
                """,
                "world\r\n{\r\n\t\"id\" \"1\"\r\n}\r\n" +
                BrushEntity(21, "func_door", -2, 0, 0, 2, 64, 96) +
                "entity\r\n{\r\n\t\"id\" \"31\"\r\n\t\"classname\" \"prop_door_rotating\"\r\n\t\"origin\" \"0 32 0\"\r\n}\r\n" +
                BrushEntity(41, "func_brush", -1, 0, 0, 9, 8, 8) +
                BrushEntity(50, "func_areaportal", 0, 0, 0, 8, 8, 8));

            Assert.Equal(3, RoundTrip.Apply(file, review));
            Assert.Equal(Decision.Deny, file["areaportal:func_door"]!.Decision);
            Assert.False(file["areaportal:func_door"]!.Altered);
            Assert.Equal(new Vec3(8, 8, 8), file["areaportal:func_door"]!.Maxs);
            Assert.Equal(Decision.Deny, file["areaportal:prop_door"]!.Decision);
            Assert.Equal(Decision.Deny, file["window:0_0_0"]!.Decision);
            Assert.Equal(Decision.Pending, file["areaportal:door_gone"]!.Decision);
        }

        [Fact]
        public void AMovedWindowPortalMovesTheWindowAPinnedRunPutsBack()
        {
            using var space = new Workspace();
            var file = DecisionFile.Load(Path.Combine(space.Root, "maps", "x.viswright.json"));
            file.Merge([Item("window:0_0_0", "areaportals", ItemStatus.Applied, WindowData())]);
            file.Save();

            string review = EditedReview(space, """
                { "items": [ { "id": "window:0_0_0", "pass": "areaportals", "nodes": [40, 41] } ] }
                """,
                "world\r\n{\r\n\t\"id\" \"1\"\r\n}\r\n" +
                BrushEntity(40, "func_areaportalwindow", 0, 16, 32, 8, 80, 96) +
                BrushEntity(41, "func_brush", -1, 0, 0, 9, 8, 8));

            Assert.Equal(1, RoundTrip.Apply(file, review));
            var moved = file["window:0_0_0"]!;
            Assert.Equal(Decision.Accept, moved.Decision);
            Assert.True(moved.Altered);
            static bool Near(Vec3 a, Vec3 b) => (a - b).Length < 0.01;
            Assert.True(Near(moved.Mins, new Vec3(0, 16, 32)) && Near(moved.Maxs, new Vec3(8, 80, 96)));
            // Pinned runs put a window back from these keys, so they have to move with it, not only Z.
            var window = DecisionFile.Window(moved.Data);
            Assert.True(Near(window.Mins, new Vec3(0, 16, 32)) && Near(window.Maxs, new Vec3(8, 80, 96)));
            Assert.False(moved.Data.ContainsKey("a0"));
        }

        [Fact]
        public void APinnedMapCompilesTheSameEveryTime()
        {
            if (AreaportalTests.Tools() is not { } tools)
                return;
            using var space = new Workspace();
            string vmf = space.Write("maps/rooms.vmf", AreaportalTests.TwoRooms());
            var options = new OptimizeOptions { Input = vmf, Areaportals = true, Tools = tools, Mode = null, Cpu = true };

            // First run: a review. The compile keeps its own map, and the portal waits for a decision.
            var review = Optimizer.Run(options);
            Assert.Null(review.CopyPath);
            var decisions = DecisionFile.Load(DecisionFile.PathFor(vmf));
            Assert.Equal(1, decisions.Pending);

            // Accepting runs pinned: the portal goes in with no search.
            var first = Optimizer.Run(options with { Accept = true });
            Assert.NotNull(first.CopyPath);
            string firstText = File.ReadAllText(first.CopyPath!);
            Assert.Contains("func_areaportal", firstText);

            var second = Optimizer.Run(options);
            Assert.Equal(firstText, File.ReadAllText(second.CopyPath!));
        }
    }
}
