using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public enum PizzaSnackAction : byte { StepIn = 1, StartEvent = 2, Deliver = 3 }

    // Commands for the native PizzaSnack event use a stable route record so guest
    // requests remain valid while the host advances the event's route state.
    public readonly struct PizzaSnackCommand
    {
        public const ushort Adapter = 72;
        public const byte Marker = 0xE8;
        public const int Size = 51;
        public readonly bool IndependentAim;
        public const float Reach = 2.4f;
        public static readonly ulong Entity = StaticId.Hash("monmp/events/pizza-snack/command/v1");
        public static readonly ulong StateEntity = StaticId.Hash("monmp/events/pizza-snack/state/v1");

        public readonly Guid Operation;
        public readonly PizzaSnackAction Action;
        public readonly ulong Box;
        public readonly uint Generation;
        public readonly float CameraX, CameraY, CameraZ, CameraYaw, CameraPitch;

        public PizzaSnackCommand(Guid operation, PizzaSnackAction action, ulong box, uint generation,
            float cameraX = 0, float cameraY = 0, float cameraZ = 0, float cameraYaw = 0, float cameraPitch = 0, bool independentAim = false)
        {
            Operation = operation; Action = action; Box = box; Generation = generation;
            CameraX = cameraX; CameraY = cameraY; CameraZ = cameraZ; CameraYaw = cameraYaw; CameraPitch = cameraPitch;
            IndependentAim = independentAim;
            Validate();
        }

        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write((byte)Action);
            writer.Write(Box); writer.Write(Generation);
            writer.Write(CameraX); writer.Write(CameraY); writer.Write(CameraZ); writer.Write(CameraYaw); writer.Write(CameraPitch);
            writer.Write(IndependentAim);
            return stream.ToArray();
        }

        public static PizzaSnackCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid PizzaSnack command length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Marker) throw new InvalidDataException("Invalid PizzaSnack command marker.");
            var command = new PizzaSnackCommand(new Guid(reader.ReadBytes(16)), (PizzaSnackAction)reader.ReadByte(),
                reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing PizzaSnack command data.");
            return command;
        }

        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;
        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        private void Validate()
        {
            if (Operation == Guid.Empty || !Enum.IsDefined(typeof(PizzaSnackAction), Action))
                throw new InvalidDataException("Invalid PizzaSnack command identity.");
            if (Action == PizzaSnackAction.StepIn)
            {
                if (Box != 0 || Generation != 0 || CameraX != 0 || CameraY != 0 || CameraZ != 0 || CameraYaw != 0 || CameraPitch != 0)
                    throw new InvalidDataException("Invalid PizzaSnack step-in command.");
                return;
            }
            if (Box <= WorldRecord.DynamicIdStart || Generation == 0)
                throw new InvalidDataException("Invalid PizzaSnack box lease.");
            if (Action == PizzaSnackAction.Deliver)
            {
                if (CameraX != 0 || CameraY != 0 || CameraZ != 0 || CameraYaw != 0 || CameraPitch != 0)
                    throw new InvalidDataException("Delivery command cannot carry a camera ray.");
                return;
            }
            if (!Finite(CameraX) || Math.Abs(CameraX) > 100000 || !Finite(CameraY) || Math.Abs(CameraY) > 100000 ||
                !Finite(CameraZ) || Math.Abs(CameraZ) > 100000 || !Finite(CameraYaw) || CameraYaw < 0 || CameraYaw > 360 ||
                !Finite(CameraPitch) || CameraPitch < -90 || CameraPitch > 90)
                throw new InvalidDataException("Invalid PizzaSnack camera ray.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public enum PizzaSnackResult : byte
    {
        Applied = 1, Duplicate = 2, Capacity = 3, InvalidLease = 4, StalePlayer = 5,
        PlayerNotWalking = 6, InvalidCamera = 7, WrongTarget = 8, StoreClosed = 9,
        DoorAlreadyOpen = 10, EventAlreadyRunning = 11, EventNotRunning = 12,
        BoxNotAtDropOff = 13, NativeEffectFailed = 14
    }

    public readonly struct PizzaSnackReply
    {
        public const int Size = 17;
        public readonly Guid Operation;
        public readonly PizzaSnackResult Result;
        public PizzaSnackReply(Guid operation, PizzaSnackResult result) { Operation = operation; Result = result; Validate(); }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Operation.ToByteArray()); writer.Write((byte)Result); return stream.ToArray();
        }
        public static PizzaSnackReply Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid PizzaSnack reply length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            var reply = new PizzaSnackReply(new Guid(reader.ReadBytes(16)), (PizzaSnackResult)reader.ReadByte());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing PizzaSnack reply data.");
            return reply;
        }
        private void Validate()
        {
            if (Operation == Guid.Empty || !Enum.IsDefined(typeof(PizzaSnackResult), Result))
                throw new InvalidDataException("Invalid PizzaSnack reply.");
        }
    }

    // Event route state is small and presentation-only on guests. Route point IDs
    // are indices into the host-authored scene array, never network object paths.
    public sealed class PizzaSnackState
    {
        public const byte Marker = 0xE9;
        public bool StoreOpen { get; }
        public bool TakeOutDoorOpen { get; }
        public bool EventRunning { get; }
        public ushort[] Route { get; }
        public byte TargetIndex { get; }

        public PizzaSnackState(bool storeOpen, bool takeOutDoorOpen, bool eventRunning, IEnumerable<ushort>? route = null, byte targetIndex = byte.MaxValue)
        {
            StoreOpen = storeOpen; TakeOutDoorOpen = takeOutDoorOpen; EventRunning = eventRunning;
            Route = route?.ToArray() ?? Array.Empty<ushort>(); TargetIndex = targetIndex; Validate();
        }

        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(8 + Route.Length * 2); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); byte flags = (byte)((StoreOpen ? 1 : 0) | (TakeOutDoorOpen ? 2 : 0) | (EventRunning ? 4 : 0));
            writer.Write(flags); writer.Write((byte)Route.Length); foreach (ushort index in Route) writer.Write(index);
            writer.Write(TargetIndex); return stream.ToArray();
        }

        public static PizzaSnackState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 4 || payload.Length > 12) throw new InvalidDataException("Invalid PizzaSnack state length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Marker) throw new InvalidDataException("Invalid PizzaSnack state marker.");
            byte flags = reader.ReadByte(), count = reader.ReadByte();
            if ((flags & ~7) != 0 || count > 3 || payload.Length != 4 + count * 2) throw new InvalidDataException("Invalid PizzaSnack state header.");
            var route = new ushort[count]; for (int i = 0; i < route.Length; i++) route[i] = reader.ReadUInt16();
            var state = new PizzaSnackState((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, route, reader.ReadByte());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing PizzaSnack state data.");
            return state;
        }

        private void Validate()
        {
            // Native store, take-out door and route lifecycles are independent.
            // A retained pizza box can start a route after the store has closed.
            // These flags describe host presentation; only the route shape must
            // be consistent here. New guest actions retain their policy checks.
            if (Route.Length > 3 ||
                (EventRunning && (Route.Length == 0 || TargetIndex >= Route.Length)) ||
                (!EventRunning && (Route.Length != 0 || TargetIndex != byte.MaxValue)))
                throw new InvalidDataException("Invalid PizzaSnack route state.");
            if (Route.Any(index => index == ushort.MaxValue)) throw new InvalidDataException("Invalid PizzaSnack route index.");
        }
    }

    public static class PizzaSnackPolicy
    {
        public static PizzaSnackResult ValidateStepIn(bool poseFresh, byte playerState, bool storeOpen, bool doorOpen, bool nearDetector)
        {
            if (!poseFresh) return PizzaSnackResult.StalePlayer;
            if (playerState != 0) return PizzaSnackResult.PlayerNotWalking;
            if (!storeOpen) return PizzaSnackResult.StoreClosed;
            if (doorOpen) return PizzaSnackResult.DoorAlreadyOpen;
            return nearDetector ? PizzaSnackResult.Applied : PizzaSnackResult.WrongTarget;
        }

        public static PizzaSnackResult ValidateStart(bool exactLease, bool poseFresh, byte playerState,
            bool cameraOriginFits, bool cameraAimFits, bool rayHitsExactBox, bool nearPickup, bool storeOpen, bool eventRunning)
        {
            if (!exactLease) return PizzaSnackResult.InvalidLease;
            if (!poseFresh) return PizzaSnackResult.StalePlayer;
            if (playerState != 0) return PizzaSnackResult.PlayerNotWalking;
            if (!cameraOriginFits || !cameraAimFits) return PizzaSnackResult.InvalidCamera;
            if (!rayHitsExactBox || !nearPickup) return PizzaSnackResult.WrongTarget;
            if (!storeOpen) return PizzaSnackResult.StoreClosed;
            if (eventRunning) return PizzaSnackResult.EventAlreadyRunning;
            return PizzaSnackResult.Applied;
        }

        public static PizzaSnackResult ValidateDelivery(bool exactLease, bool poseFresh, byte playerState,
            bool eventRunning, bool playerNearTarget, bool boxOverlapsTarget)
        {
            if (!exactLease) return PizzaSnackResult.InvalidLease;
            if (!poseFresh) return PizzaSnackResult.StalePlayer;
            if (playerState != 0) return PizzaSnackResult.PlayerNotWalking;
            if (!eventRunning) return PizzaSnackResult.EventNotRunning;
            return playerNearTarget && boxOverlapsTarget ? PizzaSnackResult.Applied : PizzaSnackResult.BoxNotAtDropOff;
        }
    }

    public enum PizzaSnackClaimResult : byte { Claimed, Duplicate, Capacity }
    public sealed class PizzaSnackClaims
    {
        public const int Capacity = 8192;
        private readonly HashSet<(uint Peer, Guid Operation)> claims = new HashSet<(uint, Guid)>();
        public int Count => claims.Count;
        public PizzaSnackClaimResult Claim(uint peer, Guid operation)
        {
            if (peer == 0 || operation == Guid.Empty) throw new InvalidDataException("Invalid PizzaSnack claim.");
            var key = (peer, operation);
            if (claims.Contains(key)) return PizzaSnackClaimResult.Duplicate;
            if (claims.Count >= Capacity) return PizzaSnackClaimResult.Capacity;
            claims.Add(key); return PizzaSnackClaimResult.Claimed;
        }
        public void Clear() => claims.Clear();
    }
}
