using System.Collections.Generic;
using System.ComponentModel;
using Muesli.Windows.Services;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>MAC-37: an unchanged refresh raises no PropertyChanged (macOS #632 feedback loop).</summary>
public sealed class BoundListsTests
{
    /// <summary>Same equality rule as CommunityToolkit's SetProperty (EqualityComparer.Default).</summary>
    private sealed class Bound : INotifyPropertyChanged
    {
        private IReadOnlyList<string> _items = [];
        public event PropertyChangedEventHandler? PropertyChanged;
        public IReadOnlyList<string> Items
        {
            get => _items;
            set
            {
                if (EqualityComparer<IReadOnlyList<string>>.Default.Equals(_items, value)) return;
                _items = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Items)));
            }
        }
        public void Refresh(IReadOnlyList<string> next) => Items = BoundLists.Same(Items, next);
    }

    [Fact]
    public void Unchanged_refresh_raises_nothing_and_a_change_raises_once()
    {
        var bound = new Bound();
        bound.Refresh(["Automatic", "Mic"]);
        var raised = new List<string?>();
        bound.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        bound.Refresh(new List<string> { "Automatic", "Mic" });
        Assert.Empty(raised);

        bound.Refresh(["Automatic", "Mic", "Headset"]);
        Assert.Equal([nameof(Bound.Items)], raised);
    }
}
