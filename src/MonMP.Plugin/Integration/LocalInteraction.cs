using System;
using UnityEngine;

namespace MonMP.Plugin.Integration
{
    internal interface ILocalInteractionProvider
    {
        bool OwnsView { get; }
        bool BlocksInput { get; }
        bool IndependentAim { get; }
        bool SelectsOperatedItem { get; }
        bool TryRay(Camera camera, out Ray ray);
        bool TryHead(out float yaw, out float pitch);
        int PickupIntent(int instance);
        IDisposable? OpenPickup(int instance);
    }

    internal readonly struct ToolAimSample
    {
        internal readonly Ray Ray;
        internal readonly Vector3 Origin;
        internal readonly float Yaw, Pitch;
        internal readonly bool Independent;
        internal ToolAimSample(Ray ray, Vector3 origin, float yaw, float pitch, bool independent)
        { Ray = ray; Origin = origin; Yaw = yaw; Pitch = pitch; Independent = independent; }
    }

    // A pickup remembers its presenter until the corresponding grant or cancellation.
    // A replacement provider cannot materialize a ticket issued for the previous owner.
    internal readonly struct PickupPresentation
    {
        private readonly ILocalInteractionProvider? owner;
        private readonly int instance;
        internal PickupPresentation(ILocalInteractionProvider owner, int instance)
        { this.owner = owner; this.instance = instance; }

        internal int Intent => owner == null ? 0 :
            ReferenceEquals(owner, LocalInteraction.Provider) && owner.PickupIntent(instance) == 1 ? 1 : -1;
        internal bool TryBeginGrant(int grantedInstance, bool occupied, out IDisposable? scope)
        {
            scope = null;
            if (owner == null) return true;
            if (grantedInstance != instance || Intent != 1) return false;
            if (!occupied) return true;
            scope = owner.OpenPickup(instance);
            return scope != null;
        }
    }

    internal static class LocalInteraction
    {
        internal static ILocalInteractionProvider? Provider { get; set; }
        internal static bool ExternalView => Provider?.OwnsView == true;
        internal static bool BlocksInput => Provider?.BlocksInput == true;
        internal static bool IndependentAim => Provider?.IndependentAim == true;
        internal static bool SelectsOperatedItem => Provider?.SelectsOperatedItem == true;
        internal static bool TryHead(out float yaw, out float pitch)
        {
            yaw = pitch = 0;
            return Provider?.TryHead(out yaw, out pitch) == true;
        }

        internal static PickupPresentation CapturePickup(int instance)
        {
            var provider = Provider;
            return provider != null && provider.PickupIntent(instance) != 0 ? new PickupPresentation(provider, instance) : default;
        }

        internal static Ray SelectionRay(Camera camera) =>
            Provider?.TryRay(camera, out var ray) == true ? ray : CenterRay(camera);

        private static Ray CenterRay(Camera camera) => camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));

        // The desktop hit test uses the near plane. Its wire origin is the native eye.
        // For an independent ray both come from the same accepted sample.
        internal static ToolAimSample SampleTool(Camera camera)
        {
            var provider = Provider;
            if (provider?.IndependentAim == true && provider.TryRay(camera, out var ray))
            {
                var d = ray.direction;
                float yaw = Mathf.Abs(d.x) < 1e-6f && Mathf.Abs(d.z) < 1e-6f ? 0 :
                    Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360f);
                float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg, -90f, 90f);
                return new ToolAimSample(ray, ray.origin, yaw >= 360f ? 0 : yaw, pitch, true);
            }
            var angles = camera.transform.eulerAngles;
            return new ToolAimSample(CenterRay(camera), camera.transform.position, Mathf.Repeat(angles.y, 360f),
                Mathf.DeltaAngle(0, angles.x), false);
        }
    }
}
