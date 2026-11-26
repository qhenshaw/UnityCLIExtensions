using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>Sets <see cref="Time.timeScale"/> for <c>cliext.playmode time_scale</c>, within the range Unity accepts.</summary>
    internal static class PlayModeTime
    {
        /// <summary>The largest time scale Unity accepts.</summary>
        public const float MaxTimeScale = 100f;

        public static bool IsValid(float value) => !float.IsNaN(value) && value >= 0f && value <= MaxTimeScale;

        /// <summary>Sets the time scale and returns the previous one.</summary>
        public static float Set(float value)
        {
            float previous = Time.timeScale;
            Time.timeScale = value;
            return previous;
        }
    }
}
