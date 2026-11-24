using System;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Lift: the host places a car lift on the home land (generator running). The guest freezes it,
    // raises it to the top and lowers it with the native buttons, moves an arm and unfreezes it; the
    // host runs every step natively and the guest's replica follows height, arms and motor audio.
    // A second guest raise deliberately spans host session disposal, without releasing its button.
    internal sealed class PairedCarLiftTest
    {
        private const string Path = "Tool/CarLift";
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ulong lift;
        private int phase;
        private float next, deadline, top, easeDeadline;
        private Furniture_CarLift? stoppedLift;
        private AudioSource? observedMotor;
        private float stoppedHeight;
        private bool heardUp, heardDown;
        internal string Evidence => "Native guest freeze, raise/lower and arm inputs replicated without guest hydraulic simulation; " +
            "up/down motor AudioSource clips and play/stop state followed host travel. Host disposal during a second guest raise stopped hydraulics, " +
            "removed the guest operator and left host height stable offline. Perceived sound and raised-vehicle authority transfer remain manual checks.";
        internal string Status
        {
            get
            {
                var items = plugin.World?.Items;
                string record = items != null && lift != 0 ? Format(items.TestState(lift).Values) : "-";
                return "lift phase=" + phase + "; lift=" + lift.ToString("X") + " record=" + record + " top=" + top.ToString("0.000");
            }
        }
        private static string Format(ItemValues v) => "frozen" + v.IntValue1 + "/h" + v.Value1.ToString("0.000") + "/arms" + v.IntValue2;
        internal PairedCarLiftTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }
        internal bool Tick()
        {
            if (deadline != 0 && Time.unscaledTime > deadline) throw new InvalidOperationException("Lift test stalled: " + Status);
            if (phase >= 90) return AfterDisposal();
            var items = plugin.World!.Items;
            if (!items.Ready) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 150;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Lift test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host(items) : Guest(items);
        }
        private ItemValues Values(SharedItems items) => items.TestState(lift).Values;
        private bool Host(SharedItems items)
        {
            // The home trailer's lift needs power: keep the generator running for the test.
            var generator = UnityEngine.Object.FindObjectOfType<HomeGenerator>();
            if (generator != null) AccessTools.Field(typeof(HomeGenerator), "isEngineRunning").SetValue(generator, true);
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                Require(SharedForest.TestPlantPoint(out var point), "No free home-land ground for the lift.");
                items.TestSpawn(point + Vector3.up * 0.3f, items.TestPrefab(Path));
                phase = 1; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 1 && (lift = items.TestNewestPrefab(items.TestPrefab(Path))) != 0)
            {
                var native = (Furniture_CarLift)items.TestItem(lift)!;
                if (native.rb != null) { native.rb.velocity = Vector3.zero; native.rb.angularVelocity = Vector3.zero; native.rb.Sleep(); }
                top = CarLiftState.UpperHeight(native);
                phase = 2;
            }
            else if (phase == 2 && Values(items).IntValue1 == 1)
            {
                Require(items.TestState(lift).Lease.Owner == ItemLease.Unowned,
                    "Freezing retained a transient carry lease on the host lift.");
                phase = 3;
            }
            else if (phase == 3 && Values(items).Value1 >= top - 0.001f) phase = 4;
            else if (phase == 4 && CarLiftState.AtGround((Furniture_CarLift)items.TestItem(lift)!)) phase = 5;
            else if (phase == 5 && Values(items).IntValue2 == 15666) phase = 6;
            else if (phase == 6 && Values(items).IntValue1 == 0) phase = 7;
            else if (phase == 7 && Values(items).IntValue1 == 1) phase = 8;
            else if (phase == 8 && Values(items).Value2 > 0 && Values(items).Value1 >
                CarLiftState.LowerHeight((Furniture_CarLift)items.TestItem(lift)!) + 0.1f)
            {
                stoppedLift = (Furniture_CarLift)items.TestItem(lift)!;
                Require(CarLiftState.IsMoving(stoppedLift) && items.TestState(lift).Lease.Owner == ItemLease.Unowned,
                    "The disposal trigger was not a guest-operated moving unheld lift (IP-03).");
                phase = 81; next = Time.unscaledTime + 1f; // let the paired guest observe the same second raise
            }
            else if (phase == 81)
            {
                Require(stoppedLift != null && CarLiftState.Direction(stoppedLift) > 0 &&
                    stoppedLift.Value1 < top - 0.05f,
                    "Second raise reached the endstop before host disposal; moving-disposal trigger was not exercised.");
                observedMotor = Motor(stoppedLift!);
                plugin.EndPairedTestSession(); // production world/items Dispose, while the guest still holds Up
                Require(!CarLiftState.IsMoving(stoppedLift!), "Host disposal left guest-operated hydraulics moving offline (IP-03).");
                Require(observedMotor == null || !observedMotor.isPlaying, "Host disposal left lift motor audio playing.");
                stoppedHeight = stoppedLift!.Value1;
                phase = 91; next = Time.unscaledTime + 0.75f;
            }
            return false;
        }
        private bool Guest(SharedItems items)
        {
            if (phase == 0)
            {
                // Finish the one-time join placement before approaching the fixture lift.
                if (plugin.World?.PersonalProfileReady != true || plugin.World.ReconnectPositions?.Settled != true) return false;
                ushort prefab = items.TestPrefab(Path);
                if ((lift = items.TestNewestPrefab(prefab)) == 0 || items.TestItem(lift) == null) return false;
                phase = 1; next = Time.unscaledTime + 3; // let the host's lift settle on the ground
            }
            else if (phase == 1)
            {
                var native = items.TestItem(lift)!;
                MoveNear(native.transform);
                top = CarLiftState.UpperHeight((Furniture_CarLift)native);
                phase = 2; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 2) { items.TestUse(lift); phase = 3; }
            else if (phase == 3 && Values(items).IntValue1 == 1)
            {
                var native = (Furniture_CarLift)items.TestItem(lift)!;
                Require(native.IntValue1 == 1 && !native.HasARigidBody && !native.itemCollider.enabled, "The guest's lift replica did not freeze.");
                Require(items.TestState(lift).Lease.Owner == ItemLease.Unowned, "Guest freeze retained a transient carry lease.");
                native.LiftButtonPressed(true);
                Require(!CarLiftState.IsMoving(native), "The guest's lift ran its hydraulics locally.");
                phase = 4;
            }
            else if (phase == 4 && Values(items).Value2 > 0)
            {
                var native = (Furniture_CarLift)items.TestItem(lift)!;
                VerifyAudio(native, true);
                heardUp = true;
                Require(!CarLiftState.IsMoving(native), "Replicated up travel started guest native hydraulic simulation.");
                // Keep observing until the top target arrives.
                if (Values(items).Value1 < top - 0.001f) return false;
                phase = 40;
            }
            if ((phase == 4 || phase == 40) && Values(items).Value1 >= top - 0.001f)
            {
                var native = (Furniture_CarLift)items.TestItem(lift)!;
                // The guest's platform eases toward each host sample (#200); give it a moment to arrive.
                if (easeDeadline == 0) easeDeadline = Time.unscaledTime + 2f;
                if (Mathf.Abs(native.Value1 - top) >= 0.001f && Time.unscaledTime < easeDeadline) return false;
                Require(Mathf.Abs(native.Value1 - top) < 0.001f, "The guest's lift platform did not follow the host's height.");
                Require(heardUp, "No native guest up-travel audio sample was observed (IP-04).");
                native.LiftButtonReleased(); native.LiftButtonPressed(false);
                phase = 5;
            }
            else if (phase == 5 && Values(items).Value2 < 0)
            {
                var native = (Furniture_CarLift)items.TestItem(lift)!;
                VerifyAudio(native, false);
                heardDown = true;
                Require(!CarLiftState.IsMoving(native), "Replicated down travel started guest native hydraulic simulation.");
                if (Values(items).Value1 > CarLiftState.LowerHeight(native) + 0.001f) return false;
                phase = 50;
            }
            if ((phase == 5 || phase == 50) && Values(items).Value1 <= CarLiftState.LowerHeight((Furniture_CarLift)items.TestItem(lift)!) + 0.001f)
            {
                var native = (Furniture_CarLift)items.TestItem(lift)!;
                Require(heardDown, "No native guest down-travel audio sample was observed (IP-04).");
                native.LiftButtonReleased();
                phase = 6; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 6)
            {
                var native = (Furniture_CarLift)items.TestItem(lift)!;
                Require(Values(items).Value2 == 0 && !Motor(native).isPlaying,
                    "Guest lift motor did not stop after the host stopped at ground (IP-04).");
                native.MoveArms(-1, 0); phase = 7;
            }
            else if (phase == 7 && Values(items).IntValue2 == 15666)
            {
                Require(items.TestItem(lift)!.IntValue2 == 15666, "The guest's lift arms did not follow the host.");
                phase = 8; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 8) { items.TestUse(lift); phase = 9; }
            else if (phase == 9 && Values(items).IntValue1 == 0)
            {
                Require(items.TestState(lift).Lease.Owner == ItemLease.Unowned, "Unfreeze retained a carry owner.");
                phase = 10; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 10) { items.TestUse(lift); phase = 11; }
            else if (phase == 11 && Values(items).IntValue1 == 1)
            {
                ((Furniture_CarLift)items.TestItem(lift)!).LiftButtonPressed(true);
                phase = 12;
            }
            else if (phase == 12 && Values(items).Value2 > 0 && Values(items).Value1 >
                CarLiftState.LowerHeight((Furniture_CarLift)items.TestItem(lift)!) + 0.1f)
            {
                stoppedLift = (Furniture_CarLift)items.TestItem(lift)!;
                VerifyAudio(stoppedLift, true);
                Require(!CarLiftState.IsMoving(stoppedLift), "Second raise started guest native hydraulic simulation.");
                observedMotor = Motor(stoppedLift);
                phase = 90; // deliberately keep Up held until the host stops its whole session
            }
            return false;
        }
        private bool AfterDisposal()
        {
            if (phase == 90)
            {
                if (plugin.Session?.IsRunning == true || plugin.World != null) return false;
                Require((stoppedLift == null || !stoppedLift.gameObject.activeInHierarchy) &&
                    (observedMotor == null || !observedMotor.isPlaying),
                    "Host disconnect left guest lift simulation or motor presentation active.");
                return true;
            }
            if (Time.unscaledTime < next) return false;
            Require(stoppedLift != null && !CarLiftState.IsMoving(stoppedLift) &&
                Mathf.Abs(stoppedLift.Value1 - stoppedHeight) < 0.0001f,
                "Guest-operated host lift continued moving after world disposal (IP-03).");
            return true;
        }
        private static AudioSource Motor(Furniture_CarLift native)
        {
            var sound = AccessTools.Field(typeof(Furniture_CarLift), "SOUND_CarLift")!.GetValue(native) as SOUND_CarLift;
            return sound == null ? throw new InvalidOperationException("Lift sound component is unavailable.") :
                AccessTools.Field(typeof(SOUND_CarLift), "HydraulicMotor")!.GetValue(sound) as AudioSource ??
                    throw new InvalidOperationException("Native lift motor AudioSource has not initialized.");
        }
        private static void VerifyAudio(Furniture_CarLift native, bool up)
        {
            var motor = Motor(native);
            var manager = UnityEngine.Object.FindObjectOfType<SOUND_Manager>();
            var clip = AccessTools.Field(typeof(SOUND_Manager), up ? "Hydraulic_Motor" : "CarLift_Down")!.GetValue(manager) as AudioClip;
            Require(clip != null && motor.clip == clip && motor.isPlaying,
                "Guest native lift " + (up ? "up" : "down") + " motor source did not play the host-directed clip (IP-04).");
        }
        // Stand clear of the lift's frame, within reach of its pivot.
        private void MoveNear(Transform target)
        {
            foreach (var direction in new[] { target.right, -target.right, target.forward, -target.forward })
            {
                var at = target.position + direction * 3.2f + Vector3.up;
                if (System.Linq.Enumerable.Any(Physics.OverlapCapsule(at, at + Vector3.up, 0.4f, ~0, QueryTriggerInteraction.Ignore), c => !(c is TerrainCollider))) continue;
                gameplay.PlayerWalking.transform.position = at; gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                return;
            }
            throw new InvalidOperationException("Lift test: no clear standing point beside the lift.");
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Lift test: " + reason); }
    }
}
