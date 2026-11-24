#if MONMP_DEVELOPMENT
using MonMP.Core.Transport;
using MonMP.Plugin.Diagnostics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MonMP.Plugin
{
    public sealed partial class MonMPPlugin
    {
        private readonly NetworkLatencySettings latencySimulation = new NetworkLatencySettings();
        private LatencySimulatedTransport? latencyTransport;
        private bool latencySimulationExpanded;

        private INetworkTransport DebugSessionTransport(INetworkTransport transport) =>
            latencyTransport = LatencySimulatedTransport.Wrap(transport, latencySimulation);

        private void LatencySimulationKeys()
        {
            var keyboard = Keyboard.current;
            if (DebugOverlay && !SocialUiBlocked && Session?.IsRunning == true && keyboard?.f10Key.wasPressedThisFrame == true &&
                (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed)) latencySimulation.Hitch(1000);
        }
        private void DrawLatencySimulation(GUIStyle label)
        {
            if (GUILayout.Button("Latency simulator (debug only)" + (latencySimulation.Enabled ? " — ACTIVE" : "")))
                latencySimulationExpanded = !latencySimulationExpanded;
            if (!latencySimulationExpanded) return;
            latencySimulation.Enabled = GUILayout.Toggle(latencySimulation.Enabled, "Simulate latency on this copy (Steam or Direct)");
            GUILayout.Label("Added one-way delay: " + latencySimulation.DelayMilliseconds + " ms; jitter ±" + latencySimulation.JitterMilliseconds + " ms", label);
            latencySimulation.DelayMilliseconds = Mathf.RoundToInt(GUILayout.HorizontalSlider(latencySimulation.DelayMilliseconds, 0, 2000));
            GUILayout.BeginHorizontal();
            foreach (int value in new[] { 0, 50, 100, 250, 500 })
                if (GUILayout.Button(value + " ms")) { latencySimulation.DelayMilliseconds = value; latencySimulation.Enabled = true; }
            GUILayout.EndHorizontal();
            latencySimulation.JitterMilliseconds = Mathf.RoundToInt(GUILayout.HorizontalSlider(latencySimulation.JitterMilliseconds, 0, 500));
            GUILayout.BeginHorizontal();
            bool previous = GUI.enabled; GUI.enabled = previous && Session?.IsRunning == true;
            if (GUILayout.Button("1 s traffic hitch (Ctrl+F10)")) latencySimulation.Hitch(1000);
            if (GUILayout.Button("2 s hitch")) latencySimulation.Hitch(2000);
            GUI.enabled = previous;
            if (GUILayout.Button("Off / reset")) latencySimulation.Reset();
            GUILayout.EndHorizontal();
            GUILayout.Label("Added RTT ≈ twice this delay per simulated copy. Native ping is unchanged. Off flushes queued messages in order.", label);
            GUILayout.Label("Queued: " + (latencyTransport?.QueuedPackets ?? 0) + " messages / " + (latencyTransport?.QueuedBytes ?? 0) +
                " bytes | overflow disconnects: " + (latencyTransport?.OverflowDisconnects ?? 0), label);
        }
    }
}
#endif
