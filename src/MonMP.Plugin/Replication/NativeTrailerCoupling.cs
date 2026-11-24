using System;
using System.Collections.Generic;
using HarmonyLib;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Modules.Trailer;

namespace MonMP.Plugin.Replication
{
    internal static class NativeTrailerCoupling
    {
        private static readonly System.Reflection.FieldInfo Candidates =
            AccessTools.Field(typeof(TrailerHitchModule), "_trailerModules");

        // NWH selects the first nearby scene trailer, not necessarily the one in
        // the accepted host topology. Scope only that candidate list; leave all
        // native distance, hitch-installation, joint and event logic in place.
        internal static void AttachExpected(TrailerHitchModule hitch, TrailerModule expected, VehicleController tow)
        {
            object? previous = Candidates.GetValue(hitch);
            var scoped = new List<TrailerModule> { expected };
            Candidates.SetValue(hitch, scoped);
            try { hitch.AttachTrailer(tow); }
            finally
            {
                if (ReferenceEquals(Candidates.GetValue(hitch), scoped)) Candidates.SetValue(hitch, previous);
            }
        }
    }
}
