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
            file.Save();

            // The next review finds the portal again and not the hint: the hint is kept, marked stale.
            var again = DecisionFile.Load(path);
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
