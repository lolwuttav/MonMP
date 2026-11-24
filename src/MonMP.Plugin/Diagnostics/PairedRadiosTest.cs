using System;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // The guest presses native buttons on a standalone house radio; the host runs them and both
    // radios follow the host's power, preset and volume. The guest's station songs follow the host.
    internal sealed class PairedRadiosTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ulong radio;
        private int phase;
        private float next, deadline;
        private bool hostSawOn, hostSawPreset, hostSawOff;
        private FurnitureRadioState before;
        private FurnitureRadioState Authority => FurnitureRadioState.Decode(plugin.World!.Channel.Records[radio].PayloadSpan);
        internal string Status => "radios phase=" + phase + "; radio=" + radio.ToString("X16") + "; count=" + plugin.World?.FurnitureRadios.Count +
            (radio == 0 || plugin.World == null ? "" : "; native=" + Describe(plugin.World.FurnitureRadios.TestNative(radio)) + "; authority=" + Describe(Authority)) +
            "; hostSaw=" + hostSawOn + "/" + hostSawPreset + "/" + hostSawOff + "; radios=" + plugin.World?.FurnitureRadios.TestInventory;
        internal PairedRadiosTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }
        private static string Describe(FurnitureRadioState s) => (s.On ? "on" : "off") + "/" + s.Mode + "/" + s.StationTenths + "/" + s.VolumeThousandths;

        internal bool Tick()
        {
            var radios = plugin.World!.FurnitureRadios;
            if (deadline == 0) deadline = Time.unscaledTime + 120f;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Radios test stalled: " + Status);
            if (radio == 0) { radio = radios.TestAnyRadio; if (radio == 0) return false; }
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host(radios) : Guest(radios);
        }
        private bool Host(Replication.SharedFurnitureRadios radios)
        {
            var native = radios.TestNative(radio);
            if (phase == 0)
            {
                // Start from a known off state before the guest acts.
                if (native.On) radios.TestPress(radio, FurnitureRadioButton.Power);
                phase = 1; next = Time.unscaledTime + 0.5f;
                return false;
            }
            hostSawOn |= native.On;
            hostSawPreset |= native.On && native.StationTenths == 1075;
            hostSawOff |= hostSawPreset && !native.On;
            if (phase == 1 && hostSawOff) phase = 2;
            else if (phase == 2 && plugin.Session!.Peers.Count == 1) { plugin.EndPairedTestSession(); return true; }
            return false;
        }
        private bool Guest(Replication.SharedFurnitureRadios radios)
        {
            var native = radios.TestNative(radio);
            var authority = Authority;
            switch (phase)
            {
                case 0:
                    // Stand beside the radio so the host's reach check accepts the presses.
                    var at = radios.TestPosition(radio) + new Vector3(0, 0, 1f);
                    gameplay.PlayerWalking.transform.position = at;
                    if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
                    phase = 1; next = Time.unscaledTime + 2f;
                    return false;
                case 1:
                    if (authority.On || native.On) return false; // wait for the host's reset to arrive
                    Require(radios.TestClockMatchesRecord, "Guest station songs do not follow the host's station clock.");
                    radios.TestPress(radio, FurnitureRadioButton.Power);
                    // The guest previews the button while its retained world record
                    // stays authoritative until the host publishes the result.
                    Require(!Authority.On, "Guest power preview changed the authoritative radio record.");
                    Require(radios.TestNative(radio).On, "Guest power press did not preview the eligible standalone radio.");
                    phase = 2; return false;
                case 2:
                    if (!authority.On || !native.On) return false;
                    radios.TestPress(radio, FurnitureRadioButton.Preset, 3);
                    phase = 3; return false;
                case 3:
                    if (!authority.On || !native.On || authority.StationTenths != 1075 || native.StationTenths != authority.StationTenths) return false;
                    before = authority;
                    radios.TestPress(radio, FurnitureRadioButton.Volume, 10);
                    phase = 4; return false;
                case 4:
                    if (!authority.On || !native.On || authority.VolumeThousandths <= before.VolumeThousandths || native.VolumeThousandths != authority.VolumeThousandths) return false;
                    radios.TestPress(radio, FurnitureRadioButton.Power);
                    phase = 5; return false;
                case 5:
                    if (authority.On || native.On) return false;
                    phase = 6; next = Time.unscaledTime + 1f;
                    return false;
                case 6:
                    plugin.EndPairedTestSession();
                    return true;
            }
            return false;
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
