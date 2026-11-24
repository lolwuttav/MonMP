using System;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Hydro availability is host-owned. Guests must use it for native power checks while their
    // local HydroData remains untouched and returns when the session ends.
    internal sealed class PairedHydroTest
    {
        private static readonly System.Reflection.FieldInfo BuildDay = AccessTools.Field(typeof(Store_HydroSaintClin), "DaySinceBuildStarted");
        private static readonly System.Reflection.FieldInfo HydroCut = AccessTools.Field(typeof(Store_HydroSaintClin), "HydroCut");
        private static readonly System.Reflection.FieldInfo EngineRunning = AccessTools.Field(typeof(HomeGenerator), "isEngineRunning");
        private static readonly System.Reflection.FieldInfo KillSwitchOff = AccessTools.Field(typeof(HomeGenerator), "isKillSwitchOff");
        private static readonly System.Reflection.FieldInfo KillSwitch = AccessTools.Field(typeof(HomeGenerator), "KillSwitchButton");
        private static int originalBuildDay;
        private static bool originalHydroCut, originalHydroEnabled, originalEngineRunning, originalKillSwitchOff, originalButtonOn, prepared;
        private static Store_HydroSaintClin? hydroStore;
        private static HomeGenerator? preparedGenerator;
        private static Furniture_SwitchButton? preparedButton;

        private readonly MonMPPlugin plugin;
        private int phase;
        private float next, deadline;
        private bool guestReady, readySent;
        private const byte GuestReadySignal = 0x48;

        internal string Status
        {
            get
            {
                var shared = plugin.World?.Generator;
                return "hydro phase=" + phase + "; record=" + (shared?.TestState.HydroRunning.ToString() ?? "?") +
                    "; query=" + (shared?.TestHydroQuery.ToString() ?? "?") + "; power=" + (shared?.TestPowerQuery.ToString() ?? "?") +
                    "; presentation=" + (shared?.TestHydroPresentationUpdates.ToString() ?? "?");
            }
        }

        internal static void Prepare(bool host)
        {
            var generator = UnityEngine.Object.FindObjectOfType<HomeGenerator>();
            var hydro = UnityEngine.Object.FindObjectOfType<Store_HydroSaintClin>();
            if (generator == null || hydro == null) throw new InvalidOperationException("Hydro test requires the home generator and hydro store.");
            var button = KillSwitch.GetValue(generator) as Furniture_SwitchButton;
            if (button == null) throw new InvalidOperationException("Hydro test requires the home generator kill switch.");

            originalBuildDay = (int)BuildDay.GetValue(hydro);
            originalHydroCut = (bool)HydroCut.GetValue(hydro);
            originalHydroEnabled = hydro.enabled;
            originalEngineRunning = (bool)EngineRunning.GetValue(generator);
            originalKillSwitchOff = (bool)KillSwitchOff.GetValue(generator);
            originalButtonOn = button.isOn;
            hydroStore = hydro;
            preparedGenerator = generator;
            preparedButton = button;
            prepared = true;

            // The host starts cut; the guest starts powered. This creates a deterministic mismatch
            // without advancing the clock or running construction, invoice, or payment code.
            bool wasPowered = originalEngineRunning || hydro.Get_isHydroRunning;
            BuildDay.SetValue(hydro, 5);
            HydroCut.SetValue(hydro, host);
            hydro.enabled = false; // Keep StoreManagers from advancing the hydro fixture mid-test.
            EngineRunning.SetValue(generator, false);
            KillSwitchOff.SetValue(generator, true);
            button.isOn = true;
            button.UpdateVisual();
            var sound = generator.GetComponent<SOUND_WoodSplitter>();
            if (originalEngineRunning && sound != null) sound.StopEngine();
            if (wasPowered && !hydro.Get_isHydroRunning) generator.Signal_PowerIsOff();
        }

        internal PairedHydroTest(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            if (plugin.TestOptions.IsHost) plugin.World!.Channel.CommandRequested += GuestReady;
        }
        // The host's availability cycle only starts once the guest's test is observing: a guest whose
        // profile finished late first saw hydro already running and missed the transitions.
        private void GuestReady(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ClockState.Entity && payload.Length == 1 && payload[0] == GuestReadySignal) guestReady = true; }

        internal bool Tick()
        {
            var world = plugin.World!;
            var shared = world.Generator;
            var generator = shared.TestGenerator ?? throw new InvalidOperationException("The shared generator adapter was not registered.");
            if (deadline == 0) deadline = Time.unscaledTime + 45;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Hydro test stalled: " + Status);
            if (Time.unscaledTime < next || world.Channel.FullySynchronized != true) return false;
            return plugin.TestOptions.IsHost ? Host(shared) : Guest(shared, generator);
        }

        private bool Host(SharedGenerator shared)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2 || !guestReady) return false;
                plugin.World!.Channel.CommandRequested -= GuestReady;
                Require(!shared.TestState.HydroRunning && !shared.TestHydroQuery && !shared.TestPowerQuery,
                    "host fixture did not start with hydro cut and combustion stopped.");
                shared.TestSetHydroAvailability(true);
                phase = 1;
            }
            else if (phase == 1 && shared.TestState.HydroRunning)
            {
                next = Time.unscaledTime + 0.8f;
                phase = 2;
            }
            else if (phase == 2 && Time.unscaledTime >= next)
            {
                shared.TestSetHydroAvailability(false);
                phase = 3;
            }
            else if (phase == 3 && !shared.TestState.HydroRunning)
            {
                next = Time.unscaledTime + 0.8f;
                phase = 4;
            }
            else if (phase == 4 && Time.unscaledTime >= next)
            {
                shared.TestSetHydroAvailability(true);
                phase = 5;
            }
            else if (phase == 5 && shared.TestState.HydroRunning)
            {
                next = Time.unscaledTime + 0.8f;
                phase = 6;
            }
            else if (phase == 6 && Time.unscaledTime >= next)
            {
                shared.TestSetHydroAvailability(false);
                phase = 7;
            }
            else if (phase == 7 && !shared.TestState.HydroRunning && plugin.Session!.Peers.Count == 1)
            {
                RestorePreparedHydro(shared.TestGenerator!);
                return true;
            }
            return false;
        }

        private bool Guest(SharedGenerator shared, HomeGenerator generator)
        {
            if (phase == 0)
            {
                if (!readySent) { plugin.World!.Channel.Request(ClockState.Entity, new[] { GuestReadySignal }); readySent = true; }
                if (shared.TestState.HydroRunning || shared.TestHydroQuery || shared.TestPowerQuery) return false;
                Require(shared.TestOriginal.HydroRunning && shared.TestRawHydroQuery,
                    "the guest fixture did not preserve a distinct powered HydroData baseline.");
                phase = 1;
            }
            else if (phase == 1 && shared.TestState.HydroRunning && shared.TestHydroQuery && shared.TestPowerQuery)
            {
                Require(shared.TestRawHydroQuery, "host availability changed the guest's local HydroData.");
                Require(shared.TestHydroPresentationUpdates >= 1, "hydro becoming available did not refresh dependent native presentation.");
                int beforePowerTransitions = shared.TestPowerPresentationTransitions;
                shared.TestApplyState(new MonMP.Core.World.GeneratorState(true, shared.TestState.KillSwitchEnabled, true));
                Require(shared.TestPowerQuery && shared.TestPowerPresentationTransitions == beforePowerTransitions,
                    "combustion starting while hydro already supplied power signaled a redundant power transition.");
                shared.TestApplyState(new MonMP.Core.World.GeneratorState(false, shared.TestState.KillSwitchEnabled, true));
                Require(shared.TestPowerQuery && shared.TestPowerPresentationTransitions == beforePowerTransitions,
                    "combustion stopping while hydro still supplied power signaled a redundant power transition.");
                phase = 2;
            }
            else if (phase == 2 && shared.TestHydroPresentationUpdates >= 5 &&
                !shared.TestState.HydroRunning && !shared.TestHydroQuery && !shared.TestPowerQuery)
            {
                Require(shared.TestRawHydroQuery,
                    "hydro availability transitions did not preserve guest HydroData and refresh presentation.");
                plugin.EndPairedTestSession();
                Require(shared.TestRestored, "disconnect did not restore the guest's original hydro availability query.");
                RestorePreparedHydro(generator);
                return true;
            }
            return false;
        }

        private static void RestorePreparedHydro(HomeGenerator generator)
        {
            if (!prepared) return;
            var hydro = hydroStore;
            var originalGenerator = preparedGenerator ?? generator;
            var button = preparedButton;
            if (hydro == null || originalGenerator == null || button == null) throw new InvalidOperationException("Hydro fixture rollback lost its native objects.");
            bool before = (bool)EngineRunning.GetValue(originalGenerator) || hydro.Get_isHydroRunning;
            bool engineBefore = (bool)EngineRunning.GetValue(originalGenerator);
            BuildDay.SetValue(hydro, originalBuildDay);
            HydroCut.SetValue(hydro, originalHydroCut);
            EngineRunning.SetValue(originalGenerator, originalEngineRunning);
            KillSwitchOff.SetValue(originalGenerator, originalKillSwitchOff);
            button.isOn = originalButtonOn;
            button.UpdateVisual();
            var sound = originalGenerator.GetComponent<SOUND_WoodSplitter>();
            if (engineBefore != originalEngineRunning && sound != null)
            {
                if (originalEngineRunning) sound.StartEngine();
                else sound.StopEngine();
            }
            hydro.enabled = originalHydroEnabled;
            bool after = originalEngineRunning || hydro.Get_isHydroRunning;
            if (before != after)
            {
                if (after) originalGenerator.Signal_PowerIsOn();
                else originalGenerator.Signal_PowerIsOff();
            }
            hydroStore = null;
            preparedGenerator = null;
            preparedButton = null;
            prepared = false;
        }

        private static void Require(bool ok, string reason)
        { if (!ok) throw new InvalidOperationException("Hydro test: " + reason); }
    }
}
