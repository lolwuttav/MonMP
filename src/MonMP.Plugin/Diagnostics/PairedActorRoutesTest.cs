using System;
using BepInEx;
using MonMP.Plugin.Replication;
using System.IO;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedActorRoutesTest
    {
        private readonly MonMPPlugin plugin;
        private readonly SharedActorRoutes routes;
        private readonly ulong entity;
        private readonly string resultPath;
        private readonly bool host;
        private Vector3 startPosition;
        private int startInterpolationFrames;
        private float phaseStarted;
        private int phase;
        private bool remoteSeen;
        internal string Status { get; private set; } = "Waiting for a bound on-foot route actor.";

        internal PairedActorRoutesTest(MonMPPlugin plugin, bool host)
        {
            this.plugin = plugin; this.host = host;
            routes = plugin.World?.ActorRoutes ?? throw new InvalidOperationException("Actor routes are not attached.");
            entity = routes.TestEntity;
            if (entity == 0) throw new InvalidOperationException("No bound on-foot route actor is available for the paired fixture.");
            resultPath = Path.Combine(Paths.BepInExRootPath, "MonMP", "diagnostics", "paired-actor-routes.txt");
            File.WriteAllText(resultPath, "RUNNING: host route start and guest pose/activity snapshot");
        }

        internal bool Tick()
        {
            if (host) return TickHost();
            return TickGuest();
        }

        private bool TickHost()
        {
            if (phase == 0)
            {
                if (plugin.RemoteAvatarCount == 0) return false;
                if (!remoteSeen) { remoteSeen = true; phaseStarted = Time.unscaledTime; }
                if (Time.unscaledTime - phaseStarted < 1f) return false;
                if (!routes.TestStart(entity)) throw new InvalidOperationException("Host could not start the selected route actor.");
                startPosition = routes.TestPosition(entity) ?? throw new InvalidOperationException("Host route actor disappeared.");
                phaseStarted = Time.unscaledTime; phase = 1;
                Status = "Host route is walking; waiting for measured movement before stop.";
                File.WriteAllText(resultPath, "RUNNING: host route is walking; waiting for movement");
            }
            if (phase == 1)
            {
                var state = routes.TestState(entity);
                var position = routes.TestPosition(entity);
                if (state == null || position == null || !state.Value.Active || !state.Value.Walking)
                    throw new InvalidOperationException("Host route actor did not remain active and walking.");
                if (Vector3.Distance(startPosition, position.Value) >= 0.2f && Time.unscaledTime - phaseStarted >= 2f)
                {
                    if (!routes.TestStop(entity)) throw new InvalidOperationException("Host route actor did not stop cleanly.");
                    phase = 2; phaseStarted = Time.unscaledTime; Status = "Host stopped the route; waiting for the guest replica to deactivate.";
                    File.WriteAllText(resultPath, "RUNNING: route moved on host; waiting for guest stop state");
                    return false;
                }
                if (Time.unscaledTime - phaseStarted > 15f) throw new InvalidOperationException("Host route actor did not move along its native route.");
                return false;
            }
            if (phase == 2)
            {
                if (Time.unscaledTime - phaseStarted < 1f) return false;
                Status = "Host route was stopped and guest disconnect is pending.";
                return plugin.RemoteAvatarCount == 0;
            }
            return false;
        }

        private bool TickGuest()
        {
            var state = routes.TestState(entity);
            var nativePosition = routes.TestPosition(entity);
            var active = routes.TestActive(entity);
            if (phase == 0)
            {
                if (state == null || !state.Value.Active || !state.Value.Walking || nativePosition == null) return false;
                startPosition = nativePosition.Value;
                startInterpolationFrames = routes.TestInterpolationFrames;
                phaseStarted = Time.unscaledTime; phase = 1;
                Status = "Received host walking route state; checking smooth replicated movement.";
            }
            if (phase == 1)
            {
                if (state == null || nativePosition == null || !state.Value.Active || !state.Value.Walking || active != true)
                    throw new InvalidOperationException("Guest route actor lost host active/walking state.");
                if (Time.unscaledTime - phaseStarted >= 1f && Vector3.Distance(startPosition, nativePosition.Value) >= 0.1f &&
                    routes.TestInterpolationFrames > startInterpolationFrames)
                { phase = 2; Status = "Guest interpolated host route movement; waiting for the host stop snapshot."; }
                else if (Time.unscaledTime - phaseStarted > 15f) throw new InvalidOperationException("Guest route actor did not receive moving host poses.");
                return false;
            }
            if (phase == 2)
            {
                if (state == null || state.Value.Active || state.Value.Walking || active != false) return false;
                if (nativePosition == null || Vector3.Distance(new Vector3(state.Value.X, state.Value.Y, state.Value.Z), nativePosition.Value) > 0.05f)
                    throw new InvalidOperationException("Guest route actor did not snap to the final host pose when the route stopped.");
                plugin.EndPairedTestSession();
                return true;
            }
            return false;
        }
    }
}
