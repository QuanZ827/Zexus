using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Views;

namespace Zexus
{
    /// <summary>
    /// Zexus - General-purpose AI Agent for Revit BIM workflows
    /// </summary>
    public class App : IExternalApplication
    {
        public static ExternalEvent RevitExternalEvent { get; private set; }
        public static RevitEventHandler RevitEventHandler { get; private set; }

        /// <summary>Revit major version (e.g. 2024, 2025, 2026). Set on startup.</summary>
        public static int RevitVersion { get; private set; }

        /// <summary>True when running on Revit 2025+ (.NET 8, new ElementId API).</summary>
        public static bool IsRevit2025OrGreater => RevitVersion >= 2025;

        private static ChatWindow _chatWindow;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Detect Revit version from ControlledApplication.VersionNumber
                if (int.TryParse(application.ControlledApplication.VersionNumber, out int ver))
                    RevitVersion = ver;
                else
                    RevitVersion = 2024; // safe fallback

                System.Diagnostics.Debug.WriteLine($"[Zexus] Revit version detected: {RevitVersion}");

                // Initialize ExternalEvent handler
                RevitEventHandler = new RevitEventHandler();
                RevitExternalEvent = ExternalEvent.Create(RevitEventHandler);
                
                // Create Ribbon UI
                CreateRibbonUI(application);
                
                // Subscribe to document events
                application.ControlledApplication.DocumentOpened += OnDocumentOpened;
                application.ControlledApplication.DocumentClosed += OnDocumentClosed;
                
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Zexus Error", $"Failed to initialize: {ex.Message}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
            
            _chatWindow?.Close();
            RevitExternalEvent?.Dispose();
            
            return Result.Succeeded;
        }

        private void CreateRibbonUI(UIControlledApplication application)
        {
            string tabName = "Zexus";
            
            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch { } // Tab might already exist
            
            var panel = application.CreateRibbonPanel(tabName, "AI Agent");

            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var buttonData = new PushButtonData(
                "ZexusAgent",
                "Zexus",
                assemblyPath,
                typeof(OpenAgentCommand).FullName
            );

            buttonData.ToolTip = "Open Zexus AI Agent for Revit model analysis and automation";
            buttonData.LongDescription = "A general-purpose AI assistant that helps you query, analyze, and validate your Revit models using natural language.";
            
            // Load icons from embedded resources
            buttonData.LargeImage = GetEmbeddedImage("Zexus.Resources.icon_32.png");
            buttonData.Image = GetEmbeddedImage("Zexus.Resources.icon_16.png");
            
            panel.AddItem(buttonData);
        }
        
        private BitmapSource GetEmbeddedImage(string resourceName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Zexus] Resource not found: {resourceName}");
                        return null;
                    }
                    
                    var decoder = new PngBitmapDecoder(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.Default);
                    return decoder.Frames[0];
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Failed to load icon: {ex.Message}");
                return null;
            }
        }

        private void OnDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
            _chatWindow?.UpdateDocumentContext(e.Document);
        }

        private void OnDocumentClosed(object sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs e)
        {
            _chatWindow?.UpdateDocumentContext(null);
        }

        public static void ShowChatWindow(Document doc)
        {
            if (_chatWindow == null || !_chatWindow.IsLoaded)
            {
                _chatWindow = new ChatWindow();
                _chatWindow.Closed += (s, e) => _chatWindow = null;
            }
            
            _chatWindow.UpdateDocumentContext(doc);
            _chatWindow.Show();
            _chatWindow.Activate();
        }
    }

    /// <summary>
    /// Command to open the Zexus chat window
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenAgentCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                App.ShowChatWindow(doc);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
