using System;
using System.Buffers.Binary;
using System.IO;

namespace MonMP.Core.World
{
    public readonly struct ItemPose
    {
        public readonly float X, Y, Z, Qx, Qy, Qz, Qw;
        public ItemPose(float x, float y, float z, float qx = 0, float qy = 0, float qz = 0, float qw = 1)
        { X = x; Y = y; Z = z; Qx = qx; Qy = qy; Qz = qz; Qw = qw; }
        public void Validate()
        {
            if (!Finite(X, 100000) || !Finite(Y, 100000) || !Finite(Z, 100000) ||
                !Finite(Qx, 1) || !Finite(Qy, 1) || !Finite(Qz, 1) || !Finite(Qw, 1) ||
                Math.Abs(Qx * Qx + Qy * Qy + Qz * Qz + Qw * Qw - 1) > 0.02f) throw new InvalidDataException("Invalid item pose.");
        }
        public void Write(BinaryWriter w) { Validate(); w.Write(X); w.Write(Y); w.Write(Z); w.Write(Qx); w.Write(Qy); w.Write(Qz); w.Write(Qw); }
        public static ItemPose Read(BinaryReader r)
        {
            var pose = new ItemPose(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); pose.Validate(); return pose;
        }
        public bool IsValid
        {
            get
            {
                try { Validate(); return true; }
                catch (InvalidDataException) { return false; }
            }
        }
        // A local transform read: the rotation is renormalized (engine drift is not a protocol
        // error), and a non-finite or out-of-world position is reported instead of thrown.
        public static bool TryLocal(float x, float y, float z, float qx, float qy, float qz, float qw, out ItemPose pose)
        {
            double length = Math.Sqrt((double)qx * qx + (double)qy * qy + (double)qz * qz + (double)qw * qw);
            if (double.IsNaN(length) || double.IsInfinity(length) || length < 1e-6) { qx = qy = qz = 0; qw = 1; }
            else { qx = (float)(qx / length); qy = (float)(qy / length); qz = (float)(qz / length); qw = (float)(qw / length); }
            pose = new ItemPose(x, y, z, qx, qy, qz, qw);
            return Finite(x, 100000) && Finite(y, 100000) && Finite(z, 100000);
        }
        public static bool FiniteValue(float value, float bound) => Finite(value, bound);
        internal static bool Finite(float value, float bound) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= bound;
    }

    [Flags]
    // Burned and Sold only mark why the host removed an item, so guests can present it natively.
    // CapOff: a fuel canister whose cap is not screwed down (its cap is an item child, not a shared part).
    public enum ItemFlags : byte { None = 0, Refrigerated = 1, Burned = 2, Sold = 4, CapOff = 8, RemovalMarks = Burned | Sold }

    // A fuel canister's cap travel rides in the flag byte's high nibble. Level 0 is removed;
    // 1..14 are partial travel; 15 preserves a seated cap whose positive travel rounds to 0%.
    // A fully screwed-down cap has CapOff clear and no high-nibble travel.
    public static class CanisterCap
    {
        public const int Levels = 15;
        public const byte SeatedZeroCommand = 16;
        public const float SeatedZeroTravel = 0.0001f;
        public static byte CommandLevel(float screwed)
        {
            if (!ItemPose.FiniteValue(screwed, 1) || screwed < 0) throw new InvalidDataException("Invalid canister cap travel.");
            if (screwed >= 1f) return Levels;
            int level = Math.Max(0, Math.Min(Levels - 1, (int)Math.Round(screwed * Levels)));
            return screwed > 0 && level == 0 ? SeatedZeroCommand : (byte)level;
        }
        public static float CommandTravel(byte level)
        {
            if (level > SeatedZeroCommand) throw new InvalidDataException("Invalid canister cap command.");
            return level == SeatedZeroCommand ? SeatedZeroTravel : level / (float)Levels;
        }
        public static ItemFlags Encode(float screwed)
        {
            byte command = CommandLevel(screwed);
            if (command == Levels) return ItemFlags.None;
            int level = command == SeatedZeroCommand ? Levels : command;
            return ItemFlags.CapOff | (ItemFlags)(level << 4);
        }
        public static float Screwed(ItemFlags flags) => (flags & ItemFlags.CapOff) == 0 ? 1f :
            ((byte)flags >> 4) == Levels ? SeatedZeroTravel : ((byte)flags >> 4) / (float)Levels;
        public static bool Valid(ItemFlags flags) => ((byte)flags >> 4) == 0 || (flags & ItemFlags.CapOff) != 0;
        public static ItemFlags Strip(ItemFlags flags) => (ItemFlags)((byte)flags & 15);
    }

    // A guest turning a canister's cap it does not hold: its native screw coroutine runs locally and
    // reports the travel (0..15, 15 screwed down; 16 seated at displayed 0%) for the host.
    public readonly struct CanisterCapCommand
    {
        public const byte Marker = 0xCB;
        public const int Size = 2;
        public readonly byte Level;
        public CanisterCapCommand(byte level) { Level = level; }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public byte[] Encode() { _ = CanisterCap.CommandTravel(Level); return new[] { Marker, Level }; }
        public static CanisterCapCommand Decode(byte[] payload)
        {
            if (!Is(payload) || payload[1] > CanisterCap.SeatedZeroCommand) throw new InvalidDataException("Invalid canister cap command.");
            return new CanisterCapCommand(payload[1]);
        }
    }

    // Explicit native save fields; no runtime type names or serialized objects on the wire.
    public readonly struct ItemValues
    {
        public readonly float Value1, Value2;
        public readonly int IntValue1, IntValue2;
        public readonly ItemFlags Flags;
        public ItemValues(float value1, float value2, int intValue1, int intValue2, ItemFlags flags = ItemFlags.None)
        { Value1 = value1; Value2 = value2; IntValue1 = intValue1; IntValue2 = intValue2; Flags = flags; }
        public void Validate()
        {
            if (!ItemPose.Finite(Value1, 1000000) || !ItemPose.Finite(Value2, 1000000) || !CanisterCap.Valid(Flags))
                throw new InvalidDataException("Invalid item values.");
        }
        internal void Write(BinaryWriter w)
        { Validate(); w.Write(Value1); w.Write(Value2); w.Write(IntValue1); w.Write(IntValue2); w.Write((byte)Flags); }
        internal static ItemValues Read(BinaryReader r)
        { var values = new ItemValues(r.ReadSingle(), r.ReadSingle(), r.ReadInt32(), r.ReadInt32(), (ItemFlags)r.ReadByte()); values.Validate(); return values; }
    }

    public sealed class ItemState
    {
        public const ushort Adapter = 2;
        public readonly ushort Prefab;
        public readonly bool Exists;
        public readonly ItemLease Lease;
        public readonly ItemPose Pose;
        public readonly float ScaleX, ScaleY, ScaleZ;
        public readonly ConsumptionReceipt? Consumption;
        public readonly ItemValues Values;
        // Session-only containment. Magnetic tray bolts stay individual item entities so their
        // color, lease and world pose remain authoritative; the parent id and native tray slot
        // restore the hierarchy on replicas. Uncontained items use (0, byte.MaxValue).
        public readonly ulong ContainerEntity;
        public readonly byte ContainerSlot;
        // A loose item resting in a moving vehicle (truck bed, wheelbarrow) is shared relative to that
        // carrier's entity, so every peer places it in its own copy of the vehicle instead of at a
        // lagging world position the vehicle drives into (#119, #120, #127). 0 = world pose.
        public readonly ulong Carrier;
        public ItemState(ushort prefab, bool exists, ItemLease lease, ItemPose pose, float scaleX = 1, float scaleY = 1, float scaleZ = 1,
            ConsumptionReceipt? consumption = null, ItemValues values = default, ulong containerEntity = 0, byte containerSlot = byte.MaxValue, ulong carrier = 0)
        { Carrier = carrier; Prefab = prefab; Exists = exists; Lease = lease; Pose = pose; ScaleX = scaleX; ScaleY = scaleY; ScaleZ = scaleZ; Consumption = consumption; Values = values; ContainerEntity = containerEntity; ContainerSlot = containerSlot; }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Prefab); w.Write(Exists); w.Write(Lease.Owner); w.Write(Lease.Generation); Pose.Write(w); w.Write(ScaleX); w.Write(ScaleY); w.Write(ScaleZ);
            Values.Write(w); w.Write(ContainerEntity); w.Write(ContainerSlot); w.Write(Consumption != null); Consumption?.Write(w);
            if (Carrier != 0) w.Write(Carrier);
            return stream.ToArray();
        }
        public static ItemState Decode(byte[] payload)
        {
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            ushort prefab = r.ReadUInt16(); byte exists = r.ReadByte(); if (exists > 1) throw new InvalidDataException("Invalid existence flag.");
            var lease = new ItemLease(r.ReadUInt32(), r.ReadUInt32()); var pose = ItemPose.Read(r);
            float sx = r.ReadSingle(), sy = r.ReadSingle(), sz = r.ReadSingle();
            var values = ItemValues.Read(r); ulong containerEntity = r.ReadUInt64(); byte containerSlot = r.ReadByte();
            byte receipt = r.ReadByte(); if (receipt > 1) throw new InvalidDataException("Invalid receipt flag.");
            var consumption = receipt == 1 ? ConsumptionReceipt.Read(r) : null;
            ulong carrier = stream.Length - stream.Position >= 8 ? r.ReadUInt64() : 0;
            var state = new ItemState(prefab, exists == 1, lease, pose, sx, sy, sz, consumption, values, containerEntity, containerSlot, carrier);
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing item state."); state.Validate(); return state;
        }
        // World-channel classification borrows an immutable record's bytes. Ordinary
        // live records have a fixed layout; read them without cloning the payload or
        // constructing a stream/reader for each old/new state of every publication.
        // Keep receipt and short-record handling on the existing decoder, including
        // its validation and legacy receipt formats.
        public static ItemState Decode(ReadOnlySpan<byte> payload)
        {
            const int baseSize = 78;
            if (payload.Length < baseSize || payload[77] != 0) return Decode(payload.ToArray());
            if (payload.Length != baseSize && payload.Length != baseSize + 8)
                throw new InvalidDataException("Trailing item state.");
            byte exists = payload[2];
            if (exists > 1) throw new InvalidDataException("Invalid existence flag.");
            var lease = new ItemLease(BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(3, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(7, 4)));
            var pose = new ItemPose(ReadSingle(payload, 11), ReadSingle(payload, 15), ReadSingle(payload, 19),
                ReadSingle(payload, 23), ReadSingle(payload, 27), ReadSingle(payload, 31), ReadSingle(payload, 35));
            var values = new ItemValues(ReadSingle(payload, 51), ReadSingle(payload, 55),
                BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(59, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(63, 4)), (ItemFlags)payload[67]);
            var state = new ItemState(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2)), exists == 1,
                lease, pose, ReadSingle(payload, 39), ReadSingle(payload, 43), ReadSingle(payload, 47),
                values: values, containerEntity: BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(68, 8)),
                containerSlot: payload[76], carrier: payload.Length == baseSize + 8
                    ? BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(baseSize, 8)) : 0);
            state.Validate(); return state;
        }
        private static float ReadSingle(ReadOnlySpan<byte> payload, int offset) =>
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4)));
        private void Validate()
        {
            Pose.Validate(); Values.Validate();
            bool uncontained = ContainerEntity == 0 && ContainerSlot == byte.MaxValue;
            bool contained = ContainerEntity > WorldRecord.DynamicIdStart && ContainerSlot < 55 && ContainerEntity != 0;
            if (Prefab == 0 || Lease.Generation == 0 || (!Exists && Lease.Owner != ItemLease.Unowned) || (Exists && Consumption != null) ||
                (!Exists && !uncontained) || (!uncontained && !contained) ||
                (Carrier != 0 && (!Exists || !uncontained || Lease.Owner != ItemLease.Unowned)) ||
                !ItemPose.Finite(ScaleX, 100) || !ItemPose.Finite(ScaleY, 100) || !ItemPose.Finite(ScaleZ, 100) ||
                // Native right-side vehicle parts use mirrored transforms. Preserve their
                // signed scale; only collapsed axes and non-finite/out-of-bound sizes are invalid.
                ScaleX == 0 || ScaleY == 0 || ScaleZ == 0) throw new InvalidDataException("Invalid item state.");
        }
    }

    // Absorb: the owner's held item was installed into world structure (bolt into a hole, part into a
    // socket); the structure's own adapter replicates the install, so the loose item simply ends.
    // Unpacked: a guest natively took one item out of a shared pack; the host decrements the pack.
    // Ignite: a guest's torch reached a shared newspaper or envelope; the host lights its copy.
    // Planted: a guest planted one cutting from the pile it holds; the host lowers the pile's count.
    public enum ItemAction : byte { Acquire = 1, Move, Drop, Throw, Consume, ConsumeApplied, ConsumeRejected, Interact, Absorb, Unpacked, Ignite, Planted, Merged, BasketPoured, SelectHandTool }

    // A guest's native action (unscrewing a bolt, unpacking, removing a part) spawned a new catalogued
    // item locally. The guest asks the host to adopt it as a shared entity. Sent to that prefab's
    // descriptor record; the nonce pairs the host's reply (new entity id) with the guest's local object.
    public readonly struct ItemSpawnRequest
    {
        public readonly uint Nonce;
        public readonly bool Hold;
        public readonly ItemPose Pose;
        public readonly ItemValues Values;
        public ItemSpawnRequest(uint nonce, bool hold, ItemPose pose, ItemValues values) { Nonce = nonce; Hold = hold; Pose = pose; Values = values; }
        public byte[] Encode()
        {
            if (Nonce == 0) throw new InvalidDataException("Invalid spawn nonce.");
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)0xA5); w.Write(Nonce); w.Write(Hold); Pose.Write(w); Values.Write(w); return stream.ToArray();
        }
        public static ItemSpawnRequest Decode(byte[] payload)
        {
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            if (r.ReadByte() != 0xA5) throw new InvalidDataException("Invalid spawn request.");
            uint nonce = r.ReadUInt32(); byte hold = r.ReadByte();
            var request = new ItemSpawnRequest(nonce, hold == 1, ItemPose.Read(r), ItemValues.Read(r));
            if (nonce == 0 || hold > 1 || stream.Position != stream.Length) throw new InvalidDataException("Invalid spawn request.");
            return request;
        }
    }
    public readonly struct ItemSpawnReply
    {
        public readonly uint Nonce;
        public readonly ulong Entity;
        public ItemSpawnReply(uint nonce, ulong entity) { Nonce = nonce; Entity = entity; }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Nonce); w.Write(Entity); return stream.ToArray();
        }
        public static ItemSpawnReply Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 12) throw new InvalidDataException("Invalid spawn reply.");
            var reply = new ItemSpawnReply(BitConverter.ToUInt32(payload, 0), BitConverter.ToUInt64(payload, 4));
            if (reply.Nonce == 0 || reply.Entity <= WorldRecord.DynamicIdStart) throw new InvalidDataException("Invalid spawn reply.");
            return reply;
        }
    }
    public readonly struct ItemCommand
    {
        public readonly ItemAction Action;
        public readonly uint Generation;
        public readonly ItemPose Pose;
        public readonly bool PlayerDrop;
        public ItemCommand(ItemAction action, uint generation, ItemPose pose, bool playerDrop = false)
        {
            if (playerDrop && action != ItemAction.Drop && action != ItemAction.Throw) throw new InvalidDataException("Invalid player drop action.");
            Action = action; Generation = generation; Pose = pose; PlayerDrop = playerDrop;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)Action); w.Write(Generation); Pose.Write(w); w.Write(PlayerDrop); return stream.ToArray();
        }
        public const int Size = 1 + 4 + 28 + 1;

        // Other systems (bolt tools, hydro mail, ...) address commands to the same item entity
        // with their own marked formats; only an exactly sized, valid payload is an ItemCommand.
        public static bool TryDecode(byte[] payload, out ItemCommand command)
        {
            command = default;
            if (payload == null || payload.Length != Size) return false;
            try { command = Decode(payload); return true; }
            catch (InvalidDataException) { return false; }
        }

        public static ItemCommand Decode(byte[] payload)
        {
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            var action = (ItemAction)r.ReadByte(); uint generation = r.ReadUInt32(); var pose = ItemPose.Read(r);
            byte playerDrop = r.ReadByte();
            if (playerDrop > 1) throw new InvalidDataException("Invalid player drop flag.");
            var command = new ItemCommand(action, generation, pose, playerDrop == 1);
            if (!Enum.IsDefined(typeof(ItemAction), command.Action) || command.Generation == 0 || stream.Position != stream.Length) throw new InvalidDataException("Invalid item command.");
            return command;
        }
    }

    // A player placed a shared chainsaw into the quad's native holder. The holder
    // id is a stable scene identity; the host still validates the live item lease,
    // player pose, item pose and native held-state before invoking the game action.
    public readonly struct ChainsawHolderCommand
    {
        public const byte Marker = 0xD8;
        public const int Size = 1 + 8 + 4 + 28;
        public readonly ulong HolderId;
        public readonly uint Generation;
        public readonly ItemPose Pose;

        public ChainsawHolderCommand(ulong holderId, uint generation, ItemPose pose)
        { HolderId = holderId; Generation = generation; Pose = pose; }

        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Marker); w.Write(HolderId); w.Write(Generation); Pose.Write(w); return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static ChainsawHolderCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid chainsaw-holder command.");
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            if (r.ReadByte() != Marker) throw new InvalidDataException("Invalid chainsaw-holder command marker.");
            var command = new ChainsawHolderCommand(r.ReadUInt64(), r.ReadUInt32(), ItemPose.Read(r));
            command.Validate();
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing chainsaw-holder command data.");
            return command;
        }

        private void Validate()
        {
            if (HolderId == 0 || Generation == 0) throw new InvalidDataException("Invalid chainsaw-holder command identity.");
            Pose.Validate();
        }
    }
}

namespace MonMP.Core.World
{
    public enum LiftOp : byte { Up = 1, Down = 2, Stop = 3, Arm = 4 }
    // A guest's car lift button or arm click, sent to the lift's item record; the host runs the
    // native hydraulics and the lift's height and arm positions replicate as the item's save values.
    public readonly struct LiftCommand
    {
        public const byte Marker = 0xB7;
        public const int Size = 4;
        public readonly LiftOp Op;
        public readonly byte Arm;
        public readonly sbyte Direction;
        public LiftCommand(LiftOp op, byte arm = 0, sbyte direction = 0) { Op = op; Arm = arm; Direction = direction; }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public byte[] Encode() { Validate(); return new[] { Marker, (byte)Op, Arm, unchecked((byte)Direction) }; }
        public static LiftCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid lift command.");
            var command = new LiftCommand((LiftOp)payload[1], payload[2], unchecked((sbyte)payload[3]));
            command.Validate(); return command;
        }
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(LiftOp), Op) || Arm > 3 || (Op == LiftOp.Arm ? Direction != -1 && Direction != 1 : Direction != 0 || Arm != 0))
                throw new InvalidDataException("Invalid lift command.");
        }
        // The native IntValue2 arm encoding: a leading 1, then one 0..6 digit per arm.
        public static bool ValidArms(int value)
        {
            if (value < 10000 || value > 16666) return false;
            for (int i = 0; i < 4; i++, value /= 10) if (value % 10 > 6) return false;
            return true;
        }
    }
}
