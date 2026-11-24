using System.Collections;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Native coroutines the host starts for a guest's request finish after any synchronous scope has
    // ended, so their banners and 2D sounds landed on the host. The whole coroutine carries the scope.
    internal sealed class RemoteCoroutine : IEnumerator
    {
        private readonly IEnumerator inner;
        private readonly Vector3 at;
        private readonly bool quiet;
        public RemoteCoroutine(IEnumerator inner, Vector3 at, bool quiet) { this.inner = inner; this.at = at; this.quiet = quiet; }
        public object Current => inner.Current;
        public bool MoveNext()
        {
            using var messages = quiet ? RemoteActionScope.Enter() : null;
            using var sound = SpatialSounds.At(at);
            return inner.MoveNext();
        }
        public void Reset() => inner.Reset();
    }

    // A guest's post: the "letter posted" banner is the poster's (it shows its own), the sound is at the box.
    [HarmonyPatch(typeof(Store_PostOffice), "MoveEnveloppe")]
    internal static class GuestPostCompletionPatch
    {
        private static void Postfix(Store_PostOffice __instance, ref IEnumerator __result)
        {
            if (SharedHydroMail.PostingForGuest && __result != null) __result = new RemoteCoroutine(__result, __instance.transform.position, true);
        }
    }

    // A guest's cottage purchase: the gate squeaks at the gate, not in the host's ears.
    [HarmonyPatch(typeof(Cottage), "BoughtAnimation")]
    internal static class GuestCottageGatePatch
    {
        private static void Postfix(Cottage __instance, ref IEnumerator __result)
        {
            if (SharedCottagePurchase.PurchasingForGuest && __result != null) __result = new RemoteCoroutine(__result, __instance.transform.position, false);
        }
    }
}
