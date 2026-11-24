using System;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Host combustion state must reach a late joiner; guest switch/start controls must execute on
    // the host, reject distant presses and restore the guest's own generator on disconnect.
    internal sealed class PairedGeneratorTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private int phase;
        private float next, deadline;
        internal string Status => "generator phase=" + phase + "; state=" + (plugin.World?.Generator.TestState.Flags.ToString() ?? "?");

        internal static void Prepare(Gameplay gameplay, bool host)
        {
            var generator = UnityEngine.Object.FindObjectOfType<HomeGenerator>();
            if (generator == null || generator.Get_FuelTank == null) throw new InvalidOperationException("Generator test requires the home generator and fuel tank.");
            var button = AccessTools.Field(typeof(HomeGenerator), "KillSwitchButton").GetValue(generator) as Furniture_SwitchButton;
            if (button == null) throw new InvalidOperationException("Generator test requires the native kill switch.");
            if (host)
            {
                generator.Get_FuelTank.Get_FuelLevel = Mathf.Max(generator.Get_FuelTank.Get_FuelLevel, 10f);
                generator.KillSwitchOn();
                button.isOn = true;
            }
            else
            {
                generator.KillSwitchOff();
                button.isOn = false;
            }
            button.UpdateVisual();
        }

        internal PairedGeneratorTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }

        internal bool Tick()
        {
            var shared = plugin.World!.Generator;
            var generator = shared.TestGenerator ?? throw new InvalidOperationException("The shared generator adapter was not registered.");
            if (deadline == 0) deadline = Time.unscaledTime + 90;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Generator test stalled: " + Status);
            if (Time.unscaledTime < next || plugin.World.Channel.FullySynchronized != true) return false;
            if (plugin.TestOptions.IsHost) return Host(shared, generator);
            return Guest(shared, generator);
        }

        private bool Host(SharedGenerator shared, HomeGenerator generator)
        {
            if (phase == 0)
            {
                if (!shared.TestState.KillSwitchEnabled) throw new InvalidOperationException("Host generator kill switch was not prepared on.");
                if (!shared.TestState.Running) { generator.StartEngine(); return false; }
                if (plugin.Session!.Peers.Count < 2) return false;
                phase = 1;
            }
            else if (phase == 1 && !shared.TestState.KillSwitchEnabled && !shared.TestState.Running) phase = 2;
            else if (phase == 2 && shared.TestState.KillSwitchEnabled && !shared.TestState.Running) phase = 3;
            else if (phase == 3 && shared.TestState.KillSwitchEnabled && shared.TestState.Running) phase = 4;
            else if (phase == 4 && !shared.TestState.KillSwitchEnabled && !shared.TestState.Running) phase = 5;
            else if (phase == 5 && plugin.Session!.Peers.Count == 1) return true;
            return false;
        }

        private bool Guest(SharedGenerator shared, HomeGenerator generator)
        {
            var state = shared.TestState;
            var button = shared.TestKillSwitch!;
            if (phase == 0)
            {
                if (!state.Running || !state.KillSwitchEnabled || !shared.TestNative.Equals(state)) return false;
                Require(!shared.TestOriginal.Running && !shared.TestOriginal.KillSwitchEnabled, "fixture did not preserve a distinct guest baseline.");
                Require(generator.Get_FuelTank.Get_FuelLevel > 0, "host fuel state did not reach the guest generator.");
                MoveNear(generator.transform.position + Vector3.forward * 30);
                phase = 1; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 1)
            {
                button.MouseDown(); // The far request must be intercepted locally and rejected by host range validation.
                phase = 2; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 2)
            {
                Require(state.Running && state.KillSwitchEnabled && button.isOn, "a distant kill-switch command changed the generator.");
                MoveNear(generator.transform.position + Vector3.forward * 1.5f);
                phase = 3; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 3)
            {
                button.MouseDown(); phase = 4;
            }
            else if (phase == 4 && !state.KillSwitchEnabled && !state.Running && !button.isOn)
            {
                Require(!shared.TestNative.KillSwitchEnabled && !shared.TestNative.Running, "host shutdown was not applied to the guest's native generator.");
                next = Time.unscaledTime + 0.6f; phase = 8;
            }
            else if (phase == 8)
            {
                button.MouseDown(); phase = 5;
            }
            else if (phase == 5 && state.KillSwitchEnabled && !state.Running && button.isOn)
            {
                Require(!shared.TestNative.Running, "enabling the kill switch started the guest engine locally.");
                next = Time.unscaledTime + 0.6f; phase = 9;
            }
            else if (phase == 9)
            {
                generator.StartEngine(); phase = 6;
            }
            else if (phase == 6 && state.KillSwitchEnabled && state.Running && shared.TestNative.Equals(state))
            {
                next = Time.unscaledTime + 0.6f;
                button.MouseDown(); phase = 7;
            }
            else if (phase == 7 && !state.KillSwitchEnabled && !state.Running && !button.isOn)
            {
                plugin.EndPairedTestSession();
                Require(shared.TestRestored, "disconnect did not restore the guest's pre-session engine and kill switch.");
                return true;
            }
            return false;
        }

        private void MoveNear(Vector3 location)
        {
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private static void Require(bool ok, string reason)
        { if (!ok) throw new InvalidOperationException("Generator test: " + reason); }
    }
}
