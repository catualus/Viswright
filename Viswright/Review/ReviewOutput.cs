using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright.Review
{
    /// <summary>
    /// Writes a run's review to a folder: the items as JSON, the review VMF for Hammer, plans of each
    /// floor (items, what is visible before and after, the change), a picture of each top item, and a
    /// page that puts them together.
    /// </summary>
    public static class ReviewOutput
    {
        public const int Thumbnails = 40;
        private const int PlanPixels = 1600;

        /// <summary>Writes everything and returns the page's path.</summary>
        public static string Write(string folder, string name, VmfDocument working, ReviewLog log, SamplePoints samples,
            WorkCompile baseline, WorkCompile? final, Slack? slackBefore = null, Slack? slackAfter = null)
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(Path.Combine(folder, "items"));

            var numbered = log.Ordered().Select((item, i) => (Number: i + 1, Item: item)).ToList();
            double[] before = CompileMeasure.PerPoint(baseline, samples);
            double[]? after = final == null ? null : CompileMeasure.PerPoint(final, samples);

            WriteJson(Path.Combine(folder, "items.json"), numbered, samples);
            string vmf = Path.Combine(folder, name + "_review.vmf");
            var slack = slackAfter ?? slackBefore;
            ReviewVmf.Build(working, numbered, samples, before, after, slack?.Values).Save(vmf);

            var space = BspSpace.Load((final ?? baseline).Bsp);
            var vis = BspVisibilityData.Load((final ?? baseline).Bsp);
            var plans = WritePlans(folder, space, samples, numbered, before, after, slack);
            var thumbs = WriteThumbnails(folder, space, samples, numbered, before, after);

            string page = Path.Combine(folder, "review.html");
            File.WriteAllText(page, Page(name, vmf, numbered, plans, thumbs, after != null, before, after, samples, slackBefore, slackAfter));
            return page;
        }

        /// <summary>Written by hand rather than by reflection: Compile Pal ships Viswright trimmed.</summary>
        private static void WriteJson(string path, List<(int Number, ReviewItem Item)> items, SamplePoints samples)
        {
            var cameras = ReviewVmf.Highlights(items).Take(ReviewVmf.CameraCount).Select((h, i) => (h.Number, i)).ToDictionary(t => t.Number, t => t.i);
            using var file = File.Create(path);
            using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            void Vector(string name, Vec3 v)
            {
                json.WriteStartArray(name);
                json.WriteNumberValue(Math.Round(v.X, 1));
                json.WriteNumberValue(Math.Round(v.Y, 1));
                json.WriteNumberValue(Math.Round(v.Z, 1));
                json.WriteEndArray();
            }
            json.WriteStartObject();
            json.WriteStartArray("items");
            foreach (var (number, item) in items)
            {
                json.WriteStartObject();
                json.WriteNumber("number", number);
                json.WriteString("id", item.Id);
                json.WriteString("pass", item.Pass);
                json.WriteString("status", item.Status.ToString().ToLowerInvariant());
                json.WriteString("reason", item.Reason);
                if (item.Detail != null) json.WriteString("detail", item.Detail);
                if (item.Gain is { } g) json.WriteNumber("gain", Math.Round(g, 4));
                Vector("mins", item.Mins);
                Vector("maxs", item.Maxs);
                if (cameras.TryGetValue(number, out int c)) json.WriteNumber("camera", c + 1);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        private sealed record Plan(double Z, string Items, string Before, string? After, string? Change, string? Slack);

        private static List<Plan> WritePlans(string folder, BspSpace space, SamplePoints samples, List<(int Number, ReviewItem Item)> items,
            double[] before, double[]? after, Slack? slack)
        {
            var plans = new List<Plan>();
            double top = Math.Max(1, Percentile(before.Where(v => v >= 0), 0.95));
            int level = 0;
            foreach (double z in PlanRenderer.Levels(samples))
            {
                level++;
                var near = samples.Points.Where(p => Math.Abs(p.Z - z) <= 96).ToList();
                var view = PlanView.Around(near, z, 512, PlanPixels);
                var baseImage = PlanRenderer.Base(space, view, out var open);

                string Save(string suffix, Action<Canvas> draw)
                {
                    var canvas = baseImage.Crop(0, 0, baseImage.Width - 1, baseImage.Height - 1);
                    draw(canvas);
                    string file = $"level{level}-{suffix}.png";
                    canvas.SavePng(Path.Combine(folder, file));
                    return file;
                }

                var highlights = ReviewVmf.Highlights(items).ToList();
                var numbered = highlights.Take(ReviewVmf.CameraCount).Select(h => h.Number).ToHashSet();
                string itemsFile = Save("items", c => PlanRenderer.Items(c, view, highlights, numbered));
                string beforeFile = Save("before", c => PlanRenderer.Heat(c, open, view, samples, before, v => Rgb.Heat(v / top)));
                string? afterFile = after == null ? null : Save("after", c => PlanRenderer.Heat(c, open, view, samples, after, v => Rgb.Heat(v / top)));
                string? changeFile = after == null ? null : Save("change", c =>
                {
                    var change = before.Zip(after, (b, a) => b < 0 || a < 0 ? -1 : 1 + (a - b) / Math.Max(1, b)).ToArray();
                    PlanRenderer.Heat(c, open, view, samples, change, v => Rgb.Change((v - 1) * 5), alpha: 0.75);
                    PlanRenderer.Items(c, view, highlights.Where(h => h.Item.Status == ItemStatus.Applied));
                });
                string? slackFile = slack == null ? null : Save("slack", c => PlanRenderer.Heat(c, open, view, samples, slack.Values, Rgb.Heat, alpha: 0.7));
                plans.Add(new Plan(z, itemsFile, beforeFile, afterFile, changeFile, slackFile));
            }
            return plans;
        }

        private static Dictionary<int, string> WriteThumbnails(string folder, BspSpace space, SamplePoints samples,
            List<(int Number, ReviewItem Item)> items, double[] before, double[]? after)
        {
            var files = new Dictionary<int, string>();
            var highlights = ReviewVmf.Highlights(items).ToList();
            foreach (var (number, item) in highlights.Take(Thumbnails))
            {
                // The floor the item stands on: eye height above its bottom, but no higher than its top.
                double z = Math.Min(item.Mins.Z + SamplePoints.EyeHeight, Math.Max(item.Mins.Z + 1, item.Maxs.Z - 1));
                var c = item.Centre;
                var view = new PlanView(c.X - 768, c.Y - 768, c.X + 768, c.Y + 768, z, 4);
                var canvas = PlanRenderer.Base(space, view, out var open);
                if (after != null)
                {
                    var change = before.Zip(after, (b, a) => b < 0 || a < 0 ? -1 : 1 + (a - b) / Math.Max(1, b)).ToArray();
                    PlanRenderer.Heat(canvas, open, view, samples, change, v => Rgb.Change((v - 1) * 5), band: 160, alpha: 0.5);
                }
                PlanRenderer.Items(canvas, view, highlights.Where(h => h.Number != number));
                PlanRenderer.Items(canvas, view, [(number, item with { Mins = item.Mins with { Z = z - 1 }, Maxs = item.Maxs with { Z = Math.Max(item.Maxs.Z, z + 1) } })],
                    new HashSet<int> { number });
                string file = Path.Combine("items", $"{number}.png");
                canvas.SavePng(Path.Combine(folder, file));
                files[number] = file.Replace('\\', '/');
            }
            return files;
        }

        private static double Percentile(IEnumerable<double> values, double p)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
        }

        private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

        private static string Page(string name, string vmf, List<(int Number, ReviewItem Item)> items, List<Plan> plans,
            Dictionary<int, string> thumbs, bool hasAfter, double[] before, double[]? after, SamplePoints samples, Slack? slackBefore, Slack? slackAfter)
        {
            var html = new StringBuilder();
            html.Append($$"""
                <!doctype html>
                <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>{{H(name)}} review</title>
                <style>
                :root { --bg:#f3f4f6; --panel:#fff; --ink:#1b222c; --soft:#566070; --line:#d7dce3; --applied:#1f8f4c; --rejected:#c83a3a; --suggested:#c98a12; color-scheme: light; }
                @media (prefers-color-scheme: dark) { :root { --bg:#12161c; --panel:#1a2029; --ink:#e4e8ee; --soft:#a0aab8; --line:#2c3541; --applied:#5fd08e; --rejected:#f07b7b; --suggested:#f0c060; color-scheme: dark; } }
                body { margin:0; background:var(--bg); color:var(--ink); font:15px/1.5 "Segoe UI", system-ui, sans-serif; }
                main { max-width:1700px; margin:0 auto; padding:24px max(16px, 2vw) 64px; }
                h1 { margin:0 0 4px; font-size:28px; } h2 { margin:36px 0 10px; font-size:20px; border-top:2px solid var(--ink); padding-top:10px; }
                .soft { color:var(--soft); } code { font-family:Consolas, monospace; font-size:.9em; }
                table { border-collapse:collapse; width:100%; background:var(--panel); font-size:14px; }
                th, td { text-align:left; padding:6px 10px; border-bottom:1px solid var(--line); vertical-align:top; }
                th { font-size:12px; text-transform:uppercase; letter-spacing:.05em; color:var(--soft); }
                td.n { text-align:right; font-variant-numeric:tabular-nums; }
                .applied { color:var(--applied); font-weight:600; } .rejected { color:var(--rejected); font-weight:600; } .suggested { color:var(--suggested); font-weight:600; }
                .plans { display:grid; grid-template-columns:repeat(auto-fit, minmax(420px, 1fr)); gap:12px; }
                figure { margin:0; background:var(--panel); border:1px solid var(--line); padding:8px; }
                figure img { width:100%; height:auto; image-rendering:pixelated; display:block; }
                figcaption { font-size:13px; color:var(--soft); margin-top:4px; }
                img.thumb { width:192px; height:192px; image-rendering:pixelated; }
                .wrap { overflow-x:auto; }
                .key span { display:inline-block; margin-right:16px; }
                .sw { display:inline-block; width:12px; height:12px; vertical-align:-1px; margin-right:6px; border-radius:2px; }
                </style></head><body><main>
                <h1>{{H(name)}}</h1>
                <p class="soft">Viswright review. Open <code>{{H(vmf)}}</code> in Hammer++: the <b>Viswright</b> visgroup holds every item below,
                <b>Applied</b> shown and <b>Markers</b>, <b>Rejected</b> and <b>Heatmap</b> hidden until ticked, and the Camera tool steps through the
                numbered items in order. Load the map's <code>.prt</code> (Map &gt; Load Portal File) to see its leaves.</p>
                """);

            // Summary.
            html.Append("<h2>Summary</h2><div class=\"wrap\"><table><tr><th>Pass</th><th>Applied</th><th>Rejected</th><th>Suggested</th><th>Most common reason refused</th></tr>");
            foreach (var pass in items.Select(i => i.Item.Pass).Distinct())
            {
                var mine = items.Where(i => i.Item.Pass == pass).Select(i => i.Item).ToList();
                string common = mine.Where(i => i.Status == ItemStatus.Rejected).GroupBy(i => Regex(i.Reason)).OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} ({g.Count()})").FirstOrDefault() ?? "";
                html.Append($"<tr><td>{H(pass)}</td><td class=\"n\">{mine.Count(i => i.Status == ItemStatus.Applied)}</td>" +
                    $"<td class=\"n\">{mine.Count(i => i.Status == ItemStatus.Rejected)}</td><td class=\"n\">{mine.Count(i => i.Status == ItemStatus.Suggested)}</td><td>{H(common)}</td></tr>");
            }
            html.Append("</table></div>");
            if (hasAfter)
            {
                var pairs = before.Zip(after!).Where(p => p.First >= 0 && p.Second >= 0).ToList();
                if (pairs.Count > 0)
                    html.Append($"<p>Triangles visible from a standing position, on average over {pairs.Count:N0} places a player can reach: " +
                        $"<b>{pairs.Average(p => p.First):N0}</b> before, <b>{pairs.Average(p => p.Second):N0}</b> after.</p>");
            }

            foreach (var (label, s) in new[] { ("before", slackBefore), ("after", slackAfter) })
            {
                if (s == null) continue;
                var ok = Enumerable.Range(0, s.Values.Length).Where(i => s.Values[i] >= 0).ToList();
                if (ok.Count == 0) continue;
                html.Append($"<p><b>PVS slack {label}</b> ({H(s.How)}): a standing point really sees <b>{ok.Average(i => s.Seen[i]):N0}</b> triangles " +
                    $"of the <b>{ok.Average(i => s.Pvs[i]):N0}</b> its PVS lets through, {s.Average:P0} slack. " +
                    $"{ok.Average(i => s.SplitSlack[i]):P0} is seen from elsewhere in the same cluster, which cutting clusters up could save; " +
                    $"{ok.Average(i => s.FlowSlack[i]):P0} is seen from nowhere in the cluster, VVIS being cautious through the portals.</p>");
            }

            // Plans.
            html.Append("""
                <h2>Plans</h2>
                <p class="key"><span><i class="sw" style="background:#3a3e48"></i>world brush</span><span><i class="sw" style="background:#846c52"></i>func_detail</span>
                <span><i class="sw" style="background:#28be5a"></i>applied</span><span><i class="sw" style="background:#e63c3c"></i>rejected</span><span><i class="sw" style="background:#fab01e"></i>suggested</span>
                <span>Heat: <i class="sw" style="background:#1e286e"></i>little visible to <i class="sw" style="background:#dc3228"></i>most visible.
                Change: <i class="sw" style="background:#1e5ac8"></i>less visible, <i class="sw" style="background:#d22828"></i>more visible.</span></p>
                """);
            foreach (var plan in plans)
            {
                html.Append($"<h3>Floor at eye height {plan.Z:0}</h3><div class=\"plans\">");
                Figure(html, plan.Items, "Items, numbered as in the table below");
                Figure(html, plan.Before, "Visible from each standing place, before");
                if (plan.After != null) Figure(html, plan.After, "Visible from each standing place, after");
                if (plan.Change != null) Figure(html, plan.Change, "Change, with the items outlined");
                if (plan.Slack != null) Figure(html, plan.Slack, "PVS slack: the share of what the PVS lets through that cannot be seen from here");
                html.Append("</div>");
            }

            // Items.
            var cameraOf = ReviewVmf.Highlights(items).Take(ReviewVmf.CameraCount).Select((h, i) => (h.Number, i)).ToDictionary(t => t.Number, t => t.i + 1);
            html.Append("<h2>Items</h2><p class=\"soft\">Camera is the number of the Hammer camera that looks at the item: press Page Down in the Camera tool to step through them.</p>");
            html.Append("<div class=\"wrap\"><table><tr><th>#</th><th>Pass</th><th>Status</th><th>Why</th><th>Seen around it</th><th>Where</th><th>Camera</th><th>Detail</th><th>Picture</th></tr>");
            foreach (var (number, item) in items)
            {
                string status = item.Status.ToString().ToLowerInvariant();
                string gain = item.Gain is { } g ? (g >= 0 ? $"{g:P1} less" : $"{-g:P1} more") : "";
                var c = item.Centre;
                string thumb = thumbs.TryGetValue(number, out var t) ? $"<a href=\"{t}\"><img class=\"thumb\" src=\"{t}\" alt=\"Item {number}\" loading=\"lazy\"></a>" : "";
                html.Append($"<tr><td class=\"n\">{number}</td><td>{H(item.Pass)}</td><td class=\"{status}\">{status}</td><td>{H(item.Reason)}</td>" +
                    $"<td>{gain}</td><td><code>setpos {c.X:0} {c.Y:0} {c.Z:0}</code></td><td class=\"n\">{(cameraOf.TryGetValue(number, out int cam) ? cam : "")}</td>" +
                    $"<td>{H(item.Detail)}</td><td>{thumb}</td></tr>");
            }
            html.Append("</table></div></main></body></html>");
            return html.ToString();
        }

        /// <summary>A reason without its numbers, so reasons that differ only in a percentage count together.</summary>
        private static string Regex(string reason) => System.Text.RegularExpressions.Regex.Replace(reason, @"[-\d.,]+ ?%?", "N").Trim();

        private static void Figure(StringBuilder html, string file, string caption) =>
            html.Append($"<figure><a href=\"{file}\"><img src=\"{file}\" alt=\"{H(caption)}\" loading=\"lazy\"></a><figcaption>{H(caption)}</figcaption></figure>");
    }
}
