using System;
using System.Buffers.Binary;
using System.IO;

namespace MonMP.Core.World
{
    // Adapter 103 v2 carries the native presentation literally. RGB never gets
    // reconstructed from a palette/hue, and pulse never uses the receiver's clock.
    public readonly struct UnderglowColor
    {
        public readonly float Red, Green, Blue, Alpha;
        public UnderglowColor(float red, float green, float blue, float alpha)
        { Red = red; Green = green; Blue = blue; Alpha = alpha; }
        internal void Validate()
        {
            if (!Valid(Red) || !Valid(Green) || !Valid(Blue) || !Valid(Alpha))
                throw new InvalidDataException("Invalid underglow color.");
        }
        private static bool Valid(float value) => ItemPose.FiniteValue(value, 16f) && value >= 0;
    }

    public readonly struct UnderglowLight
    {
        public readonly UnderglowColor Color;
        public readonly float Intensity;
        public UnderglowLight(UnderglowColor color, float intensity) { Color = color; Intensity = intensity; }
        internal void Validate()
        {
            Color.Validate();
            if (!ItemPose.FiniteValue(Intensity, 125f) || Intensity < 0)
                throw new InvalidDataException("Invalid underglow intensity.");
        }
    }

    public readonly struct UnderglowState
    {
        public const ushort Adapter = 103;
        public const int EncodedLength = 100;
        private const byte Marker = 0xE4, Version = 2;
        public readonly byte Active, Mode, Cycle, Intensity, ColorIndex;
        public readonly bool Ready, Ignition, UnderEnabled, LeftEnabled, RightEnabled, BloomActive;
        public readonly UnderglowLight Under, Left, Right;
        public readonly UnderglowColor Bloom, Emission;

        public UnderglowState(byte active, byte mode, byte cycle, byte intensity, byte colorIndex,
            bool ready, bool ignition, bool underEnabled, bool leftEnabled, bool rightEnabled, bool bloomActive,
            UnderglowLight under, UnderglowLight left, UnderglowLight right, UnderglowColor bloom, UnderglowColor emission)
        {
            Active = active; Mode = mode; Cycle = cycle; Intensity = intensity; ColorIndex = colorIndex;
            Ready = ready; Ignition = ignition; UnderEnabled = underEnabled; LeftEnabled = leftEnabled;
            RightEnabled = rightEnabled; BloomActive = bloomActive;
            Under = under; Left = left; Right = right; Bloom = bloom; Emission = emission;
            Validate();
        }

        public bool SameSettings(UnderglowState other) => Active == other.Active && Mode == other.Mode &&
            Cycle == other.Cycle && Intensity == other.Intensity && ColorIndex == other.ColorIndex;

        public byte[] Encode()
        {
            var data = new byte[EncodedLength];
            WriteTo(data);
            return data;
        }

        public void WriteTo(Span<byte> data)
        {
            if (data.Length != EncodedLength) throw new ArgumentException("Invalid underglow destination size.", nameof(data));
            Validate();
            data[0] = Marker; data[1] = Version; data[2] = Active; data[3] = Mode;
            data[4] = Cycle; data[5] = Intensity; data[6] = ColorIndex;
            data[7] = (byte)((Ready ? 1 : 0) | (Ignition ? 2 : 0) | (UnderEnabled ? 4 : 0) |
                (LeftEnabled ? 8 : 0) | (RightEnabled ? 16 : 0) | (BloomActive ? 32 : 0));
            int at = 8;
            Write(data, ref at, Under); Write(data, ref at, Left); Write(data, ref at, Right);
            Write(data, ref at, Bloom); Write(data, ref at, Emission);
        }

        public static UnderglowState Decode(byte[] data)
        {
            if (data == null || data.Length != EncodedLength || data[0] != Marker || data[1] != Version || (data[7] & 0xC0) != 0)
                throw new InvalidDataException("Invalid underglow state.");
            int at = 8;
            return new UnderglowState(data[2], data[3], data[4], data[5], data[6],
                (data[7] & 1) != 0, (data[7] & 2) != 0, (data[7] & 4) != 0,
                (data[7] & 8) != 0, (data[7] & 16) != 0, (data[7] & 32) != 0,
                ReadLight(data, ref at), ReadLight(data, ref at), ReadLight(data, ref at),
                ReadColor(data, ref at), ReadColor(data, ref at));
        }

        private void Validate()
        {
            if (Active > 1 || Mode > 5 || Cycle > 2 || Intensity > 20)
                throw new InvalidDataException("Invalid underglow settings.");
            Under.Validate(); Left.Validate(); Right.Validate(); Bloom.Validate(); Emission.Validate();
        }
        private static void Write(Span<byte> data, ref int at, float value)
        { BinaryPrimitives.WriteInt32LittleEndian(data.Slice(at, 4), BitConverter.SingleToInt32Bits(value)); at += 4; }
        private static void Write(Span<byte> data, ref int at, UnderglowColor value)
        { Write(data, ref at, value.Red); Write(data, ref at, value.Green); Write(data, ref at, value.Blue); Write(data, ref at, value.Alpha); }
        private static void Write(Span<byte> data, ref int at, UnderglowLight value)
        { Write(data, ref at, value.Color); Write(data, ref at, value.Intensity); }
        private static float Read(byte[] data, ref int at)
        { float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at, 4))); at += 4; return value; }
        private static UnderglowColor ReadColor(byte[] data, ref int at) =>
            new UnderglowColor(Read(data, ref at), Read(data, ref at), Read(data, ref at), Read(data, ref at));
        private static UnderglowLight ReadLight(byte[] data, ref int at) => new UnderglowLight(ReadColor(data, ref at), Read(data, ref at));
    }
}
