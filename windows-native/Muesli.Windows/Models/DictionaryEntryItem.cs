using System.ComponentModel;
using Muesli.Windows.Services;

namespace Muesli.Windows;

public sealed class DictionaryEntryItem : INotifyPropertyChanged
{
    private string _phrase;
    private string _replacement;
    private double _matchingThreshold;

    public DictionaryEntryItem(DictionaryEntryRecord record)
        : this(record.Id, record.Phrase, record.Replacement, record.MatchingThreshold)
    {
    }

    public DictionaryEntryItem(string id, string phrase, string replacement, double matchingThreshold)
    {
        Id = id;
        _phrase = phrase;
        _replacement = replacement;
        _matchingThreshold = matchingThreshold <= 0 ? 0.85 : matchingThreshold;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }

    public string Phrase
    {
        get => _phrase;
        set
        {
            if (_phrase == value)
                return;

            _phrase = value;
            NotifyDictionaryChanged();
        }
    }

    public string Replacement
    {
        get => _replacement;
        set
        {
            if (_replacement == value)
                return;

            _replacement = value;
            NotifyDictionaryChanged();
        }
    }

    public double MatchingThreshold
    {
        get => _matchingThreshold;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0.70, 0.95), 2);
            if (Math.Abs(_matchingThreshold - next) < 0.001)
                return;

            _matchingThreshold = next;
            NotifyDictionaryChanged();
        }
    }

    public string ThresholdDisplay => MatchingThreshold.ToString("0.00");
    public string Display => $"{Phrase} → {Replacement}";

    public DictionaryEntryRecord Record => new()
    {
        Id = Id,
        Phrase = Phrase,
        Replacement = Replacement,
        MatchingThreshold = MatchingThreshold
    };

    private void NotifyDictionaryChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Record)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThresholdDisplay)));
    }
}
