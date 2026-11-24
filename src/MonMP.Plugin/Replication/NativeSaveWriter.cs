using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Persistence;
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
using MonMP.Plugin.Testing;
#endif
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class NativeSaveWriter
    {
        private sealed class Context
        {
            internal string NativeDirectory = "", Stage = "";
            internal readonly List<FileStream> Streams = new List<FileStream>();
            internal readonly HashSet<string> Files = new HashSet<string>(StringComparer.Ordinal);
        }
        private static readonly object sync = new object();
        [ThreadStatic] private static Context? current;
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
        internal static string PersistentRoot => LocalTestIsolation.Installed ? LocalTestIsolation.SaveRoot : Application.persistentDataPath;
        internal static string SelectedSave => LocalTestIsolation.Installed ? LocalTestIsolation.GetString("SaveName") : PlayerPrefs.GetString("SaveName");
        internal static int SelectedTimestamp => LocalTestIsolation.Installed ? LocalTestIsolation.GetInt("SaveTimestamp") : PlayerPrefs.GetInt("SaveTimestamp");
        internal static void SelectTimestamp(int value)
        { if (LocalTestIsolation.Installed) LocalTestIsolation.SetInt("SaveTimestamp", value); else PlayerPrefs.SetInt("SaveTimestamp", value); }
#else
        internal static string PersistentRoot => Application.persistentDataPath;
        internal static string SelectedSave => PlayerPrefs.GetString("SaveName");
        internal static int SelectedTimestamp => PlayerPrefs.GetInt("SaveTimestamp");
        internal static void SelectTimestamp(int value) => PlayerPrefs.SetInt("SaveTimestamp", value);
#endif
        internal static NativeSaveFingerprint Write(SaveSlot slot, SharedWorld world, House_Location location, Action<string>? checkpoint = null, bool recovery = false)
        {
            if (!world.NativeProductionSettled)
                throw new InvalidOperationException("Native production is still unsettled; the previous checkpoint has been preserved.");
            if (MonMPPlugin.Instance?.Session?.IsHost != true || !world.SaveGate.Frozen || slot.Published ||
                (!recovery && (world.SaveRounds?.Phase != SavePhase.Writing || !world.TransactionsSettled || !world.LocalSaveFlushComplete || world.SaveOwners.Length != 0)))
                throw new InvalidOperationException("Native writes require a fully flushed, frozen writing round.");
            lock (sync)
            {
                if (current != null) throw new InvalidOperationException("Native save writer is already active.");
                string expected = Path.GetFullPath(Path.Combine(PersistentRoot, SelectedSave, slot.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (!string.Equals(expected, Path.GetFullPath(slot.DestinationDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Selected native save changed after reservation.");
                var scope = new Context { NativeDirectory = expected, Stage = slot.StagingDirectory };
                var hooks = new List<ILHook>(); current = scope;
                try
                {
                    try
                    {
                        foreach (string name in new[] { "SavePlayer", "SaveVehicles", "SaveItems", "SaveGarden" })
                        {
                            var method = AccessTools.Method(typeof(SaveSystem), name);
                            hooks.Add(new ILHook(method, RewriteFileOpen));
                        }
                    }
                    catch (Exception ex)
                    {
                        // File-redirection setup is mod machinery. The four native serializers
                        // can still write into a uniquely reserved empty output directory.
                        current = null;
                        DisposeHooks(hooks);
                        Debug.LogWarning("MonMP native save redirection fallback: " + ex.Message);
                        return WriteWithoutHooks(slot, expected, location, checkpoint);
                    }
                    AccessTools.Method(typeof(SaveSystem), "SavePlayer").Invoke(null, new object[] { slot.Timestamp, location });
                    checkpoint?.Invoke("SavePlayer");
                    foreach (string name in new[] { "SaveVehicles", "SaveItems", "SaveGarden" })
                    { AccessTools.Method(typeof(SaveSystem), name).Invoke(null, new object[] { slot.Timestamp }); checkpoint?.Invoke(name); }
                    if (scope.Files.Count != 4) throw new InvalidDataException("Native save did not write all four files.");
                    foreach (string name in scope.Files)
                        using (var file = new FileStream(Path.Combine(scope.Stage, name), FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) file.Flush(true);
                    return NativeSaveFingerprint.Capture(scope.Stage);
                }
                finally
                {
                    current = null;
                    try { foreach (var stream in scope.Streams) stream.Dispose(); }
                    finally { DisposeHooks(hooks); }
                }
            }
        }
        private static void DisposeHooks(List<ILHook> hooks)
        {
            for (int i = hooks.Count - 1; i >= 0; i--)
                try { hooks[i].Dispose(); }
                catch (Exception ex) { Debug.LogWarning("MonMP save redirection cleanup: " + ex.Message); }
            hooks.Clear();
        }
        private static NativeSaveFingerprint WriteWithoutHooks(SaveSlot slot, string output, House_Location location, Action<string>? checkpoint)
        {
            if (!string.Equals(Path.GetFullPath(output), Path.GetFullPath(slot.ReserveNativeOutput()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native fallback output differs from its reservation.");
            try
            {
                var methods = new[] { "SavePlayer", "SaveVehicles", "SaveItems", "SaveGarden" };
                var files = new[] { "marguerite.txt", "braaaap.txt", "blopblop.txt", "lyne.txt" };
                for (int i = 0; i < methods.Length; i++)
                {
                    string name = files[i];
                    // A hard stop must never expose all four native filenames under
                    // this numeric reservation. Stage each file before starting the next.
                    object[] args = i == 0 ? new object[] { slot.Timestamp, location } : new object[] { slot.Timestamp };
                    AccessTools.Method(typeof(SaveSystem), methods[i]).Invoke(null, args);
                    checkpoint?.Invoke(methods[i]);
                    string destination = Path.Combine(slot.StagingDirectory, name);
                    if (File.Exists(destination)) File.Move(destination, destination + ".ignored-" + Guid.NewGuid().ToString("N"));
                    File.Move(Path.Combine(output, name), destination);
                    using var file = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    file.Flush(true);
                    checkpoint?.Invoke("Staged:" + methods[i]);
                }
                return NativeSaveFingerprint.Capture(slot.StagingDirectory);
            }
            finally
            {
                // The owner manifest also lets startup recover this reservation when
                // process termination prevents this finally block from running.
                if (Directory.Exists(output))
                    Directory.Move(output, Path.Combine(slot.StagingDirectory, "native-output-" + Guid.NewGuid().ToString("N") + ".ignored"));
            }
        }
        private static void RewriteFileOpen(ILContext il)
        {
            int count = 0;
            foreach (var instruction in il.Body.Instructions)
                if (instruction.OpCode == OpCodes.Newobj && instruction.Operand is MethodReference called &&
                    called.DeclaringType.FullName == typeof(FileStream).FullName && called.Name == ".ctor" && called.Parameters.Count == 2 &&
                    called.Parameters[0].ParameterType.FullName == typeof(string).FullName && called.Parameters[1].ParameterType.FullName == typeof(FileMode).FullName)
                {
                    instruction.OpCode = OpCodes.Call; instruction.Operand = il.Import(AccessTools.Method(typeof(NativeSaveWriter), nameof(OpenFile))); count++;
                }
            if (count != 1) throw new InvalidOperationException("Unsupported native save file-open layout: " + il.Method.Name);
        }
        private static FileStream OpenFile(string nativePath, FileMode mode)
        {
            var scope = current;
            if (scope == null) return new FileStream(nativePath, mode);
            string name = Path.GetFileName(nativePath);
            if (mode != FileMode.Create || (name != "marguerite.txt" && name != "braaaap.txt" && name != "blopblop.txt" && name != "lyne.txt") ||
                !string.Equals(Path.GetFullPath(nativePath), Path.Combine(scope.NativeDirectory, name), StringComparison.OrdinalIgnoreCase) || !scope.Files.Add(name))
                throw new InvalidDataException("Unexpected native save output path.");
            var stream = new FileStream(Path.Combine(scope.Stage, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            scope.Streams.Add(stream); return stream;
        }
    }
}
