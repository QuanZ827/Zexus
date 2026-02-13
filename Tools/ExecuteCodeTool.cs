using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;
using Zexus.Services;

namespace Zexus.Tools
{
    /// <summary>
    /// Executes dynamically generated C# code inside the Revit process.
    /// This is the "universal tool" — Claude generates code for operations
    /// not covered by the predefined tools.
    /// </summary>
    public class ExecuteCodeTool : IAgentTool
    {
        public string Name => "ExecuteCode";

        public string Description =>
            "Execute custom C# code inside Revit. Use this ONLY when no predefined tool can accomplish the task. " +
            "You write the method body of: public static object Execute(Document doc, UIDocument uiDoc, StringBuilder output). " +
            "Use output.AppendLine() to report results. For write operations, wrap in a Transaction. " +
            "Available namespaces: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB, Autodesk.Revit.UI, " +
            "Autodesk.Revit.DB.Architecture, Autodesk.Revit.DB.Mechanical, Autodesk.Revit.DB.Electrical, Autodesk.Revit.DB.Plumbing. " +
            "If compilation fails, read the error messages and fix your code.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["code"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "C# method body to execute. This is inserted into: " +
                            "public static object Execute(Document doc, UIDocument uiDoc, StringBuilder output) { YOUR_CODE_HERE }. " +
                            "Use output.AppendLine() to print results. Return a value or return null."
                    },
                    ["description"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Brief description of what this code does (for logging and user transparency)."
                    }
                },
                Required = new List<string> { "code" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            // Extract parameters
            if (!parameters.ContainsKey("code") || parameters["code"] == null)
            {
                return ToolResult.Fail("Missing required parameter: 'code'");
            }

            var code = parameters["code"].ToString();
            var description = parameters.ContainsKey("description") ? parameters["description"]?.ToString() : "Dynamic code execution";

            if (string.IsNullOrWhiteSpace(code))
            {
                return ToolResult.Fail("Code parameter is empty.");
            }

            // Log what we're executing
            System.Diagnostics.Debug.WriteLine($"[Zexus] ExecuteCode: {description}");
            System.Diagnostics.Debug.WriteLine($"[Zexus] Code:\n{code}");

            // Compile and execute
            var result = CodeExecutionService.CompileAndExecute(code, doc, uiDoc);

            if (!result.Success)
            {
                // Compilation errors — return them so Claude can fix and retry
                if (result.CompilationErrors != null && result.CompilationErrors.Count > 0)
                {
                    var errorMsg = "Compilation failed. Fix the errors and try again:\n" +
                                   string.Join("\n", result.CompilationErrors);
                    var failResult = ToolResult.Fail(errorMsg);
                    failResult.Data["failure_type"] = "compilation";
                    failResult.Data["description"] = description;
                    return failResult;
                }

                // Runtime error
                if (!string.IsNullOrEmpty(result.RuntimeError))
                {
                    var errorMsg = "Runtime error:\n" + result.RuntimeError;
                    if (!string.IsNullOrEmpty(result.Output))
                    {
                        errorMsg += "\n\nOutput before error:\n" + result.Output;
                    }
                    var failResult = ToolResult.Fail(errorMsg);
                    failResult.Data["failure_type"] = "runtime";
                    failResult.Data["description"] = description;
                    return failResult;
                }

                var unknownFail = ToolResult.Fail("Code execution failed: " + result.Output);
                unknownFail.Data["failure_type"] = "unknown";
                unknownFail.Data["description"] = description;
                return unknownFail;
            }

            // Success — return output and return value
            var data = new Dictionary<string, object>
            {
                ["output"] = result.Output ?? "",
                ["description"] = description
            };

            if (result.ReturnValue != null)
            {
                data["return_value"] = result.ReturnValue.ToString();
            }

            var message = string.IsNullOrEmpty(result.Output)
                ? (result.ReturnValue != null ? $"Code executed successfully. Return value: {result.ReturnValue}" : "Code executed successfully (no output).")
                : result.Output.TrimEnd();

            return ToolResult.Ok(message, data);
        }
    }
}
