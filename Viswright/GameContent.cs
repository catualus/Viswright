using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Viswright
{
    /// <summary>
    /// The game's files, as the map will see them: materials to decide opacity, models to measure.
    /// Opened once per run and shared by the passes.
    /// </summary>
    public sealed class GameContent
    {
        private readonly Func<string, byte[]?> read;
        private readonly Dictionary<string, StudioModel?> models = new(StringComparer.OrdinalIgnoreCase);

        public MaterialOpacity Opacity { get; }

        public GameContent(Func<string, byte[]?> read)
        {
            this.read = read;
            Opacity = new MaterialOpacity(read);
        }

        public static GameContent Open(string gameFolder)
        {
            // GameFiles infers the install from a BSP's folder; a BSP that does not exist in the game's
            // maps folder gives it the game's own search paths and no pakfile.
            var files = GameFiles.Open(Path.Combine(gameFolder, "maps", "__viswright__.bsp"));
            return new GameContent(path => files.TryRead(path, out var bytes) ? bytes : null);
        }

        public StudioModel? Model(string path)
        {
            if (models.TryGetValue(path, out var known))
                return known;
            var bytes = read(path.Replace('\\', '/').ToLowerInvariant());
            var model = bytes == null ? null : StudioModel.Parse(bytes);
            models[path] = model is { Valid: true } ? model : null;
            return models[path];
        }

        /// <summary>
        /// Whether every texture a model draws with is opaque. Each texture is looked for in the model's
        /// folders in order, as the engine does; one that is not found anywhere makes the answer no.
        /// </summary>
        public bool IsModelOpaque(StudioModel model)
        {
            if (model.TextureNames.Count == 0)
                return false;
            foreach (string name in model.TextureNames)
            {
                bool found = false;
                foreach (string folder in model.TextureFolders.DefaultIfEmpty(""))
                {
                    string material = (folder + name).Replace('\\', '/');
                    if (read("materials/" + material.ToLowerInvariant() + ".vmt") == null)
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
