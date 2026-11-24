using System;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Garages: the host finishes its home garage build and starts the door opening; the guest's
    // garage appears with the door moving. The guest then presses the speedway garage button twice:
    // each press is intercepted locally, run natively on the host and replayed on the guest.
    internal sealed class PairedGarageTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ulong home, speedway;
        private int phase;
        private float next, deadline, start;
        internal string Status => "garage phase=" + phase + (home == 0 ? "" : "; home=" + Describe(home)) + (speedway == 0 ? "" : "; speedway=" + Describe(speedway));
        private string Describe(ulong id)
        {
            var garages = plugin.World!.Garages; var s = garages.TestState(id); var l = garages.TestLocal(id);
            return "rec " + s.Stage + "/" + s.Motion.ToString("0.0") + "/" + s.Position.ToString("0.00") + " local " + l.Stage + "/" + l.Motion.ToString("0.0") + "/" + l.Position.ToString("0.00");
        }
        internal PairedGarageTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }
        internal bool Tick()
        {
            var garages = plugin.World!.Garages;
            if (deadline == 0)
            {
                deadline = Time.unscaledTime + 120;
                home = garages.TestHome; speedway = garages.TestSpeedway;
                Require(home != 0 && speedway != 0, "Missing home or speedway garage records (" + garages.Count + ").");
            }
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Garage test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host(garages) : Guest(garages);
        }
        private bool Host(SharedGarages garages)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                garages.TestSetHomeStage(5); start = garages.TestLocal(speedway).Motion;
                phase = 1; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 1)
            {
                garages.TestSetHomeMotion(0.7f);
                Require(Mathf.Abs(garages.TestLocal(home).Motion - 0.7f) < 0.01f, "The host home garage animator did not accept the door motion.");
                phase = 2;
            }
            else if (phase == 2 && garages.TestLocal(speedway).Motion != start) phase = 3;
            else if (phase == 3 && garages.TestLocal(speedway).Motion == start) phase = 4;
            else if (phase == 4 && plugin.Session!.Peers.Count == 1) return true;
            return false;
        }
        private bool Guest(SharedGarages garages)
        {
            if (phase == 0)
            {
                var s = garages.TestState(home);
                if (s.Stage != 5 || Mathf.Abs(s.Motion - 0.7f) > 0.01f) return false;
                Require(garages.TestHomeFinalActive && garages.TestLocal(home).Stage == 5, "The guest's home garage did not build from the host record.");
                Require(Mathf.Abs(garages.TestLocal(home).Motion - 0.7f) < 0.01f, "The guest's home garage door did not follow the host's motion.");
                var at = garages.TestPosition(speedway) + new Vector3(0, 1, 0);
                gameplay.PlayerWalking.transform.position = at; gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                phase = 1; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 1)
            {
                start = garages.TestLocal(speedway).Motion;
                garages.TestPress(speedway);
                Require(garages.TestLocal(speedway).Motion == start, "A guest press moved the door locally instead of asking the host.");
                phase = 2;
            }
            else if (phase == 2 && garages.TestState(speedway).Motion != start && garages.TestLocal(speedway).Motion != start && Mathf.Abs(garages.TestLocal(speedway).Position - start) > 0.05f)
            { phase = 3; next = Time.unscaledTime + 1; }
            else if (phase == 3) { garages.TestPress(speedway); phase = 4; }
            else if (phase == 4 && garages.TestState(speedway).Motion == start && garages.TestLocal(speedway).Motion == start)
            {
                plugin.EndPairedTestSession();
                return true;
            }
            return false;
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Garage test: " + reason); }
    }
}
