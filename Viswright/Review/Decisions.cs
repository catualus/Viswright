using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using SourceKit.Geometry;

namespace Viswright.Review
{
    public enum Decision { Pending, Accept, Deny }

    /// <summary>One item as decided: what it is, where, the data to apply it again, and what was decided.</summary>
    public sealed record DecidedItem(string Id, string Pass, Decision Decision, string Reason, Vec3 Mins, Vec3 Maxs)
    {
        public string? Detail { get; init; }

        /// <summary>What re-applying the item needs that its id and box do not say: a doorway's extents, a hint's height.</summary>
        public IReadOnlyDictionary<string, double> Data { get; init; } = new Dictionary<string, double>();

        /// <summary>Set when the mapper moved or resized the item in Hammer: its box and data are theirs, not a run's.</summary>
        public bool Altered { get; init; }

        /// <summary>Set when the newest run did not come up with the item: the map has changed around it.</summary>
        public bool Stale { get; init; }
    }

    /// <summary>
    /// What the mapper decided about each item, kept beside the map as <c>&lt;map&gt;.viswright.json</c>.
    ///
    /// A review run adds what it found: an applied item as pending, a refused one that could be applied
    /// anyway as denied. Decisions already in the file are kept - the mapper's word stands over a run's -
    /// and items a run no longer finds are kept too, marked stale. A pinned run applies the accepted items
    /// and nothing else.
    /// </summary>
    public sealed class DecisionFile
    {
        public const int Version = 1;

        public string Path { get; }

        /// <summary>The folder the last review was written to, for Compile Pal's preview to find its items and pictures.</summary>
        public string? ReviewFolder { get; set; }
        public List<DecidedItem> Items { get; } = [];

        private DecisionFile(string path) => Path = path;

        public static string PathFor(string sourceVmf) =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sourceVmf))!,
                System.IO.Path.GetFileNameWithoutExtension(sourceVmf) + ".viswright.json");

        public bool Exists => File.Exists(Path);

        public int Pending => Items.Count(i => i.Decision == Decision.Pending && !i.Stale);

        public DecidedItem? this[string id] => Items.FirstOrDefault(i => i.Id == id);

        public static DecisionFile Load(string path)
        {
            var file = new DecisionFile(path);
            if (!File.Exists(path))
                return file;
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            if (json.RootElement.TryGetProperty("review", out var review))
                file.ReviewFolder = review.GetString();
            if (!json.RootElement.TryGetProperty("items", out var items))
                return file;
            foreach (var e in items.EnumerateArray())
            {
                static Vec3 V(JsonElement a) => new(a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble());
                var data = new Dictionary<string, double>();
                if (e.TryGetProperty("data", out var d))
                    foreach (var kv in d.EnumerateObject())
                        data[kv.Name] = kv.Value.GetDouble();
                string decision = e.TryGetProperty("decision", out var dec) ? dec.GetString() ?? "pending" : "pending";
                file.Items.Add(new DecidedItem(
                    e.GetProperty("id").GetString()!,
                    e.GetProperty("pass").GetString()!,
                    decision.ToLowerInvariant() switch { "accept" => Decision.Accept, "deny" => Decision.Deny, _ => Decision.Pending },
                    e.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                    V(e.GetProperty("mins")), V(e.GetProperty("maxs")))
                {
                    Detail = e.TryGetProperty("detail", out var det) ? det.GetString() : null,
                    Data = data,
                    Altered = e.TryGetProperty("altered", out var alt) && alt.GetBoolean(),
                    Stale = e.TryGetProperty("stale", out var st) && st.GetBoolean(),
                });
            }
            return file;
        }

        /// <summary>
        /// Takes in a run's items. Known ids keep their decision (and, if the mapper altered them, their
        /// box and data); new applied items come in pending; new refused items that carry the data to apply
        /// them come in denied; ids the run did not produce are marked stale.
        /// </summary>
        public void Merge(IEnumerable<ReviewItem> found)
        {
            var seen = new HashSet<string>();
            foreach (var item in found)
            {
                bool applicable = item.Status == ItemStatus.Applied || item.Data.Count > 0;
                if (!applicable || !seen.Add(item.Id))
                    continue;
                int at = Items.FindIndex(i => i.Id == item.Id);
                if (at < 0)
                {
                    Items.Add(new DecidedItem(item.Id, item.Pass, item.Status == ItemStatus.Applied ? Decision.Pending : Decision.Deny,
                        item.Reason, item.Mins, item.Maxs) { Detail = item.Detail, Data = item.Data });
                    continue;
                }
                var old = Items[at];
                Items[at] = old.Altered
                    ? old with { Reason = item.Reason, Stale = false }
                    : old with { Reason = item.Reason, Detail = item.Detail, Mins = item.Mins, Maxs = item.Maxs, Data = item.Data, Stale = false };
            }
            for (int i = 0; i < Items.Count; i++)
                if (!seen.Contains(Items[i].Id) && !Items[i].Stale)
                    Items[i] = Items[i] with { Stale = true };
        }

        /// <summary>Accepts every pending item. Returns how many.</summary>
        public int AcceptPending()
        {
            int n = 0;
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Decision == Decision.Pending && !Items[i].Stale)
                {
                    Items[i] = Items[i] with { Decision = Decision.Accept };
                    n++;
                }
            return n;
        }

        public void Set(string id, Func<DecidedItem, DecidedItem> change)
        {
            int at = Items.FindIndex(i => i.Id == id);
            if (at >= 0)
                Items[at] = change(Items[at]);
        }

        public bool Accepted(string id) => this[id] is { Decision: Decision.Accept, Stale: false };

        public void Save()
        {
            using var stream = File.Create(Path);
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            void Vector(string name, Vec3 v)
            {
                json.WriteStartArray(name);
                json.WriteNumberValue(Math.Round(v.X, 2));
                json.WriteNumberValue(Math.Round(v.Y, 2));
                json.WriteNumberValue(Math.Round(v.Z, 2));
                json.WriteEndArray();
            }
            json.WriteStartObject();
            json.WriteNumber("version", Version);
            json.WriteString("about", "Viswright's items for this map. Set decision to accept or deny; a pinned compile applies the accepted ones and nothing else.");
            if (ReviewFolder != null)
                json.WriteString("review", ReviewFolder);
            json.WriteStartArray("items");
            foreach (var item in Items.OrderBy(i => Array.IndexOf(ReviewLog.PassOrder, i.Pass)).ThenBy(i => i.Id, StringComparer.Ordinal))
            {
                json.WriteStartObject();
                json.WriteString("id", item.Id);
                json.WriteString("pass", item.Pass);
                json.WriteString("decision", item.Decision.ToString().ToLowerInvariant());
                json.WriteString("reason", item.Reason);
                if (item.Detail != null) json.WriteString("detail", item.Detail);
                Vector("mins", item.Mins);
                Vector("maxs", item.Maxs);
                if (item.Data.Count > 0)
                {
                    json.WriteStartObject("data");
                    foreach (var (key, value) in item.Data.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                        json.WriteNumber(key, Math.Round(value, 3));
                    json.WriteEndObject();
                }
                if (item.Altered) json.WriteBoolean("altered", true);
                if (item.Stale) json.WriteBoolean("stale", true);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        /// <summary>A doorway as numbers, for an item's data.</summary>
        public static Dictionary<string, double> DoorwayData(Doorway d)
        {
            var data = new Dictionary<string, double>
            {
                ["axis"] = d.NormalAxis, ["a0"] = d.A0, ["a1"] = d.A1, ["w0"] = d.W0, ["w1"] = d.W1, ["z0"] = d.Z0, ["z1"] = d.Z1,
                ["closes"] = d.Closes ? 1 : 0, ["startOpen"] = d.StartOpen ? 1 : 0,
            };
            if (!double.IsNaN(d.WallA0)) data["wallA0"] = d.WallA0;
            if (!double.IsNaN(d.WallA1)) data["wallA1"] = d.WallA1;
            return data;
        }

        /// <summary>The doorway an item's data describes, in <paramref name="door"/>.</summary>
        public static Doorway Doorway(SourceKit.Vmf.VmfNode door, IReadOnlyDictionary<string, double> d) =>
            new(door, (int)d["axis"], d["a0"], d["a1"], d["w0"], d["w1"], d["z0"], d["z1"], d["closes"] != 0, d["startOpen"] != 0,
                d.TryGetValue("wallA0", out var wa0) ? wa0 : double.NaN, d.TryGetValue("wallA1", out var wa1) ? wa1 : double.NaN);
    }
}
