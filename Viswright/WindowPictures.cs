using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using SourceKit;
using SourceKit.Geometry;
using Viswright.Review;

namespace Viswright
{
    /// <summary>
    /// What a shut window shows, for Compile Pal's BAKE step to draw once the map is lit: a box of the
    /// room behind it, with what stands in the room in slices of its depth.
    ///
    /// A flat picture makes a room look as shallow as the glass. So for each side a window is seen
    /// from, the room on the other side is traced in the first compile - how deep it goes, how wide
    /// and how tall halfway in - and lined with a shell: a back wall, side walls, floor and ceiling,
    /// each a thin brush whose face looks into the box and shows that part of the room. The back is drawn
    /// square on from the window inward, so what stands in the room lands on it as seen from the front;
    /// a side, the floor and the ceiling show only what lies within a few units of them, square on. Being
    /// real geometry, the box moves as the room would.
    ///
    /// The shell is part of the window's stand-in, so it is only drawn while the window is shut, and
    /// its faces look one way, into the box, so it is only seen from the viewer's side. Each face
    /// stands a few units beyond the wall it copies, cut into cells, and a cell is kept only where a
    /// real wall stands behind it; a side open to a street is left out. The face the room is seen
    /// through carries what is in the opening itself - glass, bars, a frame - cut out elsewhere.
    /// Only one side of a window gets a shell, the room more closed in; a window whose room cannot
    /// be traced, or whose back wall is mostly open, gets one flat picture on each side instead.
    ///
    /// The slices look along the window's normal, so a surface running into the room - a mezzanine
    /// floor, a walkway, the side of a partition - is edge on to all of them and lost. The room's
    /// largest flat surfaces each get a card of their own instead, where they are, drawn looking
    /// straight at them, and the slices leave them out.
    ///
    /// Every face gets a material of its own, <c>viswright/&lt;map&gt;/w_&lt;where&gt;_&lt;side&gt;_&lt;part&gt;</c>,
    /// its texture laid exactly across it at half a texel a unit, and a black placeholder of that size
    /// is written into the game's materials for VBSP. <c>bake.json</c> beside them tells BAKE where
    /// each face is seen from and where the room starts.
    /// </summary>
    public sealed class WindowPictures(string map, string gameFolder)
    {
        /// <summary>A shell brush: its box, which face shows the room, that face's material and texture axes.</summary>
        public sealed record Brush(Vec3 Lo, Vec3 Hi, int Face, string Material, (string U, string V) Axes);

        private sealed record BakeEntry(string Material, Vec3 Eye, Vec3 ClipNormal, double ClipDistance, bool Square = false,
            double? FarDistance = null, bool Alpha = false, IReadOnlyList<(int Axis, double At)>? Skip = null, bool Translucent = false);

        /// <summary>A world triangle lying flat on an axis plane: the plane, the way it faces, its area and extent.</summary>
        private readonly record struct Flat(int Axis, int Sign, double At, double Area, Vec3 Min, Vec3 Max);

        private List<Flat>? flats;

        private readonly HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<BakeEntry> entries = [];

        /// <summary>How far in front of a window its shell is baked from.</summary>
        public const double EyeDistance = 600;

        private const double Margin = 4, Thickness = 2, MaxDepth = 2048, MaxReach = 1024;

        /// <summary>The most slices a room's depth is cut into.</summary>
        private const int MaxSlices = 8;

        /// <summary>The most surface cards a room gets, and the least area a surface needs for one.</summary>
        private const int MaxCards = 6;
        private const double MinCardArea = 64 * 64;

        public string Map { get; } = map.ToLowerInvariant();

        /// <summary>The first compile, to trace rooms in; without it every window gets a flat picture.</summary>
        public BspSpace? Space { get; set; }

        /// <summary>
        /// The first compile's drawn surfaces, which a shell face must sit behind: solid that is sky does
        /// not hide anything, since the sky is drawn behind everything else.
        /// </summary>
        public (BspGeometry Geometry, TriangleBvh Bvh)? Surfaces { get; set; }

        /// <summary>The placeholders written this run, by material name.</summary>
        public IReadOnlyCollection<string> Written => written;

        /// <summary>
        /// The brushes of the shell seen from the <paramref name="high"/> or low side of
        /// <paramref name="window"/>, lining the room on the other side; empty when it cannot be traced.
        /// </summary>
        public List<Brush> Shell(Window window, bool high)
        {
            var brushes = new List<Brush>();
            if (Space is not { } space)
                return brushes;
            int a = window.NormalAxis, c = 1 - a;
            int r = high ? -1 : 1;                       // into the room, along the normal axis
            var centre = window.Centre;
            double far = r > 0 ? window.Maxs[a] : window.Mins[a];
            string side = high ? "b" : "a";

            Vec3 At(double along, double across, double z) => a == 0 ? new Vec3(along, across, z) : new Vec3(across, along, z);
            bool Blocked(Vec3 p) => space.IsSolid(p);

            // how deep the room goes straight in, then how wide and tall it is halfway
            double depth = 0;
            while (depth < MaxDepth && !Blocked(At(far + r * (depth + 1), centre[c], centre.Z)))
                depth += 4;
            if (depth < 48 || depth >= MaxDepth)
                return brushes;
            double mid = far + r * depth / 2;
            double? Reach(int axis, int sign)
            {
                for (double d = 4; d <= MaxReach; d += 4)
                {
                    var p = axis == 2 ? At(mid, centre[c], centre.Z + sign * d) : At(mid, centre[c] + sign * d, centre.Z);
                    if (Blocked(p))
                        return (axis == 2 ? centre.Z : centre[c]) + sign * (d - 4);
                }
                return null;
            }
            double? left = Reach(c, -1), right = Reach(c, +1), floor = Reach(2, -1), ceiling = Reach(2, +1);
            // across and up the box spans at least the window
            double l0 = Math.Min(left ?? window.Mins[c] - 256, window.Mins[c]), l1 = Math.Max(right ?? window.Maxs[c] + 256, window.Maxs[c]);
            double z0 = Math.Min(floor ?? window.Mins.Z - 256, window.Mins.Z), z1 = Math.Max(ceiling ?? window.Maxs.Z + 256, window.Maxs.Z);
            double back = far + r * depth;

            // seen from in front of the window on the viewer's side; the room starts at the window
            var normal = a == 0 ? new Vec3(r, 0, 0) : new Vec3(0, r, 0);
            var eye = At(centre[a] - r * (window.Maxs[a] - window.Mins[a]) / 2 - r * EyeDistance, centre[c], centre.Z);
            double clip = normal.Dot(centre);

            string Name(string part) => $"viswright/{Map}/w_{ReviewLog.Anchor(window.Mins)}_{side}_{part}".ToLowerInvariant();

            // back wall: mostly hidden behind the real one, or no shell at all
            if (Place(space, Surfaces, At, a, r, back, back, 0, l0, l1, z0, z1, far) is not { } bf || bf.Hidden < 0.6)
                return brushes;
            int backFace = r > 0 ? (a == 0 ? 2 : 5) : (a == 0 ? 3 : 4);

            // the room's large flat surfaces inside the box, each a card where it is
            var room = Box(At(far, l0, z0), At(back, l1, z1));
            var cards = Cards(room, a, r);
            var skip = cards.Select(k => (k.Axis, k.At)).ToList();
            for (int k = 0; k < cards.Count; k++)
            {
                var (axis, sign, at, lo, hi) = cards[k];
                int face = axis == 2 ? (sign > 0 ? 0 : 1) : axis == 0 ? (sign > 0 ? 3 : 2) : (sign > 0 ? 4 : 5);
                string name = Name($"card{k}");
                var axes = Axes(face, lo, hi, out int w, out int h, density: 0.5);
                Placeholder(name, w, h);
                var n = axis == 0 ? new Vec3(sign, 0, 0) : axis == 1 ? new Vec3(0, sign, 0) : new Vec3(0, 0, sign);
                // looking straight at the surface: what lies from 8 units in front of it to 4 behind
                entries.Add(new BakeEntry(name, (lo + hi) / 2 + n * EyeDistance, -n, -sign * at - 8, Square: true,
                    FarDistance: -sign * at + 4, Alpha: true));
                brushes.Add(new Brush(lo, hi, face, name, axes));
            }

            // What stands in the room - partitions, doors, props - in slices of its depth: a card at the
            // front of each slice, showing what is in the slice and see-through where nothing is, so
            // nearer things slide over farther ones as the room would. The back shows what lies beyond.
            int slices = Math.Clamp((int)(depth / 128), 0, MaxSlices);
            double step = depth / (slices + 1);
            for (int k = 0; k < slices; k++)
            {
                double front = far + r * (k * step + 2);
                var card = Box(At(front, l0, z0), At(front + r * 1, l1, z1));
                string name = Name($"slice{k}");
                var axes = Axes(backFace, card.Lo, card.Hi, out int w, out int h, density: 0.5);
                Placeholder(name, w, h);
                entries.Add(new BakeEntry(name, eye, normal, normal.Dot(At(far + r * (k * step + 1), centre[c], centre.Z)), Square: true,
                    FarDistance: normal.Dot(At(far + r * ((k + 1) * step + 2), centre[c], centre.Z)), Alpha: true, Skip: skip));
                brushes.Add(new Brush(card.Lo, card.Hi, backFace, name, axes));
            }
            // drawn from where the last slice ends; from the window's own plane the parallel rays would
            // meet the inside of the front wall all round the window, and show the room turned round
            double beyond = normal.Dot(At(far + r * (slices * step + 1), centre[c], centre.Z));
            brushes.AddRange(Make(bf, backFace, Name("back"), eye, normal, beyond, square: true, skip));

            // the sides, floor and ceiling that have a wall to hide in
            void Add(string part, int axis, int sign, double at, int face, bool traced)
            {
                if (!traced) return;
                // what lies within a few units of the wall, seen square on
                var toward = axis == 2 ? new Vec3(0, 0, sign) : c == 0 ? new Vec3(sign, 0, 0) : new Vec3(0, sign, 0);
                double surface = sign * at - 16;
                if (Place(space, Surfaces, At, a, r, at, back, axis == 2 ? 2 : 1, l0, l1, z0, z1, far, sign) is { } placed && placed.Hidden >= 0.25)
                    brushes.AddRange(Make(placed, face, Name(part), eye, toward, surface, square: true));
            }
            int acrossLow = c == 0 ? 3 : 4, acrossHigh = c == 0 ? 2 : 5;   // faces looking along +across, -across
            Add("left", c, -1, l0, acrossLow, left != null);
            Add("right", c, +1, l1, acrossHigh, right != null);
            Add("floor", 2, -1, z0, 0, floor != null);
            Add("ceiling", 2, +1, z1, 1, ceiling != null);
            return brushes;
        }

        /// <summary>One shell face as placed: its whole box, the pieces of it that are hidden, and how much of it they are.</summary>
        private sealed record Placed((Vec3 Lo, Vec3 Hi) Whole, List<(Vec3 Lo, Vec3 Hi)> Pieces, double Hidden);

        private const double Cell = 32;

        /// <summary>
        /// One shell face in the pieces that can be hidden. The shells are always drawn, so a piece
        /// anywhere it could be seen from the front - over a doorway or another window of the room,
        /// against open sky - would hang in the air. The face is cut into cells, and each cell stands
        /// at the nearest of four depths past <paramref name="at"/> where it is hidden; a cell hidden at
        /// none is left out, a hole in the face.
        /// </summary>
        /// <param name="back">Where the back wall is, along the window's normal: how deep the sides run.</param>
        /// <param name="kind">0 the back wall, 1 a side, 2 the floor or ceiling.</param>
        private static Placed? Place(BspSpace space, (BspGeometry Geometry, TriangleBvh Bvh)? surfaces, Func<double, double, double, Vec3> At, int a, int r, double at, double back, int kind,
            double l0, double l1, double z0, double z1, double far, int sign = 0)
        {
            const int Pushes = 4;
            double near = far, deep = back + r * (Margin + (Pushes - 1) * 16 + Thickness);
            // the slab at each depth; the extents along the face are the same at every depth
            var boxes = new (Vec3 Lo, Vec3 Hi)[Pushes];
            for (int push = 0; push < Pushes; push++)
            {
                double plane = at + (kind == 0 ? r : sign) * (Margin + push * 16);
                boxes[push] = kind switch
                {
                    0 => Box(At(plane, l0 - Margin, z0 - Margin), At(plane + r * Thickness, l1 + Margin, z1 + Margin)),
                    1 => Box(At(near, plane, z0 - Margin), At(deep, plane + sign * Thickness, z1 + Margin)),
                    _ => Box(At(near, l0 - Margin, plane), At(deep, l1 + Margin, plane + sign * Thickness)),
                };
            }
            int axis = kind == 0 ? a : kind == 1 ? 1 - a : 2, facing = kind == 0 ? -r : -sign;
            int u = axis == 0 ? 1 : 0, v = axis == 2 ? 1 : 2;
            var whole = boxes[0];
            int cols = Math.Max(1, (int)Math.Ceiling((whole.Hi[u] - whole.Lo[u]) / Cell)), rows = Math.Max(1, (int)Math.Ceiling((whole.Hi[v] - whole.Lo[v]) / Cell));
            double U(int i) => Math.Min(whole.Hi[u], whole.Lo[u] + i * Cell);
            double V(int j) => Math.Min(whole.Hi[v], whole.Lo[v] + j * Cell);
            var toward = axis == 0 ? new Vec3(-facing, 0, 0) : axis == 1 ? new Vec3(0, -facing, 0) : new Vec3(0, 0, -facing);

            // A cell stands at a depth when a ray to it from a little inside the room meets a drawn surface
            // that is not sky first: the face copies a real wall, floor or ceiling there. The shell is only
            // drawn while its window is shut and only from inside the room, so it need not be out of sight.
            bool HiddenAt(int push, double s, double tt)
            {
                double plane = facing > 0 ? boxes[push].Hi[axis] : boxes[push].Lo[axis];
                double offset = Margin + push * 16;
                var p = axis switch
                {
                    0 => new Vec3(plane - facing, s, tt),
                    1 => new Vec3(s, plane - facing, tt),
                    _ => new Vec3(s, tt, plane - facing),
                };
                if (surfaces is { } found)
                {
                    int hit = found.Bvh.Trace(p - toward * (offset + 9), toward, offset + 10);
                    if (hit < 0 || found.Geometry.Sky[hit])
                        return false;
                }
                return true;
            }
            var level = new int[cols, rows];
            int count = 0;
            for (int i = 0; i < cols; i++)
                for (int j = 0; j < rows; j++)
                {
                    level[i, j] = -1;
                    for (int push = 0; push < Pushes && level[i, j] < 0; push++)
                        if (HiddenAt(push, (U(i) + U(i + 1)) / 2, (V(j) + V(j + 1)) / 2))
                            level[i, j] = push;
                    if (level[i, j] >= 0)
                        count++;
                }
            if (count == 0)
                return null;

            // cells at one depth: runs along each row, each carried down while the rows below match
            var pieces = new List<(Vec3 Lo, Vec3 Hi)>();
            var done = new bool[cols, rows];
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < cols; i++)
                {
                    int depth = level[i, j];
                    if (depth < 0 || done[i, j]) continue;
                    int i1 = i;
                    while (i1 + 1 < cols && level[i1 + 1, j] == depth && !done[i1 + 1, j]) i1++;
                    int j1 = j;
                    while (j1 + 1 < rows && Enumerable.Range(i, i1 - i + 1).All(k => level[k, j1 + 1] == depth && !done[k, j1 + 1]))
                        j1++;
                    for (int x = i; x <= i1; x++)
                        for (int y = j; y <= j1; y++)
                            done[x, y] = true;
                    var lo = With(With(boxes[depth].Lo, u, U(i)), v, V(j));
                    var hi = With(With(boxes[depth].Hi, u, U(i1 + 1)), v, V(j1 + 1));
                    pieces.Add((lo, hi));
                }
            return new Placed(whole, pieces, (double)count / (cols * rows));
        }

        /// <summary>
        /// The largest flat surfaces well inside <paramref name="room"/>, by plane, each as a card a unit
        /// in front of it over the part of the plane it covers. A surface square to the window counts
        /// only if it faces the window; the walls, floor and ceiling the shell already copies are left out.
        /// </summary>
        private List<(int Axis, int Sign, double At, Vec3 Lo, Vec3 Hi)> Cards((Vec3 Lo, Vec3 Hi) room, int a, int r)
        {
            const double Inset = 24;
            var inner = (Lo: room.Lo + new Vec3(Inset, Inset, Inset), Hi: room.Hi - new Vec3(Inset, Inset, Inset));
            var planes = new Dictionary<(int Axis, int Sign, double At), (double Area, Vec3 Min, Vec3 Max)>();
            foreach (var f in Flats())
            {
                var mid = (f.Min + f.Max) / 2;
                if (mid.X < inner.Lo.X || mid.Y < inner.Lo.Y || mid.Z < inner.Lo.Z || mid.X > inner.Hi.X || mid.Y > inner.Hi.Y || mid.Z > inner.Hi.Z)
                    continue;
                if (f.Axis == a && f.Sign != -r)
                    continue;
                var key = (f.Axis, f.Sign, f.At);
                planes[key] = planes.TryGetValue(key, out var p) ? (p.Area + f.Area, Vec3.Min(p.Min, f.Min), Vec3.Max(p.Max, f.Max)) : (f.Area, f.Min, f.Max);
            }
            var lo = room.Lo + new Vec3(Margin, Margin, Margin);
            var hi = room.Hi - new Vec3(Margin, Margin, Margin);
            return planes.Where(p => p.Value.Area >= MinCardArea).OrderByDescending(p => p.Value.Area).Take(MaxCards)
                .Select(p =>
                {
                    var (axis, sign, at) = p.Key;
                    var cardLo = Vec3.Max(p.Value.Min - new Vec3(2, 2, 2), lo);
                    var cardHi = Vec3.Min(p.Value.Max + new Vec3(2, 2, 2), hi);
                    double near = at + sign * 1, outer = at + sign * 2;
                    return (axis, sign, at, With(cardLo, axis, Math.Min(near, outer)), With(cardHi, axis, Math.Max(near, outer)));
                })
                .ToList();
        }

        /// <summary>The first compile's world triangles that lie flat on an axis plane, read once.</summary>
        private List<Flat> Flats()
        {
            if (flats != null)
                return flats;
            flats = [];
            if (Surfaces is not { } surfaces)
                return flats;
            var g = surfaces.Geometry;
            for (int t = 0; t < g.TriangleCount; t++)
            {
                if (g.Sky[t])
                    continue;
                Vec3 p = g.Corners[t * 3], q = g.Corners[t * 3 + 1], s = g.Corners[t * 3 + 2];
                var n = (s - p).Cross(q - p);    // faces are wound clockwise seen from in front
                double area = n.Length / 2;
                if (area < 1)
                    continue;
                n /= 2 * area;
                int axis = Math.Abs(n.X) > 0.999 ? 0 : Math.Abs(n.Y) > 0.999 ? 1 : Math.Abs(n.Z) > 0.999 ? 2 : -1;
                if (axis < 0)
                    continue;
                flats.Add(new Flat(axis, n[axis] > 0 ? 1 : -1, Math.Round(p[axis]), area, Vec3.Min(Vec3.Min(p, q), s), Vec3.Max(Vec3.Max(p, q), s)));
            }
            return flats;
        }

        private static (Vec3 Lo, Vec3 Hi) Box(Vec3 p, Vec3 q) => (Vec3.Min(p, q), Vec3.Max(p, q));

        private static Vec3 With(Vec3 p, int axis, double value) => axis switch
        {
            0 => p with { X = value },
            1 => p with { Y = value },
            _ => p with { Z = value },
        };

        /// <summary>The brushes of one placed face: its texture laid across the whole face, so the pieces join up.</summary>
        private List<Brush> Make(Placed placed, int face, string name, Vec3 eye, Vec3 normal, double clip, bool square,
            IReadOnlyList<(int Axis, double At)>? skip = null)
        {
            var axes = Axes(face, placed.Whole.Lo, placed.Whole.Hi, out int w, out int h, density: 0.5);
            Placeholder(name, w, h);
            entries.Add(new BakeEntry(name, eye, normal, clip, square, Skip: skip));
            return placed.Pieces.Select(p => new Brush(p.Lo, p.Hi, face, name, axes)).ToList();
        }

        /// <summary>
        /// The material and texture axes for the face of the stand-in <paramref name="lo"/>..<paramref name="hi"/>
        /// on the viewer's side of a window with a room shell: what is in the opening itself - glass,
        /// bars, a frame - square on and see-through elsewhere, since it is part of the room shut away.
        /// </summary>
        public (string Material, (string U, string V) Axes) Front(Window window, Vec3 lo, Vec3 hi, bool high)
        {
            string name = $"viswright/{Map}/w_{ReviewLog.Anchor(window.Mins)}_{(high ? "b" : "a")}_front".ToLowerInvariant();
            int a = window.NormalAxis;
            int face = high ? (a == 0 ? 3 : 4) : (a == 0 ? 2 : 5);
            var axes = Axes(face, lo, hi, out int w, out int h, density: 1);
            Placeholder(name, w, h);
            int r = high ? -1 : 1;
            var normal = a == 0 ? new Vec3(r, 0, 0) : new Vec3(0, r, 0);
            // from the viewer's face of the wall to a unit past its other face
            double near = r > 0 ? window.Mins[a] : -window.Maxs[a], beyond = r > 0 ? window.Maxs[a] + 1 : -(window.Mins[a] - 1);
            entries.Add(new BakeEntry(name, window.Centre - normal * 1500, normal, near, Square: true, FarDistance: beyond, Translucent: true));
            return (name, axes);
        }

        /// <summary>
        /// The material and texture axes for one broad face of the flat stand-in <paramref name="lo"/>..<paramref name="hi"/>
        /// in <paramref name="window"/>, for a side whose room could not be traced: a picture from far out in front.
        /// </summary>
        public (string Material, (string U, string V) Axes) Face(Window window, Vec3 lo, Vec3 hi, bool high)
        {
            string name = $"viswright/{Map}/w_{ReviewLog.Anchor(window.Mins)}_{(high ? "b" : "a")}".ToLowerInvariant();
            int a = window.NormalAxis;
            int face = high ? (a == 0 ? 3 : 4) : (a == 0 ? 2 : 5);
            var axes = Axes(face, lo, hi, out int w, out int h, density: 1);
            Placeholder(name, w, h);
            int r = high ? -1 : 1;
            var normal = a == 0 ? new Vec3(r, 0, 0) : new Vec3(0, r, 0);
            var centre = window.Centre;
            var eye = centre - normal * 1500;
            entries.Add(new BakeEntry(name, eye, normal, normal.Dot(centre)));
            return (name, axes);
        }

        /// <summary>
        /// Texture axes laying a texture once across face <paramref name="face"/> (top, bottom, -x, +x, +y, -y)
        /// of the box: u along the face's first other axis, v downward or along -y, a power-of-two texture
        /// of about <paramref name="density"/> texels a unit.
        /// </summary>
        private static (string U, string V) Axes(int face, Vec3 lo, Vec3 hi, out int width, out int height, double density)
        {
            int axis = face < 2 ? 2 : face < 4 ? 0 : 1;
            int ua = axis == 0 ? 1 : 0;                  // y on x faces, x otherwise
            int va = axis == 2 ? 1 : 2;                  // y on z faces, z otherwise (both downward)
            double uLength = hi[ua] - lo[ua], vLength = hi[va] - lo[va];
            width = PowerOfTwo(uLength * density);
            height = PowerOfTwo(vLength * density);
            double uScale = uLength / width, vScale = vLength / height;
            string uDir = ua == 0 ? "1 0 0" : "0 1 0";
            string vDir = va == 1 ? "0 -1 0" : "0 0 -1";
            return ($"[{uDir} {F(-lo[ua] / uScale)}] {F(uScale)}", $"[{vDir} {F(hi[va] / vScale)}] {F(vScale)}");
        }

        /// <summary>A texture edge for this many texels: a power of two, 16 to 512.</summary>
        public static int PowerOfTwo(double texels)
        {
            int size = 16;
            while (size < texels && size < 512)
                size *= 2;
            return size;
        }

        private void Placeholder(string name, int w, int h)
        {
            if (!written.Add(name))
                return;
            string path = Path.Combine(gameFolder, "materials", name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".vtf", PlaceholderVtf(w, h));
            // a slice is cut out where nothing is; its placeholder is all cut out
            // what is in a window's opening is translucent by degrees, as glass is
            string alpha = name.EndsWith("_front", StringComparison.Ordinal) ? "\t\"$translucent\" \"1\"\r\n"
                : name.Contains("_slice", StringComparison.Ordinal) || name.Contains("_card", StringComparison.Ordinal) ? "\t\"$alphatest\" \"1\"\r\n" : "";
            File.WriteAllText(path + ".vmt", "\"UnlitGeneric\"\r\n{\r\n\t\"$basetexture\" \"" + name + "\"\r\n" + alpha + "\t\"%keywords\" \"viswright\"\r\n}\r\n");
        }

        /// <summary>
        /// Writes <c>bake.json</c> beside the placeholders: for each face, the eye it is drawn from and
        /// the plane where the room starts, nearer than which nothing is drawn.
        /// </summary>
        public void WriteBakeFile()
        {
            string path = Path.Combine(gameFolder, "materials", "viswright", Map, "bake.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            json.WriteStartArray("faces");
            foreach (var e in entries.GroupBy(e => e.Material).Select(g => g.Last()))
            {
                json.WriteStartObject();
                json.WriteString("material", e.Material);
                json.WriteStartArray("eye"); json.WriteNumberValue(e.Eye.X); json.WriteNumberValue(e.Eye.Y); json.WriteNumberValue(e.Eye.Z); json.WriteEndArray();
                json.WriteStartArray("clip");
                json.WriteNumberValue(e.ClipNormal.X); json.WriteNumberValue(e.ClipNormal.Y); json.WriteNumberValue(e.ClipNormal.Z); json.WriteNumberValue(e.ClipDistance);
                json.WriteEndArray();
                // square on: straight along the clip normal rather than from the eye
                if (e.Square)
                    json.WriteBoolean("square", true);
                // a slice: only what lies before this distance, and see-through where nothing is
                if (e.FarDistance is { } farDistance)
                    json.WriteNumber("far", farDistance);
                if (e.Alpha)
                    json.WriteBoolean("alpha", true);
                // see-through by degrees, glass and all
                if (e.Translucent)
                    json.WriteBoolean("translucent", true);
                // planes a card shows, left out of this picture
                if (e.Skip is { Count: > 0 } skip)
                {
                    json.WriteStartArray("skip");
                    foreach (var (axis, at) in skip)
                    {
                        json.WriteStartArray();
                        json.WriteNumberValue(axis);
                        json.WriteNumberValue(at);
                        json.WriteEndArray();
                    }
                    json.WriteEndArray();
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        /// <summary>A black DXT1 VTF 7.2 with every mip level: all-zero blocks are black.</summary>
        internal static byte[] PlaceholderVtf(int width, int height)
        {
            int levels = 1, data = 0;
            for (int w = width, h = height; ; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2), levels++)
            {
                data += Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8;
                if (w == 1 && h == 1) break;
            }
            var bytes = new byte[80 + data];
            "VTF\0"u8.CopyTo(bytes);
            BitConverter.GetBytes(7u).CopyTo(bytes, 4);
            BitConverter.GetBytes(2u).CopyTo(bytes, 8);
            BitConverter.GetBytes(80u).CopyTo(bytes, 12);
            BitConverter.GetBytes((ushort)width).CopyTo(bytes, 16);
            BitConverter.GetBytes((ushort)height).CopyTo(bytes, 18);
            BitConverter.GetBytes(0xCu).CopyTo(bytes, 20);          // clamp s and t
            BitConverter.GetBytes((ushort)1).CopyTo(bytes, 24);     // frames
            BitConverter.GetBytes(1f).CopyTo(bytes, 48);            // bump scale
            BitConverter.GetBytes(13).CopyTo(bytes, 52);            // DXT1
            bytes[56] = (byte)levels;
            BitConverter.GetBytes(-1).CopyTo(bytes, 57);            // no low-resolution image
            BitConverter.GetBytes((ushort)1).CopyTo(bytes, 63);     // depth
            return bytes;
        }

        private static string F(double v) => Math.Round(v, 4).ToString(CultureInfo.InvariantCulture);
    }
}
