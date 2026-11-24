using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    public sealed class WeatherState
    {
        public const ushort Adapter = 7;
        public static readonly ulong Entity = StaticId.Hash("shared-weather/v1");
        // coverage, cloud bottom/thickness, fog, wind, sun, moon, wet/snow,
        // attenuation, cloud height, wind cache, render fog and fog falloff.
        private static readonly float[] bounds = { 1, 20000, 20000, 1, 100, 20, 20, 1, 1, 10, 20000, 100, 1, 100 };
        public const int ScalarCount = 14;
        public readonly ushort Current, Next;
        public readonly int Temperature;
        public readonly byte Moon, ChangeHour, GenerationMethod;
        public readonly bool GenerationEnabled;
        public IReadOnlyList<ushort> Forecast { get; }
        public IReadOnlyList<float> Scalars { get; }
        public IReadOnlyList<float> Particles { get; }
        public IReadOnlyList<float> Sounds { get; }
        public WeatherState(ushort current, ushort next, int temperature, byte moon, byte changeHour, byte generationMethod, bool generationEnabled,
            IEnumerable<ushort> forecast, IEnumerable<float> scalars, IEnumerable<float> particles, IEnumerable<float> sounds)
        {
            Current = current; Next = next; Temperature = temperature; Moon = moon; ChangeHour = changeHour;
            GenerationMethod = generationMethod; GenerationEnabled = generationEnabled;
            Forecast = Array.AsReadOnly(forecast.ToArray()); Scalars = Array.AsReadOnly(scalars.ToArray());
            Particles = Array.AsReadOnly(particles.ToArray()); Sounds = Array.AsReadOnly(sounds.ToArray());
        }
        private void Validate()
        {
            if (Current == 0 || Current > 64 || Next > 64 || Temperature < -150 || Temperature > 200 || Moon > 31 || ChangeHour > 23 ||
                GenerationMethod > 1 || (Forecast.Count != 0 && Forecast.Count != 24) || Forecast.Any(v => v == 0 || v > 64) ||
                Scalars.Count != ScalarCount || Particles.Count > 32 || Sounds.Count > 32) throw new InvalidDataException("Invalid weather layout.");
            for (int i = 0; i < Scalars.Count; i++) Check(Scalars[i], bounds[i]);
            foreach (float value in Particles) Check(value, 100000);
            foreach (float value in Sounds) Check(value, 1);
        }
        private static void Check(float value, float max)
        { if (value < 0 || !ItemPose.Finite(value, max)) throw new InvalidDataException("Invalid weather sample."); }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Current); w.Write(Next); w.Write((short)Temperature); w.Write(Moon); w.Write(ChangeHour); w.Write(GenerationMethod); w.Write(GenerationEnabled);
            w.Write((byte)Forecast.Count); foreach (ushort id in Forecast) w.Write(id);
            foreach (float value in Scalars) w.Write(value);
            w.Write((byte)Particles.Count); foreach (float value in Particles) w.Write(value);
            w.Write((byte)Sounds.Count); foreach (float value in Sounds) w.Write(value);
            return stream.ToArray();
        }
        public static WeatherState Decode(byte[] payload)
        {
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            ushort current = r.ReadUInt16(), next = r.ReadUInt16(); int temperature = r.ReadInt16();
            byte moon = r.ReadByte(), hour = r.ReadByte(), method = r.ReadByte(), enabled = r.ReadByte();
            if (enabled > 1) throw new InvalidDataException("Invalid weather generation flag.");
            int count = r.ReadByte(); if (count != 0 && count != 24) throw new InvalidDataException("Invalid forecast size.");
            var forecast = new ushort[count]; for (int i = 0; i < count; i++) forecast[i] = r.ReadUInt16();
            var scalars = new float[ScalarCount]; for (int i = 0; i < scalars.Length; i++) scalars[i] = r.ReadSingle();
            float[] ReadSamples() { int n = r.ReadByte(); if (n > 32) throw new InvalidDataException("Too many weather sources."); var a = new float[n]; for (int i = 0; i < n; i++) a[i] = r.ReadSingle(); return a; }
            var state = new WeatherState(current, next, temperature, moon, hour, method, enabled == 1, forecast, scalars, ReadSamples(), ReadSamples());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing weather bytes."); state.Validate(); return state;
        }
    }

    public sealed class WeatherGradient
    {
        public IReadOnlyList<float> Colors { get; } // time, r, g, b, a
        public IReadOnlyList<float> Alphas { get; } // time, alpha
        public WeatherGradient(IEnumerable<float> colors, IEnumerable<float> alphas)
        { Colors = Array.AsReadOnly(colors.ToArray()); Alphas = Array.AsReadOnly(alphas.ToArray()); Validate(); }
        private void Validate()
        {
            if (Colors.Count < 10 || Colors.Count > 40 || Colors.Count % 5 != 0 || Alphas.Count < 4 || Alphas.Count > 16 || Alphas.Count % 2 != 0)
                throw new InvalidDataException("Invalid weather gradient shape.");
            void Keys(IReadOnlyList<float> values, int stride)
            {
                float previous = -1;
                for (int i = 0; i < values.Count; i += stride)
                {
                    float time = values[i]; if (!ItemPose.Finite(time, 1) || time < previous || time < 0) throw new InvalidDataException("Invalid gradient time."); previous = time;
                    for (int j = 1; j < stride; j++) if (values[i + j] < 0 || !ItemPose.Finite(values[i + j], stride == 2 || j == 4 ? 1 : 32))
                        throw new InvalidDataException("Invalid gradient color.");
                }
            }
            Keys(Colors, 5); Keys(Alphas, 2);
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)(Colors.Count / 5)); foreach (float v in Colors) w.Write(v);
            w.Write((byte)(Alphas.Count / 2)); foreach (float v in Alphas) w.Write(v); return stream.ToArray();
        }
        public static WeatherGradient Decode(byte[] payload)
        {
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            float[] Read(int stride) { int n = r.ReadByte(); if (n < 2 || n > 8) throw new InvalidDataException("Invalid gradient key count."); var a = new float[n * stride]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadSingle(); return a; }
            var gradient = new WeatherGradient(Read(5), Read(2)); if (stream.Position != stream.Length) throw new InvalidDataException("Trailing gradient data."); return gradient;
        }
    }
}
