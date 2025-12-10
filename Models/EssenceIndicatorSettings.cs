using SharpDX;

namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// Contains visual settings for rendering essence indicators.
    /// Extracted from plugin settings based on essence state.
    /// </summary>
    internal readonly struct EssenceIndicatorSettings
    {
        /// <summary>Border color for the essence indicator box</summary>
        public Color BorderColor { get; init; }

        /// <summary>Text color for the action indicator ("CORRUPT" or "KILL")</summary>
        public Color TextColor { get; init; }
    }
}