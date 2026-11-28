using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace StudyDesk.Desktop.Localization;

/// <summary>Provides immutable embedded UI resources and instance-local formatting without translating notes.</summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly Lazy<IReadOnlyDictionary<string, LanguageCatalog>> Catalogs = new(LoadCatalogs);
    private LanguageCatalog current;

    /// <summary>Gets the shared UI service; startup selects its persisted language before showing windows.</summary>
    public static LocalizationService Instance { get; } = new();

    /// <summary>Creates an independent service for a selected supported language.</summary>
    /// <param name="language">The application language code, either it or en.</param>
    public LocalizationService(string language = "en")
    {
        current = Catalogs.Value[NormalizeLanguage(language)];
    }

    /// <summary>Notifies native bindings when the language, culture, catalog or indexer changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the normalized application language code.</summary>
    public string CurrentLanguage => current.Code;

    /// <summary>Gets a read-only formatting culture without changing process or thread culture.</summary>
    public CultureInfo Culture => current.Culture;

    /// <summary>Gets the immutable selected catalog, also suitable for the isolated editor's language message.</summary>
    public IReadOnlyDictionary<string, string> Translations => current.Texts;

    /// <summary>Gets a UI resource or an explicit diagnostic marker for an unknown semantic key.</summary>
    public string this[string key]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return Lookup(current, key);
        }
    }

    /// <summary>Changes the selected language and refreshes WPF indexer bindings without global culture mutation.</summary>
    /// <param name="language">The application language code, either it or en.</param>
    public void SetLanguage(string language)
    {
        string code = NormalizeLanguage(language);
        if (string.Equals(current.Code, code, StringComparison.Ordinal))
        {
            return;
        }

        current = Catalogs.Value[code];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Translations)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>Formats a resource using this service's selected culture and positional arguments.</summary>
    public string Format(string key, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(arguments);
        LanguageCatalog selected = current;
        return string.Format(selected.Culture, Lookup(selected, key), arguments);
    }

    /// <summary>Accepts supported codes case-insensitively without silently choosing an unrelated language.</summary>
    private static string NormalizeLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        string code = language.ToLowerInvariant();
        return code is "it" or "en" ? code
            : throw new ArgumentOutOfRangeException(nameof(language), "Supported application languages are it and en.");
    }

    /// <summary>Preserves a visible missing-key marker so incomplete localization cannot silently disappear.</summary>
    private static string Lookup(LanguageCatalog catalog, string key) =>
        catalog.Texts.TryGetValue(key, out string? value) ? value : "[" + key + "]";

    /// <summary>Loads both catalogs once, requiring equal key sets and matching format arguments.</summary>
    private static ReadOnlyDictionary<string, LanguageCatalog> LoadCatalogs()
    {
        IReadOnlyDictionary<string, string> english = LoadResource("en");
        IReadOnlyDictionary<string, string> italian = LoadResource("it");
        if (!english.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(italian.Keys))
        {
            throw new InvalidDataException("The embedded UI catalogs must contain identical semantic keys.");
        }

        foreach (string key in english.Keys)
        {
            CompositeFormat.Parse(english[key]);
            CompositeFormat.Parse(italian[key]);
            if (!ArgumentIndices(english[key]).SetEquals(ArgumentIndices(italian[key])))
            {
                throw new InvalidDataException("Embedded UI format arguments must match between languages.");
            }
        }

        return new ReadOnlyDictionary<string, LanguageCatalog>(new Dictionary<string, LanguageCatalog>(StringComparer.Ordinal)
        {
            ["en"] = new("en", CultureInfo.GetCultureInfo("en-US"), english),
            ["it"] = new("it", CultureInfo.GetCultureInfo("it-IT"), italian),
        });
    }

    /// <summary>Reads only a packaged resource in the application's own assembly, without filesystem fallbacks.</summary>
    private static ReadOnlyDictionary<string, string> LoadResource(string language)
    {
        string name = "StudyDesk.Desktop.Localization." + language + ".json";
        using Stream stream = typeof(LocalizationService).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("The application is missing an embedded UI language catalog.");
        using JsonDocument document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("An embedded UI catalog must be a JSON object.");
        }

        Dictionary<string, string> texts = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != JsonValueKind.String
                || property.Value.GetString() is not { Length: > 0 } value || !texts.TryAdd(property.Name, value))
            {
                throw new InvalidDataException("An embedded UI catalog contains an invalid or duplicate resource.");
            }
        }

        if (texts.Count == 0)
        {
            throw new InvalidDataException("An embedded UI catalog cannot be empty.");
        }

        return new ReadOnlyDictionary<string, string>(texts);
    }

    /// <summary>Extracts positional indices after composite-format validation, respecting escaped opening braces.</summary>
    private static HashSet<int> ArgumentIndices(string template)
    {
        HashSet<int> result = [];
        for (int index = 0; index < template.Length; index++)
        {
            if (template[index] != '{')
            {
                continue;
            }

            if (index + 1 < template.Length && template[index + 1] == '{')
            {
                index++;
                continue;
            }

            int start = index + 1;
            int end = start;
            while (end < template.Length && char.IsAsciiDigit(template[end]))
            {
                end++;
            }

            result.Add(int.Parse(template.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture));
        }

        return result;
    }

    /// <summary>Keeps each language's identity, culture and immutable texts in one atomic selection.</summary>
    private sealed record LanguageCatalog(string Code, CultureInfo Culture, IReadOnlyDictionary<string, string> Texts);
}
