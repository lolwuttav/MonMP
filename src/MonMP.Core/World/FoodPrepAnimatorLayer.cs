using System;
using System.IO;

namespace MonMP.Core.World
{
    public readonly struct FoodPrepAnimatorLayer : IEquatable<FoodPrepAnimatorLayer>
    {
        public const int Size = 10;
        public readonly byte LayerIndex;
        public readonly int StateHash;
        public readonly ushort Time, QuantizedWeight;
        public readonly bool Loop;
        public float NormalizedTime => Time / 65535f;
        public float Weight => QuantizedWeight / 65535f;
        public FoodPrepAnimatorLayer(byte layerIndex, int stateHash, float normalizedTime, bool loop, float weight = 1)
        {
            if (layerIndex >= FoodPrepState.MaxAnimatorLayers || !Unit(normalizedTime) || !Unit(weight) ||
                (stateHash == 0 && (normalizedTime != 0 || loop)))
                throw new InvalidDataException("Invalid food preparation animator layer.");
            LayerIndex = layerIndex; StateHash = stateHash; Time = (ushort)Math.Round(normalizedTime * 65535f);
            QuantizedWeight = (ushort)Math.Round(weight * 65535f); Loop = loop;
        }
        private static bool Unit(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value <= 1;
        internal void Write(BinaryWriter writer)
        {
            writer.Write(LayerIndex); writer.Write(StateHash); writer.Write(Time);
            writer.Write(QuantizedWeight); writer.Write((byte)(Loop ? 1 : 0));
        }
        internal static FoodPrepAnimatorLayer Read(BinaryReader reader)
        {
            byte index = reader.ReadByte(); int hash = reader.ReadInt32();
            ushort time = reader.ReadUInt16(), weight = reader.ReadUInt16(); byte loop = reader.ReadByte();
            if (loop > 1) throw new InvalidDataException("Invalid food preparation animator loop flag.");
            return new FoodPrepAnimatorLayer(index, hash, time / 65535f, loop != 0, weight / 65535f);
        }
        public bool Equals(FoodPrepAnimatorLayer other) => LayerIndex == other.LayerIndex && StateHash == other.StateHash &&
            Time == other.Time && QuantizedWeight == other.QuantizedWeight && Loop == other.Loop;
        public override bool Equals(object? obj) => obj is FoodPrepAnimatorLayer other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LayerIndex, StateHash, Time, QuantizedWeight, Loop);
    }
}
