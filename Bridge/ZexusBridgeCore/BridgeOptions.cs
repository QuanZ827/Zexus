using System;
using System.IO;

namespace Zexus.Bridge
{
    /// <summary>Configuration for the local HTTP bridge.</summary>
    public sealed class BridgeOptions
    {
        /// <summary>Only loopback binding is allowed. Anything else makes Start() fail.</summary>
        public string BindHost { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 4821;

        public int PortFallbackCount { get; set; } = 10;

        public string TokenFile { get; set; }

        public string AuditLogPath { get; set; }

        public int MaxQueuedRequests { get; set; } = 50;

        public int DefaultTimeoutSeconds { get; set; } = 30;

        public int MaxTimeoutSeconds { get; set; } = 300;

        public int ConfirmationTimeoutSeconds { get; set; } = 120;

        public int WatchdogIntervalMs { get; set; } = 1000;

        public int MaxBodyBytes { get; set; } = 1024 * 1024;

        public static BridgeOptions CreateDefault()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var bridgeDir = Path.Combine(appData, "Zexus", "bridge");

            var options = new BridgeOptions();
            options.TokenFile = Path.Combine(bridgeDir, "token.txt");
            options.AuditLogPath = Path.Combine(bridgeDir, "bridge-audit.log");

            var envPort = Environment.GetEnvironmentVariable("ZEXUS_BRIDGE_PORT");
            if (int.TryParse(envPort, out int port) && port > 0 && port < 65536)
                options.Port = port;

            var envTokenFile = Environment.GetEnvironmentVariable("ZEXUS_BRIDGE_TOKEN_FILE");
            if (!string.IsNullOrEmpty(envTokenFile)) options.TokenFile = envTokenFile;

            var envAudit = Environment.GetEnvironmentVariable("ZEXUS_BRIDGE_AUDIT_LOG");
            if (!string.IsNullOrEmpty(envAudit)) options.AuditLogPath = envAudit;

            return options;
        }
    }
}
