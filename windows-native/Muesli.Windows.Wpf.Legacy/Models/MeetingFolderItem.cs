using System.ComponentModel;
using System.Windows;

namespace Muesli.Windows;

public sealed class MeetingFolderItem : INotifyPropertyChanged
{
    private int _count;
    private bool _isRenaming;
    private string _name;

    public MeetingFolderItem(string id, string name, string? parentId = null)
    {
        Id = id;
        _name = name;
        ParentId = parentId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string? ParentId { get; set; }
    public int Depth { get; set; }
    public Thickness Indent => new(8 + Depth * 14, 0, 0, 0);

    public void NotifyIndentChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Indent)));

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
