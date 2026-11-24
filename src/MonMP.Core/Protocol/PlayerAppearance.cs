using System;
using System.IO;

namespace MonMP.Core.Protocol
{
    // Local catalogue IDs, never asset names supplied by a peer. RGB is sRGB, 8 bits/channel.
    public readonly struct PlayerAppearance : IEquatable<PlayerAppearance>
    {
        public readonly byte Head, Torso, Legs, Shoes;
        public readonly bool CustomColor, NativeTexture;
        public readonly uint Rgb;
        // Head retains its historical wire/API name; it now selects hair/headwear only.
        public PlayerAppearance(byte head, byte torso, byte legs, byte shoes, bool customColor = false, uint rgb = 0, bool nativeTexture = false)
        { Head = head; Torso = torso; Legs = legs; Shoes = shoes; CustomColor = customColor; Rgb = customColor ? rgb : 0; NativeTexture = nativeTexture; }
        public PlayerAppearance(byte head, byte torso, byte legs, byte shoes, bool customColor, uint rgb)
            : this(head, torso, legs, shoes, customColor, rgb, false) { }
        public static PlayerAppearance Whole(byte model) => new PlayerAppearance(model, model, model, model);
        public PlayerAppearance WithoutColor() => new PlayerAppearance(Head, Torso, Legs, Shoes, false, 0, NativeTexture);
        public PlayerAppearance WithTexture(bool native) => new PlayerAppearance(Head, Torso, Legs, Shoes, CustomColor, Rgb, native);
        public bool SameGeometry(in PlayerAppearance other) => Head == other.Head && Torso == other.Torso && Legs == other.Legs && Shoes == other.Shoes;
        public void Validate()
        {
            if (!PlayerModelPolicy.Valid(Head) || !PlayerModelPolicy.Valid(Torso) || !PlayerModelPolicy.Valid(Legs) ||
                !PlayerModelPolicy.Valid(Shoes) || Rgb > 0xFFFFFF || !CustomColor && Rgb != 0)
                throw new InvalidDataException("Invalid player appearance.");
        }
        // Head retains its existing pose slot. The extension is four bytes, seven with RGB.
        internal void WriteParts(BinaryWriter writer)
        {
            Validate(); writer.Write(Torso); writer.Write(Legs); writer.Write(Shoes); writer.Write((byte)((CustomColor ? 1 : 0) | (NativeTexture ? 2 : 0)));
            if (CustomColor) { writer.Write((byte)(Rgb >> 16)); writer.Write((byte)(Rgb >> 8)); writer.Write((byte)Rgb); }
        }
        internal static PlayerAppearance ReadParts(BinaryReader reader, byte head)
        {
            byte torso = reader.ReadByte(), legs = reader.ReadByte(), shoes = reader.ReadByte(), flags = reader.ReadByte();
            if (flags > 3) throw new InvalidDataException("Invalid player shirt flags.");
            uint rgb = (flags & 1) != 0 ? (uint)(reader.ReadByte() << 16 | reader.ReadByte() << 8 | reader.ReadByte()) : 0;
            var result = new PlayerAppearance(head, torso, legs, shoes, (flags & 1) != 0, rgb, (flags & 2) != 0); result.Validate(); return result;
        }
        public bool Equals(PlayerAppearance other) => SameGeometry(other) && CustomColor == other.CustomColor && Rgb == other.Rgb && NativeTexture == other.NativeTexture;
        public override bool Equals(object? value) => value is PlayerAppearance other && Equals(other);
        public override int GetHashCode() => unchecked((int)(Head | Torso << 5 | Legs << 10 | Shoes << 15) * 397 ^ (int)Rgb ^ (CustomColor ? 1 : 0) ^ (NativeTexture ? 2 : 0));
        public static bool operator ==(PlayerAppearance left, PlayerAppearance right) => left.Equals(right);
        public static bool operator !=(PlayerAppearance left, PlayerAppearance right) => !left.Equals(right);
    }
}
