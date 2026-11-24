using System;
using System.Collections.Generic;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.Session
{
    public sealed partial class SessionOptions
    {
        public const string SupportedGameHash = "A7FF10D8090B788DE54DE31F31B8B0BCD338F94FF6C943653EA53D66395A20EE";
        public string GameHash { get; set; } = SupportedGameHash;
        public string ModVersion { get; set; } = CurrentModVersion;
        public string Scene { get; set; } = "";
        public string DisplayName { get; set; } = PlayerNamePolicy.DefaultName;
        public Guid ProfileId { get; set; } = Guid.NewGuid();
        // Legacy private resume metadata. Friends can resume without it; never sent to other guests.
        public Guid ProfileKey { get; set; } = Guid.NewGuid();
        // Host: readable same-world saved identities, independent of credential/personal-state validity.
        public Func<IEnumerable<KeyValuePair<Guid, string>>>? SavedProfileIdentities { get; set; }
        public uint ColorRgb { get; set; } = 0x40BBDD;
        public string Password { get; set; } = "";
        public bool EnableIPv6 { get; set; } = true;
        public string WorldCatalog { get; set; } = "";
        public bool LoadWorldAfterHandshake { get; set; }
        public bool HardMode { get; set; }
        public bool Permadeath { get; set; }
        public byte HardModeLayout { get; set; }
        public uint HiddenVehicles { get; set; }
        public uint StoredVehicles { get; set; }
        public PlayerPose Spawn { get; set; } = new PlayerPose(0, 0, 0, 0, 0);

        internal void Validate()
        {
            Wire.ValidateName(DisplayName);
            if (ProfileId == Guid.Empty || ProfileKey == Guid.Empty || GameHash.Length != 64 || string.IsNullOrWhiteSpace(Scene) || ColorRgb > 0xFFFFFF)
                throw new InvalidDataException("Invalid session identity or scene.");
            // Check all string bounds before starting a socket.
            Hello();
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); World().Write(writer);
        }

        internal byte[] Hello() => Wire.Encode(MessageKind.Hello, Guid.Empty, w =>
        {
            Wire.WriteText(w, GameHash, 64);
            Wire.WriteText(w, ModVersion, 32);
            Wire.WriteText(w, Scene, 200);
            Wire.WriteText(w, Password, 128);
            new PeerInfo(0, ProfileId, DisplayName, ColorRgb).Write(w);
            Wire.WriteText(w, WorldCatalog, 64);
            w.Write(LoadWorldAfterHandshake ? (byte)1 : (byte)0);
            w.Write(ProfileKey.ToByteArray());
        });

        public static byte[] ProfileKeyHash(Guid profile, Guid key)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var input = new byte[48];
            System.Text.Encoding.ASCII.GetBytes("MonMP-profile-key").CopyTo(input, 0);
            profile.ToByteArray().CopyTo(input, 16); key.ToByteArray().CopyTo(input, 32);
            return sha.ComputeHash(input);
        }

        internal SessionWorld World() => new SessionWorld(Scene, WorldCatalog, HardMode, Permadeath, Spawn, HardMode ? HardModeLayout : (byte)0, HiddenVehicles, StoredVehicles);

        internal SessionOptions Copy() => (SessionOptions)MemberwiseClone();
    }

    public sealed class PeerInfo
    {
        public uint Id { get; }
        public Guid ProfileId { get; }
        public string DisplayName { get; }
        public uint ColorRgb { get; }
        public PeerInfo(uint id, Guid profileId, string name, uint colorRgb)
        {
            Wire.ValidateName(name);
            if (profileId == Guid.Empty || colorRgb > 0xFFFFFF) throw new InvalidDataException("Invalid peer identity.");
            Id = id; ProfileId = profileId; DisplayName = name; ColorRgb = colorRgb;
        }
        internal void Write(BinaryWriter w)
        {
            w.Write(Id); w.Write(ProfileId.ToByteArray());
            Wire.WriteText(w, DisplayName, Wire.MaxNameBytes); w.Write(ColorRgb);
        }
        internal static PeerInfo Read(BinaryReader r) => new PeerInfo(r.ReadUInt32(), new Guid(r.ReadBytes(16)), Wire.ReadText(r, Wire.MaxNameBytes), r.ReadUInt32());
    }
}
