using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using CefSharp;
using StudyDesk.Desktop.Localization;

namespace StudyDesk.Desktop;

/// <summary>Connects the owner's saved language choice to native bindings and the isolated editor.</summary>
public partial class MainWindow
{
    private bool selectingLanguage;

    /// <summary>Selects the persisted preference without writing it again during startup.</summary>
    private void InitializeLanguageSelection()
    {
        selectingLanguage = true;
        Language = XmlLanguage.GetLanguage(LocalizationService.Instance.Culture.IetfLanguageTag);
        LanguagePicker.SelectedItem = LanguagePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, settings.Language));
        selectingLanguage = false;
    }

    /// <summary>Persists a deliberate language change without translating any stored note or title.</summary>
    private async void LanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (selectingLanguage || LanguagePicker.SelectedItem is not ComboBoxItem item || item.Tag is not string preference)
        {
            return;
        }

        await RunSafelyAsync(async () =>
        {
            settings.SaveLanguagePreference(preference);
            LocalizationService.Instance.SetLanguage(settings.EffectiveLanguage);
            Language = XmlLanguage.GetLanguage(LocalizationService.Instance.Culture.IetfLanguageTag);
            if (current is null)
            {
                TitleBox.Text = LocalizationService.Instance["empty.title"];
                PageSummary.Text = LocalizationService.Instance["empty.summary"];
                DetailsText.Text = LocalizationService.Instance["details.empty"];
            }
            else
            {
                UpdateDetails();
            }

            UpdateActions();
            UpdateConnectionStatus();
            if (initialized)
            {
                await RefreshIssuesAsync();
                SaveStatus.Text = revision == savedRevision ? LocalizationService.Instance["status.saved"] : LocalizationService.Instance["status.unsaved"];
            }

            if (rendererApiReady)
            {
                await ApplyEditorLanguageAsync();
            }
        });
    }

    /// <summary>Transfers only embedded UI translations; note content and native credentials are untouched.</summary>
    private async Task ApplyEditorLanguageAsync()
    {
        string language = JsonSerializer.Serialize(LocalizationService.Instance.CurrentLanguage);
        string dictionary = JsonSerializer.Serialize(LocalizationService.Instance.Translations);
        JavascriptResponse result = await EditorBrowser.EvaluateScriptAsync("window.studyDesk.setLanguage(" + language + "," + dictionary + ")");
        if (!result.Success)
        {
            throw new InvalidOperationException("The editor could not apply its packaged UI language.");
        }
    }
}
