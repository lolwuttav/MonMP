using System.Reflection;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // One native shop factory deliberately saves as the ordinary crate. Replicas use that
    // canonical crate plus its existing count/type fields, rather than a second prefab ID.
    internal static class PrefilledSyrupCanCrate
    {
        internal const string FactoryPath = "Pack/MapleSyrupCrateFull";
        internal const string CanonicalPath = "Pack/MapleSyrupCrate";
        private static readonly FieldInfo? Full = typeof(Pack_MapleSyrupCrate).GetField("IsCrateFull", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? Contents = typeof(Pack_MapleSyrupCrate).GetField("IsCrateFull_Type", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static GameObject? CanonicalFactory(GameObject asset)
        {
            if (asset == null || asset.GetComponent<InteractableItems>() is not Pack_MapleSyrupCrate crate ||
                crate.GetType() != typeof(Pack_MapleSyrupCrate) || crate.PrefabName != CanonicalPath ||
                Full?.GetValue(crate) is not true || Contents?.GetValue(crate) is not int kind || kind != 1 ||
                crate.Value1_Max != 12 || asset != Resources.Load<GameObject>(FactoryPath) ||
                asset.GetComponentsInChildren<InteractableItems>(true).Length != 1 ||
                asset.GetComponentsInChildren<Rigidbody>(true).Length > 1 || asset.GetComponent<Collider>() == null ||
                asset.GetComponentsInChildren<MeshRenderer>(true).Length == 0) return null;
            var canonical = Resources.Load<GameObject>(CanonicalPath);
            if (canonical == null || canonical.GetComponent<InteractableItems>() is not Pack_MapleSyrupCrate original ||
                original.GetType() != typeof(Pack_MapleSyrupCrate) || original.PrefabName != CanonicalPath ||
                original.Value1_Max != 12 || Full?.GetValue(original) is not false) return null;
            return canonical;
        }

        // Native Start fills this factory with twelve empty cans. Do not lease it to a guest
        // earlier: the guest could unpack the serialized 10-can/default-type placeholder.
        internal static bool Ready(InteractableItems item) => item is Pack_MapleSyrupCrate crate &&
            crate.GetType() == typeof(Pack_MapleSyrupCrate) && crate.PrefabName == CanonicalPath &&
            crate.Value1_Max == 12 && crate.Value1 == 12 && crate.IntValue1 == 1;
    }
}
