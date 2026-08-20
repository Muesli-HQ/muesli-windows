using System.ComponentModel;
using Muesli.Windows.Services;

namespace Muesli.Windows;

public sealed class TranscriptionModelItem : INotifyPropertyChanged
{
    private TranscriptionModelSnapshot _snapshot;
    private string _progressText = "";

    public TranscriptionModelItem(TranscriptionModelSnapshot snapshot) => _snapshot = snapshot;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id => _snapshot.Model.Id;
    public string DisplayName => _snapshot.Model.DisplayName;
    public string Summary => _snapshot.Model.Summary;
    public string Languages => _snapshot.Model.Languages;
    public string DownloadSize => _snapshot.Model.SizeLabel;
    public string StatusText => string.IsNullOrWhiteSpace(ProgressText) ? _snapshot.StatusText : ProgressText;
    public string DiskSize => _snapshot.DiskSize;
    public string Diagnostics => _snapshot.Diagnostics;
    public bool CanPrepare => _snapshot.CanPrepare;
    public bool CanCancel => _snapshot.CanCancel;
    public bool CanRetry => _snapshot.CanRetry;
    public bool CanVerify => _snapshot.CanVerify;
    public bool CanDelete => _snapshot.CanDelete;

    public string ProgressText
    {
        get => _progressText;
        set
        {
            if (_progressText == value)
                return;

            _progressText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public void Apply(TranscriptionModelSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (!snapshot.IsBusy)
            _progressText = "";

        foreach (var property in new[]
                 {
                     nameof(StatusText), nameof(DiskSize), nameof(Diagnostics), nameof(CanPrepare),
                     nameof(CanCancel), nameof(CanRetry), nameof(CanVerify), nameof(CanDelete), nameof(ProgressText)
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
