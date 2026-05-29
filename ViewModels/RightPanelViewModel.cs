using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Zexus.Models;
using Zexus.Services;

namespace Zexus.ViewModels
{
    /// <summary>
    /// Right Panel. Top: Model Health. Bottom: Final Results (output cards).
    /// All Revit-API side effects (navigate to view, undo a record) are injected as
    /// callback delegates so this VM stays Revit-API-free.
    /// </summary>
    public class RightPanelViewModel : ViewModelBase
    {
        private readonly Action<OutputRecord> _onNavigate;
        private readonly Action<OutputRecord> _onUndoRecord;
        private readonly Action<string> _onWarningClicked;

        // ── Model Health ──
        private HealthLevel _healthLevel = HealthLevel.Green;
        public HealthLevel HealthLevel
        {
            get => _healthLevel;
            set
            {
                if (SetProperty(ref _healthLevel, value))
                    OnPropertyChanged(nameof(HealthDotColor));
            }
        }

        public System.Windows.Media.Color HealthDotColor => _healthLevel switch
        {
            HealthLevel.Red => System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44),
            HealthLevel.Yellow => System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B),
            _ => System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E),
        };

        private string _healthText = "No document loaded";
        public string HealthText
        {
            get => _healthText;
            set => SetProperty(ref _healthText, value);
        }

        private bool _hasHealthData;
        public bool HasHealthData
        {
            get => _hasHealthData;
            set => SetProperty(ref _hasHealthData, value);
        }

        private int _warningCount;
        public int WarningCount
        {
            get => _warningCount;
            set => SetProperty(ref _warningCount, value);
        }

        private bool _isHealthExpanded;
        public bool IsHealthExpanded
        {
            get => _isHealthExpanded;
            set => SetProperty(ref _isHealthExpanded, value);
        }

        public ObservableCollection<WarningGroupInfo> WarningGroups { get; }
            = new ObservableCollection<WarningGroupInfo>();

        // ── Briefing details ──
        private string _disciplineLabel;
        public string DisciplineLabel
        {
            get => _disciplineLabel;
            set => SetProperty(ref _disciplineLabel, value);
        }

        private string _topCategoriesText;
        public string TopCategoriesText
        {
            get => _topCategoriesText;
            set => SetProperty(ref _topCategoriesText, value);
        }

        private bool _hasDiscipline;
        public bool HasDiscipline
        {
            get => _hasDiscipline;
            set => SetProperty(ref _hasDiscipline, value);
        }

        private string _statsLine = "";
        public string StatsLine
        {
            get => _statsLine;
            set => SetProperty(ref _statsLine, value);
        }

        public ObservableCollection<string> Suggestions { get; }
            = new ObservableCollection<string>();

        public bool HasSuggestions => Suggestions.Count > 0;

        // ── Final Results ──
        public ObservableCollection<OutputRecordViewModel> OutputRecords { get; }
            = new ObservableCollection<OutputRecordViewModel>();

        private bool _hasOutput;
        public bool HasOutput
        {
            get => _hasOutput;
            set => SetProperty(ref _hasOutput, value);
        }

        // ── Transaction Journal (footer summary) ──
        private int _transactionCount;
        public int TransactionCount
        {
            get => _transactionCount;
            set => SetProperty(ref _transactionCount, value);
        }

        private int _totalCreated;
        public int TotalCreated
        {
            get => _totalCreated;
            set => SetProperty(ref _totalCreated, value);
        }

        private int _totalModified;
        public int TotalModified
        {
            get => _totalModified;
            set => SetProperty(ref _totalModified, value);
        }

        private int _totalDeleted;
        public int TotalDeleted
        {
            get => _totalDeleted;
            set => SetProperty(ref _totalDeleted, value);
        }

        public string JournalSummaryLine
        {
            get
            {
                if (TransactionCount == 0) return "";
                var parts = new List<string>();
                if (TotalCreated > 0) parts.Add($"+{TotalCreated} created");
                if (TotalModified > 0) parts.Add($"{TotalModified} modified");
                if (TotalDeleted > 0) parts.Add($"-{TotalDeleted} deleted");
                var detail = parts.Count > 0 ? "  -  " + string.Join(", ", parts) : "";
                return $"{TransactionCount} transaction{(TransactionCount > 1 ? "s" : "")}{detail}";
            }
        }

        // ── Commands ──
        public IRelayCommand ToggleHealthCommand { get; }
        public IRelayCommand<WarningGroupInfo> WarningGroupClickCommand { get; }

        public RightPanelViewModel(
            Action<OutputRecord> onNavigate = null,
            Action<OutputRecord> onUndoRecord = null,
            Action<string> onWarningClicked = null)
        {
            _onNavigate = onNavigate;
            _onUndoRecord = onUndoRecord;
            _onWarningClicked = onWarningClicked;

            ToggleHealthCommand = new RelayCommand(
                () => IsHealthExpanded = !IsHealthExpanded,
                () => WarningGroups.Count > 0);
            WarningGroupClickCommand = new RelayCommand<WarningGroupInfo>(group =>
            {
                if (group != null) _onWarningClicked?.Invoke(group.Description);
            });

            Suggestions.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasSuggestions));
            OutputRecords.CollectionChanged += (s, e) => HasOutput = OutputRecords.Count > 0;
        }

        /// <summary>
        /// Populate Health/Stats/Discipline/Suggestions from a <see cref="ModelBriefing"/>.
        /// Called by ChatWindow when App.OnDocumentOpened fires.
        /// </summary>
        public void UpdateFromBriefing(ModelBriefing b)
        {
            RunOnUi(() =>
            {
                if (b == null)
                {
                    HasHealthData = false;
                    HasDiscipline = false;
                    StatsLine = "";
                    Suggestions.Clear();
                    WarningGroups.Clear();
                    HealthText = "No document loaded";
                    WarningCount = 0;
                    OnPropertyChanged(nameof(HasSuggestions));
                    return;
                }

                // Discipline
                if (b.Discipline != ModelDiscipline.Unknown)
                {
                    DisciplineLabel = b.DisciplineLabel ?? "";
                    TopCategoriesText = b.TopCategories != null && b.TopCategories.Count > 0
                        ? string.Join("  -  ", b.TopCategories.Select(kv => $"{kv.Value:N0} {kv.Key}"))
                        : "";
                    HasDiscipline = true;
                }
                else
                {
                    HasDiscipline = false;
                }

                // Stats
                StatsLine = $"{b.TotalElements:N0} elements  -  {b.LevelCount} levels  -  " +
                            $"{b.ViewCount} views  -  {b.LinkedModelCount} linked models";

                // Health
                HealthLevel = b.Health;
                WarningCount = b.WarningTotal;
                HasHealthData = true;

                if (b.WarningTotal > 0)
                {
                    var parts = new List<string>();
                    if (b.WarningHigh > 0) parts.Add($"{b.WarningHigh} high");
                    if (b.WarningMedium > 0) parts.Add($"{b.WarningMedium} medium");
                    if (b.WarningLow > 0) parts.Add($"{b.WarningLow} low");
                    HealthText = $"{b.WarningTotal} warning{(b.WarningTotal > 1 ? "s" : "")} - {string.Join(", ", parts)}";
                }
                else
                {
                    HealthText = "No warnings";
                }

                WarningGroups.Clear();
                if (b.WarningGroups != null)
                {
                    foreach (var g in b.WarningGroups)
                        WarningGroups.Add(g);
                }
                ToggleHealthCommand.NotifyCanExecuteChanged();

                // Suggestions
                Suggestions.Clear();
                if (b.Suggestions != null)
                {
                    foreach (var s in b.Suggestions) Suggestions.Add(s);
                }
                OnPropertyChanged(nameof(HasSuggestions));
            });
        }

        /// <summary>Add a new OutputRecord to the Final Results panel (wrapping it in a VM).</summary>
        public void AddOutputRecord(OutputRecord record)
        {
            if (record == null) return;
            RunOnUi(() =>
            {
                OutputRecords.Add(new OutputRecordViewModel(record, _onNavigate, _onUndoRecord));
            });
        }

        /// <summary>Refresh a record's VM after the underlying OutputRecord was mutated (e.g. undo applied).</summary>
        public void RefreshRecord(OutputRecord record)
        {
            if (record == null) return;
            RunOnUi(() =>
            {
                var vm = OutputRecords.FirstOrDefault(r => ReferenceEquals(r.Record, record));
                vm?.RefreshDerived();
            });
        }

        /// <summary>Update the journal footer counts.</summary>
        public void SetJournalStats(int transactionCount, int totalCreated, int totalModified, int totalDeleted)
        {
            RunOnUi(() =>
            {
                TransactionCount = transactionCount;
                TotalCreated = totalCreated;
                TotalModified = totalModified;
                TotalDeleted = totalDeleted;
                OnPropertyChanged(nameof(JournalSummaryLine));
            });
        }

        /// <summary>Clear everything (called on new chat).</summary>
        public void ClearAll()
        {
            RunOnUi(() =>
            {
                OutputRecords.Clear();
                TransactionCount = 0;
                TotalCreated = 0;
                TotalModified = 0;
                TotalDeleted = 0;
                OnPropertyChanged(nameof(JournalSummaryLine));
            });
        }
    }
}
