using System.ComponentModel;

namespace Muesli.Windows;

public sealed class MeetingFolderItem : INotifyPropertyChanged
{
    private int _count;
    private bool _isRenaming;
    private string _name;

    public MeetingFolderItem(string id, string name)
    {
        Id = id;
        _name = name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;

            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value)
                return;

            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountDisplay)));
        }
    }

    public string CountDisplay => Count < 1000 ? Count.ToString() : Count < 10000 ? $"{Count / 1000.0:0.0}k" : $"{Count / 1000}k";

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value)
                return;

            _isRenaming = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRenaming)));
        }
    }
}
