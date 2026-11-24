using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MonMP.Core.Session;
using MonMP.Plugin.Testing;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Three-process Phase 0 check: seed one isolated save, then cold-load that exact
    // save twice and compare the two loaded-world inventories.
    internal sealed class SavedWorldInventoryTest
    {
        private static readonly string[] NativeSaveFiles = { "marguerite.txt", "braaaap.txt", "blopblop.txt", "lyne.txt" };
        private readonly MonMPPlugin plugin;
        private readonly RuntimeProbe probe;
        private readonly GameplayProbe loader;
        private readonly string stage, saveName, resultPath;
        private readonly int requestedTimestamp;
        private float startedAt, saveStartedAt;
        private int phase;
        private bool finished;

        internal SavedWorldInventoryTest(MonMPPlugin plugin, RuntimeProbe probe, string stage, string saveName, int timestamp)
        {
            if (stage != "seed" && stage != "first" && stage != "second")
                throw new ArgumentException("Inventory stage must be seed, first, or second.", nameof(stage));
            if (string.IsNullOrWhiteSpace(saveName) || saveName.Length > 48 || saveName.Any(c => char.IsControl(c) || c == '/' || c == '\\'))
                throw new ArgumentException("Inventory save name is invalid.", nameof(saveName));
            if ((stage == "seed" && timestamp != -1) || (stage != "seed" && timestamp < 0))
                throw new ArgumentOutOfRangeException(nameof(timestamp), "Seed requires timestamp -1; loaded stages require a saved timestamp.");

            this.plugin = plugin;
            this.probe = probe;
            this.stage = stage;
            this.saveName = saveName;
            requestedTimestamp = timestamp;
            loader = new GameplayProbe(probe, false, saveName, timestamp, 15f);
            resultPath = Path.Combine(probe.DirectoryPath, "saved-world-inventory-test.txt");
            startedAt = Time.unscaledTime;
            File.WriteAllText(resultPath, "RUNNING: " + stage + " saved-world inventory stage\nSaveName=" + saveName + "\nRequestedTimestamp=" + timestamp);
        }

        internal void Tick(Gameplay? gameplay)
        {
            if (finished) return;
            if (Time.unscaledTime - startedAt > 240f) { Fail("Timed out during " + stage + " stage " + phase + "."); return; }
            if (plugin.Session?.IsRunning == true) throw new InvalidOperationException("Saved-world inventory test unexpectedly started networking.");

            if (phase == 0) loader.Tick(gameplay);
            if (phase == 0 && loader.Ready && gameplay?.Player != null)
            {
                VerifySelectedSave();
                if (stage == "seed")
                {
                    SaveSystem.Save(House_Location.SpecialMenu);
                    saveStartedAt = Time.unscaledTime;
                    phase = 1;
                    WriteProgress("RUNNING: seeded isolated world; waiting for native save publication");
                    return;
                }

                CopySnapshot("world", loader.WorldInventoryPath!, "saved-world-" + stage + "-world.tsv");
                CopySnapshot("hierarchy", loader.HierarchyPath!, "saved-world-" + stage + "-hierarchy.tsv");
                CopySnapshot("collisions", loader.CollisionPath!, "saved-world-" + stage + "-collisions.tsv");
                CopySnapshot("inactive interactions", loader.InactiveInteractionsPath!, "saved-world-" + stage + "-inactive-interactions.tsv");
                var collisions = StableRows(CollisionPath(stage), 1);
                if (collisions.Length != 0)
                    throw new InvalidDataException("Diagnostic inventory reported identity collisions; see " + CollisionPath(stage));
                _ = StableRows(InactiveInteractionsPath(stage), 1);

                if (stage == "first")
                {
                    WriteLoadedResult("MONMP_SAVED_WORLD_INVENTORY_CAPTURED_FIRST");
                    finished = true;
                    Application.Quit();
                    return;
                }

                CompareSnapshots();
            }

            if (phase == 1) VerifyNativeSave();
        }

        private void VerifySelectedSave()
        {
            if (LocalTestIsolation.GetString("SaveName") != saveName)
                throw new InvalidOperationException("The isolated profile selected a different save name.");
            if (LocalTestIsolation.GetInt("SaveTimestamp") != requestedTimestamp)
                throw new InvalidOperationException("The isolated profile selected a different save timestamp.");
            // LastSaveTimestamp is set to the current clock in SaveSystem.Start and changes
            // on Save; vanilla does not set it to the timestamp of a loaded save.
            if (stage != "seed" && !SaveSystem.IsSaveExist(saveName, requestedTimestamp))
                throw new InvalidOperationException("The selected native save is missing.");
        }

        private void VerifyNativeSave()
        {
            if (Time.unscaledTime - saveStartedAt > 90f) throw new InvalidOperationException("Native save did not publish within 90 seconds.");
            int timestamp = SaveSystem.Get_LastSaveTimestamp;
            if (timestamp < 0 || !SaveSystem.IsSaveExist(saveName, timestamp)) return;
            string directory = Path.Combine(LocalTestIsolation.SaveRoot, saveName, timestamp.ToString(CultureInfo.InvariantCulture));
            var saveFiles = GetSaveFileRows(directory);
            if (saveFiles == null) return;
            if (timestamp == requestedTimestamp) throw new InvalidOperationException("Native save reused the seed timestamp.");

            var summary = new StringBuilder()
                .AppendLine("MONMP_SAVED_WORLD_INVENTORY_SEEDED")
                .AppendLine("SaveName=" + saveName)
                .AppendLine("SaveTimestamp=" + timestamp.ToString(CultureInfo.InvariantCulture))
                .AppendLine("SaveDirectory=" + directory)
                .AppendLine("GameplayAssemblySHA256=" + probe.GameHash)
                .AppendLine("ModVersion=" + SessionOptions.CurrentModVersion)
                .AppendLine("The isolated native save exists with all four non-empty files.");
            foreach (string row in saveFiles) summary.AppendLine("SaveFile=" + row);
            File.WriteAllText(resultPath, summary.ToString());
            finished = true;
            Application.Quit();
        }

        private string[]? GetSaveFileRows(string directory)
        {
            var rows = new List<string>();
            foreach (string name in NativeSaveFiles)
            {
                string path = Path.Combine(directory, name);
                if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
                rows.Add(name + "\t" + new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture) + "\t" + HashFile(path));
            }
            return rows.ToArray();
        }

        private void WriteLoadedResult(string marker)
        {
            File.WriteAllText(resultPath,
                marker + "\n" +
                "Stage=" + stage + "\n" +
                "SaveName=" + saveName + "\n" +
                "SaveTimestamp=" + requestedTimestamp.ToString(CultureInfo.InvariantCulture) + "\n" +
                "GameplayAssemblySHA256=" + probe.GameHash + "\n" +
                "ModVersion=" + SessionOptions.CurrentModVersion + "\n" +
                "WorldInventorySHA256=" + HashFile(InventoryPath(stage)) + "\n" +
                "HierarchySHA256=" + HashFile(HierarchyPath(stage)) + "\n" +
                "InactiveInteractionsSHA256=" + HashFile(InactiveInteractionsPath(stage)) + "\n" +
                "CollisionsSHA256=" + HashFile(CollisionPath(stage)) + "\n" +
                "WorldRows=" + StableRows(InventoryPath(stage), 3).Length + "\n" +
                "HierarchyRows=" + StableRows(HierarchyPath(stage), 0).Length + "\n" +
                "InactiveInteractionRows=" + StableRows(InactiveInteractionsPath(stage), 1).Length + "\n" +
                "CollisionRows=0\n" +
                "Inactive object identities, concrete types, coverage gaps and native virtual dispatch targets captured after loading the selected saved world.");
        }

        private void CompareSnapshots()
        {
            var firstWorld = StableRows(InventoryPath("first"), 3);
            var secondWorld = StableRows(InventoryPath("second"), 3);
            var firstHierarchyRaw = StableRows(HierarchyPath("first"), 0);
            var secondHierarchyRaw = StableRows(HierarchyPath("second"), 0);
            // NWH creates and reorders tire skid-mesh effects during native settling.
            // They are transient visuals, not save-stable world hierarchy identities.
            var firstHierarchy = firstHierarchyRaw.Where(StableHierarchyRow).ToArray();
            var secondHierarchy = secondHierarchyRaw.Where(StableHierarchyRow).ToArray();
            var firstInactive = StableRows(InactiveInteractionsPath("first"), 1);
            var secondInactive = StableRows(InactiveInteractionsPath("second"), 1);
            var worldDiff = WriteDiff("saved-world-inventory-diff.tsv", firstWorld, secondWorld);
            var hierarchyDiff = WriteDiff("saved-world-hierarchy-diff.tsv", firstHierarchy, secondHierarchy);
            var inactiveDiff = WriteDiff("saved-world-inactive-interaction-diff.tsv", firstInactive, secondInactive);
            bool equal = worldDiff.added == 0 && worldDiff.removed == 0 && hierarchyDiff.added == 0 && hierarchyDiff.removed == 0 &&
                inactiveDiff.added == 0 && inactiveDiff.removed == 0;
            string marker = equal ? "MONMP_SAVED_WORLD_INVENTORY_PASS" : "MONMP_SAVED_WORLD_INVENTORY_FAIL";
            var summary = new StringBuilder()
                .AppendLine(marker)
                .AppendLine("SaveName=" + saveName)
                .AppendLine("SaveTimestamp=" + requestedTimestamp.ToString(CultureInfo.InvariantCulture))
                .AppendLine("GameplayAssemblySHA256=" + probe.GameHash)
                .AppendLine("ModVersion=" + SessionOptions.CurrentModVersion)
                .AppendLine("FirstWorldRows=" + firstWorld.Length)
                .AppendLine("SecondWorldRows=" + secondWorld.Length)
                .AppendLine("WorldAdded=" + worldDiff.added)
                .AppendLine("WorldRemoved=" + worldDiff.removed)
                .AppendLine("FirstHierarchyRows=" + firstHierarchy.Length)
                .AppendLine("SecondHierarchyRows=" + secondHierarchy.Length)
                .AppendLine("FirstTransientSkidRowsExcluded=" + (firstHierarchyRaw.Length - firstHierarchy.Length))
                .AppendLine("SecondTransientSkidRowsExcluded=" + (secondHierarchyRaw.Length - secondHierarchy.Length))
                .AppendLine("HierarchyAdded=" + hierarchyDiff.added)
                .AppendLine("HierarchyRemoved=" + hierarchyDiff.removed)
                .AppendLine("FirstInactiveInteractionRows=" + firstInactive.Length)
                .AppendLine("SecondInactiveInteractionRows=" + secondInactive.Length)
                .AppendLine("InactiveInteractionAdded=" + inactiveDiff.added)
                .AppendLine("InactiveInteractionRemoved=" + inactiveDiff.removed)
                .AppendLine("FirstWorldSHA256=" + HashFile(InventoryPath("first")))
                .AppendLine("SecondWorldSHA256=" + HashFile(InventoryPath("second")))
                .AppendLine("FirstHierarchySHA256=" + HashFile(HierarchyPath("first")))
                .AppendLine("SecondHierarchySHA256=" + HashFile(HierarchyPath("second")))
                .AppendLine("FirstInactiveInteractionsSHA256=" + HashFile(InactiveInteractionsPath("first")))
                .AppendLine("SecondInactiveInteractionsSHA256=" + HashFile(InactiveInteractionsPath("second")))
                .AppendLine("FirstCollisionsSHA256=" + HashFile(CollisionPath("first")))
                .AppendLine("SecondCollisionsSHA256=" + HashFile(CollisionPath("second")))
                .AppendLine("Compared two cold launches of the same saved timestamp; compared interactable ID/path, save-stable transform path/activeSelf, and inactive interactable identity/type/coverage/native-dispatch rows. Transient NWH SkidContainer/SkidMesh visual rows, positions, and timestamps inside rows are excluded. Raw hierarchy files and hashes are retained.");
            File.WriteAllText(resultPath, summary.ToString());
            finished = true;
            Application.Quit(equal ? 0 : 1);
        }

        private (int added, int removed) WriteDiff(string filename, string[] before, string[] after)
        {
            var beforeSet = new HashSet<string>(before, StringComparer.Ordinal);
            var afterSet = new HashSet<string>(after, StringComparer.Ordinal);
            var removed = beforeSet.Except(afterSet, StringComparer.Ordinal).OrderBy(row => row, StringComparer.Ordinal).ToArray();
            var added = afterSet.Except(beforeSet, StringComparer.Ordinal).OrderBy(row => row, StringComparer.Ordinal).ToArray();
            File.WriteAllLines(Path.Combine(probe.DirectoryPath, filename), new[] { "change\trow" }
                .Concat(removed.Select(row => "removed\t" + row))
                .Concat(added.Select(row => "added\t" + row)));
            return (added.Length, removed.Length);
        }

        private static string[] StableRows(string path, int skip)
        {
            var allRows = File.ReadAllLines(path);
            if (skip == 3 && (allRows.Length < 3 || allRows[2] != "id\tcanonical-path"))
                throw new InvalidDataException("Unknown interactable inventory schema: " + path);
            if (skip == 1 && (allRows.Length == 0 || allRows[0] != "id\texisting-path\tcandidate-path") &&
                (allRows.Length == 0 || allRows[0] != "id\tidentity-path\tgameobject-path\ttype\tactive-self\tactive-in-hierarchy\tcomponent-enabled\tdisposition\tadapter\texecution\tlate-join\ttests\tdispatch\tgap-flags"))
                throw new InvalidDataException("Unknown collision or inactive interaction inventory schema: " + path);
            if ((skip == 1 && allRows.Length < 1) || (skip != 1 && allRows.Length <= skip))
                throw new InvalidDataException("Empty inventory: " + path);
            var rows = allRows.Skip(skip).OrderBy(row => row, StringComparer.Ordinal).ToArray();
            if (rows.Distinct(StringComparer.Ordinal).Count() != rows.Length)
                throw new InvalidDataException("Inventory contains duplicate rows: " + path);
            return rows;
        }

        private static bool StableHierarchyRow(string row) =>
            row.IndexOf(":SkidContainer[", StringComparison.Ordinal) < 0 ||
            row.IndexOf(":SkidMesh_", StringComparison.Ordinal) < 0;

        private void CopySnapshot(string kind, string source, string targetName)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) throw new FileNotFoundException("Missing " + kind + " snapshot.", source);
            File.Copy(source, Path.Combine(probe.DirectoryPath, targetName), true);
        }

        private string InventoryPath(string stageName) => Path.Combine(probe.DirectoryPath, "saved-world-" + stageName + "-world.tsv");
        private string HierarchyPath(string stageName) => Path.Combine(probe.DirectoryPath, "saved-world-" + stageName + "-hierarchy.tsv");
        private string CollisionPath(string stageName) => Path.Combine(probe.DirectoryPath, "saved-world-" + stageName + "-collisions.tsv");
        private string InactiveInteractionsPath(string stageName) => Path.Combine(probe.DirectoryPath, "saved-world-" + stageName + "-inactive-interactions.tsv");
        private void WriteProgress(string value) => File.WriteAllText(resultPath, value + "\nSaveName=" + saveName);

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        internal void Fail(string message)
        {
            if (finished) return;
            finished = true;
            File.WriteAllText(resultPath, "MONMP_SAVED_WORLD_INVENTORY_FAIL\nStage=" + stage + "\n" + message);
            Application.Quit(1);
        }
    }
}
