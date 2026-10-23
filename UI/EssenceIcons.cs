using ExileCore;
using System.Collections.Generic;
using System.IO;

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

        /// <summary>Load passes left. A failed icon is retried on a later pass, so one bad first frame doesn't lose icons all session.</summary>
        private int _attemptsLeft = 3;

        private const int RetryDelayMs = 5000;
        private long _nextAttemptMs;

        /// <summary>The icon texture for an essence type ("Misery", "Horror"), or null when unavailable.</summary>
        public nint? For(string typeName)
        {
            if (_attemptsLeft > 0 && System.Environment.TickCount64 >= _nextAttemptMs)
                Load();

            return _textures.TryGetValue(typeName, out var id) && id != 0 ? id : null;
        }

        private void Load()
        {
            _attemptsLeft--;
            _nextAttemptMs = System.Environment.TickCount64 + RetryDelayMs;
            var assembly = typeof(EssenceIcons).Assembly;
            var failed = 0;
            try
            {
                Directory.CreateDirectory(_directory);
            }
            catch (System.Exception ex)
            {
                DebugWindow.LogError($"[BECH] Essence icon folder could not be created: {ex.Message}");
                return;
            }

            foreach (var resource in assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(ResourcePrefix, System.StringComparison.Ordinal))
                    continue;

                var type = Path.GetFileNameWithoutExtension(resource[ResourcePrefix.Length..]);
                if (_textures.ContainsKey(type))
                    continue;

                try
                {
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
                    else
                        failed++;
                }
                catch (System.Exception ex)
                {
                    failed++;
                    if (_attemptsLeft == 0)
                        DebugWindow.LogError($"[BECH] Essence icon '{type}' could not be loaded: {ex.Message}");
                }
            }

            if (failed == 0)
                _attemptsLeft = 0;
        }
    }
}
