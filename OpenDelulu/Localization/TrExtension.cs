using System;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace OpenDelulu.Localization;

/// <summary>
/// <c>Text="{l:Tr play.enter}"</c>: a translated UI string that follows language changes.
/// </summary>
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new TranslationObservable(Key).ToBinding();

    /// <summary>Emits the current translation on subscribe and again after each language change.</summary>
    sealed class TranslationObservable(string key) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            observer.OnNext(Translator.Get(key));
            void Changed() => observer.OnNext(Translator.Get(key));
            Translator.Instance.LanguageChanged += Changed;
            return new Unsubscriber(() => Translator.Instance.LanguageChanged -= Changed);
        }
    }

    sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
