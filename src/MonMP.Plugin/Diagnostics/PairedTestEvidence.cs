using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Plugin.Testing;
using Newtonsoft.Json;

namespace MonMP.Plugin.Diagnostics
{
    // Present only in development builds. A text PASS from an older run is not acceptance.
    internal sealed class PairedTestEvidence
    {
        private readonly string path, runId, suite, instance, pluginHash, coreHash;
        private readonly DateTime started = DateTime.UtcNow;
        private DateTime? passed;
        private readonly Stopwatch settle = new Stopwatch();

        internal PairedTestEvidence(MonMPPlugin plugin, string directory)
        {
            path = Path.Combine(directory, "paired-result.json");
            runId = Argument("--monmp-paired-run-id=") ?? Guid.NewGuid().ToString("N");
            suite = Argument("--monmp-paired-suite=") ?? "unspecified";
            instance = plugin.TestOptions.Instance;
            pluginHash = Hash(typeof(MonMPPlugin).Assembly.Location);
            coreHash = Hash(typeof(Wire).Assembly.Location);
            Write("running", "", 0);
        }

        internal void PassedChecks()
        {
            passed = DateTime.UtcNow;
            settle.Restart();
        }
        internal bool HasSettled(float seconds) => passed.HasValue && settle.Elapsed.TotalSeconds >= seconds &&
            (DateTime.UtcNow - passed.Value).TotalSeconds >= seconds;
        internal void Complete(string evidence, float settleSeconds) => Write("passed", evidence, settleSeconds);
        internal void Fail(string reason) => Write("failed", reason, 0);

        private void Write(string status, string evidence, float settleSeconds)
        {
            var record = new
            {
                schema = 1, status, instance, suite, runId,
                preownedPurchasedTruck = Environment.GetCommandLineArgs().Contains("--monmp-preowned-truck-windows"),
                version = SessionOptions.CurrentModVersion, wire = Wire.Version,
                pluginSha256 = pluginHash, coreSha256 = coreHash,
                startedUtc = started.ToString("o"), passedUtc = passed?.ToString("o"),
                completedUtc = DateTime.UtcNow.ToString("o"),
                isolation = LocalTestIsolation.Installed && Directory.Exists(LocalTestIsolation.SaveRoot),
                saveRoot = LocalTestIsolation.SaveRoot, settleSeconds, evidence,
                failures = status == "failed" ? new[] { evidence } : Array.Empty<string>(),
                transport = "Direct IP / loopback",
                acceptance = "Only this native paired fixture and its recorded assertions; not every audit trigger."
            };
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(record, Formatting.Indented));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static string? Argument(string prefix) => Environment.GetCommandLineArgs()
            .SingleOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length);

        private static string Hash(string file)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(file);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
