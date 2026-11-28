using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StudyDesk.Desktop.Localization;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests.Localization;

/// <summary>Checks resource parity, immutable editor catalogs, formatting and native binding notifications.</summary>
public sealed partial class LocalizationServiceTests
{
    /// <summary>Both packaged catalogs cover the same semantic keys and use the same positional arguments.</summary>
    [Fact]
    public void CatalogsHaveIdenticalKeysAndFormatArguments()
    {
        LocalizationService english = new("en");
        LocalizationService italian = new("it");
        Assert.True(english.Translations.Count >= 100);
        Assert.Equal(english.Translations.Keys.Order(StringComparer.Ordinal), italian.Translations.Keys.Order(StringComparer.Ordinal));
        foreach (string key in english.Translations.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(english[key]));
            Assert.False(string.IsNullOrWhiteSpace(italian[key]));
            Assert.Equal(Indices(english[key]), Indices(italian[key]));
            Assert.Equal(CompositeFormat.Parse(english[key]).MinimumArgumentCount,
                CompositeFormat.Parse(italian[key]).MinimumArgumentCount);
        }

        Assert.Equal("Bold", english["editor.bold"]);
        Assert.Equal("Grassetto", italian["editor.bold"]);
        Assert.Equal("Dettagli", italian["tabs.details"]);
    }

    /// <summary>Independent instances format numbers using their own culture without affecting global state.</summary>
    [Fact]
    public void SelectedCultureAndInstancesDoNotMutateGlobalCulture()
    {
        CultureInfo beforeCulture = CultureInfo.CurrentCulture;
        CultureInfo beforeUiCulture = CultureInfo.CurrentUICulture;
        LocalizationService english = new("en");
        LocalizationService italian = new("it");
        Assert.Equal("Zoom 12.5%", english.Format("editor.zoom", 12.5m));
        Assert.Equal("Zoom 12,5%", italian.Format("editor.zoom", 12.5m));
        Assert.Equal("en-US", english.Culture.Name);
        Assert.Equal("it-IT", italian.Culture.Name);
        Assert.True(english.Culture.IsReadOnly);
        italian.SetLanguage("EN");
        Assert.Equal("en", italian.CurrentLanguage);
        Assert.Equal("en", english.CurrentLanguage);
        Assert.Same(beforeCulture, CultureInfo.CurrentCulture);
        Assert.Same(beforeUiCulture, CultureInfo.CurrentUICulture);
    }

    /// <summary>The UI receives all binding notifications once, while selecting the existing language is a no-op.</summary>
    [Fact]
    public void LanguageChangesRefreshNativeIndexerAndCatalogBindings()
    {
        LocalizationService service = new("en");
        List<string> changed = [];
        service.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);
        service.SetLanguage("it");
        Assert.Equal(["CurrentLanguage", "Culture", "Translations", "Item[]"], changed.ToArray());
        Assert.Equal("Nuovo appunto", service["toolbar.newNote"]);
        service.SetLanguage("IT");
        Assert.Equal(4, changed.Count);
    }

    /// <summary>Invalid preferences cannot silently replace the selected language or emit false change events.</summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("it-IT")]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedLanguageLeavesExistingSelectionUnchanged(string? language)
    {
        LocalizationService service = new("it");
        int changes = 0;
        service.PropertyChanged += (_, _) => changes++;
        Assert.ThrowsAny<ArgumentException>(() => service.SetLanguage(language!));
        Assert.Equal("it", service.CurrentLanguage);
        Assert.Equal(0, changes);
    }

    /// <summary>Renderer catalogs remain immutable snapshots even after the native language changes.</summary>
    [Fact]
    public void TranslationSnapshotsAreImmutableAndUnknownKeysRemainVisible()
    {
        LocalizationService service = new("en");
        IReadOnlyDictionary<string, string> previous = service.Translations;
        IDictionary<string, string> mutableView = Assert.IsAssignableFrom<IDictionary<string, string>>(previous);
        Assert.Throws<NotSupportedException>(() => mutableView.Add("editor.injected", "Unexpected"));
        service.SetLanguage("it");
        Assert.Equal("Bold", previous["editor.bold"]);
        Assert.Equal("Grassetto", service.Translations["editor.bold"]);
        Assert.Equal("[unknown.semantic.key]", service["unknown.semantic.key"]);
        Assert.DoesNotContain("editor.injected", service.Translations.Keys);
    }

    /// <summary>All formatted resources can be rendered in both supported languages using their declared arguments.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("it")]
    public void EveryResourceRendersUsingItsDeclaredArguments(string language)
    {
        LocalizationService service = new(language);
        foreach ((string key, string template) in service.Translations)
        {
            int count = CompositeFormat.Parse(template).MinimumArgumentCount;
            object?[] arguments = Enumerable.Range(0, count)
                .Select(index => index == 2 ? (object)new DateTime(2026, 11, 28, 14, 30, 0, DateTimeKind.Unspecified) : 42)
                .ToArray();
            Assert.False(string.IsNullOrWhiteSpace(service.Format(key, arguments)));
        }
    }

    /// <summary>Details format dates using the selected culture and preserve user text as data, including braces.</summary>
    [Fact]
    public void DetailsFormatDatesWithoutTranslatingOrReinterpretingUserContent()
    {
        DateTime timestamp = new(2026, 11, 28, 14, 30, 0, DateTimeKind.Unspecified);
        const string userTitle = "My appunti {0} <data>";
        const string userSpace = "Biologia / Biology";
        LocalizationService service = new("it");
        string italian = service.Format("details.template", userTitle, userSpace, timestamp, 7);
        Assert.StartsWith(userTitle + "\n\n", italian, StringComparison.Ordinal);
        Assert.Contains("Spazio: " + userSpace, italian, StringComparison.Ordinal);
        Assert.Contains("Ultimo salvataggio: " + timestamp.ToString("g", CultureInfo.GetCultureInfo("it-IT")), italian, StringComparison.Ordinal);
        service.SetLanguage("en");
        string english = service.Format("details.template", userTitle, userSpace, timestamp, 7);
        Assert.StartsWith(userTitle + "\n\n", english, StringComparison.Ordinal);
        Assert.Contains("Space: " + userSpace, english, StringComparison.Ordinal);
        Assert.Contains("Last saved: " + timestamp.ToString("g", CultureInfo.GetCultureInfo("en-US")), english, StringComparison.Ordinal);
    }

    /// <summary>Extracts argument sets independently from the resource loader's composite-format parser.</summary>
    private static IEnumerable<int> Indices(string template) => ArgumentPattern().Matches(template)
        .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order();

    /// <summary>Matches positional indices while excluding escaped literal opening braces.</summary>
    [GeneratedRegex(@"(?<!\{)\{(\d+)")]
    private static partial Regex ArgumentPattern();
}
