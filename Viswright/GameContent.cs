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
