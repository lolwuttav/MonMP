using System;
using NWH;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // The guest buys a Konig tow at the towing-service truck; the host charges it, runs the native
    // recovery and the Konig's move to the tow yard reaches the guest through the shared vehicle.
    internal sealed class PairedTowingTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private int phase;
        private float next, deadline;
        internal string Status
        {
            get
            {
                var world = plugin.World;
                var towing = world?.TowingService;
                var walker = gameplay == null ? null : gameplay.PlayerWalking;
                var body = gameplay == null ? null : gameplay.Get_Player_Rigidbody;
                var konig = world == null ? null : Konig();
                return "towing phase=" + phase + "; world=" + (world == null ? "absent" : "present") +
                    "; state=" + towing?.TestState.CurrentVehicleType + "; accepted=" + towing?.TestAccepted +
                    "; rejected=" + towing?.TestRejected + "; konigToSpawn=" +
                    (konig == null || towing == null ? -1 : Vector3.Distance(konig.transform.position, towing.TestVehicleSpawn)) +
                    "; activated=" + (plugin.GuestWorld == null ? "not-guest" : plugin.GuestWorld.Activated.ToString()) +
                    "; profileReady=" + (world?.PersonalProfileReady == true) +
                    "; reconnectSettled=" + (world?.ReconnectPositions?.Settled == true) +
                    "; walker=" + (walker == null ? "missing" : walker.transform.position.ToString("F3")) +
                    "; rigidbody=" + (body == null ? "missing" : body.position.ToString("F3")) +
                    "; playerState=" + (gameplay == null ? "missing" : gameplay.Get_PlayerState.ToString()) +
                    "; distanceToTruck=" + (walker == null || towing == null ? -1 :
                        Vector3.Distance(walker.transform.position, towing.TestTruckPosition)) +
                    "; lastValidation=" + towing?.TestLastValidationStatus + "; lastDecision=" + towing?.TestLastDecisionStatus +
                    "; lastReply=" + towing?.TestLastReplyStatus + "; validationDetails=" + towing?.TestLastValidation +
                    "; menuStatus=" + plugin.MenuStatus;
            }
        }
        internal PairedTowingTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }

        private static Vehicle? Konig() => Singleton<GlobalClassManager>.i?.GetVehicles(Vehicle_Type.MainVehicle);

        internal bool Tick()
        {
            var towing = plugin.World?.TowingService;
            if (towing == null) throw new InvalidOperationException("Towing test world unavailable: " + Status);
            if (deadline == 0) deadline = Time.unscaledTime + 120f;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Towing test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            var konig = Konig() ?? throw new InvalidOperationException("No Konig to tow.");
            float toSpawn = Vector3.Distance(konig.transform.position, towing.TestVehicleSpawn);
            if (plugin.TestOptions.IsHost)
            {
                towing.TestHostOpen();
                if (phase == 0)
                {
                    if (toSpawn < 8f) throw new InvalidOperationException("The Konig already sits at the tow yard; the fixture cannot observe a move.");
                    phase = 1;
                }
                else if (phase == 1 && towing.TestAccepted > 0)
                {
                    // The store's morning opening lands while shared actions are blocked (a sleep or
                    // save): the recovery must be deferred, then retried, not dropped (#123).
                    towing.TestDeferNextNewDay = true;
                    towing.TestRunRecovery();
                    phase = 2; next = Time.unscaledTime + 1f;
                }
                else if (phase == 2 && toSpawn < 3f && towing.TestState.CurrentVehicleType == MonMP.Core.World.TowingServiceState.UselessVehicleType)
                {
                    if (towing.TestDeferredNewDays != 1) throw new InvalidOperationException("The blocked morning recovery was not deferred: " + Status);
                    phase = 3;
                }
                else if (phase == 3 && plugin.Session!.Peers.Count == 1) { plugin.EndPairedTestSession(); return true; }
                return false;
            }
            switch (phase)
            {
                case 0:
                    // Reconnect placement can arrive after profile/world activation.
                    if (plugin.World?.ReconnectPositions?.Settled != true) return false;
                    // Walk up to the truck so the host's reach and walking checks accept the request.
                    var at = towing.TestTruckPosition + new Vector3(0, 0.5f, 2f);
                    gameplay.PlayerWalking.transform.position = at;
                    if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
                    if (toSpawn < 8f) throw new InvalidOperationException("The guest's Konig already sits at the tow yard.");
                    phase = 1; next = Time.unscaledTime + 2f;
                    return false;
                case 1:
                    if (!towing.TestState.StoreOpen) return false;
                    towing.TestRequest(Vehicle_Type.MainVehicle);
                    phase = 2; next = Time.unscaledTime + 1f;
                    return false;
                case 2:
                    // Retry once a second until the host accepts; the queue then shows the Konig.
                    if (towing.TestState.CurrentVehicleType == (int)Vehicle_Type.MainVehicle) { phase = 3; return false; }
                    towing.TestRequest(Vehicle_Type.MainVehicle);
                    next = Time.unscaledTime + 1f;
                    return false;
                case 3:
                    if (toSpawn > 3f) return false;
                    phase = 4; next = Time.unscaledTime + 1f;
                    return false;
                case 4:
                    plugin.EndPairedTestSession();
                    return true;
            }
            return false;
        }
    }
}
