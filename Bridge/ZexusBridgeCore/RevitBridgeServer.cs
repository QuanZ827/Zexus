using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Zexus.Bridge
{
    /// <summary>
    /// Local HTTP bridge. Binds ONLY to 127.0.0.1, requires a per-session random
    /// token for every endpoint except /health, and hands requests to an
    /// IBridgeExecutor that runs them on the target application's UI thread.
    /// </summary>
    public sealed partial class RevitBridgeServer : IDisposable
    {
        private readonly IBridgeExecutor _executor;
        private readonly BridgeOptions _options;
        private readonly BridgeRequestStore _store = new BridgeRequestStore();
        private readonly BridgeAuditLog _audit;
        private readonly DateTime _startedAtUtc = DateTime.UtcNow;
        private readonly object _lifecycleLock = new object();

        private HttpListener _listener;
        private Timer _watchdog;
        private string _token;
        private bool _stopping;
        private Thread _acceptThread;

        private readonly object _auditLock = new object();
        private readonly HashSet<string> _auditedTerminal = new HashSet<string>();

        public string Token => _token;
        public int Port { get; private set; }
        public bool IsRunning { get; private set; }
        public BridgeRequestStore Store => _store;

        public RevitBridgeServer(IBridgeExecutor executor, BridgeOptions options)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _audit = new BridgeAuditLog(options.AuditLogPath);
        }

        public string GenerateToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var sb = new StringBuilder(48);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public bool Start()
        {
            lock (_lifecycleLock)
            {
                if (IsRunning) return true;

                if (!string.Equals(_options.BindHost, "127.0.0.1", StringComparison.Ordinal))
                {
                    _audit.Write("bridge_start_failed", null, null,
                        "Refusing to bind to anything except 127.0.0.1: " + _options.BindHost);
                    return false;
                }

                _token = GenerateToken();
                Port = _options.Port;
                _listener = new HttpListener();

                for (int attempt = 0; attempt < Math.Max(1, _options.PortFallbackCount); attempt++)
                {
                    try
                    {
                        _listener = new HttpListener();
                        _listener.Prefixes.Add("http://" + _options.BindHost + ":" + Port + "/");
                        _listener.Start();
                        break;
                    }
                    catch (HttpListenerException ex) when (ex.ErrorCode == 10048 || ex.ErrorCode == 10013)
                    {
                        Port++;
                        if (attempt == _options.PortFallbackCount - 1)
                        {
                            _audit.Write("bridge_start_failed", null, null, "No free port found near " + _options.Port);
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _audit.Write("bridge_start_failed", null, null, ex.Message);
                        return false;
                    }
                }

                IsRunning = true;
                _stopping = false;

                WriteTokenFile();

                _watchdog = new Timer(OnWatchdogTick, null, _options.WatchdogIntervalMs, _options.WatchdogIntervalMs);
                // Revit's .NET Framework host can delay or starve Task.Run work while
                // the UI is loading a document. A dedicated background thread keeps
                // the loopback health/execute endpoint responsive independently.
                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "Zexus Bridge HTTP"
                };
                _acceptThread.Start();

                _audit.WriteStart(Port, _options.BindHost);
                return true;
            }
        }

        private void WriteTokenFile()
        {
            try
            {
                if (string.IsNullOrEmpty(_options.TokenFile)) return;
                var dir = Path.GetDirectoryName(_options.TokenFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var content = new StringBuilder();
                content.AppendLine("ZEXUS_BRIDGE_TOKEN=" + _token);
                content.AppendLine("ZEXUS_BRIDGE_URL=http://127.0.0.1:" + Port);
                content.AppendLine("ZEXUS_BRIDGE_PORT=" + Port);
                content.AppendLine("ZEXUS_BRIDGE_PID=" + System.Diagnostics.Process.GetCurrentProcess().Id);
                content.AppendLine("ZEXUS_BRIDGE_STARTED_UTC=" + DateTime.UtcNow.ToString("o"));

                File.WriteAllText(_options.TokenFile, content.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _audit.Write("token_file_write_failed", null, null, ex.Message);
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (!IsRunning)
                {
                    _audit.Dispose();
                    return;
                }
                _stopping = true;
                IsRunning = false;

                try { _watchdog?.Dispose(); } catch { }
                _watchdog = null;

                try { _listener?.Stop(); } catch { }
                try { _listener?.Close(); } catch { }
                _listener = null;

                // Requests still waiting for Revit can never run: fail them fast.
                foreach (var record in _store.Snapshot())
                {
                    if (!record.IsTerminal)
                    {
                        record.Complete(new BridgeCompletionData
                        {
                            Status = BridgeRequestStatus.Cancelled,
                            Message = "Bridge stopped (Revit exiting or plugin unloaded)"
                        });
                    }
                }

                try
                {
                    if (!string.IsNullOrEmpty(_options.TokenFile) && File.Exists(_options.TokenFile))
                        File.Delete(_options.TokenFile);
                }
                catch { }

                AuditNewTerminal();
                _audit.WriteStop();
                _audit.Dispose();
            }
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch
                {
                    break;
                }

                // Handling is short and non-blocking with respect to Revit: /execute
                // only enqueues an ExternalEvent request. Keeping it on this dedicated
                // thread also avoids Revit-hosted ThreadPool starvation.
                HandleContext(context);
            }
        }

        private void OnWatchdogTick(object state)
        {
            if (_stopping) return;
            AuditNewTerminal();
            var now = DateTime.UtcNow;
            foreach (var record in _store.Snapshot())
            {
                if (record.IsTerminal) continue;
                if (now > record.DeadlineUtc)
                {
                    record.Complete(new BridgeCompletionData
                    {
                        Status = BridgeRequestStatus.Expired,
                        Message = "Request timed out (deadline " + record.DeadlineUtc.ToString("o") + ")",
                        ErrorType = "timeout"
                    });
                    _audit.WriteTransition(record, "request_expired");
                }
            }
            AuditNewTerminal();
        }

        private void AuditNewTerminal()
        {
            foreach (var record in _store.Snapshot())
            {
                if (!record.IsTerminal) continue;
                lock (_auditLock)
                {
                    if (!_auditedTerminal.Add(record.RequestId)) continue;
                }
                _audit.WriteTransition(record, "request_" + BridgeAuditLog.StatusName(record.Status));
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
