using System;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Zexus
{
    public enum RevitRequestType
    {
        None,
        ExecuteTool
    }

    public class RevitRequest
    {
        public RevitRequestType Type { get; set; } = RevitRequestType.None;
        public string ToolName { get; set; }
        public object Parameters { get; set; }
        public TaskCompletionSource<object> CompletionSource { get; set; }
    }

    public class RevitEventHandler : IExternalEventHandler
    {
        private RevitRequest _currentRequest;
        private readonly object _lockObject = new object();
        private Tools.ToolRegistry _toolRegistry;
        
        private const int TOOL_TIMEOUT_MS = 30000;

        public bool IsRegistryInitialized => _toolRegistry != null;
        public int ToolCount => _toolRegistry?.Count ?? 0;

        public void SetToolRegistry(Tools.ToolRegistry registry)
        {
            _toolRegistry = registry;
            Log($"ToolRegistry set with {registry?.Count ?? 0} tools");
        }

        public async Task<object> ExecuteToolAsync(string toolName, object parameters)
        {
            Log($"ExecuteToolAsync: {toolName}");
            
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
                CompletionSource = new TaskCompletionSource<object>()
            };
            
            lock (_lockObject)
            {
                _currentRequest = request;
            }
            
            var raiseResult = App.RevitExternalEvent.Raise();
            Log($"ExternalEvent.Raise() result: {raiseResult}");
            
            if (raiseResult != ExternalEventRequest.Accepted)
            {
                return Models.ToolResult.Fail($"Failed to raise Revit event: {raiseResult}");
            }
            
            try
            {
                var timeoutTask = Task.Delay(TOOL_TIMEOUT_MS);
                var completedTask = await Task.WhenAny(request.CompletionSource.Task, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    return Models.ToolResult.Fail($"Tool execution timed out after {TOOL_TIMEOUT_MS / 1000} seconds.");
                }
                
                return await request.CompletionSource.Task;
            }
            catch (Exception ex)
            {
                return Models.ToolResult.Fail($"Tool execution error: {ex.Message}");
            }
        }

        public void Execute(UIApplication app)
        {
            Log("ExternalEventHandler.Execute() called");
            
            RevitRequest request;
            
            lock (_lockObject)
            {
                request = _currentRequest;
                _currentRequest = null;
            }
            
            if (request == null) return;
            
            try
            {
                object result = null;
                
                if (request.Type == RevitRequestType.ExecuteTool)
                {
                    result = ExecuteTool(app, request.ToolName, request.Parameters);
                }
                
                request.CompletionSource.TrySetResult(result);
            }
            catch (Exception ex)
            {
                Log($"Execute exception: {ex.Message}");
                request.CompletionSource.TrySetResult(Models.ToolResult.Fail($"Execution error: {ex.Message}"));
            }
        }

        private object ExecuteTool(UIApplication app, string toolName, object parameters)
        {
            Log($"ExecuteTool: {toolName}");
            
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
            
            Log($"Document: {document.Title}");
            
            var paramDict = parameters as System.Collections.Generic.Dictionary<string, object>;
            
            try
            {
                var result = tool.Execute(document, uiDocument, paramDict);
                Log($"Tool result: Success={result?.Success}");
                return result ?? Models.ToolResult.Fail("Tool returned no result");
            }
            catch (Exception ex)
            {
                Log($"Tool.Execute exception: {ex.Message}");
                return Models.ToolResult.Fail($"Tool execution failed: {ex.Message}");
            }
        }

        public string GetName() => "Zexus Revit Event Handler";
        
        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[Zexus] {message}");
        }
    }
}
