using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using HarmonyLib;
using MonMP.Core.Testing;
using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace MonMP.Plugin.Testing
{
    internal static class LocalTestIsolation
    {
        private static readonly Dictionary<MethodInfo, MethodInfo> Replacements = new Dictionary<MethodInfo, MethodInfo>();
        private static readonly List<ILHook> Hooks = new List<ILHook>();
        // BepInEx 5's Harmony assembly exposes its optional ILGenerator using the
        // old mscorlib identity. Reflection avoids a netstandard reference mismatch.
        private static readonly MethodInfo ReadInstructions = typeof(PatchProcessor).GetMethods().Single(m =>
            m.Name == nameof(PatchProcessor.GetOriginalInstructions) && m.GetParameters().Length == 2 && !m.GetParameters()[1].ParameterType.IsByRef);
        private static IsolatedPreferences prefs = null!;
        internal static string SaveRoot { get; private set; } = "";
        internal static string DataRoot { get; private set; } = "";
        internal static bool Installed { get; private set; }
        internal static int PatchedMethods { get; private set; }
        // CVO reads PlayerPrefs from its own assembly, outside the game's patched
        // call sites. This test-only opt-in redirects those calls before world load.
        private static bool cvoRequired, cvoReady, cvoFailed;
        private static Assembly? cvoAssembly;
        internal static bool CvoWorldLoadAllowed => !cvoRequired || cvoReady && !cvoFailed;

        internal static void Install()
        {
            DataRoot = Path.Combine(Paths.BepInExRootPath, "MonMP", "local-test");
            SaveRoot = Path.Combine(DataRoot, "saves");
            Directory.CreateDirectory(SaveRoot);
            prefs = new IsolatedPreferences(Path.Combine(DataRoot, "preferences.xml"));
            foreach (var method in typeof(PlayerPrefs).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var replacement = typeof(LocalTestIsolation).GetMethod(method.Name, BindingFlags.Public | BindingFlags.Static,
                    null, method.GetParameters().Select(p => p.ParameterType).ToArray(), null);
                if (replacement != null) Replacements.Add(method, replacement);
            }
            Replacements.Add(AccessTools.PropertyGetter(typeof(Application), nameof(Application.persistentDataPath)),
                AccessTools.Method(typeof(LocalTestIsolation), nameof(PersistentDataPath)));

            // Inspect managed game IL, including generated coroutine methods and constructors.
            // Rewrite call sites; never detour Unity's native/internal-call implementations.
            var targets = new List<MethodBase>();
            foreach (var type in typeof(Gameplay).Assembly.GetTypes())
            {
                if (type.ContainsGenericParameters) continue;
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                    .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
                foreach (var method in methods)
                {
                    if (method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                    var instructions = (IEnumerable<CodeInstruction>)ReadInstructions.Invoke(null, new object?[] { method, null });
                    if (instructions.Any(i => i.operand is MethodInfo called && Replacements.ContainsKey(called))) targets.Add(method);
                }
            }
            if (targets.Count == 0) throw new InvalidOperationException("No game data access sites found; refusing unisolated test launch.");
            // Preserve Cecil exception regions and leave targets. The bundled HarmonyX
            // transpiler round trip can redirect a successful try/catch return to null.
            foreach (var method in targets) Hooks.Add(new ILHook(method, RewriteCalls));
            PatchedMethods = targets.Count;
            Hooks.Add(new ILHook(AccessTools.Method(typeof(LocalTestIsolation), nameof(ControlFlowProbe)), RewriteCalls));
            const string controlKey = "MonMP.ControlFlowProbe";
            SetInt(controlKey, 731);
            try
            {
                if (ControlFlowProbe(false) != 731 || ControlFlowProbe(true) != -1)
                    throw new InvalidOperationException("Save isolation altered exception control flow.");
            }
            finally { DeleteKey(controlKey); }
            // Exercise an actual patched game method against an isolated directory before proceeding.
            string sentinel = "MonMPIsolation" + Guid.NewGuid().ToString("N");
            string sentinelPath = Path.Combine(SaveRoot, sentinel);
            Directory.CreateDirectory(sentinelPath);
            try
            {
                if (!SaveSystem.CheckIfSaveAlreadyExist(sentinel)) throw new InvalidOperationException("Game save path isolation check failed.");
            }
            finally { Directory.Delete(sentinelPath); }
            Installed = true;
            File.WriteAllText(Path.Combine(DataRoot, "isolation-report.txt"), "MONMP_ISOLATION_PASS\nPatched managed methods: " + PatchedMethods +
                "\nCecil call-site rewrite; normal and exceptional control-flow probes passed.\nSaves: " + SaveRoot + "\nPreferences: " + Path.Combine(DataRoot, "preferences.xml"));
            cvoRequired = Environment.GetCommandLineArgs().Contains("--monmp-cvo-test-isolation");
            if (cvoRequired)
            {
                AppDomain.CurrentDomain.AssemblyLoad += CvoLoaded;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) InstallCvoIsolation(assembly);
            }
        }

        private static void CvoLoaded(object sender, AssemblyLoadEventArgs args) => InstallCvoIsolation(args.LoadedAssembly);

        private static void InstallCvoIsolation(Assembly assembly)
        {
            if (!cvoRequired || assembly.GetName().Name != "CatastrophicVehicleOverhaul") return;
            try
            {
                string expected = Path.GetFullPath(Path.Combine(Paths.PluginPath, "CatastrophicVehicleOverhaul", "CatastrophicVehicleOverhaul.dll"));
                if (!string.Equals(Path.GetFullPath(assembly.Location), expected, StringComparison.OrdinalIgnoreCase) ||
                    (cvoAssembly != null && !ReferenceEquals(cvoAssembly, assembly)))
                    throw new InvalidOperationException("Unexpected or duplicate overhaul assembly.");
                if (ReferenceEquals(cvoAssembly, assembly)) return;
                var expectedMethods = new HashSet<string>(StringComparer.Ordinal) {
                    "CatastrophicVehicleOverhaul.KaliGasTowingServiceSystem.SaveCompletedPostfix",
                    "CatastrophicVehicleOverhaul.KaliGasTowingServiceSystem.PersistPendingTarget",
                    "CatastrophicVehicleOverhaul.KaliGasTowingServiceSystem.GetPendingTargetId",
                    "CatastrophicVehicleOverhaul.KaliGasTowingServiceSystem.ClearPendingTarget",
                    "CatastrophicVehicleOverhaul.Systems.ConditionPersistence.GetSavePath" };
                var targets = new List<MethodBase>();
                foreach (var type in assembly.GetTypes())
                {
                    if (type.ContainsGenericParameters) continue;
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                        .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
                    foreach (var method in methods)
                    {
                        if (method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                        var instructions = (IEnumerable<CodeInstruction>)ReadInstructions.Invoke(null, new object?[] { method, null });
                        if (!instructions.Any(i => i.operand is MethodInfo called && Replacements.ContainsKey(called))) continue;
                        targets.Add(method); expectedMethods.Remove(type.FullName + "." + method.Name);
                    }
                }
                if (expectedMethods.Count != 0) throw new InvalidOperationException("Overhaul preference targets missing: " + string.Join(", ", expectedMethods));
                foreach (var method in targets) Hooks.Add(new ILHook(method, RewriteCalls));
                cvoAssembly = assembly; cvoReady = true;
                File.AppendAllText(Path.Combine(DataRoot, "isolation-report.txt"), "\nCVO_PREFS_ISOLATION_PASS methods=" + targets.Count);
                Debug.Log("CVO_PREFS_ISOLATION_PASS: isolated " + targets.Count + " overhaul preference methods.");
            }
            catch (Exception ex)
            {
                cvoFailed = true; cvoReady = false;
                Debug.LogError("MonMP local test: overhaul preference isolation failed; world loading is blocked: " + ex.GetBaseException().Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ControlFlowProbe(bool fail)
        {
            if (PlayerPrefs.HasKey("MonMP.ControlFlowProbe"))
            {
                try { if (fail) throw new InvalidOperationException(); return PlayerPrefs.GetInt("MonMP.ControlFlowProbe"); }
                catch { return -1; }
            }
            return -2;
        }
        private static void RewriteCalls(ILContext il)
        {
            var replacements = Replacements.ToDictionary(p => il.Import(p.Key).FullName, p => p.Value);
            foreach (var instruction in il.Body.Instructions)
                if (instruction.Operand is MethodReference called && replacements.TryGetValue(called.FullName, out var replacement))
                { instruction.OpCode = Mono.Cecil.Cil.OpCodes.Call; instruction.Operand = il.Import(replacement); }
        }
        public static string PersistentDataPath() => SaveRoot;
        public static bool HasKey(string key) => prefs.HasKey(key);
        public static string GetString(string key) => GetString(key, "");
        public static string GetString(string key, string fallback) => prefs.Get(key, fallback);
        public static int GetInt(string key) => GetInt(key, 0);
        public static int GetInt(string key, int fallback) => prefs.Get(key, fallback);
        public static float GetFloat(string key) => GetFloat(key, 0f);
        public static float GetFloat(string key, float fallback) => prefs.Get(key, fallback);
        public static void SetString(string key, string value) => prefs.Set(key, value);
        public static void SetInt(string key, int value) => prefs.Set(key, value);
        public static void SetFloat(string key, float value) => prefs.Set(key, value);
        public static void DeleteKey(string key) => prefs.DeleteKey(key);
        public static void DeleteAll() => prefs.DeleteAll();
        public static void Save() => prefs?.Save();
    }

    // Prevent manual or remote world loading if the requested third-party assembly
    // was missing, duplicated, or its preference isolation failed to install.
    [HarmonyPatch(typeof(MainMenu_Manager), "StartGame", new[] { typeof(bool) })]
    internal static class CvoIsolationWorldLoadGate
    {
        private static bool Prefix()
        {
            if (LocalTestIsolation.CvoWorldLoadAllowed) return true;
            Debug.LogError("MonMP local test: world loading blocked until overhaul preference isolation is ready.");
            return false;
        }
    }
}
