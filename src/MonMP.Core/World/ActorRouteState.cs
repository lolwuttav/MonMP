using System;
using System.IO;

namespace MonMP.Core.World
{
    // Host-authored presentation state for one AI_Actor_FollowRoute-bound on-foot Actor.
    // Route/controller identity stays in the static entity catalog; this payload never commands the route.
    public readonly struct ActorRouteState : IEquatable<ActorRouteState>
    {
        public const ushort Adapter = 46;
        public const int Size = 29;
        private const byte ActiveFlag = 1;
        private const byte WalkingFlag = 2;
        private const byte GoingBackFlag = 4;
        private const byte AnimatorEnabledFlag = 8;
        private const byte KnownFlags = ActiveFlag | WalkingFlag | GoingBackFlag | AnimatorEnabledFlag;

        public readonly bool Active;
        public readonly bool Walking;
        public readonly bool GoingBack;
        public readonly bool AnimatorEnabled;
        public readonly int CurrentWaypoint;
        public readonly float X, Y, Z, EulerX, EulerY, EulerZ;

        public ActorRouteState(bool active, bool walking, bool goingBack, bool animatorEnabled,
            int currentWaypoint, float x, float y, float z, float eulerX, float eulerY, float eulerZ)
        {
            Active = active; Walking = walking; GoingBack = goingBack; AnimatorEnabled = animatorEnabled;
            CurrentWaypoint = currentWaypoint; X = x; Y = y; Z = z; EulerX = eulerX; EulerY = eulerY; EulerZ = eulerZ;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Flags()); writer.Write(CurrentWaypoint);
            writer.Write(X); writer.Write(Y); writer.Write(Z);
            writer.Write(EulerX); writer.Write(EulerY); writer.Write(EulerZ);
            return stream.ToArray();
        }

        public static ActorRouteState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid actor route state payload.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            byte flags = reader.ReadByte();
            if ((flags & ~KnownFlags) != 0) throw new InvalidDataException("Invalid actor route flags.");
            return new ActorRouteState((flags & ActiveFlag) != 0, (flags & WalkingFlag) != 0,
                (flags & GoingBackFlag) != 0, (flags & AnimatorEnabledFlag) != 0, reader.ReadInt32(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private byte Flags() => (byte)((Active ? ActiveFlag : 0) | (Walking ? WalkingFlag : 0) |
            (GoingBack ? GoingBackFlag : 0) | (AnimatorEnabled ? AnimatorEnabledFlag : 0));

        private void Validate()
        {
            if ((Walking && !Active) || (GoingBack && !Walking) || (CurrentWaypoint == -1 && !GoingBack) || CurrentWaypoint < -1 || CurrentWaypoint > 100000 ||
                !Coordinate(X) || !Coordinate(Y) || !Coordinate(Z) || !Angle(EulerX) || !Angle(EulerY) || !Angle(EulerZ))
                throw new InvalidDataException("Invalid actor route state.");
        }
        private static bool Coordinate(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= -100000f && value <= 100000f;
        private static bool Angle(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= -360f && value <= 360f;

        public bool Equals(ActorRouteState other) => Active == other.Active && Walking == other.Walking && GoingBack == other.GoingBack &&
            AnimatorEnabled == other.AnimatorEnabled && CurrentWaypoint == other.CurrentWaypoint && X == other.X && Y == other.Y && Z == other.Z &&
            EulerX == other.EulerX && EulerY == other.EulerY && EulerZ == other.EulerZ;
        public override bool Equals(object? obj) => obj is ActorRouteState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(HashCode.Combine(Active, Walking, GoingBack, AnimatorEnabled, CurrentWaypoint, X, Y, Z), EulerX, EulerY, EulerZ);
    }
}
