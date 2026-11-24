using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Preserve the original native iterator: its inputs have already been charged and its
    // selected slot result / tire are private iterator locals. Starting a new operation would
    // debit or pay twice. Only the supported build's finite production factories are retained.
    internal static class NativeSaveProduction
    {
        private sealed class Operation
        {
            internal IEnumerator Routine = null!;
            internal FieldInfo[] Timers = Array.Empty<FieldInfo>();
            internal FieldInfo? State;
            internal bool Supported, Failed;
        }
        private sealed class OwnerOperations
        {
            internal readonly Dictionary<string, List<Operation>> Values = new Dictionary<string, List<Operation>>(StringComparer.Ordinal);
        }
        private sealed class StepDescriptor
        {
            internal string Method = "";
            internal FieldInfo Owner = null!, State = null!;
            internal MethodInfo Step = null!;
        }
        private static readonly ConditionalWeakTable<MonoBehaviour, OwnerOperations> operations = new ConditionalWeakTable<MonoBehaviour, OwnerOperations>();
        // Metadata only: never retain scene owners or coroutine instances in a global cache.
        private static readonly Dictionary<Type, StepDescriptor> steps = StepDescriptors();
        private const int MaximumSteps = 4096;

        internal static IEnumerable<MethodInfo> Factories()
        {
            foreach (var pair in SupportedFactories())
            {
                var method = pair.Type.GetMethod(pair.Method, BindingFlags.Instance | BindingFlags.NonPublic);
                if (method == null || method.ReturnType != typeof(IEnumerator))
                    throw new MissingMethodException(pair.Type.FullName, pair.Method);
                yield return method;
            }
        }

        internal static IEnumerable<MethodInfo> Steps()
        {
            foreach (var descriptor in steps.Values) yield return descriptor.Step;
        }

        private static Dictionary<Type, StepDescriptor> StepDescriptors()
        {
            var result = new Dictionary<Type, StepDescriptor>();
            foreach (var factory in Factories())
            {
                var owner = factory.DeclaringType!;
                var matches = owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(type =>
                    type.Name.StartsWith("<" + factory.Name + ">d__", StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1 || !typeof(IEnumerator).IsAssignableFrom(matches[0]))
                    throw new InvalidOperationException("Unsupported native production iterator: " + owner.Name + "." + factory.Name);
                var iterator = matches[0];
                var ownerField = iterator.GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var state = iterator.GetField("<>1__state", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var step = iterator.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (ownerField?.FieldType != owner || state?.FieldType != typeof(int) || step == null ||
                    step.IsStatic || step.ReturnType != typeof(bool) || step.DeclaringType != iterator)
                    throw new InvalidOperationException("Unsupported native production step: " + owner.Name + "." + factory.Name);
                result.Add(iterator, new StepDescriptor { Method = factory.Name, Owner = ownerField, State = state, Step = step });
            }
            return result;
        }

        // Mono can inline a tiny iterator factory and bypass its Harmony postfix. MoveNext
        // still observes the exact native iterator; resolve its cached reference field once
        // per step and return before reflection scans/allocations when already retained.
        internal static void TrackStep(IEnumerator routine)
        {
            if (routine == null || !steps.TryGetValue(routine.GetType(), out var descriptor) ||
                !(descriptor.Owner.GetValue(routine) is MonoBehaviour owner) || owner == null) return;
            Track(owner, descriptor.Method, routine);
        }

        private static IEnumerable<(Type Type, string Method, string[] Timers)> SupportedFactories()
        {
            yield return (typeof(Item_CoffeePercolator), "BrewingCoffee", new[] { "timer" });
            yield return (typeof(Furniture_TireChanger_Manual), "MachineDeRim", Array.Empty<string>());
            yield return (typeof(Furniture_TireChanger_Manual), "MachineRim", new[] { "Timer_MoveHandle", "Timer" });
            yield return (typeof(SlotMachine), "Coroutine_Spin", new[] { "timerSpin", "timer_Jackpot" });
            yield return (typeof(Furniture_CannabisGrinder), "BuildPack", new[] { "Timer" });
            yield return (typeof(Furniture_CannabisPackager), "BuildPack", new[] { "Timer" });
            yield return (typeof(Furniture_FrenchFryCutter), "BuildPack", new[] { "Timer" });
            yield return (typeof(Furniture_FrenchFryPackager), "BuildPack", new[] { "Timer" });
            yield return (typeof(SugarShack_CrimpingMachine), "Working", new[] { "Timer" });
            yield return (typeof(Home_Bunker), "BuildCannabisPack", new[] { "Timer" });
            yield return (typeof(Home_Bunker), "BuildFrenchFriesPack", new[] { "Timer" });
            yield return (typeof(Bunker_CompostMachine), "BuildFertilizer", new[] { "Timer" });
            yield return (typeof(Home_Bunker), "Coroutine_OutdoorPlanter_Bought", Array.Empty<string>());
        }

        internal static void Track(MonoBehaviour owner, string method, IEnumerator routine)
        {
            if (owner == null || routine == null) return;
            if (operations.TryGetValue(owner, out var existing) && existing.Values.TryGetValue(method, out var active))
                for (int index = 0; index < active.Count; index++)
                    if (ReferenceEquals(active[index].Routine, routine)) return;
            TrackNew(owner, method, routine);
        }

        // Keep captured slow-path predicates outside Track: the compiler must not allocate
        // their closure before the already-retained per-step fast path can return.
        private static void TrackNew(MonoBehaviour owner, string method, IEnumerator routine)
        {
            // A completed retained iterator may receive one final scheduler probe after a
            // save drained it. Never register that old operation as newly paid work.
            if (steps.TryGetValue(routine.GetType(), out var descriptor) && descriptor.Method == method &&
                (int)descriptor.State.GetValue(routine)! == -1) return;
            var known = SupportedFactories().FirstOrDefault(pair => pair.Type == owner.GetType() && pair.Method == method);
            if (known.Type == null) return;
            var iteratorType = routine.GetType();
            var operation = new Operation { Routine = routine };
            // The generated layout is part of the supported native build. Never touch arbitrary
            // yielded iterators or another mod's replacement when the expected locals differ.
            if (iteratorType.DeclaringType == known.Type && iteratorType.Name.StartsWith("<" + method + ">d__", StringComparison.Ordinal))
            {
                var fields = iteratorType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                operation.State = fields.SingleOrDefault(field => field.FieldType == typeof(int) && field.Name == "<>1__state");
                var timers = new List<FieldInfo>();
                foreach (string local in known.Timers)
                {
                    var matches = fields.Where(field => field.FieldType == typeof(float) &&
                        field.Name.StartsWith("<" + local + ">5__", StringComparison.Ordinal)).ToArray();
                    if (matches.Length != 1) break;
                    timers.Add(matches[0]);
                }
                operation.Timers = timers.ToArray();
                operation.Supported = operation.State != null && timers.Count == known.Timers.Length;
            }
            // A later registration must not discard an earlier paid/uncertain operation.
            // Native guards normally limit overlap; bunker machines/planter packs can overlap.
            var retained = operations.GetValue(owner, _ => new OwnerOperations());
            foreach (var previous in retained.Values.Values) previous.RemoveAll(Completed);
            if (!retained.Values.TryGetValue(method, out var pending)) retained.Values[method] = pending = new List<Operation>();
            pending.Add(operation);
        }

        private static bool Completed(Operation operation) => !operation.Failed && operation.State != null &&
            (int)operation.State.GetValue(operation.Routine)! == -1;

        internal static bool Pending(MonoBehaviour owner, string method) => owner != null &&
            operations.TryGetValue(owner, out var retained) && retained.Values.TryGetValue(method, out var pending) &&
            pending.Any(operation => !Completed(operation));

        // Teardown may abandon guest-only presentation work. Stop only the retained native
        // iterators for this operation, leaving every unrelated coroutine untouched.
        internal static bool Cancel(MonoBehaviour owner, string method)
        {
            if (owner == null || !operations.TryGetValue(owner, out var retained) ||
                !retained.Values.TryGetValue(method, out var pending) || pending.Count == 0) return false;
            try
            {
                foreach (var operation in pending) owner.StopCoroutine(operation.Routine);
                retained.Values.Remove(method);
                return true;
            }
            catch (Exception ex) { Debug.LogWarning("MonMP could not cancel native presentation: " + ex.Message); return false; }
        }

        internal static bool Finish(MonoBehaviour owner, string method)
        {
            if (owner == null || !operations.TryGetValue(owner, out var retained) ||
                !retained.Values.TryGetValue(method, out var pending) || pending.Count == 0) return false;
            bool complete = true;
            foreach (var operation in pending.ToArray())
            {
                if (Completed(operation)) { pending.Remove(operation); continue; }
                if (!Finish(owner, operation)) { complete = false; continue; }
                pending.Remove(operation);
            }
            if (pending.Count == 0) retained.Values.Remove(method);
            return complete;
        }

        private static bool Finish(MonoBehaviour owner, Operation operation)
        {
            if (!operation.Supported || operation.Failed) return false;
            try
            {
                // Stop the exact IEnumerator passed to Unity, so it cannot subsequently execute
                // an output or payout a second time. No unrelated coroutine is cancelled.
                owner.StopCoroutine(operation.Routine);
                for (int step = 0; step < MaximumSteps; step++)
                {
                    // Every supported native loop yields once per increment. Expire only its
                    // known elapsed-time locals; waits are presentation delays, not new actions.
                    foreach (var timer in operation.Timers) timer.SetValue(operation.Routine, 1000000f);
                    if (!operation.Routine.MoveNext())
                    {
                        return true;
                    }
                    if (operation.Routine.Current != null && operation.Routine.Current is not WaitForSeconds &&
                        operation.Routine.Current is not WaitForEndOfFrame)
                    {
                        // A changed factory introduced a dependency. Resume that yielded
                        // dependency first, and leave this operation to the normal scheduler.
                        operation.Supported = false;
                        owner.StartCoroutine(ResumeIncludingCurrent(operation.Routine));
                        return false;
                    }
                }
                // If the supported shape ever changes, resume this same iterator rather than
                // cancelling its paid work or starting a fresh factory after the bounded pass.
                owner.StartCoroutine(operation.Routine);
                return false;
            }
            catch (Exception ex)
            {
                // An exception can follow an output mutation. Do not restart or replay it.
                operation.Failed = true;
                Debug.LogWarning("MonMP could not finish native production for save: " + owner.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static IEnumerator ResumeIncludingCurrent(IEnumerator routine)
        {
            yield return routine.Current;
            while (routine.MoveNext()) yield return routine.Current;
        }
    }
}
