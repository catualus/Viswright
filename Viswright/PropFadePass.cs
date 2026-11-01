using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceKit.Geometry;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>
    /// Pass E: static props with no fade distance get one, at the distance where they shrink to a few
    /// pixels.
    ///
    /// A prop_static with no fade is drawn as far as the world is visible, however small it has become.
    /// The distance chosen is where the prop's bounding sphere covers <see cref="Pixels"/> pixels on a
    /// 1920-wide screen at a 90° field of view: nothing a player would miss. Props in the 3D skybox (seen
    /// from far further than they are), props bigger than <see cref="LandmarkDiameter"/> (landmarks), and
    /// models marked never to be faded are left alone, as is any prop that already has a fade.
    /// </summary>
    public sealed class PropFadePass
    {
        /// <summary>Half the screen width in pixels over tan(45°): 1920 / 2 at a 90° horizontal FOV.</summary>
        public const double FocalPixels = 960;

        public const double LandmarkDiameter = 512;

        /// <summary>STUDIOHDR_FLAGS_NO_FORCED_FADE: the model asks never to be faded.</summary>
        private const int NoForcedFade = 0x800;

        public double Pixels { get; }

        private readonly List<(VmfNode Prop, string? MinBefore, string? MaxBefore)> changed = [];

        /// <summary>No fade is set beyond this: the map's own diagonal, past which nothing is ever seen.</summary>
        public double LongestView { get; }

        public PropFadePass(double pixels = 6, double longestView = double.MaxValue)
        {
            Pixels = pixels;
            LongestView = longestView;
        }

        public IReadOnlyList<(VmfNode Prop, string? MinBefore, string? MaxBefore)> Changed => changed;

        /// <summary>Why props were passed over, for the report.</summary>
        public Dictionary<string, int> Skipped { get; } = new();

        /// <summary>Per model: how many props got a fade, and at what distance.</summary>
        public SortedDictionary<string, (int Count, double Max)> Models { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Each prop passed over, and why, for the review.</summary>
        public List<(VmfNode Prop, string Reason)> SkippedProps { get; } = [];

        /// <summary>
        /// The fade distance for a prop of <paramref name="radius"/>: where its diameter projects to
        /// <see cref="Pixels"/> pixels, rounded to the nearest 64 units.
        /// </summary>
        public double FadeDistance(double radius) => Math.Round(2 * radius * FocalPixels / Pixels / 64) * 64;

        /// <param name="inSkybox">Whether a point is in the 3D skybox; props there are left alone.</param>
        public int Apply(VmfDocument map, GameContent content, Func<Vec3, bool> inSkybox)
        {
            foreach (var prop in map.Entities.Where(e => string.Equals(e.Classname, "prop_static", StringComparison.OrdinalIgnoreCase)))
            {
                if (double.TryParse(prop.Get("fademaxdist"), NumberStyles.Float, CultureInfo.InvariantCulture, out double existing) && existing > 0)
                    continue;
                string? path = prop.Get("model");
                var model = path == null ? null : content.Model(path);
                if (model == null) { Skip("the model was not found", prop); continue; }
                if ((model.Flags & NoForcedFade) != 0) { Skip("the model is marked never to fade", prop); continue; }
                if (!Vec3.TryParse(prop.Get("origin") ?? "", out var origin)) { Skip("no origin", prop); continue; }
                if (inSkybox(origin)) { Skip("in the 3D skybox", prop); continue; }

                double scale = double.TryParse(prop.Get("modelscale") ?? prop.Get("uniformscale"), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 1;
                var size = new Vec3(model.HullMax.X - model.HullMin.X, model.HullMax.Y - model.HullMin.Y, model.HullMax.Z - model.HullMin.Z) * scale;
                double radius = size.Length / 2;
                if (radius * 2 > LandmarkDiameter) { Skip("too big to fade (a landmark)", prop); continue; }
                if (radius <= 0) { Skip("the model has no size", prop); continue; }

                double max = Math.Max(256, FadeDistance(radius));
                // A fade further away than anything in the map can be seen from changes nothing.
                if (max >= LongestView) { Skip("it would never fade within the map at this many pixels", prop); continue; }
                double min = Math.Round(max * 0.85);
                changed.Add((prop, prop.Get("fademindist"), prop.Get("fademaxdist")));
                prop.Set("fademaxdist", max.ToString(CultureInfo.InvariantCulture));
                prop.Set("fademindist", min.ToString(CultureInfo.InvariantCulture));
                var (count, _) = Models.GetValueOrDefault(path!);
                Models[path!] = (count + 1, max);
            }
            return changed.Count;
        }

        private void Skip(string reason, VmfNode prop)
        {
            Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;
            SkippedProps.Add((prop, reason));
        }

        public void RevertAll()
        {
            foreach (var (prop, min, max) in changed)
            {
                Restore(prop, "fademindist", min);
                Restore(prop, "fademaxdist", max);
            }
            changed.Clear();
            Models.Clear();
        }

        private static void Restore(VmfNode node, string key, string? value)
        {
            if (value != null)
                node.Set(key, value);
            else if (node.GetPair(key) is { } pair)
                node.Remove(pair);
        }

        /// <summary>What the pass may change: the two fade keys of the props it changed.</summary>
        public void Declare(ChangeGuard guard)
        {
            var ids = changed.Select(c => c.Prop.Id).OfType<long>().Select(id => id.ToString(CultureInfo.InvariantCulture)).ToHashSet();
            guard.Allow("fades", change =>
            {
                if (change.Kind == VmfChangeKind.Removed) return false;
                var parts = change.Path.Split('/');
                if (parts.Length != 2 || !(parts[1] is "fademaxdist" or "fademindist")) return false;
                string head = parts[0];
                if (!head.StartsWith("entity#", StringComparison.Ordinal)) return false;
                string id = head["entity#".Length..].Split('~')[0];
                return ids.Contains(id);
            });
        }
    }
}
