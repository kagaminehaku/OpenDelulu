using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenDelulu.Localization;

/// <summary>
/// A list entry whose text is translated and follows language changes (combo box items keep their selection,
/// unlike a list of plain strings that would have to be rebuilt). Shown through the DataTemplate in MainView.
/// </summary>
public sealed class LocalizedText : ObservableObject
{
    readonly Func<string> _produce;
    readonly Action _refresh;

    public LocalizedText(Func<string> produce)
    {
        _produce = produce;
        _refresh = () => OnPropertyChanged(nameof(Text));
        Translator.Instance.Subscribe(_refresh);
    }

    public LocalizedText(string key) : this(() => Translator.Get(key))
    {
    }

    public string Text => _produce();

    public override string ToString() => Text;
}
