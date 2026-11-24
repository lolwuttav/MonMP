using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The host's sugar shack pumps syrup into the OlTruck bed tank or the tanker trailer. Neither
    // level is part of the vehicle-part or fuel records, so guests mirror them here along with the
    // outside pump's hose connection.
    internal sealed class SharedSyrupCargo : IDisposable
    {
        public const ushort Adapter = 95;
        private static readonly ulong Id = StaticId.Hash("syrup-cargo-v1");
        private static readonly FieldInfo ConnectedField = AccessTools.Field(typeof(SugarShack_PumpSyrupOutside), "IsConnectedTo");
        private static readonly FieldInfo BedTankField = AccessTools.Field(typeof(SugarShack_PumpSyrupOutside), "Part_BedTank");
        private static readonly FieldInfo TankerField = AccessTools.Field(typeof(SugarShack_PumpSyrupOutside), "Trailer_MapleSyrupTank");
        private static readonly FieldInfo RolledField = AccessTools.Field(typeof(SugarShack_PumpSyrupOutside), "TubeRolledOutside");
        private static readonly MethodInfo TubeRoutine = AccessTools.Method(typeof(SugarShack_PumpSyrupOutside), "UpdateTubeRenderer");
        private static readonly FieldInfo TubeField = AccessTools.Field(typeof(SugarShack_PumpSyrupOutside), "Outside_TubeController");
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private SugarShack_PumpSyrupOutside? pump;
        private Part_Spoiler_BedTank? bedTank;
        private Trailer_MapleSyrupTank? tanker;
        private float nextCapture, nextLookup;
        private byte[]? lastPublished;
        // Guest: the host's hose connection, re-applied until the native pump shows it.
        private byte desired;
        private float desiredBedLevel = float.NaN, desiredTankerLevel = float.NaN;
        private readonly Dictionary<Part_Spoiler_BedTank, float> originalBedLevels = new Dictionary<Part_Spoiler_BedTank, float>();

        public SharedSyrupCargo(bool host, string diagnostics)
        {
            this.host = host;
            Lookup();

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-syrup-cargo.tsv"), new[]
            {
                "id\tpump\tbed-tank\ttanker",
                Id.ToString("X16") + "\t" + (pump != null) + "\t" + (bedTank != null) + "\t" + (tanker != null)
            });
#endif

        }

        private void Lookup()
        {
            if (pump == null) pump = UnityEngine.Object.FindObjectOfType<SugarShack_PumpSyrupOutside>(true);
            // The tank installed on the truck; a loose or store copy can't take the hose.
            if (bedTank == null || !bedTank.isPlaced)
            {
                var placed = UnityEngine.Object.FindObjectsOfType<Part_Spoiler_BedTank>().FirstOrDefault(tank => tank != null && tank.isPlaced);
                if (placed != null || bedTank == null) bedTank = placed ?? UnityEngine.Object.FindObjectOfType<Part_Spoiler_BedTank>();
            }
            if (tanker == null) tanker = UnityEngine.Object.FindObjectOfType<Trailer_MapleSyrupTank>();
        }

        internal IEnumerable<WorldRecord> InitialRecords() => new[] { new WorldRecord(Id, 1, Adapter, Encode(float.NaN, float.NaN, 0)) };

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
        }

        private static byte[] Encode(float bed, float tank, byte connection)
        {
            var payload = new byte[9];
            BitConverter.GetBytes(bed).CopyTo(payload, 0);
            BitConverter.GetBytes(tank).CopyTo(payload, 4);
            payload[8] = connection;
            return payload;
        }

        // Only an installed bed tank is cargo here; a loose one belongs to the item system.
        private float BedLevel() => bedTank != null && bedTank.isPlaced ? bedTank.Value1 : float.NaN;
        private float TankerLevel() => tanker != null ? tanker.SyrupAmount : float.NaN;

        public void Update()
        {
            if (session?.IsRunning != true || channel?.Ready != true || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.25f;
            if (Time.unscaledTime >= nextLookup && (pump == null || bedTank == null || !bedTank.isPlaced || tanker == null))
            {
                nextLookup = Time.unscaledTime + (host ? 5f : 2f);
                Lookup();
            }
            if (!host) { ApplyLevels(); Reconcile(); return; }
            byte connection = pump != null && ConnectedField != null ? Convert.ToByte(ConnectedField.GetValue(pump)) : (byte)0;
            float bed = Round(BedLevel()), tank = Round(TankerLevel());
            // Equals treats the absent-tank NaN sentinel as unchanged, unlike ==.
            if (lastPublished != null && bed.Equals(BitConverter.ToSingle(lastPublished, 0)) &&
                tank.Equals(BitConverter.ToSingle(lastPublished, 4)) && connection == lastPublished[8]) return;
            var payload = Encode(bed, tank, connection);
            lastPublished = payload;
            channel.Publish(Id, payload);
        }

        private static float Round(float value) => float.IsNaN(value) ? value : Mathf.Round(value * 10f) / 10f;

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (!host && records.TryGetValue(Id, out var record)) Apply(record.Payload);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Id == Id) Apply(record.Payload);
        }

        private void Apply(byte[] payload)
        {
            if (payload.Length != 9) return;
            Lookup();
            desiredBedLevel = BitConverter.ToSingle(payload, 0);
            desiredTankerLevel = BitConverter.ToSingle(payload, 4);
            ApplyLevels();
            desired = payload[8];
            Reconcile();
        }

        // Native purchased/installed tanks may appear after the cargo snapshot.
        // Replay the latest accepted levels after lookup without requiring a new
        // host mutation; unchanged host captures intentionally send no new packet.
        private void ApplyLevels()
        {
            if (!float.IsNaN(desiredBedLevel) && bedTank != null && bedTank.isPlaced && Mathf.Abs(bedTank.Value1 - desiredBedLevel) > 0.05f)
            {
                // Capture each native installed object before the first shared overwrite;
                // installation changes can introduce another original tank during a join.
                if (!host && !originalBedLevels.ContainsKey(bedTank)) originalBedLevels.Add(bedTank, bedTank.Value1);
                bedTank.Value1 = desiredBedLevel;
            }
            if (!float.IsNaN(desiredTankerLevel) && tanker != null && Mathf.Abs(tanker.SyrupAmount - desiredTankerLevel) > 0.05f)
            {
                tanker.SyrupAmount = desiredTankerLevel;
                tanker.SyrupChanged();
            }
        }

        // Mirrors the native hose: the rolled-up hose hides and the tube follows the connected tank.
        // The guest's own shack code (the pump motor switch, its range check) can roll the hose back
        // up, so this runs every capture and restores the host's connection whenever it drifts.
        private void Reconcile()
        {
            if (pump == null || ConnectedField == null || RolledField == null || TubeRoutine == null || !pump.gameObject.activeInHierarchy) return;
            if (!(RolledField.GetValue(pump) is GameObject rolled) || rolled == null) return;
            byte connection = desired;
            if (connection == 1 && (bedTank == null || !bedTank.isPlaced)) connection = 0;
            if (connection == 2 && tanker == null) connection = 0;
            byte native = Convert.ToByte(ConnectedField.GetValue(pump));
            if (connection == 0)
            {
                // The native coroutine sees the rolled hose and hides the tube itself.
                if (native != 0) ConnectedField.SetValue(pump, Enum.ToObject(ConnectedField.FieldType, 0));
                if (!rolled.activeSelf) rolled.SetActive(true);
                return;
            }
            bool drawn = TubeField?.GetValue(pump) is Component tube && tube != null && tube.gameObject.activeSelf;
            if (native == connection && !rolled.activeSelf && drawn &&
                (connection != 1 || ReferenceEquals(BedTankField?.GetValue(pump), bedTank))) return;
            if (BedTankField != null && bedTank != null) BedTankField.SetValue(pump, bedTank);
            if (TankerField != null && tanker != null) TankerField.SetValue(pump, tanker);
            ConnectedField.SetValue(pump, Enum.ToObject(ConnectedField.FieldType, (int)connection));
            if (rolled.activeSelf) rolled.SetActive(false);
            // A running tube coroutine follows a changed target itself; start one only when none draws.
            if (!drawn) pump.StartCoroutine((IEnumerator)TubeRoutine.Invoke(pump, null));
        }

        public void Dispose()
        {
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (!host && desired != 0 && pump != null) { desired = 0; Reconcile(); }
            RestoreBedLevels();
            session = null; channel = null;
        }

        private void RestoreBedLevels()
        {
            if (!host)
                foreach (var original in originalBedLevels)
                    if (original.Key != null) original.Key.Value1 = original.Value;
            originalBedLevels.Clear();
        }
    }
}
