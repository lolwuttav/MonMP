using System;
using System.Collections.Generic;

namespace MonMP.Core.Input
{
    public enum EmoteWheelSlot : byte
    {
        None = 0,
        Wave = 1,
        Point = 2,
        ThumbsUp = 3,
        Sorry = 4,
        Spit = 5,
        Slur = 6
    }

    public static class EmoteWheelSelection
    {
        public const int SlotCount = 6;

        // Offsets use screen-style coordinates (positive X right, positive Y up). Wave is at
        // twelve o'clock, followed clockwise by Point, ThumbsUp, Sorry, Spit and Slur.
        public static EmoteWheelSlot Select(float x, float y, float innerRadius, float outerRadius)
        {
            if (!Finite(x) || !Finite(y) || !Finite(innerRadius) || !Finite(outerRadius) ||
                innerRadius < 0 || outerRadius <= innerRadius)
                return EmoteWheelSlot.None;

            double distanceSquared = (double)x * x + (double)y * y;
            if (distanceSquared < (double)innerRadius * innerRadius || distanceSquared > (double)outerRadius * outerRadius)
                return EmoteWheelSlot.None;

            double angle = Math.Atan2(x, y);
            if (angle < 0) angle += Math.PI * 2;
            double step = Math.PI * 2 / SlotCount;
            int index = (int)Math.Floor((angle + step * 0.5) / step) % SlotCount;
            return (EmoteWheelSlot)(index + 1);
        }

        public static EmoteWheelSlot Release(bool isOpen, bool openingFrame, bool stillEligible, EmoteWheelSlot underPointer) =>
            isOpen && !openingFrame && stillEligible && (byte)underPointer >= (byte)EmoteWheelSlot.Wave && (byte)underPointer <= (byte)EmoteWheelSlot.Slur
                ? underPointer
                : EmoteWheelSlot.None;

        // Input System can report a release and a subsequent press within one update. The key
        // remains held in that case; only a release edge (current-frame or latched from the
        // matching keyboard event) paired with the final unpressed state ends the wheel hold.
        public static bool IsHoldReleased(bool wasReleasedThisFrame, bool isPressed, bool releaseLatched = false) =>
            !isPressed && (wasReleasedThisFrame || releaseLatched);

        public static void MovePointer(ref float x, ref float y, float deltaX, float deltaY, float width, float height)
        {
            width = Finite(width) && width > 0f ? width : 0f;
            height = Finite(height) && height > 0f ? height : 0f;
            x = Finite(x) ? Clamp(x, 0f, width) : width * 0.5f;
            y = Finite(y) ? Clamp(y, 0f, height) : height * 0.5f;
            double dx = Finite(deltaX) ? deltaX : 0d;
            double dy = Finite(deltaY) ? deltaY : 0d;
            x = Clamp((double)x + dx, 0d, width);
            y = Clamp((double)y + dy, 0d, height);
        }

        public static bool HasBindingConflict(string keyControlPath, IEnumerable<string> effectiveBindingPaths)
        {
            if (string.IsNullOrWhiteSpace(keyControlPath) || effectiveBindingPaths == null) return false;
            foreach (string path in effectiveBindingPaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string binding = path.Trim();
                if (string.Equals(binding, keyControlPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(binding, "<Keyboard>", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(binding, "<Keyboard>/anyKey", StringComparison.OrdinalIgnoreCase) ||
                    binding.StartsWith("<Keyboard>/", StringComparison.OrdinalIgnoreCase) && binding.IndexOf('*') >= 0)
                    return true;
            }
            return false;
        }

        public static bool CanOpen(bool sessionRunning, bool sessionReady, bool worldSynchronized, bool walking,
            bool paused, bool dead, bool sleeping, bool pluginUiBlocked, bool nativeModalOpen,
            bool textEntryActive, bool cleanFrame, bool sceneLoaded) =>
            sessionRunning && sessionReady && worldSynchronized && walking && !paused && !dead && !sleeping &&
            !pluginUiBlocked && !nativeModalOpen && !textEntryActive && !cleanFrame && sceneLoaded;

        public static bool CanRestoreNativeInput(bool sameOwner, bool walking, bool paused, bool dead, bool sleeping,
            bool pluginUiBlocked, bool nativeModalOpen, bool textEntryActive, bool cleanFrame, bool saveFreeze) =>
            sameOwner && walking && !paused && !dead && !sleeping && !pluginUiBlocked && !nativeModalOpen &&
            !textEntryActive && !cleanFrame && !saveFreeze;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static float Clamp(float value, float minimum, float maximum) =>
            value < minimum ? minimum : value > maximum ? maximum : value;

        private static float Clamp(double value, double minimum, double maximum) =>
            value < minimum ? (float)minimum : value > maximum ? (float)maximum : (float)value;
    }
}
