using System;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Seed native sound while the real equipped Spanner coroutine is alive, then let its native
    // unequip exit send the delayed off after activeTool becomes null. This is a sound lifecycle
    // probe, not proof that actual bolt turning or perceived audio quality works.
    internal sealed class BoltSoundLifecycleProbe : IDisposable
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly AudioSource screw, unscrew;
        private readonly int screwStarts, screwStops, unscrewStarts, unscrewStops;
        private readonly ushort spannerPrefab;
        private readonly ulong previousSpanner;
        private Tool_AdjustableSpanner? tool;
        private ulong toolId;
        private int phase, cycle, unequipFrame;
        private float next;
        private bool disposed, complete;
        internal string Status => "bolt-sound cycle=" + cycle + "; phase=" + phase + "; complete=" + complete;

        internal BoltSoundLifecycleProbe(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            var prefabs = plugin.Items!.TestPrefabs(typeof(Tool_AdjustableSpanner));
            Require(prefabs.Length > 0, "No shared spanner prefab exists.");
            spannerPrefab = prefabs[0]; previousSpanner = plugin.Items.TestNewestPrefab(spannerPrefab);
            var sound = Singleton<SOUND_Manager>.i.GetComponent<SOUND_General>();
            screw = Source(sound, "AdjustableSpanner_Screw");
            unscrew = Source(sound, "AdjustableSpanner_UnScrew");
            var a = SpatialSounds.TestLoop(screw, 0); var b = SpatialSounds.TestLoop(unscrew, 0);
            screwStarts = a.Starts; screwStops = a.Stops; unscrewStarts = b.Starts; unscrewStops = b.Stops;
        }

        private static AudioSource Source(SOUND_General sound, string name) =>
            AccessTools.Field(typeof(SOUND_General), name)?.GetValue(sound) as AudioSource ??
            throw new InvalidOperationException("Bolt lifecycle: missing native source " + name);

        internal bool Tick(bool startHost)
        {
            if (complete) return true;
            Require(plugin.Session?.Peers.Count >= 2, "A peer left before the native sound lifecycle checks finished.");
            if (!plugin.TestOptions.IsHost)
            {
                var a = SpatialSounds.TestLoop(screw, 0); var b = SpatialSounds.TestLoop(unscrew, 0);
                // Counters record actual emitter.Play and explicit scoped stop, not peer-left cleanup.
                if (a.Starts - screwStarts < 2 || a.Stops - screwStops < 2 ||
                    b.Starts - unscrewStarts < 2 || b.Stops - unscrewStops < 2) return false;
                Require(!a.Active && !a.Playing && !b.Active && !b.Playing,
                    "An observer spanner emitter survived the native unequip stop.");
                // Wait for host cleanup to publish the probe tool's tombstone. Otherwise the guest
                // could disconnect on the last stop before the host checked its own native exit.
                ulong finishedTool = plugin.Items!.TestNewestPrefab(spannerPrefab);
                if (finishedTool <= previousSpanner || plugin.Items.TestState(finishedTool).Exists) return false;
                complete = true;
                Debug.Log("MonMP bolts #325: observer saw two playing/stopped screw and unscrew cycles while actor remained connected.");
                return true;
            }
            if (!startHost || Time.unscaledTime < next) return false;
            var items = plugin.Items!;
            var sound = Singleton<SOUND_Manager>.i;
            if (phase == 0)
            {
                Require(gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == null,
                    "Lifecycle probe requires empty hands; refusing to replace the player's existing tool/item.");
                tool = (Tool_AdjustableSpanner)items.TestSpawn(gameplay.PlayerWalking.transform.position + Vector3.up, spannerPrefab);
                phase = 1; return false;
            }
            if (phase == 1)
            {
                if (tool == null || (toolId = items.TestBoundId(tool)) == 0) return false;
                if (items.TestState(toolId).Lease.Owner != ItemLease.Unowned) return false;
                items.TestGrab(toolId); phase = 2; return false;
            }
            if (phase == 2)
            {
                if (items.TestState(toolId).Lease.Owner != 0 || !items.TestLocalHeld(toolId)) return false;
                gameplay.Player.EquipThisTool(tool!);
                Require(tool!.isEquiped && gameplay.Player.Get_ActiveTool == tool,
                    "Native equip did not start the real spanner coroutine.");
                if (cycle < 2) sound.PlaySound_BoltScrewing(); else sound.PlaySound_BoltUnScrewing();
                Require((cycle < 2 ? screw : unscrew).isPlaying, "Seeded native loop did not play.");
                phase = 3; next = Time.unscaledTime + 1f; return false;
            }
            if (phase == 3)
            {
                // Do not call SOUND_Manager off: the native Spanner coroutine must produce it.
                items.TestPutAwayTool();
                Require(gameplay.Player.Get_ActiveTool == null && !tool!.isEquiped,
                    "Native unequip did not clear activeTool before the coroutine's off.");
                unequipFrame = Time.frameCount; phase = 4; next = Time.unscaledTime + 0.6f; return false;
            }
            if (phase == 4)
            {
                if (Time.frameCount <= unequipFrame + 1) return false;
                Require(!screw.isPlaying && !unscrew.isPlaying, "Native coroutine exit did not stop its own loops.");
                if (items.TestState(toolId).Lease.Owner != ItemLease.Unowned) return false;
                cycle++;
                if (cycle < 4) { phase = 1; return false; }
                complete = true;
                Debug.Log("MonMP bolts #325: actor completed two native seeded screw and unscrew unequip cycles; activeTool was null before each delayed off.");
                Dispose(); return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (!plugin.TestOptions.IsHost || tool == null) return;
            // Explicit off is cleanup only; it cannot count toward the assertions above.
            try
            {
                var sound = Singleton<SOUND_Manager>.i;
                sound.PlaySound_BoltScrewingOff(); sound.PlaySound_BoltUnScrewingOff();
            }
            finally
            {
                try
                {
                    if (gameplay.Player.Get_ActiveTool == tool) plugin.Items?.TestPutAwayTool();
                    else if (gameplay.Player.Get_HoldingItem == tool) plugin.Items?.TestDrop(false);
                }
                finally { UnityEngine.Object.Destroy(tool.gameObject); }
            }
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Bolt lifecycle #325: " + message); }
    }
}
