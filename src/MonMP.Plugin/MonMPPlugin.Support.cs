using System;
using System.Globalization;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Plugin.Runtime;
using UnityEngine;

namespace MonMP.Plugin
{
    public sealed partial class MonMPPlugin
    {
        private readonly SupportLog supportLog = new SupportLog();
        private bool supportListening;
        private string supportHeading = "", lastDisconnectReport = "";
        // A problem that never disconnects anyone (silent CD music, for example)
        // still needs a report, so a running session copies the live log rather
        // than the snapshot frozen when that session started.
        internal bool MenuHasErrorReport => lastDisconnectReport.Length != 0 || Session?.IsRunning == true;
        internal string MenuErrorReport
        {
            get
            {
                var running = Session;
                if (running?.IsRunning != true) return lastDisconnectReport;
                return supportLog.Report("MonMP " + SessionOptions.CurrentModVersion + " | wire " + Wire.Version +
                    "\nUTC: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                    "\nSession running: " + (running.IsHost ? "host" : "guest") + "; " + running.TransportDescription +
                    "\nRecent warnings/errors and MonMP context:");
            }
        }
        private void StartSupportCapture()
        {
            if (supportListening) return;
            BepInEx.Logging.Logger.Listeners.Add(supportLog);
            Application.logMessageReceivedThreaded += CaptureUnityError;
            supportListening = true;
        }
        private void StopSupportCapture()
        {
            if (supportListening)
            {
                BepInEx.Logging.Logger.Listeners.Remove(supportLog);
                Application.logMessageReceivedThreaded -= CaptureUnityError;
                supportListening = false;
            }
            supportLog.Dispose();
        }
        private void CaptureUnityError(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                supportLog.Add("[Unity " + type + "] " + message + "\n" + stack);
        }
        private void BeginDisconnectReport(string reason)
        {
            supportHeading = "MonMP " + SessionOptions.CurrentModVersion + " | wire " + Wire.Version +
                "\nUTC: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                "\nDisconnect: " + reason + "\nRecent warnings/errors and MonMP context:";
        }
        private void FinishDisconnectReport() => lastDisconnectReport = supportLog.Report(supportHeading);
    }
}
