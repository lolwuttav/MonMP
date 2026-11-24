using System;
using System.IO;

namespace MonMP.Core.Protocol
{
    // The native one-shot emotes: the "Oups" and "Sacre" voice lines (any source, including Sorry),
    // spitting, drunk vomiting, the interaction hand poke and petting Leo. Sorry's raised hand is
    // held, so it travels as a pose flag instead. Wave, Point and ThumbsUp are cosmetic overlays
    // on the stock avatar body; they do not invoke native player or vehicle actions.
    public enum PlayerEmoteKind : byte
    {
        None = 0, Oups = 1, Sacre = 2, Spit = 3, Vomit = 4, Interact = 5, PetLeo = 6,
        Wave = 7, Point = 8, ThumbsUp = 9
    }

    // Native first-person hand states, active tool effects, and whether a tool is equipped.
    [Flags]
    public enum PlayerHands : byte { None = 0, Map = 1, Console = 2, Joint = 4, Smoking = 8, Tool = 16, Equipped = 32, Zoom = 64 }

    // Asleep in a bed (the pose position is then the sleeper's eyes and the yaw points along the
    // bed towards the feet) or down on the ground after dying or passing out.
    public enum PlayerRest : byte { None = 0, Sleeping = 1, Down = 2 }

    public static class PlayerActivityPolicy
    {
        public const float MovementDistanceSquared = 0.0025f;

        // Camera yaw/pitch and UI-only input are intentionally absent: only avatar travel and
        // recent gameplay actions change the shared presence indicator.
        public static bool IsActive(float previousX, float previousY, float previousZ,
            float currentX, float currentY, float currentZ, bool recentAction)
        {
            if (recentAction) return true;
            double x = (double)currentX - previousX, y = (double)currentY - previousY, z = (double)currentZ - previousZ;
            return x * x + y * y + z * z > MovementDistanceSquared;
        }
    }

    public static class PlayerEmotePolicy
    {
        public static bool IsCosmeticGesture(PlayerEmoteKind kind) =>
            kind == PlayerEmoteKind.Wave || kind == PlayerEmoteKind.Point || kind == PlayerEmoteKind.ThumbsUp;

        public static bool CanPlayCosmeticGesture(PlayerEmoteKind kind, bool availableState, PlayerRest rest, bool sorry) =>
            IsCosmeticGesture(kind) && availableState && rest == PlayerRest.None && !sorry;

        public static bool CanContinueCosmeticGesture(PlayerEmoteKind kind, bool availableState, PlayerRest rest,
            PlayerHands hands, bool sorry, bool map, bool console, bool heldGoal, bool nativeGesture)
        {
            const PlayerHands blockingHands = PlayerHands.Map | PlayerHands.Console | PlayerHands.Zoom;
            // Held-item and Wheelbarrow grips are applied after this cosmetic overlay by the body
            // solver, so they keep their native hand targets while the emote remains available.
            // Map and console take both hands; Sorry and native hand gestures retain priority.
            return IsCosmeticGesture(kind) && availableState && rest == PlayerRest.None && (hands & blockingHands) == 0 &&
                !sorry && !map && !console && !nativeGesture;
        }
    }

    // Tracks the latest one-shot sequence seen for one remote peer. The first pose is only a
    // baseline (joining mid-emote must not replay it); any later distinct byte is a new event.
    // Pose transport ordering is checked before this gate, so equality is sufficient here and
    // byte wrap from 255 to 0 remains a valid next event.
    public struct PlayerEmoteSequenceGate
    {
        private bool initialized;
        private byte sequence;

        public bool Accept(byte candidate)
        {
            if (!initialized)
            {
                initialized = true;
                sequence = candidate;
                return false;
            }
            if (candidate == sequence) return false;
            sequence = candidate;
            return true;
        }

        public void Reset()
        {
            initialized = false;
            sequence = 0;
        }
    }

    public readonly struct PlayerPose
    {
        // The original five hand states fill the pose flags byte. Equipment and zoom
        // share a bounded byte, leaving existing packed flags and shirt values intact.
        private const PlayerHands PackedHands = PlayerHands.Map | PlayerHands.Console | PlayerHands.Joint | PlayerHands.Smoking | PlayerHands.Tool;
        private const PlayerHands KnownHands = PackedHands | PlayerHands.Equipped | PlayerHands.Zoom;
        public const byte WalkingState = 0;
        public const int MaxEmoteVariant = 31;
        public readonly float X, Y, Z, Yaw, Pitch;
        public readonly byte State, Shirt, Model;
        public readonly PlayerAppearance Appearance;
        public readonly bool Crouched, Flashlight, Sorry, Spectating, RecentAction;
        // The high bit of the shirt byte describes the current pose. Wire 91 tool
        // requests also carry the mode sampled with their ray, since poses can arrive late.
        public readonly bool IndependentAim;
        private const byte IndependentAimBit = 0x80;
        public readonly PlayerHands Hands;
        public readonly PlayerRest Rest;
        // A one-shot emote is repeated in several poses under one sequence number; receivers play
        // it when the sequence changes. Kind and variant have separate bytes so new kinds do not
        // reduce the native voice-clip variant range (0..31).
        public readonly byte EmoteSequence, EmoteVariant;
        public readonly PlayerEmoteKind Emote;
        // How drunk (0..MaxDrunk) and whether hungover, for the body's sway and slump.
        public const byte MaxDrunk = 7;
        public readonly byte Drunk;
        public readonly bool Hungover;
        // The shared vehicle or wheelbarrow the player stands on, with the feet in its space. A rider's
        // world position follows the sender's copy of the carrier, which lags or leads everyone else's:
        // placed on the receiver's own copy it stays put on the bed (#249). Sent after the pose in the
        // Pose message only; Write leaves it out, so poses embedded in other messages are unchanged.
        public readonly ulong Carrier;
        public readonly float CarrierX, CarrierY, CarrierZ;
        // While spectating, X/Y/Z stay at the real walking-controller root for interaction,
        // seat, persistence, and movement policy. This separate point lets peers show only the
        // spectator's floating head at their orbit camera without moving that native body anchor.
        public readonly float SpectatorHeadX, SpectatorHeadY, SpectatorHeadZ;
        public const float MaxCarrierOffset = 100;
        public PlayerPose(float x, float y, float z, float yaw, float pitch, byte state = 0, bool crouched = false, byte shirt = 0, bool flashlight = false,
            bool sorry = false, byte emoteSequence = 0, PlayerEmoteKind emote = PlayerEmoteKind.None, byte emoteVariant = 0,
            PlayerHands hands = PlayerHands.None, PlayerRest rest = PlayerRest.None, byte drunk = 0, bool hungover = false,
            bool spectating = false, float? spectatorHeadX = null, float? spectatorHeadY = null, float? spectatorHeadZ = null,
            bool recentAction = false, byte model = 0, PlayerAppearance? appearance = null)
        {
            X = x; Y = y; Z = z; Yaw = yaw; Pitch = pitch; State = state; Crouched = crouched; Shirt = shirt; Flashlight = flashlight;
            Sorry = sorry; EmoteSequence = emoteSequence; Emote = emote; EmoteVariant = emoteVariant; Hands = hands; Rest = rest;
            Drunk = drunk; Hungover = hungover;
            Spectating = spectating;
            RecentAction = recentAction;
            Appearance = appearance ?? PlayerAppearance.Whole(model);
            Model = Appearance.Head;
            SpectatorHeadX = spectating ? spectatorHeadX ?? x : x;
            SpectatorHeadY = spectating ? spectatorHeadY ?? y : y;
            SpectatorHeadZ = spectating ? spectatorHeadZ ?? z : z;
            Carrier = 0; CarrierX = CarrierY = CarrierZ = 0;
            IndependentAim = false;
        }

        // Retain the pre-88 constructor signature for optional modules already compiled
        // against Core. New wire peers still require matching builds.
        public PlayerPose(float x, float y, float z, float yaw, float pitch, byte state, bool crouched, byte shirt, bool flashlight,
            bool sorry, byte emoteSequence, PlayerEmoteKind emote, byte emoteVariant, PlayerHands hands, PlayerRest rest,
            byte drunk, bool hungover, bool spectating, float? spectatorHeadX, float? spectatorHeadY, float? spectatorHeadZ, bool recentAction, byte model)
            : this(x, y, z, yaw, pitch, state, crouched, shirt, flashlight, sorry, emoteSequence, emote, emoteVariant,
                hands, rest, drunk, hungover, spectating, spectatorHeadX, spectatorHeadY, spectatorHeadZ, recentAction, model, null) { }

        private PlayerPose(in PlayerPose p, ulong carrier, float x, float y, float z)
            : this(p.X, p.Y, p.Z, p.Yaw, p.Pitch, p.State, p.Crouched, p.Shirt, p.Flashlight, p.Sorry, p.EmoteSequence, p.Emote, p.EmoteVariant,
                p.Hands, p.Rest, p.Drunk, p.Hungover, p.Spectating, p.SpectatorHeadX, p.SpectatorHeadY, p.SpectatorHeadZ, p.RecentAction, p.Model, p.Appearance)
        {
            Carrier = carrier; CarrierX = x; CarrierY = y; CarrierZ = z;
            IndependentAim = p.IndependentAim;
        }

        private PlayerPose(in PlayerPose p, bool spectating, float headX, float headY, float headZ)
            : this(p.X, p.Y, p.Z, p.Yaw, p.Pitch, p.State, p.Crouched, p.Shirt, p.Flashlight, p.Sorry, p.EmoteSequence, p.Emote, p.EmoteVariant,
                p.Hands, p.Rest, p.Drunk, p.Hungover, spectating, headX, headY, headZ, p.RecentAction, p.Model, p.Appearance)
        {
            Carrier = p.Carrier; CarrierX = p.CarrierX; CarrierY = p.CarrierY; CarrierZ = p.CarrierZ;
            IndependentAim = p.IndependentAim;
        }

        private PlayerPose(in PlayerPose p, bool recentAction)
            : this(p.X, p.Y, p.Z, p.Yaw, p.Pitch, p.State, p.Crouched, p.Shirt, p.Flashlight, p.Sorry, p.EmoteSequence, p.Emote, p.EmoteVariant,
                p.Hands, p.Rest, p.Drunk, p.Hungover, p.Spectating, p.SpectatorHeadX, p.SpectatorHeadY, p.SpectatorHeadZ, recentAction, p.Model, p.Appearance)
        {
            Carrier = p.Carrier; CarrierX = p.CarrierX; CarrierY = p.CarrierY; CarrierZ = p.CarrierZ;
            IndependentAim = p.IndependentAim;
        }

        public PlayerPose WithCarrier(ulong carrier, float x, float y, float z)
        {
            if (carrier == 0 || !Finite(x, MaxCarrierOffset) || !Finite(y, MaxCarrierOffset) || !Finite(z, MaxCarrierOffset))
                throw new InvalidDataException("Invalid player carrier.");
            return new PlayerPose(this, carrier, x, y, z);
        }

        public PlayerPose WithSpectatorHead(float x, float y, float z) => new PlayerPose(this, true, x, y, z);

        public PlayerPose WithSpectating(bool spectating) => new PlayerPose(this, spectating, X, Y, Z);

        public PlayerPose WithRecentAction(bool recentAction) => new PlayerPose(this, recentAction);

        private PlayerPose(in PlayerPose p, bool independentAim, byte unused)
            : this(p, p.RecentAction)
        {
            IndependentAim = independentAim;
        }

        public PlayerPose WithIndependentAim(bool independentAim) =>
            independentAim == IndependentAim ? this : new PlayerPose(this, independentAim, 0);

        public void WriteCarrier(BinaryWriter writer)
        {
            if (Carrier == 0) return;
            writer.Write(Carrier); writer.Write(CarrierX); writer.Write(CarrierY); writer.Write(CarrierZ);
        }

        public PlayerPose ReadCarrier(BinaryReader reader)
        {
            if (reader.BaseStream.Position == reader.BaseStream.Length) return this;
            ulong carrier = reader.ReadUInt64();
            float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
            return WithCarrier(carrier, x, y, z);
        }

        public void Write(BinaryWriter writer)
        {
            Validate();
            writer.Write(X); writer.Write(Y); writer.Write(Z);
            writer.Write(Yaw); writer.Write(Pitch); writer.Write(State);
            writer.Write((byte)((Crouched ? 1 : 0) | (Flashlight ? 2 : 0) | (Sorry ? 4 : 0) | (byte)(Hands & PackedHands) << 3));
            writer.Write((byte)(Shirt | (IndependentAim ? IndependentAimBit : 0)));
            writer.Write(EmoteSequence); writer.Write((byte)Emote); writer.Write(EmoteVariant);
            writer.Write((byte)((byte)Rest | Drunk << 2 | (Hungover ? 32 : 0) | (Spectating ? 64 : 0) | (RecentAction ? 128 : 0)));
            // Two equipment bits and the hair/headwear ID retain their original slot. Wire 89
            // carries the bounded parts, optional RGB and native-texture flag before spectator/carrier data.
            writer.Write((byte)(((Hands & PlayerHands.Equipped) != 0 ? 1 : 0) | ((Hands & PlayerHands.Zoom) != 0 ? 2 : 0) | Model << 2));
            Appearance.WriteParts(writer);
            if (Spectating) { writer.Write(SpectatorHeadX); writer.Write(SpectatorHeadY); writer.Write(SpectatorHeadZ); }
        }

        public static PlayerPose Read(BinaryReader reader)
        {
            float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
            float yaw = reader.ReadSingle(), pitch = reader.ReadSingle();
            byte state = reader.ReadByte(), flags = reader.ReadByte(), shirtByte = reader.ReadByte();
            byte shirt = (byte)(shirtByte & ~IndependentAimBit);
            bool independentAim = (shirtByte & IndependentAimBit) != 0;
            byte sequence = reader.ReadByte(), emote = reader.ReadByte(), variant = reader.ReadByte(), rest = reader.ReadByte();
            byte equipped = reader.ReadByte();
            byte model = (byte)(equipped >> 2);
            if (!PlayerModelPolicy.Valid(model)) throw new InvalidDataException("Invalid player appearance state.");
            var appearance = PlayerAppearance.ReadParts(reader, model);
            bool spectating = (rest & 64) != 0;
            float spectatorHeadX = x, spectatorHeadY = y, spectatorHeadZ = z;
            if (spectating) { spectatorHeadX = reader.ReadSingle(); spectatorHeadY = reader.ReadSingle(); spectatorHeadZ = reader.ReadSingle(); }
            var hands = (PlayerHands)(flags >> 3) | ((equipped & 1) != 0 ? PlayerHands.Equipped : PlayerHands.None) |
                ((equipped & 2) != 0 ? PlayerHands.Zoom : PlayerHands.None);
            var pose = new PlayerPose(x, y, z, yaw, pitch, state, (flags & 1) != 0, shirt, (flags & 2) != 0,
                (flags & 4) != 0, sequence, (PlayerEmoteKind)emote, variant, hands, (PlayerRest)(rest & 3),
                (byte)(rest >> 2 & 7), (rest & 32) != 0, spectating, spectatorHeadX, spectatorHeadY, spectatorHeadZ, (rest & 128) != 0, model, appearance);
            if (independentAim) pose = pose.WithIndependentAim(true);
            pose.Validate();
            return pose;
        }

        public void Validate()
        {
            Appearance.Validate();
            if (!Finite(X, 100000) || !Finite(Y, 100000) || !Finite(Z, 100000) ||
                !Finite(Yaw, 360) || !Finite(Pitch, 90) || State > 5 || Shirt > 8 || !PlayerModelPolicy.Valid(Model) ||
                Emote > PlayerEmoteKind.ThumbsUp || EmoteVariant > MaxEmoteVariant || (Emote == PlayerEmoteKind.None && EmoteVariant != 0) ||
                (PlayerEmotePolicy.IsCosmeticGesture(Emote) && EmoteVariant != 0) ||
                (Hands & ~KnownHands) != 0 || Rest > PlayerRest.Down || Drunk > MaxDrunk ||
                !Finite(SpectatorHeadX, 100000) || !Finite(SpectatorHeadY, 100000) || !Finite(SpectatorHeadZ, 100000) ||
                !Spectating && (SpectatorHeadX != X || SpectatorHeadY != Y || SpectatorHeadZ != Z) ||
                Spectating && (State != WalkingState || Rest != PlayerRest.None || Hands != PlayerHands.None || Sorry || Flashlight) ||
                IndependentAim && (Spectating || Rest != PlayerRest.None))
                throw new InvalidDataException("Invalid player pose.");
        }

        private static bool Finite(float value, float bound) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= bound;
    }

    public static class SpectatorAdmissionPolicy
    {
        public static bool CanStart(bool sessionReady, bool worldSynchronized, byte playerState, bool hasHeldItemOrTool,
            PlayerHands hands, PlayerRest rest, bool sorry, bool flashlight, bool dead, bool sleeping,
            bool gameplayOrUiBlocked, bool saveBlocked, bool presentationHeld, bool cameraAvailable) =>
            sessionReady && worldSynchronized && playerState == PlayerPose.WalkingState && !hasHeldItemOrTool &&
            hands == PlayerHands.None && rest == PlayerRest.None && !sorry && !flashlight && !dead && !sleeping &&
            !gameplayOrUiBlocked && !saveBlocked && !presentationHeld && cameraAvailable;
    }
}
