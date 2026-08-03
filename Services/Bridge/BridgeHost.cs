using System;
using Zexus.Bridge;

namespace Zexus.Services.Bridge
{
    /// <summary>Owns the bridge lifecycle. Started from App.OnStartup, stopped on shutdown.</summary>
    public static class BridgeHost
    {
        private static readonly object _lock = new object();
        private static RevitBridgeServer _server;
        private static RevitBridgeExecutor _executor;

        public static bool IsRunning => _server?.IsRunning == true;
        public static int Port => _server?.Port ?? 0;

        public static bool Start()
        {
            lock (_lock)
            {
                if (_server?.IsRunning == true) return true;

                try
                {
                    BridgeApprovalSettings.Reset();
                    var options = BridgeOptions.CreateDefault();
                    _executor = new RevitBridgeExecutor();
                    _server = new RevitBridgeServer(_executor, options);

                    var ok = _server.Start();
                    if (ok)
                    {
                        ZexusLogger.Info($"[Bridge] started on http://127.0.0.1:{_server.Port}, token file: {options.TokenFile}");
                    }
                    else
                    {
                        ZexusLogger.Error("[Bridge] failed to start (see bridge audit log)");
                    }
                    return ok;
                }
                catch (Exception ex)
                {
                    ZexusLogger.Error("[Bridge] start exception: " + ex.Message);
                    return false;
                }
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                try
                {
                    _server?.Stop();
                }
                catch (Exception ex)
                {
                    ZexusLogger.Warn("[Bridge] stop error: " + ex.Message);
                }
                _server = null;
                _executor = null;
                BridgeApprovalSettings.Reset();
            }
        }

        public static string StatusSummary()
        {
            if (_server == null || !_server.IsRunning)
                return "Bridge is not running. Restart Revit or check %APPDATA%\\Zexus\\bridge\\bridge-audit.log";

            return "Bridge is running on http://127.0.0.1:" + _server.Port +
                   "\r\nApproval mode: " + BridgeApprovalSettings.Mode +
                   "\r\nToken file: %APPDATA%\\Zexus\\bridge\\token.txt" +
                   "\r\nAudit log: %APPDATA%\\Zexus\\bridge\\bridge-audit.log";
        }
    }
}
