using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Zexus.Services
{
    /// <summary>
    /// Compiles and executes C# code dynamically inside the Revit process using Roslyn.
    /// Claude generates a method body; this service wraps it in a class, compiles, and invokes it.
    /// </summary>
    public static class CodeExecutionService
    {
        // Cached metadata references (assembly references don't change during a Revit session)
        private static List<MetadataReference> _cachedReferences;

        // Line offset of the template before Claude's code is inserted
        // Used to adjust error line numbers so Claude sees correct line references
        private const int TEMPLATE_LINE_OFFSET = 15;

        /// <summary>
        /// The code template that wraps Claude's method body into a compilable class.
        /// Claude writes the body of Execute(); we provide doc, uiDoc, and output.
        /// </summary>
        private static string WrapCode(string userCode)
        {
            return @"
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

public class DynamicScript
{
    public static object Execute(Document doc, UIDocument uiDoc, StringBuilder output)
    {
" + userCode + @"
    }
}
";
        }

        /// <summary>
        /// Resolves assembly references from the currently loaded assemblies in the Revit process.
        /// This avoids hardcoding any DLL paths — everything Revit needs is already loaded.
        /// </summary>
        private static List<MetadataReference> GetReferences()
        {
            if (_cachedReferences != null)
                return _cachedReferences;

            _cachedReferences = new List<MetadataReference>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Skip dynamic assemblies (they have no file location)
                    if (assembly.IsDynamic)
                        continue;

                    var location = assembly.Location;
                    if (string.IsNullOrEmpty(location))
                        continue;

                    // Skip assemblies from temp/shadow-copy paths that may not exist
                    if (!File.Exists(location))
                        continue;

                    _cachedReferences.Add(MetadataReference.CreateFromFile(location));
                }
                catch
                {
                    // Some assemblies may throw on accessing Location; skip them
                }
            }

            return _cachedReferences;
        }

        /// <summary>
        /// Compiles and executes C# code inside the Revit process.
        /// </summary>
        /// <param name="code">C# method body (not a full class — just the code inside Execute())</param>
        /// <param name="doc">The active Revit Document</param>
        /// <param name="uiDoc">The active UIDocument</param>
        /// <returns>A result object with output text, return value, or error details</returns>
        public static CodeExecutionResult CompileAndExecute(string code, Document doc, UIDocument uiDoc)
        {
            // Step 1: Wrap code into a full compilable class
            var fullSource = WrapCode(code);

            // Step 2: Parse
            var syntaxTree = CSharpSyntaxTree.ParseText(fullSource);

            // Step 3: Compile
            var compilation = CSharpCompilation.Create(
                assemblyName: "DynamicScript_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                syntaxTrees: new[] { syntaxTree },
                references: GetReferences(),
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release
                )
            );

            using (var ms = new MemoryStream())
            {
                var emitResult = compilation.Emit(ms);

                // Step 4: Handle compilation errors
                if (!emitResult.Success)
                {
                    var errors = emitResult.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d =>
                        {
                            var lineSpan = d.Location.GetMappedLineSpan();
                            // Adjust line number to reference Claude's code, not the template
                            int adjustedLine = lineSpan.StartLinePosition.Line - TEMPLATE_LINE_OFFSET + 1;
                            if (adjustedLine < 1) adjustedLine = lineSpan.StartLinePosition.Line + 1;
                            return $"Line {adjustedLine}: {d.GetMessage()}";
                        })
                        .ToList();

                    return new CodeExecutionResult
                    {
                        Success = false,
                        CompilationErrors = errors,
                        Output = string.Join("\n", errors)
                    };
                }

                // Step 5: Load and execute
                ms.Seek(0, SeekOrigin.Begin);
                var assemblyBytes = ms.ToArray();

                Assembly assembly;
#if REVIT_2025_OR_GREATER
                // .NET 8: Use collectible AssemblyLoadContext for proper cleanup
                var loadContext = new System.Runtime.Loader.AssemblyLoadContext(null, isCollectible: true);
                assembly = loadContext.LoadFromStream(new MemoryStream(assemblyBytes));
#else
                // .NET Framework 4.8: Load from byte array
                assembly = Assembly.Load(assemblyBytes);
#endif

                var scriptType = assembly.GetType("DynamicScript");
                if (scriptType == null)
                {
                    return new CodeExecutionResult
                    {
                        Success = false,
                        Output = "Internal error: DynamicScript class not found in compiled assembly."
                    };
                }

                var executeMethod = scriptType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (executeMethod == null)
                {
                    return new CodeExecutionResult
                    {
                        Success = false,
                        Output = "Internal error: Execute method not found in DynamicScript class."
                    };
                }

                var output = new StringBuilder();

                try
                {
                    var returnValue = executeMethod.Invoke(null, new object[] { doc, uiDoc, output });

                    var result = new CodeExecutionResult
                    {
                        Success = true,
                        Output = output.ToString(),
                        ReturnValue = returnValue
                    };

                    return result;
                }
                catch (TargetInvocationException ex)
                {
                    // Unwrap the inner exception (the actual error from Claude's code)
                    var inner = ex.InnerException ?? ex;
                    return new CodeExecutionResult
                    {
                        Success = false,
                        Output = output.ToString(),
                        RuntimeError = $"{inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}"
                    };
                }
                catch (Exception ex)
                {
                    return new CodeExecutionResult
                    {
                        Success = false,
                        Output = output.ToString(),
                        RuntimeError = $"{ex.GetType().Name}: {ex.Message}"
                    };
                }
#if REVIT_2025_OR_GREATER
                finally
                {
                    // Allow the collectible AssemblyLoadContext to be garbage collected
                    loadContext.Unload();
                }
#endif
            }
        }
    }

    /// <summary>
    /// Result of a dynamic code execution attempt.
    /// </summary>
    public class CodeExecutionResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = "";
        public object ReturnValue { get; set; }
        public List<string> CompilationErrors { get; set; }
        public string RuntimeError { get; set; }
    }
}
