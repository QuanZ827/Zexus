using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Services;

namespace Zexus
{
    public enum RevitRequestType
    {
        None,
        ExecuteTool,
        NavigateToView,
        DeleteElements,
        BridgeExecute
    }

    public class RevitRequest
    {
        private int _executionState; // 0 = queued, 1 = running, 2 = cancelled before execution

        public RevitRequestType Type { get; set; } = RevitRequestType.None;
        public string ToolName { get; set; }
        public object Parameters { get; set; }
        public TaskCompletionSource<object> CompletionSource { get; set; }

        /// <summary>Set for BridgeExecute requests; the record is updated on the UI thread.</summary>
        public Zexus.Bridge.BridgeRequestRecord BridgeRecord { get; set; }

        public bool TryBeginExecution()
        {
            return Interlocked.CompareExchange(ref _executionState, 1, 0) == 0;
        }

        public bool TryCancelBeforeExecution()
        {
            return Interlocked.CompareExchange(ref _executionState, 2, 0) == 0;
        }
    }

    /// <summary>
    /// Queue-based external event handler. Every Revit API call is marshalled to the
    /// Revit UI thread via ExternalEvent; requests are processed in FIFO order.
    /// Supports both the chat-agent path (TCS + await) and the HTTP bridge path
    /// (records updated by BridgeRevitRunner).
    /// </summary>
    public class RevitEventHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<RevitRequest> _requestQueue = new ConcurrentQueue<RevitRequest>();
        private Tools.ToolRegistry _toolRegistry;
        private volatile bool _shuttingDown;

        private const int TOOL_TIMEOUT_MS = 30000;
        private const int RAISE_TIMEOUT_MS = 10000;

        public bool IsRegistryInitialized => _toolRegistry != null;
        public int ToolCount => _toolRegistry?.Count ?? 0;

        public void SetToolRegistry(Tools.ToolRegistry registry)
        {
            _toolRegistry = registry;
            ZexusLogger.Info($"ToolRegistry set with {registry?.Count ?? 0} tools");
        }

        /// <summary>Called from App.OnShutdown. Fails all queued requests fast.</summary>
        public void BeginShutdown()
        {
            _shuttingDown = true;

            while (_requestQueue.TryDequeue(out var request))
            {
                FailRequest(request, "Revit is shutting down", errorType: "shutdown");
            }
        }

        private static void FailRequest(
            RevitRequest request,
            string message,
            Zexus.Bridge.BridgeRequestStatus bridgeStatus = Zexus.Bridge.BridgeRequestStatus.Cancelled,
            string errorType = "cancelled")
        {
            request.TryCancelBeforeExecution();
            request.CompletionSource?.TrySetResult(Models.ToolResult.Fail(message));
            var record = request.BridgeRecord;
            if (record != null && !record.IsTerminal)
            {
                record.Complete(new Zexus.Bridge.BridgeCompletionData
                {
                    Status = bridgeStatus,
                    Message = message,
                    ErrorType = errorType
                });
            }
        }

        /// <summary>Enqueue a request and ask Revit to process it. Fire-and-forget raise.</summary>
        public void EnqueueRevitRequest(RevitRequest request)
        {
            _requestQueue.Enqueue(request);
            _ = RaiseBridgeRequestAsync(request);
        }

        private async Task RaiseBridgeRequestAsync(RevitRequest request)
        {
            var raised = await RaiseAsync(RAISE_TIMEOUT_MS);
            if (!raised && request.TryCancelBeforeExecution())
            {
                FailRequest(
                    request,
                    "Failed to raise Revit event (Revit busy or shutting down).",
                    Zexus.Bridge.BridgeRequestStatus.Failed,
                    "raise");
            }
        }

        public async Task<object> ExecuteToolAsync(string toolName, object parameters)
        {
            return await ExecuteToolAsync(toolName, parameters, TOOL_TIMEOUT_MS);
        }

        public async Task<object> ExecuteToolAsync(string toolName, object parameters, int timeoutMs)
        {
            ZexusLogger.Info($"ExecuteToolAsync: {toolName}");

            if (_toolRegistry == null)
            {
                return Models.ToolResult.Fail("Tool registry not initialized.");
            }

            if (!_toolRegistry.HasTool(toolName))
            {
                return Models.ToolResult.Fail($"Unknown tool: {toolName}");
            }

            if (App.RevitExternalEvent == null)
            {
                return Models.ToolResult.Fail("Revit external event not initialized.");
            }

            var request = new RevitRequest
            {
                Type = RevitRequestType.ExecuteTool,
                ToolName = toolName,
                Parameters = parameters,
                CompletionSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously)
            };

            _requestQueue.Enqueue(request);

            var raised = await RaiseAsync(timeoutMs);
            if (!raised)
            {
                request.TryCancelBeforeExecution();
                return Models.ToolResult.Fail("Failed to raise Revit event (Revit busy or shutting down). Please try again.");
            }

            try
            {
                var completedTask = await Task.WhenAny(request.CompletionSource.Task, Task.Delay(timeoutMs));
                if (completedTask != request.CompletionSource.Task)
                {
                    var cancelled = request.TryCancelBeforeExecution();
                    return Models.ToolResult.Fail(cancelled
                        ? $"Tool execution timed out before it started after {timeoutMs / 1000} seconds."
                        : $"Tool execution timed out after {timeoutMs / 1000} seconds and may still be running in Revit.");
                }
                return await request.CompletionSource.Task;
            }
            catch (Exception ex)
            {
                return Models.ToolResult.Fail($"Tool execution error: {ex.Message}");
            }
        }

        /// <summary>
        /// Raise the external event, retrying on Pending (Revit busy) with backoff.
        /// Returns false on Denied or timeout.
        /// </summary>
        private async Task<bool> RaiseAsync(int timeoutMs)
        {
            if (App.RevitExternalEvent == null) return false;

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            int delayMs = 300;

            while (DateTime.UtcNow < deadline)
            {
                if (_shuttingDown) return false;

                ExternalEventRequest raiseResult;
                try
                {
                    raiseResult = App.RevitExternalEvent.Raise();
                }
                catch (Exception ex)
                {
                    ZexusLogger.Warn("ExternalEvent.Raise() failed: " + ex.Message);
                    return false;
                }
                if (raiseResult == ExternalEventRequest.Accepted) return true;
                if (raiseResult == ExternalEventRequest.Denied) return false;

                ZexusLogger.Info("ExternalEvent.Raise() Pending; retrying...");
                await Task.Delay(Math.Min(delayMs, 2000));
                delayMs = Math.Min(delayMs * 2, 4000);
            }

            return false;
        }

        public void Execute(UIApplication app)
        {
            ZexusLogger.Info("ExternalEventHandler.Execute() called");

            while (_requestQueue.TryDequeue(out var request))
            {
                try
                {
                    ProcessRequest(app, request);
                }
                catch (Exception ex)
                {
                    ZexusLogger.Error($"Execute exception: {ex.Message}");
                    FailRequest(
                        request,
                        "Execution error: " + ex.Message,
                        Zexus.Bridge.BridgeRequestStatus.Failed,
                        "execution");
                }
            }
        }

        private void ProcessRequest(UIApplication app, RevitRequest request)
        {
            if (_shuttingDown)
            {
                FailRequest(request, "Revit is shutting down", errorType: "shutdown");
                return;
            }

            if (!request.TryBeginExecution()) return;

            if (_shuttingDown)
            {
                FailRequest(request, "Revit is shutting down", errorType: "shutdown");
                return;
            }

            switch (request.Type)
            {
                case RevitRequestType.ExecuteTool:
                    var result = ExecuteTool(app, request.ToolName, request.Parameters);
                    request.CompletionSource?.TrySetResult(result);
                    break;

                case RevitRequestType.NavigateToView:
                    request.CompletionSource?.TrySetResult(NavigateToView(app, (long)request.Parameters));
                    break;

                case RevitRequestType.DeleteElements:
                    request.CompletionSource?.TrySetResult(DeleteElements(app, (long[])request.Parameters));
                    break;

                case RevitRequestType.BridgeExecute:
                    if (request.BridgeRecord != null)
                    {
                        Zexus.Services.Bridge.BridgeRevitRunner.ExecuteOnUiThread(app, request.BridgeRecord, _toolRegistry);
                    }
                    break;
            }
        }

        private object ExecuteTool(UIApplication app, string toolName, object parameters)
        {
            ZexusLogger.Info($"ExecuteTool: {toolName}");

            if (_toolRegistry == null)
            {
                return Models.ToolResult.Fail("Tool registry not initialized");
            }

            var tool = _toolRegistry.GetTool(toolName);
            if (tool == null)
            {
                return Models.ToolResult.Fail($"Unknown tool: {toolName}");
            }

            var uiDocument = app.ActiveUIDocument;
            var document = uiDocument?.Document;

            if (document == null)
            {
                return Models.ToolResult.Fail("No active document. Please open a Revit model first.");
            }

            ZexusLogger.Info($"Document: {document.Title}");

            var paramDict = parameters as Dictionary<string, object>;

            try
            {
                if (tool is Tools.IAppAwareTool appAware)
                    appAware.SetUIApplication(app);

                var result = tool.Execute(document, uiDocument, paramDict);
                ZexusLogger.Info($"Tool result: Success={result?.Success}");
                return result ?? Models.ToolResult.Fail("Tool returned no result");
            }
            catch (Exception ex)
            {
                ZexusLogger.Error($"Tool.Execute exception: {ex.Message}");
                return Models.ToolResult.Fail($"Tool execution failed: {ex.Message}");
            }
        }

        private object NavigateToView(UIApplication app, long viewIdValue)
        {
            try
            {
                var uiDoc = app.ActiveUIDocument;
                if (uiDoc == null) return false;
                var doc = uiDoc.Document;
                var viewId = Tools.RevitCompat.CreateId(viewIdValue);
                var view = doc.GetElement(viewId) as Autodesk.Revit.DB.View;
                if (view != null && !view.IsTemplate)
                {
                    uiDoc.RequestViewChange(view);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                ZexusLogger.Warn($"NavigateToView failed: {ex.Message}");
                return false;
            }
        }

        private object DeleteElements(UIApplication app, long[] elementIds)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) return false;
                var ids = elementIds.Select(id => Tools.RevitCompat.CreateId(id)).ToList();
                using (var t = new Transaction(doc, "Delete elements"))
                {
                    t.Start();
                    doc.Delete(ids.Where(id => doc.GetElement(id) != null).Select(id => id).ToList());
                    t.Commit();
                }
                return true;
            }
            catch (Exception ex)
            {
                ZexusLogger.Warn($"DeleteElements failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DeleteElementsAsync(long[] elementIds)
        {
            if (App.RevitExternalEvent == null) return false;
            var request = new RevitRequest
            {
                Type = RevitRequestType.DeleteElements,
                Parameters = elementIds,
                CompletionSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            _requestQueue.Enqueue(request);
            var raised = await RaiseAsync(RAISE_TIMEOUT_MS);
            if (!raised)
            {
                request.TryCancelBeforeExecution();
                return false;
            }
            try
            {
                var completedTask = await Task.WhenAny(request.CompletionSource.Task, Task.Delay(10000));
                if (completedTask != request.CompletionSource.Task)
                {
                    request.TryCancelBeforeExecution();
                    return false;
                }
                return (await request.CompletionSource.Task) is bool b && b;
            }
            catch { return false; }
        }

        /// <summary>Navigate to a view by ElementId (direct Revit API call, no tool registry dependency).</summary>
        public async Task<bool> NavigateToViewAsync(long viewIdValue)
        {
            if (App.RevitExternalEvent == null) return false;

            var request = new RevitRequest
            {
                Type = RevitRequestType.NavigateToView,
                Parameters = viewIdValue,
                CompletionSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously)
            };

            _requestQueue.Enqueue(request);
            var raised = await RaiseAsync(RAISE_TIMEOUT_MS);
            if (!raised)
            {
                request.TryCancelBeforeExecution();
                return false;
            }

            try
            {
                var completedTask = await Task.WhenAny(request.CompletionSource.Task, Task.Delay(5000));
                if (completedTask != request.CompletionSource.Task)
                {
                    request.TryCancelBeforeExecution();
                    return false;
                }
                return (await request.CompletionSource.Task) is bool b && b;
            }
            catch { return false; }
        }

        public string GetName() => "Zexus Revit Event Handler";
    }
}
