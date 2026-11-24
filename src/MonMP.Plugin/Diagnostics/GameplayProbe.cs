using System;
using System.IO;
using System.Linq;
using System.Text;
using MonMP.Plugin.Patches;
using MonMP.Plugin.Testing;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Explicit automated probe against an isolated test profile. Uses the vanilla menu flow.
    internal sealed class GameplayProbe
    {
        private bool requestedWorld, finished;
        private float readyAt = -1;
        private readonly RuntimeProbe probe;
        private readonly bool exitWhenReady;
        private readonly string saveName;
        private readonly int saveTimestamp;
        private readonly float settleSeconds;
        private NpcAvatarRenderFixture? npcAvatarRenders;
        private AppearanceUiFixture? appearanceUi;
        public bool Ready { get; private set; }
        internal string? WorldInventoryPath { get; private set; }
        internal string? HierarchyPath { get; private set; }
        internal string? CollisionPath { get; private set; }
        internal string? InactiveInteractionsPath { get; private set; }
        internal GameplayProbe(RuntimeProbe probe, bool exitWhenReady = true, string saveName = "MonMPProbe", int saveTimestamp = -1, float settleSeconds = 10f)
        { this.probe = probe; this.exitWhenReady = exitWhenReady; this.saveName = saveName; this.saveTimestamp = saveTimestamp; this.settleSeconds = settleSeconds; }
        internal void Tick(Gameplay? gameplay)
        {
            if (finished) return;
            bool appearanceTest = Environment.GetCommandLineArgs().Contains("--monmp-appearance-ui-test");
            if (appearanceTest)
            {
                appearanceUi ??= new AppearanceUiFixture(probe.DirectoryPath);
                try
                {
                    if (!requestedWorld && !appearanceUi.MenuStep()) return;
                    if (appearanceUi.WorldStarted) { CompleteAppearance(appearanceUi.WorldStep(gameplay!)); return; }
                }
                catch (Exception ex) { appearanceUi.Dispose(); CompleteAppearance("MONMP_NPC_AVATAR_RENDER_FAIL\n" + ex); return; }
            }
            if (!requestedWorld)
            {
                var menu = UnityEngine.Object.FindObjectOfType<MainMenu_Manager>();
                if (menu != null && Time.unscaledTime > 8)
                {
                    if (!LocalTestIsolation.Installed) throw new InvalidOperationException("Gameplay probe requires save isolation.");
                    LocalTestIsolation.SetString("SaveName", saveName);
                    LocalTestIsolation.SetInt("SaveTimestamp", saveTimestamp);
                    LocalTestIsolation.SetInt("HardMode", 0); LocalTestIsolation.SetInt("Permadeath", 0);
                    menu.StartGame(false); requestedWorld = true;
                }
            }
            if (NativeLoadGate.Ready && gameplay?.Player != null && !gameplay.Get_isGamePaused && gameplay.Player.mainCamera != null)
            {
                if (readyAt < 0) readyAt = Time.unscaledTime + settleSeconds;
                if (Time.unscaledTime >= readyAt)
                {
                    if (appearanceTest)
                    {
                        try { CompleteAppearance(appearanceUi!.WorldStep(gameplay)); }
                        catch (Exception ex) { appearanceUi!.Dispose(); CompleteAppearance("MONMP_NPC_AVATAR_RENDER_FAIL\n" + ex); }
                        return;
                    }
                    if (Environment.GetCommandLineArgs().Contains("--monmp-npc-avatar-render-test"))
                    {
                        string? result;
                        try
                        {
                            npcAvatarRenders ??= new NpcAvatarRenderFixture(gameplay, probe.DirectoryPath);
                            result = npcAvatarRenders.Step();
                        }
                        catch (Exception ex)
                        {
                            npcAvatarRenders?.Dispose();
                            result = "MONMP_NPC_AVATAR_RENDER_FAIL\n" + ex;
                        }
                        if (result == null) return;
                        File.WriteAllText(Path.Combine(probe.DirectoryPath, "npc-avatar-render-result.txt"), result);
                        Ready = result.StartsWith("MONMP_NPC_AVATAR_RENDER_PASS", StringComparison.Ordinal); finished = true;
                        Application.Quit(Ready ? 0 : 1); return;
                    }
                    if (Environment.GetCommandLineArgs().Contains("--monmp-seat-foot-live-test"))
                    {
                        string? preview = MonMPPlugin.Instance!.TestSeatFootLiveStep(probe.DirectoryPath);
                        if (preview == null) return;
                        File.WriteAllText(Path.Combine(probe.DirectoryPath, "seat-foot-live-test.txt"), preview);
                        bool passed = preview.Contains("MONMP_SEAT_FOOT_LIVE_PASS");
                        Ready = passed; finished = true; Application.Quit(passed ? 0 : 1); return;
                    }
                    if (Environment.GetCommandLineArgs().Contains("--monmp-seat-preview-test"))
                    {
                        string preview = MonMPPlugin.Instance!.TestVehicleSeatEditor(probe.DirectoryPath);
                        File.WriteAllText(Path.Combine(probe.DirectoryPath, "seat-preview-test.txt"), preview);
                        bool passed = preview.Contains("MONMP_SEAT_EDITOR_SMOKE_PASS");
                        Ready = passed; finished = true; Application.Quit(passed ? 0 : 1); return;
                    }
                    if (Environment.GetCommandLineArgs().Contains("--monmp-held-preview-test"))
                    {
                        string preview = MonMPPlugin.Instance!.TestHeldGripEditor(probe.DirectoryPath);
                        File.WriteAllText(Path.Combine(probe.DirectoryPath, "held-preview-test.txt"), preview);
                        bool passed = preview.Contains("MONMP_HELD_EDITOR_SMOKE_PASS");
                        Ready = passed; finished = true; Application.Quit(passed ? 0 : 1); return;
                    }
                    string report = probe.DumpWorld();
                    int worldStart = report.LastIndexOf("world-", StringComparison.Ordinal);
                    if (worldStart < 0) throw new InvalidOperationException("World dump did not report its output file.");
                    string worldName = report.Substring(worldStart).Trim();
                    WorldInventoryPath = Path.Combine(probe.DirectoryPath, worldName);
                    if (!File.Exists(WorldInventoryPath)) throw new FileNotFoundException("World inventory was not written.", WorldInventoryPath);
                    string stamp = worldName.Substring("world-".Length, worldName.Length - "world-".Length - ".tsv".Length);
                    HierarchyPath = Path.Combine(probe.DirectoryPath, "hierarchy-" + stamp + ".tsv");
                    if (!File.Exists(HierarchyPath)) throw new FileNotFoundException("Hierarchy inventory was not written.", HierarchyPath);
                    CollisionPath = Path.Combine(probe.DirectoryPath, "world-collisions-" + stamp + ".tsv");
                    if (!File.Exists(CollisionPath)) throw new FileNotFoundException("Inventory collision report was not written.", CollisionPath);
                    InactiveInteractionsPath = probe.InactiveInteractionsPath;
                    if (string.IsNullOrWhiteSpace(InactiveInteractionsPath) || !File.Exists(InactiveInteractionsPath))
                        throw new FileNotFoundException("Inactive interaction parity inventory was not written.", InactiveInteractionsPath);
                    _ = Replication.ItemPrefabCatalog.Load(probe.DirectoryPath);
                    var switches = new StringBuilder();
                    foreach (var item in UnityEngine.Object.FindObjectsOfType<Furniture_SwitchButton>(true))
                    {
                        switches.Append(item.name).Append(" | ");
                        foreach (var action in new[] { item.Get_SwitchOn, item.Get_SwitchOff })
                        {
                            for (int i = 0; i < action.GetPersistentEventCount(); i++)
                                switches.Append(action.GetPersistentTarget(i)?.GetType().FullName).Append('.').Append(action.GetPersistentMethodName(i)).Append(';');
                            switches.Append(" | ");
                        }
                        switches.AppendLine();
                    }
                    File.WriteAllText(Path.Combine(probe.DirectoryPath, "switch-callbacks.txt"), switches.ToString());
                    CharacterRigSurvey.Write(probe.DirectoryPath);
                    var items = new StringBuilder("prefab\ttype\tactive\tposition\tcomponents\n");
                    foreach (var item in UnityEngine.Object.FindObjectsOfType<InteractableItems>(true))
                    {
                        items.Append(item.PrefabName).Append('\t').Append(item.GetType().Name).Append('\t').Append(item.gameObject.activeInHierarchy).Append('\t').Append(item.transform.position).Append('\t');
                        foreach (var component in item.GetComponents<Component>()) items.Append(component?.GetType().Name).Append(';');
                        items.AppendLine();
                    }
                    File.WriteAllText(Path.Combine(probe.DirectoryPath, "items.tsv"), items.ToString());
                    var vehicles = new StringBuilder("type\tpath\tactive\tposition\tdoorless\tdriver-seats\tpassenger-seats\trigidbodies\n");
                    foreach (var vehicle in UnityEngine.Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>(true))
                    {
                        vehicles.Append(vehicle.vehicle_type).Append('\t').Append(RuntimeProbe.CanonicalPath(vehicle.transform)).Append('\t')
                            .Append(vehicle.gameObject.activeInHierarchy).Append('\t').Append(vehicle.transform.position).Append('\t').Append(vehicle.NoDriverDoor).Append('\t')
                            .Append(vehicle.GetComponentsInChildren<Vehicle_DriverSeat>(true).Length).Append('\t')
                            .Append(vehicle.GetComponentsInChildren<Vehicle_PassengerSeat>(true).Length).Append('\t');
                        foreach (var body in vehicle.GetComponentsInChildren<Rigidbody>(true)) vehicles.Append(body.name).Append(':').Append(body.isKinematic).Append(';');
                        vehicles.Append(" wheels=").Append(vehicle.Wheels.Count).Append(" fuel=").Append(vehicle.vehicle_PartsManager?.Fuel_Tank != null)
                            .Append(" hitch=").Append(vehicle.GetComponent<NWH.VehiclePhysics2.Modules.Trailer.TrailerHitchModuleWrapper>() != null);
                        vehicles.AppendLine();
                    }
                    File.WriteAllText(Path.Combine(probe.DirectoryPath, "vehicles.tsv"), vehicles.ToString());
                    var cabin = new StringBuilder();
                    foreach (var vehicle in UnityEngine.Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>())
                    {
                        if (vehicle.vehicle_type != Vehicle_Type.OlTruck) continue;
                        cabin.AppendLine("TRUCK " + RuntimeProbe.CanonicalPath(vehicle.transform));
                        foreach (var component in vehicle.GetComponentsInChildren<Component>(true))
                        {
                            if (component is Rigidbody || component is Joint || component is Vehicle_Door || component is Vehicle_DoorHandle || component is Vehicle_DriverSeat || component is Vehicle_PassengerSeat)
                                cabin.AppendLine(component.GetType().Name + " " + RuntimeProbe.CanonicalPath(component.transform));
                            if (component is Joint joint) cabin.AppendLine(" connected=" + joint.connectedBody?.name);
                            if (component is Rigidbody body) cabin.AppendLine(" mass=" + body.mass + " constraints=" + body.constraints + " components=" + string.Join(",", Array.ConvertAll(body.GetComponents<Component>(), c => c?.GetType().Name)));
                            if (component is Vehicle_Door door)
                            {
                                cabin.AppendLine(" driver=" + door.isDriverDoor + " open=" + door.Get_IsOpen + " controller=" + door.vehicleController?.name + " enabled=" + door.enabled + " rotation=" + door.transform.localEulerAngles);
                                foreach (var field in new[] { "DoorOpened", "DoorClosed" })
                                {
                                    var action = (UnityEngine.Events.UnityEvent)HarmonyLib.AccessTools.Field(typeof(Vehicle_Door), field).GetValue(door);
                                    cabin.Append(field + ": ");
                                    for (int i = 0; i < action.GetPersistentEventCount(); i++) cabin.Append(action.GetPersistentTarget(i)?.GetType().FullName).Append('.').Append(action.GetPersistentMethodName(i)).Append(';');
                                    cabin.AppendLine();
                                }
                            }
                        }
                        foreach (var seat in UnityEngine.Object.FindObjectsOfType<Vehicle_PassengerSeat>(true))
                            if (seat.vehicleController == vehicle) cabin.AppendLine("ASSOCIATED PASSENGER " + RuntimeProbe.CanonicalPath(seat.transform));
                    }
                    File.WriteAllText(Path.Combine(probe.DirectoryPath, "vehicle-cabins.txt"), cabin.ToString());
                    var player = gameplay.Player;
                    string details = "MONMP_GAMEPLAY_PROBE_PASS\n" + report + "\nPlayer: " + Describe(player.transform) +
                        "\nWalker: " + Describe(gameplay.PlayerWalking?.transform) + "\nCamera: " + Describe(player.mainCamera.transform);
                    File.WriteAllText(Path.Combine(probe.DirectoryPath, "gameplay-probe.txt"), details);
                    Ready = true; finished = true; if (exitWhenReady) Application.Quit();
                }
            }
            if (Time.unscaledTime > 150 && !finished)
            {
                string reason = NativeLoadGate.Ready
                    ? "vanilla gameplay did not become ready."
                    : "native SaveSystem.LoadAfterEverythingMoved did not complete for the active scene (" + NativeLoadGate.Status + ").";
                File.WriteAllText(Path.Combine(probe.DirectoryPath, "gameplay-probe.txt"), "MONMP_GAMEPLAY_PROBE_FAIL: " + reason);
                finished = true; Application.Quit(1);
            }
        }
        private void CompleteAppearance(string? result)
        {
            if (result == null) return;
            File.WriteAllText(Path.Combine(probe.DirectoryPath, "npc-avatar-render-result.txt"), result);
            Ready = result.StartsWith("MONMP_NPC_AVATAR_RENDER_PASS", StringComparison.Ordinal); finished = true;
            Application.Quit(Ready ? 0 : 1);
        }
        private static string Describe(Transform? t)
        {
            if (t == null) return "missing";
            string path = t.name;
            for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path + " at " + t.position + " scale " + t.lossyScale;
        }
    }
}
