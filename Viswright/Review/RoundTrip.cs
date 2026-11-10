using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright.Review
{
    /// <summary>
    /// Decisions made in Hammer: what the mapper changed in the review VMF since it was written.
    ///
    /// Deleting an applied change denies it. Moving or resizing a hint brush or an areaportal brush
    /// alters the item to where it now is. Putting a refused or suggested marker into the
    /// <c>Viswright/Accept</c> visgroup accepts it. Nothing else in the file is read: the review VMF is a
    /// way of answering, not a map Viswright takes changes from.
    /// </summary>
    public static class RoundTrip
    {
        public const string StampFile = "review.stamp";

        /// <summary>Takes the mapper's edits into <paramref name="decisions"/>. Returns how many decisions changed.</summary>
        public static int Apply(DecisionFile decisions, string reviewFolder)
        {
            string itemsPath = Path.Combine(reviewFolder, "items.json");
            string stampPath = Path.Combine(reviewFolder, StampFile);
            var vmf = Directory.Exists(reviewFolder) ? Directory.EnumerateFiles(reviewFolder, "*_review.vmf").FirstOrDefault() : null;
            if (!decisions.Exists || vmf == null || !File.Exists(itemsPath) || !File.Exists(stampPath))
                return 0;
            if (!long.TryParse(File.ReadAllText(stampPath).Trim(), out long written) || File.GetLastWriteTimeUtc(vmf).Ticks <= written)
                return 0;

            var edited = VmfDocument.Load(vmf);
            var present = new Dictionary<long, VmfNode>();
            foreach (var node in edited.Root.Descendants().Where(n => n.Name is "solid" or "entity"))
                if (node.Id is { } id)
                    present.TryAdd(id, node);
            string? acceptGroup = edited.Root.Descendants()
                .FirstOrDefault(n => n.Name == "visgroup" && n.Get("name") == "Accept" && n.Parent?.Get("name") == "Viswright")?.Get("visgroupid");

            int changed = 0;
            using var json = JsonDocument.Parse(File.ReadAllBytes(itemsPath));
            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
            {
                string id = item.GetProperty("id").GetString()!;
                string pass = item.GetProperty("pass").GetString()!;
                var known = decisions[id];
                if (known == null)
                    continue;

                if (item.TryGetProperty("nodes", out var nodes) && nodes.GetArrayLength() > 0)
                {
                    // The first node is what the item added - a portal, a hint, a prop made static. Any after
                    // it came along with it: a door the portal gave a name, a window's stand-in. Deleting the
                    // portal denies the item whatever is left of those, and only its own brush says where
                    // the mapper moved it; a door's brush is not the doorway.
                    if (!present.TryGetValue(nodes[0].GetInt64(), out var own))
                    {
                        if (known.Decision != Decision.Deny)
                        {
                            decisions.Set(id, d => d with { Decision = Decision.Deny });
                            changed++;
                        }
                        continue;
                    }
                    if (Moved(pass, own, known) is { } altered)
                    {
                        decisions.Set(id, _ => altered);
                        changed++;
                    }
                    continue;
                }

                if (acceptGroup != null && item.TryGetProperty("marker", out var marker)
                    && edited.Root.Descendants().FirstOrDefault(n => n.Name == "entity" && n.Get("targetname") == marker.GetString()) is { } box
                    && box.Child("editor") is { } editor
                    && editor.KeyValues.Any(kv => kv.Key.Equals("visgroupid", StringComparison.OrdinalIgnoreCase) && kv.Value == acceptGroup)
                    && known.Decision != Decision.Accept)
                {
                    decisions.Set(id, d => d with { Decision = Decision.Accept });
                    changed++;
                }
            }
            File.WriteAllText(stampPath, File.GetLastWriteTimeUtc(vmf).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return changed;
        }

        /// <summary>The item as the mapper left it, if they moved or resized a hint or areaportal brush; else null.</summary>
        private static DecidedItem? Moved(string pass, VmfNode node, DecidedItem item)
        {
            var solid = node.Name == "solid" ? node : node.Child("solid");
            if (solid == null || VmfBrush.Build(solid) is not { } brush)
                return null;
            if (pass == "hints")
            {
                double height = brush.Maxs.Z;
                if (Math.Abs(height - item.Data.GetValueOrDefault("height", height)) < 0.5)
                    return null;
                var data = new Dictionary<string, double>(item.Data) { ["height"] = height };
                return item with { Data = data, Maxs = item.Maxs with { Z = height }, Altered = true, Decision = Decision.Accept };
            }
            if (pass == "areaportals" && item.Data.TryGetValue("axis", out double axis))
            {
                if ((brush.Mins - item.Mins).Length < 0.5 && (brush.Maxs - item.Maxs).Length < 0.5)
                    return null;
                // A window is put back from its corners (DecisionFile.Window), not from a doorway's
                // extents; its portal brush is the opening itself.
                if (item.Data.ContainsKey("x0"))
                {
                    var corners = new Dictionary<string, double>(item.Data)
                    {
                        ["x0"] = brush.Mins.X, ["y0"] = brush.Mins.Y, ["z0"] = brush.Mins.Z,
                        ["x1"] = brush.Maxs.X, ["y1"] = brush.Maxs.Y, ["z1"] = brush.Maxs.Z,
                    };
                    return item with { Data = corners, Mins = brush.Mins, Maxs = brush.Maxs, Altered = true, Decision = Decision.Accept };
                }
                bool alongX = axis == 0;
                var data = new Dictionary<string, double>(item.Data)
                {
                    ["a0"] = alongX ? brush.Mins.X : brush.Mins.Y, ["a1"] = alongX ? brush.Maxs.X : brush.Maxs.Y,
                    ["w0"] = alongX ? brush.Mins.Y : brush.Mins.X, ["w1"] = alongX ? brush.Maxs.Y : brush.Maxs.X,
                    ["z0"] = brush.Mins.Z, ["z1"] = brush.Maxs.Z,
                };
                return item with { Data = data, Mins = brush.Mins, Maxs = brush.Maxs, Altered = true, Decision = Decision.Accept };
            }
            return null;
        }
    }
}
