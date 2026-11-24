using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UniStorm;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(UniStormSystem), "Update")]
    internal static class SharedClockUpdatePatch
    {
        private static void Prefix(UniStormSystem __instance)
        {
            MonMPPlugin.Instance?.World?.Clock.BeforeNativeUpdate(__instance);
            MonMPPlugin.Instance?.World?.Weather.BeforeNativeUpdate();
        }
    }
    [HarmonyPatch]
    internal static class SharedClockAdvancePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "AddOneSecond", "AddOneHour", "AddOneDay", "CalculateTimeSlider", "CalculateDays", "HourlyUpdate" })
                yield return AccessTools.Method(typeof(UniStormSystem), name);
            yield return AccessTools.Method(typeof(TickSystem), "Tick");
            yield return AccessTools.Method(typeof(TickSystem), "DayChanged");
        }
        private static bool Prefix() => !Replication.SharedSleep.BlockNativeAdvance &&
            !Replication.GuestDeathTransition.BlockWorldAdvance &&
            MonMPPlugin.Instance?.World?.SaveGate.Frozen != true && MonMPPlugin.Instance?.World?.Clock.BlocksLocalWorldTicks != true;
    }
    [HarmonyPatch(typeof(TickSystem), "Tick")]
    internal static class SharedClockMinutePatch
    {
        private static void Postfix(bool __runOriginal)
        {
            if (__runOriginal) MonMPPlugin.Instance?.World?.Clock.PublishNativeMinute();
        }
    }
    [HarmonyPatch(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.Tick))]
    internal static class SavePersonalTickPatch
    {
        private static bool Prefix(BasicNeeds_Manager __instance, ref bool _isSaving, out Replication.ComputerTimeScope? __state)
        {
            __state = null;
            var plugin = MonMPPlugin.Instance;
            if (plugin?.World?.SaveGate.Frozen == true) return false;
            // UniStorm can emit a native saving minute from Update, outside
            // AddOneTickFromSave's scope. Keep its relief/clamp and attribution
            // active through UpdateUI as well, including exceptional exits.
            if (!Replication.ComputerTimeScope.Active && _isSaving && plugin?.Session?.IsHost == true &&
                !Replication.SharedSleep.AdvancingWorldTime)
                __state = new Replication.ComputerTimeScope(plugin.Session.LocalPeerId);
            if (Replication.ComputerTimeScope.Active)
            {
                Replication.ComputerTimeScope.BeforeNeeds(__instance);
                _isSaving = Replication.ComputerTimeScope.User == plugin?.Session?.LocalPeerId;
                plugin?.World?.Clock.ComputerTick(Replication.ComputerTimeScope.User);
            }
            return true;
        }
        private static System.Exception? Finalizer(System.Exception? __exception, Replication.ComputerTimeScope? __state)
        { __state?.Dispose(); return __exception; }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var update = AccessTools.Method(typeof(BasicNeeds_Manager), "UpdateUI");
            var clamp = AccessTools.Method(typeof(Replication.ComputerTimeScope), nameof(Replication.ComputerTimeScope.ClampNeeds));
            int sites = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(update))
                {
                    // Native already loaded its manager for UpdateUI; preserve that receiver.
                    var originalCall = new CodeInstruction(instruction.opcode, instruction.operand);
                    // Reuse the original instruction so labels and exception boundaries
                    // follow the inserted clamp without referencing Mono's Label type.
                    instruction.opcode = OpCodes.Dup; instruction.operand = null;
                    yield return instruction; yield return new CodeInstruction(OpCodes.Call, clamp);
                    yield return originalCall; sites++; continue;
                }
                yield return instruction;
            }
            if (sites != 2) throw new System.InvalidOperationException("Unsupported native needs tick UI/death ordering.");
        }
    }
    [HarmonyPatch(typeof(Gameplay), "Sleep_Start")]
    internal static class SharedClockSleepPatch
    {
        private static bool Prefix()
        {
            var sleep = MonMPPlugin.Instance?.World?.Sleep;
            if (sleep == null || sleep.CanStart(out string reason)) return true;
            MonMPPlugin.Instance!.ReportItemUseUnavailable(reason); return false;
        }
    }
    [HarmonyPatch(typeof(Gameplay), "Sleeping")]
    internal static class SharedSleepCoroutinePatch
    {
        private static void Postfix(Furniture_Bed _bed, bool _skipTheNight, ref System.Collections.IEnumerator __result)
        {
            var sleep = MonMPPlugin.Instance?.World?.Sleep;
            if (sleep?.Active == true) __result = sleep.Coordinate(__result, _skipTheNight, _bed);
        }
    }

    [HarmonyPatch(typeof(Gameplay), "Death")]
    internal static class GuestDeathCoroutinePatch
    {
        private static void Postfix(ref System.Collections.IEnumerator __result)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning == true && plugin.World != null)
                __result = Replication.GuestDeathTransition.Coordinate(__result, plugin.World, plugin.Session.IsHost);
        }
    }

    [HarmonyPatch(typeof(UniStormSystem), "Coroutine_ThisAmountOfTick")]
    internal static class NativeFastForwardCoroutinePatch
    {
        private static void Postfix(int amount, SpecialMenu _specialMenu, ref System.Collections.IEnumerator __result)
        {
            if (MonMPPlugin.Instance?.Session?.IsRunning == true)
                __result = Replication.NativeFastForwardScope.Coordinate(__result, amount, _specialMenu);
        }
    }
    [HarmonyPatch(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.Fill_Everything))]
    internal static class NativeFastForwardNeedsPatch
    {
        private static void Postfix() => MonMPPlugin.Instance?.World?.Clock.NeedsRefilled();
    }

    [HarmonyPatch]
    internal static class GuestDeathPersonalCompletionPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.Fill_Death));
            yield return AccessTools.Method(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.Fill_PassOut));
        }
        private static bool Prefix() => !Replication.GuestDeathTransition.BlockPersonalCompletion;
    }
}
