using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    public enum PassengerVehicleControl : byte { Ignition = 1, LowBeam, HighBeam, Hazard, ExtraLights, Gear, Dome, ParkingBrake, HighLow }
    public readonly struct PassengerVehicleIntent
    {
        public readonly uint Sequence, Actor, SeatGeneration, DriverOwner, DriverGeneration;
        public readonly ulong Vehicle;
        public readonly PassengerVehicleControl Control;
        public readonly VehicleControlMethod Method;
        public readonly byte ControlIndex;
        public bool Walking => SeatGeneration == 0;
        public uint Simulator => DriverOwner == ItemLease.Unowned ? 0 : DriverOwner;
        public PassengerVehicleIntent(uint sequence, ulong vehicle, uint actor, uint seatGeneration, uint driverOwner,
            uint driverGeneration, PassengerVehicleControl control, VehicleControlMethod method, byte controlIndex = 0)
        {
            Sequence = sequence; Vehicle = vehicle; Actor = actor; SeatGeneration = seatGeneration;
            DriverOwner = driverOwner; DriverGeneration = driverGeneration; Control = control; Method = method; ControlIndex = controlIndex;
            if (vehicle == 0 || actor == SeatOccupancy.Unoccupied || driverGeneration == 0 || !Supported(control, method))
                throw new InvalidDataException("Invalid passenger vehicle intent.");
        }
        // Shared by host-local and remote physical admission. A seatless request is never
        // authorized by an in-vehicle pose or by a stale/non-finite position observation.
        public bool AllowsWalkingActor(uint peer, bool walking, float poseAge, float distanceSquared) =>
            Walking && peer == Actor && walking && !float.IsNaN(poseAge) && poseAge >= 0 && poseAge <= 2 &&
            !float.IsNaN(distanceSquared) && distanceSquared >= 0 && distanceSquared <= 25;
        public bool ReleasesIgnition(PassengerVehicleIntent held) => Control == PassengerVehicleControl.Ignition &&
            Method == VehicleControlMethod.MouseUp && held.Control == PassengerVehicleControl.Ignition &&
            Vehicle == held.Vehicle && ControlIndex == held.ControlIndex;
        public static bool Supported(PassengerVehicleControl control, VehicleControlMethod method) =>
            method == VehicleControlMethod.MouseDown && Enum.IsDefined(typeof(PassengerVehicleControl), control) ||
            method == VehicleControlMethod.MouseUp && control == PassengerVehicleControl.Ignition ||
            method == VehicleControlMethod.MouseRightDown && (control == PassengerVehicleControl.LowBeam || control == PassengerVehicleControl.Gear || control == PassengerVehicleControl.ParkingBrake) ||
            method == VehicleControlMethod.Use && control == PassengerVehicleControl.ParkingBrake;
        internal void Write(BinaryWriter w)
        {
            w.Write(Sequence); w.Write(Vehicle); w.Write(Actor); w.Write(SeatGeneration); w.Write(DriverOwner); w.Write(DriverGeneration);
            w.Write((byte)Control); w.Write((byte)Method); w.Write(ControlIndex);
        }
        internal static PassengerVehicleIntent Read(BinaryReader r) => new PassengerVehicleIntent(r.ReadUInt32(), r.ReadUInt64(),
            r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), (PassengerVehicleControl)r.ReadByte(), (VehicleControlMethod)r.ReadByte(), r.ReadByte());
        public PassengerVehicleIntent Stamp(uint sequence, uint actor) => new PassengerVehicleIntent(sequence, Vehicle, actor,
            SeatGeneration, DriverOwner, DriverGeneration, Control, Method, ControlIndex);
        public byte[] EncodeRequest()
        {
            using var s = new MemoryStream(); using var w = new BinaryWriter(s); w.Write((byte)1); Write(w); return s.ToArray();
        }
        public static PassengerVehicleIntent DecodeRequest(byte[] payload)
        {
            if (payload == null || payload.Length != 32 || payload[0] != 1) throw new InvalidDataException("Invalid passenger control request.");
            using var s = new MemoryStream(payload, false); using var r = new BinaryReader(s); r.ReadByte(); return Read(r);
        }
    }
    // Durable, session-only ordered intents. Coalescing preserves every pending native action;
    // only its authenticated simulation owner can acknowledge an individual sequence.
    public static class PassengerVehicleControlState
    {
        public const ushort Adapter = 139;
        public const int Capacity = 16;
        public static byte[] Encode(IEnumerable<PassengerVehicleIntent> intents)
        {
            var entries = intents.ToArray(); if (entries.Length > Capacity) throw new InvalidDataException("Passenger control queue overflow.");
            using var s = new MemoryStream(); using var w = new BinaryWriter(s); w.Write((byte)entries.Length);
            uint previous = 0;
            foreach (var entry in entries)
            {
                if (entry.Sequence <= previous) throw new InvalidDataException("Passenger control sequence is not ordered.");
                entry.Write(w); previous = entry.Sequence;
            }
            return s.ToArray();
        }
        public static PassengerVehicleIntent[] Decode(byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload[0] > Capacity || payload.Length != 1 + 31 * payload[0])
                throw new InvalidDataException("Invalid passenger control queue.");
            using var s = new MemoryStream(payload, false); using var r = new BinaryReader(s);
            var entries = new PassengerVehicleIntent[r.ReadByte()]; uint previous = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = PassengerVehicleIntent.Read(r);
                if (entries[i].Sequence <= previous) throw new InvalidDataException("Passenger control sequence is not ordered.");
                previous = entries[i].Sequence;
            }
            return entries;
        }
        public static byte[] Ack(uint sequence)
        {
            if (sequence == 0) throw new InvalidDataException("Invalid passenger control acknowledgment.");
            using var s = new MemoryStream(); using var w = new BinaryWriter(s); w.Write((byte)2); w.Write(sequence); return s.ToArray();
        }
        public static uint DecodeAck(byte[] payload)
        {
            if (payload == null || payload.Length != 5 || payload[0] != 2) throw new InvalidDataException("Invalid passenger control acknowledgment.");
            using var s = new MemoryStream(payload, false); using var r = new BinaryReader(s); r.ReadByte(); uint sequence = r.ReadUInt32();
            if (sequence == 0) throw new InvalidDataException("Invalid passenger control acknowledgment."); return sequence;
        }
    }
}
