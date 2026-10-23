using ExileCore;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BetterEssenceCorruptionHelper.UI
{
    /// <summary>
    /// One inventory icon per essence type (the Deafening art, and the four corruption-only essences),
    /// from poewiki, 32x32. They are embedded in the plugin DLL - so Source and Compiled installs work
    /// alike - written to the plugin's config folder on first use, and loaded with ExileCore's
    /// Graphics.InitImage, like ExpeditionIcons loads its icon sheet. Loaded lazily on the render
    /// thread, the first time the settings page is drawn.
    /// </summary>
    internal sealed class EssenceIcons(Graphics graphics, string directory)
    {
        private const string ResourcePrefix = "BetterEssenceCorruptionHelper.Icons.";

        private readonly Graphics _graphics = graphics;
        private readonly string _directory = directory;
        private readonly Dictionary<string, nint> _textures = new();
        private bool _loaded;

        /// <summary>The icon texture for an essence type ("Misery", "Horror"), or null when unavailable.</summary>
        public nint? For(string typeName)
        {
            if (!_loaded)
                Load();

            return _textures.TryGetValue(typeName, out var id) && id != 0 ? id : null;
        }

        private void Load()
        {
            _loaded = true;
            try
            {
                Directory.CreateDirectory(_directory);
                var assembly = typeof(EssenceIcons).Assembly;
                foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix)))
                {
                    var type = Path.GetFileNameWithoutExtension(resource[ResourcePrefix.Length..]);
                    var path = Path.Join(_directory, type + ".png");

                    using (var stream = assembly.GetManifestResourceStream(resource)!)
                    {
                        if (!File.Exists(path) || new FileInfo(path).Length != stream.Length)
                        {
                            using var file = File.Create(path);
                            stream.CopyTo(file);
                        }
                    }

                    var name = $"bech-essence-{type}";
                    if (_graphics.InitImage(name, path))
                        _textures[type] = _graphics.GetTextureId(name);
                }
            }
            catch (System.Exception ex)
            {
                DebugWindow.LogError($"[BECH] Essence icons could not be loaded: {ex.Message}");
            }
        }
    }
}
