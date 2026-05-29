using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using Zexus.Models;

namespace Zexus.ViewModels
{
    /// <summary>
    /// Wraps a single <see cref="OutputRecord"/> with view-state (expanded, reverted)
    /// and commands that call back into ChatWindow code-behind for the Revit-API work
    /// (ExternalEvent-driven view navigation and undo).
    /// </summary>
    public class OutputRecordViewModel : ViewModelBase
    {
        private readonly OutputRecord _record;
        private readonly Action<OutputRecord> _onNavigate;
        private readonly Action<OutputRecord> _onUndoRecord;

        public OutputRecord Record => _record;

        // ── Pass-through display fields ──
        public string Title => _record.Title;
        public string Subtitle => _record.Subtitle;
        public string IconGlyph => _record.IconGlyph ?? "?";
        public System.Windows.Media.Color IconColor => _record.IconColor;
        public DateTime Timestamp => _record.Timestamp;
        public string TimeStamp => _record.Timestamp.ToString("HH:mm");
        public OutputRecordType RecordType => _record.RecordType;
        public bool IsClickable => _record.IsClickable;
        public bool IsExpandable => _record.IsExpandable;

        public List<ParameterChangeEntry> ChangeEntries => _record.ChangeEntries;
        public List<ScheduleChangeEntry> ScheduleChangeEntries => _record.ScheduleChangeEntries;

        public int ChangeEntryCount => _record.ChangeEntries?.Count ?? 0;
        public int ScheduleEntryCount => _record.ScheduleChangeEntries?.Count ?? 0;

        // ── View state ──
        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public bool IsRecordReverted => _record.IsRecordReverted;
        public bool IsFullyReverted =>
            (_record.RecordType == OutputRecordType.ParameterSet && _record.IsFullyReverted) ||
            (_record.RecordType == OutputRecordType.ScheduleModified && _record.IsScheduleFullyReverted) ||
            _record.IsRecordReverted ||
            _record.IsDeleted;

        public bool CanUndo => _record.CanUndoRecord && !_record.IsRecordReverted && !_record.IsDeleted;
        public bool CanOpenInRevit => _record.IsClickable;

        // ── Commands ──
        public IRelayCommand ToggleExpandCommand { get; }
        public IRelayCommand NavigateCommand { get; }
        public IRelayCommand UndoCommand { get; }

        public OutputRecordViewModel(OutputRecord record,
            Action<OutputRecord> onNavigate,
            Action<OutputRecord> onUndoRecord)
        {
            _record = record ?? throw new ArgumentNullException(nameof(record));
            _onNavigate = onNavigate;
            _onUndoRecord = onUndoRecord;

            ToggleExpandCommand = new RelayCommand(
                () => IsExpanded = !IsExpanded,
                () => IsExpandable);
            NavigateCommand = new RelayCommand(
                () => _onNavigate?.Invoke(_record),
                () => CanOpenInRevit);
            UndoCommand = new RelayCommand(
                () => _onUndoRecord?.Invoke(_record),
                () => CanUndo);
        }

        /// <summary>Refresh derived view-state after the underlying OutputRecord mutates.</summary>
        public void RefreshDerived()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(IsRecordReverted));
            OnPropertyChanged(nameof(IsFullyReverted));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(ChangeEntryCount));
            OnPropertyChanged(nameof(ScheduleEntryCount));
            UndoCommand.NotifyCanExecuteChanged();
        }
    }
}
