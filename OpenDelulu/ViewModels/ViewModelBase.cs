using System;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Localization;

namespace OpenDelulu.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    readonly Action _languageChanged;

    protected ViewModelBase()
    {
        _languageChanged = OnLanguageChanged;
        Translator.Instance.Subscribe(_languageChanged);
    }

    /// <summary>Re-raise change notifications of properties whose text is built in code from translations.</summary>
    protected virtual void OnLanguageChanged()
    {
    }

    protected static string T(string key) => Translator.Get(key);

    protected static string F(string key, params object?[] args) => Translator.Format(key, args);
}
