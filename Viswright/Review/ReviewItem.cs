using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright.Review
{
    public enum ItemStatus { Applied, Rejected, Suggested }

    /// <summary>
    /// One thing a pass did, or considered and refused: where it is, why, and what it was measured to do.
    ///
    /// <see cref="Id"/> is anchored to the map rather than to the run - a door's name, a brush's bounds, a
    /// square of the grid - so the same candidate gets the same id on the next compile, and a decision
    /// about it can be found again.
    /// </summary>
    public sealed record ReviewItem(string Id, string Pass, ItemStatus Status, string Reason, Vec3 Mins, Vec3 Maxs)
    {
        /// <summary>How much less the standing points around it see, as a fraction; negative means more.</summary>
        public double? Gain { get; init; }

        /// <summary>A line of detail for the reader: a model, a fade distance, a door name.</summary>
        public string? Detail { get; init; }

        /// <summary>What applying the item again needs beyond its id and box, as <see cref="DecidedItem.Data"/> keeps it.</summary>
        public IReadOnlyDictionary<string, double> Data { get; init; } = new Dictionary<string, double>();

        /// <summary>Ids of the map objects the item added or changed, when it was applied.</summary>
        public IReadOnlyList<long> NodeIds { get; init; } = [];

        public Vec3 Centre => (Mins + Maxs) / 2;
    }

    /// <summary>Every item a run produced, for the review output.</summary>
    public sealed class ReviewLog
    {
        /// <summary>The order the passes are listed in, most consequential first.</summary>
        public static readonly string[] PassOrder = ["areaportals", "hints", "detail", "nodraw", "slim", "fades"];

        private readonly List<ReviewItem> items = [];
        private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);

        public IReadOnlyList<ReviewItem> Items => items;

        public void Add(ReviewItem item)
        {
            // Two candidates can share an anchor (doors with one id, props at one spot); keep both apart.
            int seen = ids.GetValueOrDefault(item.Id);
            ids[item.Id] = seen + 1;
            items.Add(seen == 0 ? item : item with { Id = $"{item.Id}~{seen + 1}" });
        }

        /// <summary>Drops every item of <paramref name="pass"/>, for a pass that is about to report again from scratch.</summary>
        public void Clear(string pass) => items.RemoveAll(i => i.Pass == pass);

        /// <summary>Marks every applied item of <paramref name="pass"/> rejected, for a pass undone as a whole.</summary>
        public void RejectAll(string pass, string reason)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].Pass == pass && items[i].Status == ItemStatus.Applied)
                    items[i] = items[i] with { Status = ItemStatus.Rejected, Reason = reason, NodeIds = [] };
        }

        /// <summary>The items in reading order: by pass, applied before rejected before suggested, biggest effect first.</summary>
        public List<ReviewItem> Ordered() => items
            .OrderBy(i => Array.IndexOf(PassOrder, i.Pass) is var k && k >= 0 ? k : PassOrder.Length)
            .ThenBy(i => i.Status)
            .ThenByDescending(i => Math.Abs(i.Gain ?? 0))
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

        /// <summary>A position written into an id: whole units, so it survives being read back.</summary>
        public static string Anchor(Vec3 p) =>
            string.Create(CultureInfo.InvariantCulture, $"{Math.Round(p.X):0}_{Math.Round(p.Y):0}_{Math.Round(p.Z):0}");

        /// <summary>The box an entity takes up: its brushes, or a small box round its origin.</summary>
        public static (Vec3 Mins, Vec3 Maxs) Bounds(VmfNode entity, double pointSize = 16)
        {
            var brushes = entity.Children("solid").Select(VmfBrush.Build).OfType<VmfBrush>().ToList();
            if (brushes.Count > 0)
                return (brushes.Select(b => b.Mins).Aggregate(Vec3.Min), brushes.Select(b => b.Maxs).Aggregate(Vec3.Max));
            Vec3.TryParse(entity.Get("origin") ?? "", out var origin);
            var half = new Vec3(pointSize / 2, pointSize / 2, pointSize / 2);
            return (origin - half, origin + half);
        }
    }
}
