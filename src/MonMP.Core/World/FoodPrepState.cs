using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace MonMP.Core.World
{
    // Host-owned stock and literal native presentation. The fryer and French press use separate
    // animator layers; neither machine simulation nor output spawning runs on observers.
    public readonly struct FoodPrepState : IEquatable<FoodPrepState>
    {
        public const ushort Adapter = 54;
        public const int Size = 12, MaxAnimatorLayers = 8;
        public const int MaximumSize = Size + MaxAnimatorLayers * FoodPrepAnimatorLayer.Size;
        public readonly int FilledPercent, Curds;
        public readonly bool Working, BasketVisible, SteamPlaying, BoilingPlaying;
        private readonly ReadOnlyCollection<FoodPrepAnimatorLayer>? layers;
        public IReadOnlyList<FoodPrepAnimatorLayer> AnimatorLayers => layers ?? EmptyLayers;
        private static readonly ReadOnlyCollection<FoodPrepAnimatorLayer> EmptyLayers = Array.AsReadOnly(Array.Empty<FoodPrepAnimatorLayer>());
        public int EncodedSize => Size + AnimatorLayers.Count * FoodPrepAnimatorLayer.Size;
        private FoodPrepAnimatorLayer BaseLayer => AnimatorLayers.Count > 0 && AnimatorLayers[0].LayerIndex == 0 ? AnimatorLayers[0] : default;
        public int AnimatorStateHash => BaseLayer.StateHash;
        public ushort AnimatorTime => BaseLayer.Time;
        public float AnimatorNormalizedTime => BaseLayer.NormalizedTime;
        public bool AnimatorLoop => BaseLayer.Loop;

        public FoodPrepState(int filledPercent, int curds, bool working, bool basketVisible = false,
            int animatorStateHash = 0, float animatorNormalizedTime = 0, bool animatorLoop = false,
            bool steamPlaying = false, bool boilingPlaying = false)
            : this(filledPercent, curds, working, basketVisible,
                SingleLayer(animatorStateHash, animatorNormalizedTime, animatorLoop), steamPlaying, boilingPlaying) { }

        private static FoodPrepAnimatorLayer[] SingleLayer(int hash, float time, bool loop)
        {
            var layer = new FoodPrepAnimatorLayer(0, hash, time, loop);
            return hash == 0 ? Array.Empty<FoodPrepAnimatorLayer>() : new[] { layer };
        }

        public FoodPrepState(int filledPercent, int curds, bool working, bool basketVisible,
            IEnumerable<FoodPrepAnimatorLayer> animatorLayers, bool steamPlaying = false, bool boilingPlaying = false)
        {
            if (filledPercent < 0 || filledPercent > 100 || curds < 0 || animatorLayers == null)
                throw new InvalidDataException("Invalid food preparation machine state.");
            var copy = new List<FoodPrepAnimatorLayer>();
            foreach (var layer in animatorLayers)
            {
                if (copy.Count == MaxAnimatorLayers) throw new InvalidDataException("Too many food preparation animator layers.");
                _ = new FoodPrepAnimatorLayer(layer.LayerIndex, layer.StateHash, layer.NormalizedTime, layer.Loop, layer.Weight);
                foreach (var previous in copy)
                    if (previous.LayerIndex == layer.LayerIndex) throw new InvalidDataException("Duplicate food preparation animator layer.");
                copy.Add(layer);
            }
            copy.Sort((a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
            layers = Array.AsReadOnly(copy.ToArray());
            FilledPercent = filledPercent; Curds = curds; Working = working; BasketVisible = basketVisible;
            SteamPlaying = steamPlaying; BoilingPlaying = boilingPlaying;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(EncodedSize);
            using var writer = new BinaryWriter(stream);
            writer.Write(FilledPercent); writer.Write(Curds);
            writer.Write((byte)(Working ? 1 : 0)); writer.Write((byte)(BasketVisible ? 1 : 0));
            writer.Write((byte)((SteamPlaying ? 1 : 0) | (BoilingPlaying ? 2 : 0)));
            writer.Write((byte)AnimatorLayers.Count);
            foreach (var layer in AnimatorLayers) layer.Write(writer);
            return stream.ToArray();
        }

        public static FoodPrepState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < Size || payload.Length > MaximumSize)
                throw new InvalidDataException("Invalid food preparation machine state payload.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            int filled = reader.ReadInt32(), curds = reader.ReadInt32();
            byte working = reader.ReadByte(), visible = reader.ReadByte(), flags = reader.ReadByte(), count = reader.ReadByte();
            if (working > 1 || visible > 1 || flags > 3 || count > MaxAnimatorLayers || payload.Length != Size + count * FoodPrepAnimatorLayer.Size)
                throw new InvalidDataException("Invalid food preparation presentation header.");
            var animatorLayers = new FoodPrepAnimatorLayer[count];
            for (int i = 0; i < count; i++) animatorLayers[i] = FoodPrepAnimatorLayer.Read(reader);
            return new FoodPrepState(filled, curds, working != 0, visible != 0, animatorLayers, (flags & 1) != 0, (flags & 2) != 0);
        }

        public bool Equals(FoodPrepState other)
        {
            if (FilledPercent != other.FilledPercent || Curds != other.Curds || Working != other.Working || BasketVisible != other.BasketVisible ||
                SteamPlaying != other.SteamPlaying || BoilingPlaying != other.BoilingPlaying || AnimatorLayers.Count != other.AnimatorLayers.Count) return false;
            for (int i = 0; i < AnimatorLayers.Count; i++) if (!AnimatorLayers[i].Equals(other.AnimatorLayers[i])) return false;
            return true;
        }
        public override bool Equals(object? obj) => obj is FoodPrepState other && Equals(other);
        public override int GetHashCode()
        {
            int hash = HashCode.Combine(FilledPercent, Curds, Working, BasketVisible, SteamPlaying, BoilingPlaying);
            foreach (var layer in AnimatorLayers) hash = HashCode.Combine(hash, layer.GetHashCode());
            return hash;
        }
    }
}
