using System.Windows;
using CefSharp;
using StudyDesk.Contracts;
using StudyDesk.Desktop.Localization;

namespace StudyDesk.Desktop;

/// <summary>Gives an empty library, selected space and empty trash their own next action.</summary>
public partial class MainWindow
{
    /// <summary>Clears the displayed document only after the caller has saved the previous draft.</summary>
    private async Task ClearPageAsync()
    {
        JavascriptResponse response = await EditorBrowser.EvaluateScriptAsync("window.studyDesk.clear()").WaitAsync(TimeSpan.FromSeconds(5));
        if (!response.Success)
        {
            throw new InvalidOperationException("The editor could not clear its previous document safely.");
        }

        current = null;
        contentJson = EmptyDocument;
        sessionId = string.Empty;
        sequence = 0;
        revision = 0;
        savedRevision = 0;
        editing = false;
        loading = true;
        TitleBox.Text = LocalizationService.Instance["empty.title"];
        loading = false;
        PageSummary.Text = LocalizationService.Instance["empty.summary"];
        DetailsText.Text = LocalizationService.Instance["details.empty"];
        UpdateActions();
    }

    /// <summary>Chooses the empty message from the actual selected space and trash filter.</summary>
    private void UpdateEmptyState()
    {
        LocalizationService language = LocalizationService.Instance;
        bool trash = ShowTrash.IsChecked == true;
        SpaceDto? space = SpacePicker.SelectedItem as SpaceDto;
        EmptyStateAction.IsEnabled = initialized && !busy && !navigating;
        if (trash)
        {
            EmptyStateHeading.Text = language["empty.trashHeading"];
            EmptyStateBody.Text = language["empty.trashBody"];
            EmptyStateAction.Content = language["empty.backToNotes"];
            SidebarEmptyHeading.Text = language["sidebar.emptyTrashHeading"];
            SidebarEmptyBody.Text = language["sidebar.emptyTrashBody"];
        }
        else if (space is not null)
        {
            bool hasNotes = PageList.Items.Count > 0;
            EmptyStateHeading.Text = language[hasNotes ? "empty.chooseHeading" : "empty.noteHeading"];
            EmptyStateBody.Text = hasNotes ? language["empty.chooseBody"] : language.Format("empty.noteBody", space.Title);
            EmptyStateAction.Content = language["empty.createNote"];
            SidebarEmptyHeading.Text = language["sidebar.emptyNotesHeading"];
            SidebarEmptyBody.Text = language["sidebar.emptyNotesBody"];
        }
        else
        {
            EmptyStateHeading.Text = language["empty.heading"];
            EmptyStateBody.Text = language["empty.body"];
            EmptyStateAction.Content = language["empty.createSpace"];
            SidebarEmptyHeading.Text = language["sidebar.emptyHeading"];
            SidebarEmptyBody.Text = language["sidebar.emptyBody"];
        }
    }

    /// <summary>Runs the next meaningful action instead of repeatedly asking for another space.</summary>
    private void EmptyStateActionClicked(object sender, RoutedEventArgs e)
    {
        if (!initialized || busy || navigating)
        {
            return;
        }

        if (ShowTrash.IsChecked == true)
        {
            ShowTrash.IsChecked = false;
        }
        else if (SpacePicker.SelectedItem is SpaceDto)
        {
            NewNoteClicked(sender, e);
        }
        else
        {
            NewSpaceClicked(sender, e);
        }
    }
}
