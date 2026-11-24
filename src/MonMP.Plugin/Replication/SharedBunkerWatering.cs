using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The home bunker's grow room: the main switch, the auto-watering toggle and its watering pass
    // run on the host. Guests mirror both switches, play each watering pass (sprinkler VFX + sound),
    // and give their planter replicas the host's light/auto-water status, which the native minute
    // tick (blocked on guests) would otherwise never set.
    internal sealed class SharedBunkerWatering : IDisposable
    {
        public const ushort Adapter = 96;
        private const byte Powered = 1, AutoWater = 2, MainSwitch = 4, Fertilized = 8;
        private const byte OpMain = 1, OpAuto = 2, OpWater = 3;
        private const float CommandRange = 25f;
        private static readonly ulong Id = StaticId.Hash("bunker-watering-v1");
        private static readonly FieldInfo MainSwitchField = AccessTools.Field(typeof(Home_Bunker), "BunkerMainSwitch");
        private static readonly FieldInfo AutoButtonField = AccessTools.Field(typeof(Home_Bunker), "ButtonAutoWaterPlants");
        private static readonly FieldInfo VfxField = AccessTools.Field(typeof(Home_Bunker), "VFX_WaterPlants");
        private static readonly FieldInfo SoundField = AccessTools.Field(typeof(Home_Bunker), "SOUND_Bunker");
        private static readonly FieldInfo LastWaterField = AccessTools.Field(typeof(Home_Bunker), "LastTime_WaterPlants");
        private static readonly FieldInfo AutoWateringNextTickField = AccessTools.Field(typeof(Home_Bunker), "AutoWaterPlants_NextTick")
            ?? throw new MissingFieldException(typeof(Home_Bunker).FullName, "AutoWaterPlants_NextTick");
        private static readonly MethodInfo WaterPlantsMethod = AccessTools.Method(typeof(Home_Bunker), "WaterPlants");
        private static readonly MethodInfo TurnOnMethod = AccessTools.Method(typeof(Home_Bunker), "Bunker_TurnOn");
        private static readonly MethodInfo TurnOffMethod = AccessTools.Method(typeof(Home_Bunker), "Bunker_TurnOff");
        private static readonly MethodInfo ScreenMethod = AccessTools.Method(typeof(Home_Bunker), "Update_WaterPlantsScreen");
        private static readonly FieldInfo InBunkerField = AccessTools.Field(typeof(Garden_DirtPatch), "isInBunker");
        private static readonly FieldInfo PoweredField = AccessTools.Field(typeof(Garden_DirtPatch), "isBunkerPoweredOn");
        private static readonly FieldInfo AutoWateredField = AccessTools.Field(typeof(Garden_DirtPatch), "isAutoWatered");
        private static readonly FieldInfo UprightField = AccessTools.Field(typeof(Garden_DirtPatch), "isUpright");
        private readonly bool host;
        private readonly Gameplay gameplay;
        private readonly Home_Bunker? bunker;
        private readonly Furniture_SwitchButton? mainSwitch, autoButton;
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private byte flags;
        private uint pulse;
        private int nextWateringTick, originalNextWateringTick;
        private float lastWater, nextCapture, nextPlanters;
        private bool applied, applying, published, screenRefreshPending;
        public bool Applying => applying;

        public SharedBunkerWatering(Gameplay gameplay, bool host, string diagnostics)
        {
            this.gameplay = gameplay; this.host = host;
            bunker = gameplay.home != null ? gameplay.home.homeBunker : null;
            if (bunker != null && MainSwitchField != null && AutoButtonField != null)
            {
                mainSwitch = MainSwitchField.GetValue(bunker) as Furniture_SwitchButton;
                autoButton = AutoButtonField.GetValue(bunker) as Furniture_SwitchButton;
                if (LastWaterField != null) lastWater = (float)LastWaterField.GetValue(bunker);
                nextWateringTick = CaptureNextWateringTick();
                originalNextWateringTick = nextWateringTick;
                flags = Capture();
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-bunker-watering.tsv"), new[]
            {
                "id\tbunker\tmain-switch\tauto-button\tflags",
                Id.ToString("X16") + "\t" + (bunker != null) + "\t" + (mainSwitch != null) + "\t" + (autoButton != null) + "\t" + flags
            });
#endif

        }

        internal IEnumerable<WorldRecord> InitialRecords() => new[] { new WorldRecord(Id, 1, Adapter, Encode(0, 0, 0)) };

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);
        private bool GuestRunning => !host && session?.IsRunning == true && bunker != null;

        private static bool ValidFlags(byte value) => (value & ~(Powered | AutoWater | MainSwitch | Fertilized)) == 0;
        private static bool ValidScheduleTick(int value) => value == 0 || value == 600 || value == 840 || value == 1080;

        private static byte[] Encode(byte flags, uint pulse, int nextTick)
        {
            if (!ValidFlags(flags) || !ValidScheduleTick(nextTick)) throw new InvalidDataException("Invalid bunker watering state.");
            var payload = new byte[9];
            payload[0] = flags;
            BitConverter.GetBytes(pulse).CopyTo(payload, 1);
            BitConverter.GetBytes(nextTick).CopyTo(payload, 5);
            return payload;
        }

        private static bool TryDecode(byte[] payload, out byte nextFlags, out uint nextPulse, out int nextTick)
        {
            nextFlags = 0; nextPulse = 0; nextTick = 0;
            if (payload == null || payload.Length != 9) return false;
            byte flagsValue = payload[0];
            uint pulseValue = BitConverter.ToUInt32(payload, 1);
            int tickValue = BitConverter.ToInt32(payload, 5);
            if (!ValidFlags(flagsValue) || !ValidScheduleTick(tickValue)) return false;
            nextFlags = flagsValue; nextPulse = pulseValue; nextTick = tickValue;
            return true;
        }

        private int CaptureNextWateringTick()
        {
            if (bunker == null) return 0;
            int value = (int)AutoWateringNextTickField.GetValue(bunker);
            return ValidScheduleTick(value) ? value : 0;
        }

        private byte Capture()
        {
            if (bunker == null) return 0;
            byte value = 0;
            if (bunker.isBunkerPowerOn) value |= Powered;
            if (autoButton != null && autoButton.isOn) value |= AutoWater;
            if (mainSwitch != null && mainSwitch.isOn) value |= MainSwitch;
            if (VfxField?.GetValue(bunker) is ParticleSystem vfx && vfx != null && vfx.main.startColor.color == Color.green) value |= Fertilized;
            return value;
        }

        // True when a guest's click on a bunker switch went to the host instead of running natively.
        public bool Intercept(Furniture_SwitchButton button)
        {
            if (!GuestRunning || applying || button == null) return false;
            byte op = button == mainSwitch ? OpMain : button == autoButton ? OpAuto : (byte)0;
            if (op == 0) return false;
            if (channel?.Ready == true) channel.Request(Id, new[] { op });
            return true;
        }

        // The guest's manual "water now" button. Native error messages use the host's mirrored state.
        public bool InterceptManualWater(Home_Bunker native)
        {
            if (!GuestRunning || applying || native != bunker) return false;
            if ((flags & MainSwitch) == 0) Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("BUNKER_ERROR_MainSwitchIsOff"));
            else if ((flags & Powered) == 0) Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_NoGridElectricity"));
            else if ((flags & AutoWater) == 0) Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("BUNKER_ERROR_AutoWateringMachineIsOff"));
            else if (channel?.Ready == true) channel.Request(Id, new[] { OpWater });
            return true;
        }

        // Guests never run the native watering pass (it waters and fertilizes host-owned patches).
        public bool GuestBlocksWaterPass(Home_Bunker native) => GuestRunning && !applying && native == bunker;

        [MonMP.Core.World.WorldCommandAdapters(SharedBunkerWatering.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || id != Id || bunker == null || payload.Length != 1 || peer == 0) return;
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return;
            if ((new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z) - bunker.transform.position).sqrMagnitude > CommandRange * CommandRange) return;
            switch (payload[0])
            {
                case OpMain: if (mainSwitch != null) mainSwitch.MouseDown(); break;
                case OpAuto: if (autoButton != null) autoButton.MouseDown(); break;
                case OpWater:
                    // The native checks without the host-screen error message.
                    if ((mainSwitch == null || mainSwitch.isOn) && bunker.isBunkerPowerOn && autoButton != null && autoButton.isOn)
                        WaterPlantsMethod?.Invoke(bunker, null);
                    break;
            }
            Publish();
        }

        private void Publish()
        {
            if (bunker == null || channel == null) return;
            if (LastWaterField != null)
            {
                float last = (float)LastWaterField.GetValue(bunker);
                if (last != lastWater) { lastWater = last; pulse++; }
            }
            flags = Capture();
            nextWateringTick = CaptureNextWateringTick();
            channel.Publish(Id, Encode(flags, pulse, nextWateringTick));
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (!host && records.TryGetValue(Id, out var record)) Apply(record.Payload, true);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Id == Id) Apply(record.Payload, !applied);
        }

        private void Apply(byte[] payload, bool snapshot)
        {
            if (bunker == null || !TryDecode(payload, out byte next, out uint nextPulse, out int nextTick)) return;
            // A fresh session record (all zero) says nothing about the host's bunker yet.
            if (snapshot && next == 0 && nextPulse == 0 && nextTick == 0) return;
            bool firstApply = !applied;
            applied = true;
            applying = true;
            bool refreshScreen = false;
            try
            {
                bool mainOn = (next & MainSwitch) != 0;
                if (mainSwitch != null && (mainSwitch.isOn != mainOn || firstApply))
                {
                    mainSwitch.isOn = mainOn; mainSwitch.UpdateVisual();
                    if (bunker.gameObject.activeInHierarchy) (mainOn ? TurnOnMethod : TurnOffMethod)?.Invoke(bunker, null);
                    refreshScreen = true;
                }
                bool autoOn = (next & AutoWater) != 0;
                if (autoButton != null && (autoButton.isOn != autoOn || firstApply))
                {
                    autoButton.isOn = autoOn; autoButton.UpdateVisual();
                    refreshScreen = true;
                }
                // Bunker_TurnOn may calculate a guest-local schedule. The host's exact schedule
                // wins, and the display is refreshed only after that native field is restored.
                if (nextTick != 0 && (int)AutoWateringNextTickField.GetValue(bunker) != nextTick)
                {
                    AutoWateringNextTickField.SetValue(bunker, nextTick);
                    refreshScreen = true;
                }
                if (refreshScreen)
                {
                    if (bunker.gameObject.activeInHierarchy)
                    {
                        ScreenMethod?.Invoke(bunker, null);
                        screenRefreshPending = false;
                    }
                    else screenRefreshPending = true;
                }
                if (!snapshot && nextPulse != pulse && bunker.gameObject.activeInHierarchy)
                {
                    if (VfxField?.GetValue(bunker) is ParticleSystem vfx && vfx != null)
                    {
                        var main = vfx.main;
                        main.startColor = (next & Fertilized) != 0 ? Color.green : Color.white;
                        vfx.Play();
                    }
                    (SoundField?.GetValue(bunker) as SOUND_Bunker)?.WaterPlants();
                }
            }
            finally { applying = false; }
            flags = next; pulse = nextPulse;
            nextPlanters = 0;
        }

        public void Update()
        {
            if (session?.IsRunning != true || channel?.Ready != true || bunker == null) return;
            float now = Time.unscaledTime;
            if (host)
            {
                if (now < nextCapture) return;
                nextCapture = now + 0.5f;
                byte current = Capture();
                float last = LastWaterField != null ? (float)LastWaterField.GetValue(bunker) : lastWater;
                int nextTick = CaptureNextWateringTick();
                if (!published || current != flags || last != lastWater || nextTick != nextWateringTick) { published = true; Publish(); }
                return;
            }
            if (!applied) return;
            if (screenRefreshPending && bunker.gameObject.activeInHierarchy)
            {
                ScreenMethod?.Invoke(bunker, null);
                screenRefreshPending = false;
            }
            if (now < nextPlanters) return;
            nextPlanters = now + 1f;
            GuestPlanters();
        }

        private void GuestPlanters()
        {
            if (InBunkerField == null || PoweredField == null || AutoWateredField == null || gameplay.Player == null) return;
            bool powered = (flags & Powered) != 0, auto = powered && (flags & AutoWater) != 0;
            bool inside = gameplay.IsInABunkerPerimeter(gameplay.Player.transform.position);
            foreach (var planter in gameplay.DetectPlantsInHomeBunker())
            {
                var patch = planter != null ? planter.DirtPatch : null;
                if (patch == null) continue;
                // A replica placed after its native Start never ran the location check.
                patch.UpdateLocation();
                bool inBunker = (int)InBunkerField.GetValue(patch) == 1;
                PoweredField.SetValue(patch, inBunker && powered);
                AutoWateredField.SetValue(patch, inBunker && auto);
                UprightField?.SetValue(patch, Vector3.Dot(patch.transform.up, Vector3.down) < -0.5f);
                // The native cull runs on bunker exit only; replicas created or replaced while this
                // player was away stay hidden until the next entry otherwise.
                if (inside) planter!.Force_UnCull();
            }
        }

        public void Dispose()
        {
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            if (!host && bunker != null && originalNextWateringTick != 0)
            {
                AutoWateringNextTickField.SetValue(bunker, originalNextWateringTick);
                ScreenMethod?.Invoke(bunker, null);
            }
            session = null; channel = null; presence.Clear();
        }
    }
}
