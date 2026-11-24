using System;
using System.Collections.Generic;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class WeatherCapture
    {
        internal static WeatherState Reuse(WeatherState? previous, ushort current, ushort next, int temperature,
            byte moon, byte changeHour, byte generationMethod, bool generationEnabled,
            IReadOnlyList<ushort> forecast, IReadOnlyList<float> scalars, IReadOnlyList<float> particles, IReadOnlyList<float> sounds)
        {
            if (previous != null && previous.Current == current && previous.Next == next && previous.Temperature == temperature &&
                previous.Moon == moon && previous.ChangeHour == changeHour && previous.GenerationMethod == generationMethod &&
                previous.GenerationEnabled == generationEnabled && Forecast(previous.Forecast, forecast) &&
                Samples(previous.Scalars, scalars) && Samples(previous.Particles, particles) && Samples(previous.Sounds, sounds)) return previous;
            // WeatherState detaches all input collections. Scratch arrays can be
            // overwritten on the next sample without mutating accepted snapshots.
            return new WeatherState(current, next, temperature, moon, changeHour, generationMethod, generationEnabled,
                forecast, scalars, particles, sounds);
        }

        internal static WeatherGradient ReuseGradient(WeatherGradient? previous, GradientColorKey[] colors, GradientAlphaKey[] alphas)
        {
            bool same = previous != null && previous.Colors.Count == colors.Length * 5 && previous.Alphas.Count == alphas.Length * 2;
            for (int i = 0; same && i < colors.Length; i++)
            {
                var key = colors[i]; int n = i * 5;
                same = Float(previous!.Colors[n], key.time) && Float(previous.Colors[n + 1], key.color.r) &&
                    Float(previous.Colors[n + 2], key.color.g) && Float(previous.Colors[n + 3], key.color.b) && Float(previous.Colors[n + 4], key.color.a);
            }
            for (int i = 0; same && i < alphas.Length; i++)
                same = Float(previous!.Alphas[i * 2], alphas[i].time) && Float(previous.Alphas[i * 2 + 1], alphas[i].alpha);
            if (same) return previous!;
            var colorValues = new float[colors.Length * 5]; var alphaValues = new float[alphas.Length * 2];
            for (int i = 0; i < colors.Length; i++)
            {
                var key = colors[i]; int n = i * 5;
                colorValues[n] = key.time; colorValues[n + 1] = key.color.r; colorValues[n + 2] = key.color.g;
                colorValues[n + 3] = key.color.b; colorValues[n + 4] = key.color.a;
            }
            for (int i = 0; i < alphas.Length; i++) { alphaValues[i * 2] = alphas[i].time; alphaValues[i * 2 + 1] = alphas[i].alpha; }
            return new WeatherGradient(colorValues, alphaValues);
        }
        private static bool Forecast(IReadOnlyList<ushort> left, IReadOnlyList<ushort> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
            return true;
        }
        private static bool Samples(IReadOnlyList<float> left, IReadOnlyList<float> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++) if (!Float(left[i], right[i])) return false;
            return true;
        }
        private static bool Float(float left, float right) => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
    }
}
