using System.IO;
using StudyDesk.Desktop.Configuration;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Checks local preference persistence while preserving private configuration and note storage paths.</summary>
public sealed partial class DesktopBoundaryTests
{
    /// <summary>A chosen language survives a fresh settings load without altering other environment entries.</summary>
    [Fact]
    public void LanguagePreferenceSurvivesReloadAndPreservesOtherSettings()
    {
        using PrivateWorkspace fixture = new();
        DesktopSettings settings = DesktopSettings.Load();
        string dataDirectory = settings.DataDirectory;
        string tokenFile = settings.TokenFile;
        settings.SaveLanguagePreference("it");
        DesktopSettings italian = DesktopSettings.Load();
        Assert.Equal("it", italian.Language);
        Assert.Equal("it", italian.EffectiveLanguage);
        Assert.Equal(dataDirectory, italian.DataDirectory);
        Assert.Equal(tokenFile, italian.TokenFile);
        italian.SaveLanguagePreference("en");
        Assert.Equal("en", DesktopSettings.Load().EffectiveLanguage);
    }

    /// <summary>The parser and updater agree on keys containing surrounding whitespace.</summary>
    [Fact]
    public void LanguagePreferenceUpdatesSpacedKeysWithoutDuplicates()
    {
        using PrivateWorkspace fixture = new();
        DesktopSettings.Load();
        string file = Path.Combine(fixture.Root, ".env");
        string original = File.ReadAllText(file).Replace("STUDYDESK_DESKTOP_LANGUAGE=auto", "STUDYDESK_DESKTOP_LANGUAGE = it", StringComparison.Ordinal);
        File.WriteAllText(file, original);
        DesktopSettings settings = DesktopSettings.Load();
        Assert.Equal("it", settings.Language);
        settings.SaveLanguagePreference("en");
        Assert.Equal("en", DesktopSettings.Load().Language);
        Assert.Single(File.ReadAllLines(file), line => line.Contains("STUDYDESK_DESKTOP_LANGUAGE", StringComparison.Ordinal));
    }

    /// <summary>Unsupported languages cannot corrupt a saved preference or any other setting.</summary>
    [Fact]
    public void InvalidLanguageLeavesThePrivateEnvironmentUnchanged()
    {
        using PrivateWorkspace fixture = new();
        DesktopSettings settings = DesktopSettings.Load();
        string file = Path.Combine(fixture.Root, ".env");
        byte[] before = File.ReadAllBytes(file);
        Assert.Throws<ArgumentException>(() => settings.SaveLanguagePreference("unsupported"));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal("auto", settings.Language);
    }
}
