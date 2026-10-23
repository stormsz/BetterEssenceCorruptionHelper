using BetterEssenceCorruptionHelper.Catalogue;
using ImGuiNET;
using SharpDX;
using System;
using System.Numerics;
using Vector2 = System.Numerics.Vector2;

namespace BetterEssenceCorruptionHelper.UI
{
    /// <summary>
    /// Drawing shared by the settings-page previews (<see cref="IndicatorPreview"/>, <see cref="DebugPreview"/>):
    /// the canvas child window, a floor grid and a simulated monolith ground label.
    /// </summary>
    internal static class PreviewCanvas
    {
        /// <summary>Width of a simulated ground label - wide enough for 'Deafening Essence of Misery'.</summary>
        public const float LabelWidth = 280f;

        /// <summary>Height of a simulated ground label accommodating header and 3 essence rows.</summary>
        public const float LabelHeight = 92f;

        /// <summary>A monster worth corrupting (MEDS).</summary>
        public static readonly EssenceEntry[] CorruptSample = Entries("Deafening Essence of Misery", "Shrieking Essence of Scorn", "Wailing Essence of Zeal");

        /// <summary>A monster to just kill.</summary>
        public static readonly EssenceEntry[] KillSample = Entries("Deafening Essence of Greed", "Screaming Essence of Anger", "Wailing Essence of Torment");

        // Pure managed color packing to avoid P/Invoke native calls during static type initialization
        public static uint PackColor(float r, float g, float b, float a) =>
            (uint)(Math.Clamp((int)(r * 255f + 0.5f), 0, 255)) |
            ((uint)(Math.Clamp((int)(g * 255f + 0.5f), 0, 255)) << 8) |
            ((uint)(Math.Clamp((int)(b * 255f + 0.5f), 0, 255)) << 16) |
            ((uint)(Math.Clamp((int)(a * 255f + 0.5f), 0, 255)) << 24);

        private static readonly uint Background = PackColor(0.06f, 0.07f, 0.08f, 1.0f);
        private static readonly uint GridLine = PackColor(1f, 1f, 1f, 0.025f);
        private static readonly uint LabelFill = PackColor(0.08f, 0.09f, 0.11f, 0.95f);
        private static readonly uint LabelBorder = PackColor(0.38f, 0.32f, 0.22f, 0.85f);
        private static readonly uint LabelTitle = PackColor(0.85f, 0.78f, 0.60f, 0.90f);
        private static readonly uint LabelDivider = PackColor(0.38f, 0.32f, 0.22f, 0.40f);

        private static EssenceEntry[] Entries(params string[] names) =>
            Array.ConvertAll(names, n => EssenceCatalogue.FindByName(n) ?? throw new InvalidOperationException($"'{n}' is not in the catalogue"));

        /// <summary>
        /// Opens the bordered canvas child and paints its background. Returns false when ImGui clipped
        /// it; either way the caller must call <see cref="End"/>, as ImGui requires EndChild after every BeginChild.
        /// </summary>
        public static bool Begin(string id, float height, out ImDrawListPtr drawList, out Vector2 min, out Vector2 max)
        {
            var visible = ImGui.BeginChild(id, new Vector2(0, height), ImGuiChildFlags.Border, ImGuiWindowFlags.NoScrollbar);
            drawList = ImGui.GetWindowDrawList();
            min = ImGui.GetWindowPos();
            max = min + ImGui.GetWindowSize();

            if (visible)
            {
                drawList.AddRectFilled(min, max, Background);
                for (var x = min.X + 20; x < max.X; x += 25)
                    drawList.AddLine(new Vector2(x, min.Y), new Vector2(x, max.Y), GridLine);
                for (var y = min.Y + 20; y < max.Y; y += 25)
                    drawList.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), GridLine);
            }

            return visible;
        }

        public static void End() => ImGui.EndChild();

        /// <summary>A simulated "Imprisoned Monster" ground label listing <paramref name="essences"/> in their group colours.</summary>
        public static void DrawGroundLabel(ImDrawListPtr drawList, EssenceIcons icons, Vector2 min, Vector2 max, EssenceEntry[] essences, bool drawVaalButton = false)
        {
            drawList.AddRectFilled(min, max, LabelFill, 2.0f);
            drawList.AddRect(min, max, LabelBorder, 2.0f, ImDrawFlags.None, 1.5f);

            // Centered monster title matching Path of Exile monolith tooltips
            const string title = "Imprisoned Monster";
            var titleSize = ImGui.CalcTextSize(title);
            drawList.AddText(new Vector2((min.X + max.X - titleSize.X) / 2f, min.Y + 5), LabelTitle, title);
            drawList.AddLine(new Vector2(min.X + 6, min.Y + 22), new Vector2(max.X - 6, min.Y + 22), LabelDivider);

            for (var i = 0; i < essences.Length; i++)
            {
                var essence = essences[i];
                var rowY = min.Y + 26 + i * 20;
                var color = ImGui.ColorConvertFloat4ToU32(CatalogueView.GroupColor(essence.Group));

                // Pure text matching the real in-game ground label
                drawList.AddText(new Vector2(min.X + 10, rowY + 1), color, essence.Name);
            }

            if (drawVaalButton)
            {
                const float btnSize = 26f;
                var btnMin = new Vector2((min.X + max.X - btnSize) / 2f, max.Y + 2f);
                var btnMax = new Vector2(btnMin.X + btnSize, btnMin.Y + btnSize);

                // Button frame socket cleanly attached below the monolith card (no icon inside)
                drawList.AddRectFilled(btnMin, btnMax, LabelFill, 4f);
                drawList.AddRect(btnMin, btnMax, LabelBorder, 4f, ImDrawFlags.None, 1.2f);
            }
        }

        public static RectangleF GetSimulatedVaalButtonRect(Vector2 min, Vector2 max)
        {
            const float btnSize = 26f;
            var left = (min.X + max.X - btnSize) / 2f;
            var top = max.Y + 2f;
            return new RectangleF(left, top, btnSize, btnSize);
        }
    }
}
