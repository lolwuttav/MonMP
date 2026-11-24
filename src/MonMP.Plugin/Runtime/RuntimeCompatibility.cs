using System;
using System.IO;
using System.Security.Cryptography;
using MonMP.Core.Session;
using MonMP.Plugin.Diagnostics;

namespace MonMP.Plugin.Runtime
{
    // Release startup guards do not depend on the development report/scene tools.
    internal sealed class RuntimeCompatibility
    {
        internal string GameHash { get; private set; } = "";
        internal bool Compatible { get; private set; }
        internal string Status { get; private set; } = "Not checked";
        internal void Validate()
        {
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(typeof(Gameplay).Assembly.Location))
                GameHash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            foreach (var method in PatchHealth.Required()) { _ = method; }
            PatchHealth.ValidateFields();
            Compatible = GameHash == SessionOptions.SupportedGameHash;
            Status = Compatible ? "Supported game build; patch targets verified" : "Unsupported build or missing patch targets; sessions disabled";
        }
    }
}
