using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Platform;

namespace OpenDelulu.Localization;

/// <summary>A language the UI can be shown in.</summary>
/// <param name="Code">File name without extension (en, vi, pt-BR…).</param>
/// <param name="Name">The language's own name (from the file's "_language" entry).</param>
public sealed record LanguageInfo(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// UI strings by key, loaded from flat JSON files: <c>Assets/Lang/&lt;code&gt;.json</c> shipped with the app, plus
/// any <c>&lt;code&gt;.json</c> in the user's <see cref="UserLanguageFolder"/> or a <c>lang</c> folder next to the
/// executable (to try a translation without rebuilding). English is the source language: a key missing in the
/// current language falls back to English, then to the key itself. See docs/TRANSLATING.md.
/// </summary>
public sealed class Translator : INotifyPropertyChanged
{
    public const string SourceLanguage = "en";
    const string Assets = "avares://OpenDelulu/Assets/Lang/";

    public static Translator Instance { get; } = new();

    readonly Dictionary<string, Func<Stream>> _sources = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _english = [];
    Dictionary<string, string> _current = [];
    readonly List<WeakReference<Action>> _listeners = [];

    Translator()
    {
        Discover();
        _english = Load(SourceLanguage);
        _current = _english;
        Languages = _sources.Keys
            .Select(code => new LanguageInfo(code, NameOf(code)))
            .OrderBy(l => l.Code == SourceLanguage ? 0 : 1).ThenBy(l => l.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Extra translations dropped here are listed too (and override the shipped file of the same code).</summary>
    public static string UserLanguageFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDelulu", "lang");

    public IReadOnlyList<LanguageInfo> Languages { get; }

    public string Language { get; private set; } = SourceLanguage;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after <see cref="SetLanguage"/>, for strings built in code.</summary>
    public event Action? LanguageChanged;

    /// <summary>The string for a key (XAML binds to this indexer through <see cref="TrExtension"/>).</summary>
    public string this[string key] =>
        _current.TryGetValue(key, out var text) || _english.TryGetValue(key, out text) ? text : key;

    public static string Get(string key) => Instance[key];

    /// <summary>A string with {0}, {1}… placeholders filled in (current UI culture for numbers).</summary>
    public static string Format(string key, params object?[] args)
    {
        string pattern = Instance[key];
        try
        {
            return string.Format(CultureInfo.CurrentCulture, pattern, args);
        }
        catch (FormatException)
        {
            return pattern; // a broken translation must not crash the app
        }
    }

    public void SetLanguage(string? code)
    {
        code = code is not null && _sources.ContainsKey(code) ? code : SourceLanguage;
        Language = code;
        _current = code.Equals(SourceLanguage, StringComparison.OrdinalIgnoreCase) ? _english : Load(code);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
        lock (_listeners)
        {
            _listeners.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in _listeners.ToList())
                if (weak.TryGetTarget(out var listener))
                    listener();
        }
    }

    /// <summary>Calls <paramref name="listener"/> after each language change without keeping its owner alive.</summary>
    public void Subscribe(Action listener)
    {
        lock (_listeners)
            _listeners.Add(new WeakReference<Action>(listener));
    }

    /// <summary>All keys of the source language (for tooling).</summary>
    public IReadOnlyCollection<string> SourceKeys => _english.Keys;

    void Discover()
    {
        try
        {
            foreach (var uri in AssetLoader.GetAssets(new Uri(Assets), null))
                if (uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var asset = uri;
                    _sources[Path.GetFileNameWithoutExtension(uri.AbsolutePath)] = () => AssetLoader.Open(asset);
                }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            // No Avalonia asset loader (e.g. unit tests): only folders below.
        }

        foreach (var folder in new[] { Path.Combine(AppContext.BaseDirectory, "lang"), UserLanguageFolder })
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                string path = file;
                _sources[Path.GetFileNameWithoutExtension(file)] = () => File.OpenRead(path);
            }
        }
    }

    Dictionary<string, string> Load(string code)
    {
        if (!_sources.TryGetValue(code, out var open))
            return [];
        try
        {
            using var stream = open();
            return Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Flat JSON object of strings; keys starting with "_" are metadata, other non-strings are ignored.</summary>
    public static Dictionary<string, string> Parse(Stream stream)
    {
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = property.Value.GetString()!;
        return result;
    }

    string NameOf(string code) => Load(code).TryGetValue("_language", out var name) ? name : code;
}
