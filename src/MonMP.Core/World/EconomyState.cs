using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    public sealed class EconomyState
    {
        public const ushort Adapter = 8;
        public static readonly ulong Entity = StaticId.Hash("shared-economy/v1");
        public int CashCents { get; }
        public IReadOnlyList<float> SundayPayments { get; }
        public EconomyState(int cashCents, IEnumerable<float> sundayPayments)
        {
            CashCents = cashCents; SundayPayments = Array.AsReadOnly(sundayPayments.ToArray());
            // Cash is signed: native spending can create debt. Preserve native float payout precision.
            if (SundayPayments.Count != 5 || SundayPayments.Any(v => v < 0 || float.IsNaN(v) || float.IsInfinity(v)))
                throw new InvalidDataException("Invalid pending Sunday payments.");
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(CashCents); foreach (float value in SundayPayments) writer.Write(value); return stream.ToArray();
        }
        public static EconomyState Decode(byte[] payload)
        {
            if (payload.Length != 24) throw new InvalidDataException("Invalid economy payload length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            int cash = reader.ReadInt32(); var payments = new float[5];
            for (int i = 0; i < payments.Length; i++) payments[i] = reader.ReadSingle();
            return new EconomyState(cash, payments);
        }
    }
}
