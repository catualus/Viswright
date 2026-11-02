using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.Concurrent;
using System.Linq;

namespace Viswright
{
    /// <summary>
    /// The game's files, as the map will see them: materials to decide opacity, models to measure.
    /// Opened once per run and shared by the passes, from several threads at once: reads go through one
    /// lock, since the archives they come from are read through shared streams, and every answer is
    /// kept, since many doors and props share a model.
    /// </summary>
    public sealed class GameContent
    {
        private readonly Func<string, byte[]?> read;
        private readonly object reading = new();
        private readonly ConcurrentDictionary<string, StudioModel?> models = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> exists = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<StudioModel, bool> opaqueModels = new();
        private readonly ConcurrentDictionary<string, int> triangles = new(StringComparer.OrdinalIgnoreCase);

        public MaterialOpacity Opacity { get; }

        public GameContent(Func<string, byte[]?> read)
        {
            this.read = path => { lock (reading) return read(path); };
            Opacity = new MaterialOpacity(this.read);
        }

        public static GameContent Open(string gameFolder)
        {
            // GameFiles infers the install from a BSP's folder; a BSP that does not exist in the game's
            // maps folder gives it the game's own search paths and no pakfile.
            var files = GameFiles.Open(Path.Combine(gameFolder, "maps", "__viswright__.bsp"));
            return new GameContent(path => files.TryRead(path, out var bytes) ? bytes : null);
        }

        public StudioModel? Model(string path) => models.GetOrAdd(path, p =>
        {
            var bytes = read(p.Replace('\\', '/').ToLowerInvariant());
            var model = bytes == null ? null : StudioModel.Parse(bytes);
            return model is { Valid: true } ? model : null;
        });

        private bool Exists(string path) => exists.GetOrAdd(path, p => read(p) != null);

        /// <summary>
        /// The triangles a model draws at full detail, from its .dx90.vtx: each body part's first model,
        /// level of detail 0, summed over its meshes' strip groups. Zero when the file is missing or
        /// does not read as one.
        /// </summary>
        public int ModelTriangles(string modelPath) => triangles.GetOrAdd(modelPath, p =>
        {
            string path = Path.ChangeExtension(p.Replace('\\', '/').ToLowerInvariant(), ".dx90.vtx");
            return read(path) is { } bytes ? VtxTriangles(bytes) : 0;
        });

        internal static int VtxTriangles(byte[] b)
        {
            // FileHeader_t, then body parts (8 bytes), models (8), LODs (12), meshes (9, packed) and
            // strip groups (25, packed); every offset counts from the start of its own header.
            int Int(long at) => at >= 0 && at + 4 <= b.Length ? BitConverter.ToInt32(b, (int)at) : throw new InvalidDataException();
            try
            {
                if (Int(0) != 7)
                    return 0;
                long total = 0;
                int bodyParts = Int(28);
                long bodyAt = Int(32);
                for (int bp = 0; bp < bodyParts; bp++)
                {
                    long part = bodyAt + bp * 8;
                    if (Int(part) < 1)
                        continue;
                    long model = part + Int(part + 4);
                    if (Int(model) < 1)
                        continue;
                    long lod = model + Int(model + 4);
                    int meshes = Int(lod);
                    long meshAt = lod + Int(lod + 4);
                    for (int m = 0; m < meshes; m++)
                    {
                        long mesh = meshAt + m * 9;
                        int groups = Int(mesh);
                        long groupAt = mesh + Int(mesh + 4);
                        for (int g = 0; g < groups; g++)
                            total += Int(groupAt + g * 25 + 8) / 3;
                    }
                }
                return total is > 0 and < 10_000_000 ? (int)total : 0;
            }
            catch (InvalidDataException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Whether every texture a model draws with is opaque. Each texture is looked for in the model's
        /// folders in order, as the engine does; one that is not found anywhere makes the answer no.
        /// </summary>
        public bool IsModelOpaque(StudioModel model) => opaqueModels.GetOrAdd(model, Opaque);

        private bool Opaque(StudioModel model)
        {
            if (model.TextureNames.Count == 0)
                return false;
            foreach (string name in model.TextureNames)
            {
                bool found = false;
                foreach (string folder in model.TextureFolders.DefaultIfEmpty(""))
                {
                    string material = (folder + name).Replace('\\', '/');
                    if (!Exists("materials/" + material.ToLowerInvariant() + ".vmt"))
                        continue;
                    found = true;
                    if (!Opacity.IsOpaque(material))
                        return false;
                    break;
                }
                if (!found)
                    return false;
            }
            return true;
        }
    }
}
