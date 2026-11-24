using System;
using System.IO;
using System.Text;

namespace MonMP.Core.World
{
    // Optional dynamic paint records for native vehicle-save panel roots on sale clones.
    // Installed part and loose-item paint remains in DynamicPaintPanelState (adapter 51).
    public sealed class PurchasedVehiclePaintState
    {
        public const ushort Adapter = 144;
        public const byte Marker = 0xDB;
        public const byte Version = 1;
        public const int MaxRelativePathBytes = 192;
        public const int MaxPayloadSize = 1 + 1 + sizeof(ulong) + sizeof(ushort) +
            MaxRelativePathBytes + PaintPanelState.MaxPayloadSize;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public ulong VehicleEntity { get; }
        public string RelativePanelPath { get; }
        public PaintPanelState Paint { get; }

        public PurchasedVehiclePaintState(ulong vehicleEntity, string relativePanelPath, PaintPanelState paint)
        {
            if (vehicleEntity <= WorldRecord.DynamicIdStart || paint == null)
                throw new InvalidDataException("Invalid purchased vehicle paint owner or state.");
            ValidateRelativePanelPath(relativePanelPath);
            VehicleEntity = vehicleEntity;
            RelativePanelPath = relativePanelPath;
            Paint = paint;
        }

        public static ulong RecordId(ulong vehicleEntity, string relativePanelPath)
        {
            if (vehicleEntity <= WorldRecord.DynamicIdStart)
                throw new InvalidDataException("Purchased vehicle paint entity is not dynamic.");
            ValidateRelativePanelPath(relativePanelPath);
            ulong id = StaticId.Hash("purchased-vehicle-shell-paint-v1/" +
                vehicleEntity.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) + "/" + relativePanelPath) |
                WorldRecord.DynamicIdStart;
            return id == WorldRecord.DynamicIdStart ? id + 1 : id;
        }

        public byte[] Encode()
        {
            byte[] path = StrictUtf8.GetBytes(RelativePanelPath);
            byte[] paint = Paint.Encode();
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Version);
            writer.Write(VehicleEntity);
            writer.Write((ushort)path.Length);
            writer.Write(path);
            writer.Write(paint);
            if (stream.Length > MaxPayloadSize || stream.Length > WorldRecord.MaxPayloadBytes)
                throw new InvalidDataException("Purchased vehicle paint state is too large.");
            return stream.ToArray();
        }

        public bool SameState(PurchasedVehiclePaintState? other) => other != null &&
            VehicleEntity == other.VehicleEntity &&
            string.Equals(RelativePanelPath, other.RelativePanelPath, StringComparison.Ordinal) &&
            Paint.SameState(other.Paint);

        public static PurchasedVehiclePaintState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 1 + 1 + sizeof(ulong) + sizeof(ushort) + 2 + 1 + 6 * sizeof(float) ||
                payload.Length > MaxPayloadSize || payload.Length > WorldRecord.MaxPayloadBytes)
                throw new InvalidDataException("Invalid purchased vehicle paint record size.");
            try
            {
                using var stream = new MemoryStream(payload, false);
                using var reader = new BinaryReader(stream);
                if (reader.ReadByte() != Marker || reader.ReadByte() != Version)
                    throw new InvalidDataException("Unsupported purchased vehicle paint record.");
                ulong entity = reader.ReadUInt64();
                int length = reader.ReadUInt16();
                if (length < 1 || length > MaxRelativePathBytes || length > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid purchased vehicle paint path length.");
                string path = StrictUtf8.GetString(reader.ReadBytes(length));
                byte[] paint = reader.ReadBytes((int)(stream.Length - stream.Position));
                var result = new PurchasedVehiclePaintState(entity, path, PaintPanelState.Decode(paint));
                if (result.Encode().Length != payload.Length) throw new InvalidDataException("Invalid purchased vehicle paint record length.");
                return result;
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("Truncated purchased vehicle paint record.", ex);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Invalid purchased vehicle paint path encoding.", ex);
            }
        }

        private static void ValidateRelativePanelPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Purchased vehicle paint path is empty.");
            int bytes;
            try { bytes = StrictUtf8.GetByteCount(path); }
            catch (EncoderFallbackException ex) { throw new InvalidDataException("Invalid purchased vehicle paint path encoding.", ex); }
            if (bytes < 1 || bytes > MaxRelativePathBytes)
                throw new InvalidDataException("Purchased vehicle paint path is too large.");
            foreach (char character in path)
                if (char.IsControl(character)) throw new InvalidDataException("Purchased vehicle paint path contains a control character.");
        }
    }
}
