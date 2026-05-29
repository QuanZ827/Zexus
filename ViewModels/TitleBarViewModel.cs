namespace Zexus.ViewModels
{
    /// <summary>
    /// Title bar state: logo + "Zexus" + version + Revit document context.
    /// Updated from <see cref="ChatWindowViewModel.UpdateDocumentContext"/> when
    /// App.cs fires the Document Opened/Closed callbacks.
    /// </summary>
    public class TitleBarViewModel : ViewModelBase
    {
        private string _documentName = "No document loaded";
        public string DocumentName
        {
            get => _documentName;
            set => SetProperty(ref _documentName, value);
        }

        private bool _hasDocument;
        public bool HasDocument
        {
            get => _hasDocument;
            set => SetProperty(ref _hasDocument, value);
        }

        private string _modelLocation;
        public string ModelLocation
        {
            get => _modelLocation;
            set => SetProperty(ref _modelLocation, value);
        }

        private string _modelLocationTooltip;
        public string ModelLocationTooltip
        {
            get => _modelLocationTooltip;
            set => SetProperty(ref _modelLocationTooltip, value);
        }

        private bool _hasModelLocation;
        public bool HasModelLocation
        {
            get => _hasModelLocation;
            set => SetProperty(ref _hasModelLocation, value);
        }

        private string _modelHub;
        public string ModelHub
        {
            get => _modelHub;
            set => SetProperty(ref _modelHub, value);
        }

        private bool _hasModelHub;
        public bool HasModelHub
        {
            get => _hasModelHub;
            set => SetProperty(ref _hasModelHub, value);
        }

        private string _versionString = "v0.3.0";
        public string VersionString
        {
            get => _versionString;
            set => SetProperty(ref _versionString, value);
        }

        /// <summary>
        /// Apply pre-extracted document strings (Revit-API access stays in code-behind).
        /// Pass null/empty for unset fields.
        /// </summary>
        public void UpdateDocument(string docName, string modelLocation,
            string modelLocationTooltip, string modelHub)
        {
            RunOnUi(() =>
            {
                if (string.IsNullOrEmpty(docName))
                {
                    DocumentName = "No document loaded";
                    HasDocument = false;
                    HasModelLocation = false;
                    HasModelHub = false;
                    ModelLocation = null;
                    ModelHub = null;
                    return;
                }

                DocumentName = docName;
                HasDocument = true;

                ModelLocation = modelLocation;
                ModelLocationTooltip = modelLocationTooltip;
                HasModelLocation = !string.IsNullOrEmpty(modelLocation);

                ModelHub = modelHub;
                HasModelHub = !string.IsNullOrEmpty(modelHub);
            });
        }
    }
}
