using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;
using Viswright.Review;
using Xunit;

namespace Viswright.Tests
{
    public class ReviewTests
    {
        [Fact]
        public void APngHoldsThePixelsDrawn()
        {
            using var space = new Workspace();
            var canvas = new Canvas(3, 2, new Rgb(10, 20, 30));
            canvas.Set(1, 0, new Rgb(200, 100, 50));
            canvas.Set(2, 1, new Rgb(1, 2, 3));
            string path = Path.Combine(space.Root, "x.png");
            canvas.SavePng(path);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
            Assert.Equal(3, Big(bytes, 16));
            Assert.Equal(2, Big(bytes, 20));

            // The image data: one IDAT chunk after the 25-byte IHDR chunk.
            int length = Big(bytes, 33);
            Assert.Equal("IDAT", System.Text.Encoding.ASCII.GetString(bytes, 37, 4));
            using var z = new ZLibStream(new MemoryStream(bytes, 41, length), CompressionMode.Decompress);
            var raw = new MemoryStream();
            z.CopyTo(raw);
            var rows = raw.ToArray();
            Assert.Equal(2 * (1 + 3 * 3), rows.Length);
            Assert.Equal(new byte[] { 0, 10, 20, 30, 200, 100, 50, 10, 20, 30 }, rows[..10]);
            Assert.Equal(new byte[] { 1, 2, 3 }, rows[^3..]);
        }

        private static int Big(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

        [Fact]
        public void ItemsKeepTheirIdsApartAndAPassCanBeRejectedWhole()
        {
            var log = new ReviewLog();
            var box = (new Vec3(0, 0, 0), new Vec3(8, 8, 8));
            log.Add(new ReviewItem("fade:1", "fades", ItemStatus.Applied, "fades", box.Item1, box.Item2));
            log.Add(new ReviewItem("fade:1", "fades", ItemStatus.Applied, "fades", box.Item1, box.Item2));
            log.Add(new ReviewItem("ap:door", "areaportals", ItemStatus.Rejected, "joined", box.Item1, box.Item2));
            log.Add(new ReviewItem("ap:door2", "areaportals", ItemStatus.Applied, "closes", box.Item1, box.Item2) { NodeIds = [5] });

            Assert.Equal(["fade:1", "fade:1~2"], log.Items.Where(i => i.Pass == "fades").Select(i => i.Id));
            // Areaportals come first, applied before rejected.
            Assert.Equal(["ap:door2", "ap:door", "fade:1", "fade:1~2"], log.Ordered().Select(i => i.Id));

            log.RejectAll("areaportals", "undone");
            var undone = log.Items.Single(i => i.Id == "ap:door2");
            Assert.Equal(ItemStatus.Rejected, undone.Status);
            Assert.Equal("undone", undone.Reason);
            Assert.Empty(undone.NodeIds);
        }

        private const string Map = """
            versioninfo
            {
            	"editorversion" "400"
            }
            world
            {
            	"id" "1"
            	"classname" "worldspawn"
            	solid
            	{
            		"id" "2"
            		side
            		{
            			"id" "3"
            			"plane" "(0 64 64) (64 64 64) (64 0 64)"
            			"material" "BRICK/WALL"
            		}
            	}
            }
            entity
            {
            	"id" "10"
            	"classname" "prop_static"
            	"origin" "100 0 0"
            	editor
            	{
            		"color" "255 255 0"
            		"visgroupid" "4"
            		"visgroupshown" "1"
            	}
            }
            cameras
            {
            	"activecamera" "-1"
            }
            """;

        [Fact]
        public void TheReviewVmfGroupsAppliedChangesAndHidesMarkers()
        {
            var working = VmfDocument.Parse(Map.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            var items = new[]
            {
                (1, new ReviewItem("detail:a", "detail", ItemStatus.Applied, "made detail", new Vec3(0, 0, 0), new Vec3(64, 64, 64)) { NodeIds = [2] }),
                (2, new ReviewItem("fade:b", "fades", ItemStatus.Applied, "fades out by 800 units", new Vec3(92, -8, -8), new Vec3(108, 8, 8)) { NodeIds = [10] }),
                (3, new ReviewItem("areaportal:c", "areaportals", ItemStatus.Rejected, "the rooms are joined", new Vec3(200, 0, 0), new Vec3(216, 64, 96))),
            };

            var review = ReviewVmf.Build(working, items, SamplePointsFor([new Vec3(300, 0, 64)]), null, null);
            var reread = VmfDocument.Parse(review.ToString());

            // Groups: Viswright with Applied, Markers, Rejected, Suggested and Heatmap under it, before the world.
            var names = reread.Root.Nodes.Select(n => n.Name).ToList();
            Assert.True(names.IndexOf("visgroups") < names.IndexOf("world"));
            var viswright = reread.Root.Child("visgroups")!.Children("visgroup").Single(g => g.Get("name") == "Viswright");
            Assert.Equal(["Applied", "Markers", "Rejected", "Suggested", "Heatmap", "Accept"], viswright.Children("visgroup").Select(g => g.Get("name")));

            // The prop keeps the visgroup it was in and joins the fades group.
            var prop = reread.Entities.Single(e => e.Classname == "prop_static");
            Assert.Equal(2, prop.Child("editor")!.KeyValues.Count(kv => kv.Key == "visgroupid"));
            Assert.Contains("4", prop.Child("editor")!.KeyValues.Where(kv => kv.Key == "visgroupid").Select(kv => kv.Value));
            Assert.NotNull(reread.World.Children("solid").Single().Child("editor")!.Get("visgroupid"));

            // The rejected portal is a hidden, named marker carrying its reason.
            var marker = reread.Root.Child("hidden")!.Child("entity")!;
            Assert.Equal("vw_3_areaportals", marker.Get("targetname"));
            Assert.Contains("the rooms are joined", marker.Child("editor")!.Get("comments"));
            Assert.Equal("0", marker.Child("editor")!.Get("visgroupshown"));

            // One camera per highlight: the fade is not one.
            var cameras = reread.Root.Child("cameras")!;
            Assert.Equal(2, cameras.Children("camera").Count());
            Assert.Equal("[300 0 64]", cameras.Children("camera").First().Get("position"));
        }

        /// <summary>Sample points at chosen places, without compiling a map to search.</summary>
        private static SamplePoints SamplePointsFor(Vec3[] points) =>
            (SamplePoints)typeof(SamplePoints).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0]
                .Invoke([points.ToList()]);

        [Fact]
        public void AnOpenCourtyardHasLittleSlack()
        {
            if (AreaportalTests.Tools() is not { } tools)
                return;
            using var space = new Workspace();
            string vmf = space.Write("maps/courtyard.vmf", HintTests.Courtyard());
            var map = VmfDocument.Load(vmf);
            var compile = WorkCompile.Prepare(map, vmf, Path.Combine(space.Root, "c"), "courtyard");
            Assert.True(compile.RunVbsp(tools).Succeeded);
            Assert.True(compile.RunVvis(tools).Succeeded);
            var bsp = BspSpace.Load(compile.Bsp);
            var samples = SamplePoints.Find(bsp, SamplePoints.Reachable(map, SourceKit.Compile.PortalFile.Load(compile.Prt), bsp));

            var slack = Slack.Measure(compile.Bsp, samples, CompileMeasure.PerPoint(compile, samples), gpu: false, rays: 2048);

            // One open room: from anywhere in it, everything its PVS lets through is in sight.
            Assert.All(samples.Points.Select((_, i) => slack.Seen[i]), seen => Assert.True(seen > 0));
            Assert.True(slack.Average < 0.2, $"slack {slack.Average:P1}");
        }

        [Fact]
        public void TheViewDrawsWhatIsInFrontAndTheCutTrimsWalls()
        {
            var scene = new Scene(cut: 50);
            // A wall 100 tall at x = 200, facing a camera at the origin looking along +x.
            scene.AddOpaque(new Vec3(200, -100, 0), new Vec3(200, 100, 0), new Vec3(200, 100, 100), new Rgb(200, 0, 0));
            scene.AddOpaque(new Vec3(200, -100, 0), new Vec3(200, 100, 100), new Vec3(200, -100, 100), new Rgb(200, 0, 0));
            var low = scene.Render(new Vec3(0, 0, 25), new Vec3(200, 0, 25), 64, 64, 60);
            Assert.NotEqual(Scene.Background, low.Get(32, 32));
            // Above the cut there is nothing left of the wall.
            var high = scene.Render(new Vec3(0, 0, 80), new Vec3(200, 0, 80), 64, 64, 30);
            Assert.Equal(Scene.Background, high.Get(32, 32));
        }

        [Fact]
        public void ADoorwayWithAWindowAboveShowsWhereTheRoomsMeet()
        {
            if (AreaportalTests.Tools() is not { } tools)
                return;
            using var space = new Workspace();
            string vmf = space.Write("maps/rooms.vmf", AreaportalTests.TwoRooms(window: true));
            var map = VmfDocument.Load(vmf);
            var compile = WorkCompile.Prepare(map, vmf, Path.Combine(space.Root, "c"), "rooms");
            Assert.True(compile.RunVbsp(tools).Succeeded);
            var bsp = BspSpace.Load(compile.Bsp);
            var pass = new AreaportalPass(map);
            var door = map.Entities.Single(e => e.Classname == "func_door");
            var doorway = new Doorway(door, 0, 254, 258, 96, 160, 16, 112, Closes: true, StartOpen: false, WallA0: 248, WallA1: 264);

            Assert.Empty(pass.SealedDoorways([doorway], SourceKit.Compile.PortalFile.Load(compile.Prt), bsp));
            var path = Assert.Single(pass.JoinedBy).Value;
            Assert.NotEmpty(path);
            Assert.Contains("above the door", AreaportalPass.LeakKind(doorway, path));
        }

        [Fact]
        public void APlanShowsWallsAroundOpenGround()
        {
            if (AreaportalTests.Tools() is not { } tools)
                return;
            using var space = new Workspace();
            string vmf = space.Write("maps/courtyard.vmf", HintTests.Courtyard());
            var compile = WorkCompile.Prepare(VmfDocument.Load(vmf), vmf, Path.Combine(space.Root, "c"), "courtyard");
            Assert.True(compile.RunVbsp(tools).Succeeded);
            var bsp = BspSpace.Load(compile.Bsp);

            var view = new PlanView(-64, -64, 1088, 1088, 96, 8);
            var canvas = PlanRenderer.Base(bsp, view, out var open);
            var (cx, cy) = view.ToPixel(512, 512);
            Assert.True(open[cy * view.Width + cx]);
            var (wx, wy) = view.ToPixel(8, 512);
            Assert.False(open[wy * view.Width + wx]);
            Assert.Equal(PlanRenderer.Wall, canvas.Get(wx, wy));
        }
    }
}
