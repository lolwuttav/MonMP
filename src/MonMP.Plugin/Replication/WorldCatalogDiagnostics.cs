using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // Local compatibility evidence only: never includes record payloads, profiles or credentials.
    internal static class WorldCatalogDiagnostics
    {
        internal const int MaximumFieldCharacters = 512;
        internal static bool TryWrite(string directory, bool host, string version, string hash,
            IReadOnlyCollection<WorldRecord> records, Func<WorldRecord, (string Path, string Key)>? describe, out string error)
        {
            error = "";
            try
            {
                if (records.Count > SnapshotReceiver.MaxEntities) throw new InvalidOperationException("Catalog diagnostic limit exceeded.");
                Directory.CreateDirectory(directory);
                using var writer = new StreamWriter(Path.Combine(directory, "world-catalog.tsv"), false, new UTF8Encoding(false));
                writer.WriteLine("# Catalog SHA-256: " + Field(hash, 64));
                writer.WriteLine("# Role: " + (host ? "host" : "guest"));
                writer.WriteLine("# MonMP: " + Field(version, 32));
                writer.WriteLine("# Record count: " + records.Count.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine("# Hash inputs: ascending entity ID (UInt64 little-endian), adapter (UInt16 little-endian); no payloads or revisions");
                writer.WriteLine(describe == null ? "id\tadapter" : "id\tadapter\tpath\tkey");
                foreach (var record in records.OrderBy(record => record.Id))
                {
                    writer.Write(record.Id.ToString("X16", CultureInfo.InvariantCulture)); writer.Write('\t');
                    writer.Write(record.Adapter.ToString(CultureInfo.InvariantCulture));
                    if (describe != null)
                    {
                        var details = describe(record);
                        writer.Write('\t'); writer.Write(Field(details.Path));
                        writer.Write('\t'); writer.Write(Field(details.Key));
                    }
                    writer.WriteLine();
                }
                writer.WriteLine("# Complete: " + records.Count.ToString(CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                // Disk permissions/full disks and optional native path resolution must
                // never turn an otherwise valid join into a gameplay failure.
                error = ex.GetType().Name;
                return false;
            }
        }
        private static string Field(string? value, int limit = MaximumFieldCharacters)
        {
            if (value == null) return "-";
            var text = new StringBuilder(Math.Min(value.Length, limit) + 16);
            for (int i = 0; i < value.Length && i < limit; i++)
            {
                char c = value[i];
                if (c == '\t') text.Append("\\t");
                else if (c == '\r') text.Append("\\r");
                else if (c == '\n') text.Append("\\n");
                else text.Append(char.IsControl(c) ? '?' : c);
            }
            if (value.Length > limit) text.Append("[truncated]");
            return text.ToString();
        }
    }
}
