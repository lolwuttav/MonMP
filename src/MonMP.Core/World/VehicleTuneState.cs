using System;
using System.IO;

namespace MonMP.Core.World
{
    // A vehicle's per-part values that the part-install record deliberately leaves out: the ECU tune
    // (native Part_ECU IntValue1/IntValue2, digit-encoded power curve, boost, TCS and ABS) and the
    // nitrous bottle's charge. A guest's copy kept its own save's or a fresh clone's defaults, so the
    // turbo made as little as 1.6x instead of the host's up to 2.2x and NOS could be empty.
    public readonly struct VehicleTuneState : IEquatable<VehicleTuneState>
    {
        public const ushort Adapter = 109;
        private const byte Marker = 0xE7, Version = 1, EcuFlag = 1, BottleFlag = 2;
        public const int MaxPowerPoint = 25, MaxPowerTotal = 95, MaxBoost = 20;
        public readonly bool HasEcu, HasBottle;
        public readonly int Ecu1, Ecu2;
        public readonly float Charge;

        public VehicleTuneState(bool hasEcu, int ecu1, int ecu2, bool hasBottle, float charge)
        {
            HasEcu = hasEcu; Ecu1 = hasEcu ? ecu1 : 0; Ecu2 = hasEcu ? ecu2 : 0;
            HasBottle = hasBottle; Charge = hasBottle ? charge : 0;
            Validate();
        }

        public VehicleTuneState WithCharge(float charge) => new VehicleTuneState(HasEcu, Ecu1, Ecu2, HasBottle, charge);
        public VehicleTuneState WithEcu(int ecu1, int ecu2) => new VehicleTuneState(HasEcu, ecu1, ecu2, HasBottle, Charge);

        // Native layout: both values are ten digits led by 1. IntValue1 = 1 p0 p1 p2 p3 abs,
        // IntValue2 = 1 p4 p5 boost tcsOn tcs x, each p a two-digit power point.
        public static bool IsValidEcu(int ecu1, int ecu2)
        {
            if (ecu1 < 1000000000 || ecu2 < 1000000000) return false;
            string a = ecu1.ToString(), b = ecu2.ToString();
            if (a.Length != 10 || b.Length != 10) return false;
            int total = 0;
            for (int i = 0; i < 6; i++)
            {
                string s = i < 4 ? a : b; int at = i < 4 ? 1 + i * 2 : 1 + (i - 4) * 2;
                int point = (s[at] - '0') * 10 + (s[at + 1] - '0');
                if (point > MaxPowerPoint) return false;
                total += point;
            }
            int boost = (b[5] - '0') * 10 + (b[6] - '0');
            return total <= MaxPowerTotal && boost <= MaxBoost && b[7] <= '1';
        }

        public byte[] Encode()
        {
            Validate();
            var payload = new byte[15];
            payload[0] = Marker; payload[1] = Version;
            payload[2] = (byte)((HasEcu ? EcuFlag : 0) | (HasBottle ? BottleFlag : 0));
            BitConverter.GetBytes(Ecu1).CopyTo(payload, 3);
            BitConverter.GetBytes(Ecu2).CopyTo(payload, 7);
            BitConverter.GetBytes(Charge).CopyTo(payload, 11);
            return payload;
        }

        public static VehicleTuneState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 15 || payload[0] != Marker || payload[1] != Version || (payload[2] & ~3) != 0)
                throw new InvalidDataException("Invalid vehicle tune state.");
            return new VehicleTuneState((payload[2] & EcuFlag) != 0, BitConverter.ToInt32(payload, 3), BitConverter.ToInt32(payload, 7),
                (payload[2] & BottleFlag) != 0, BitConverter.ToSingle(payload, 11));
        }

        public static bool TryDecode(byte[] payload, out VehicleTuneState state)
        {
            try { state = Decode(payload); return true; }
            catch (InvalidDataException) { state = default; return false; }
        }

        private void Validate()
        {
            if ((HasEcu && !IsValidEcu(Ecu1, Ecu2)) || float.IsNaN(Charge) || float.IsInfinity(Charge) || Charge < 0 || Charge > 1000)
                throw new InvalidDataException("Invalid ECU tune or nitrous charge.");
        }

        public bool Equals(VehicleTuneState other) => HasEcu == other.HasEcu && HasBottle == other.HasBottle &&
            Ecu1 == other.Ecu1 && Ecu2 == other.Ecu2 && Charge.Equals(other.Charge);
        public override bool Equals(object? obj) => obj is VehicleTuneState other && Equals(other);
        public override int GetHashCode() => ((Ecu1 * 397 ^ Ecu2) * 397 ^ Charge.GetHashCode()) * 4 + (HasEcu ? 1 : 0) + (HasBottle ? 2 : 0);
    }
}
