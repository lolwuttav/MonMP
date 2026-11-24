using System.IO;

namespace MonMP.Core.Persistence
{
    // Each supported on-disk version must have its own reader that produces the
    // current in-memory document. Add a conversion only after its old layout is
    // known; changing the version byte on an unknown file is never a migration.
    internal static class SidecarSchemaMigrations
    {
        internal static void WriteCurrent(SidecarDocument document, BinaryWriter writer)
            => Write(SidecarDocument.SchemaVersion, document, writer);

        internal static void Write(ushort schema, SidecarDocument document, BinaryWriter writer)
        {
            switch (schema)
            {
                case 1: document.WriteVersion1(writer); return;
                case 2: document.WriteVersion1(writer); return; // Same fields, permanent receipt-reader requirement.
                default: throw new InvalidDataException("No writer for sidecar schema: " + schema);
            }
        }

        internal static void RequireKnown(ushort sourceVersion)
        {
            switch (sourceVersion)
            {
                case 1: return; // First shipped with the 0.19.0 persistence preview.
                case 2: return; // Permanent transaction history compatibility gate.
                default: throw new InvalidDataException("Unsupported sidecar schema: " + sourceVersion);
            }
        }

        internal static SidecarDocument ReadAndUpgrade(ushort sourceVersion, BinaryReader reader)
        {
            RequireKnown(sourceVersion);
            switch (sourceVersion)
            {
                case 1: return SidecarDocument.ReadVersion1(reader);
                case 2: return SidecarDocument.ReadVersion1(reader);
                default: throw new InvalidDataException("Unsupported sidecar schema: " + sourceVersion);
            }
        }
    }
}
