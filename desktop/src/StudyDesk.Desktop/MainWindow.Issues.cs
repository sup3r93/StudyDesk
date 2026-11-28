using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using StudyDesk.Contracts;
using StudyDesk.Desktop.Configuration;
using StudyDesk.Desktop.Core.Storage;
using StudyDesk.Desktop.Localization;

namespace StudyDesk.Desktop;

/// <summary>Explains rejected uploads and requires explicit choices before replacing a local draft.</summary>
public partial class MainWindow
{
    /// <summary>Pairs a durable issue with a safe local label for the native list.</summary>
    /// <param name="Issue">The durable storage issue.</param>
    /// <param name="Label">A local title and protocol status without remote error details.</param>
    private sealed record IssueItem(LocalSyncIssue Issue, string Label);

    /// <summary>Refreshes blocked entities without freezing or uploading pending operations.</summary>
    private async Task RefreshIssuesAsync()
    {
        var spaces = await store.GetSpacesAsync();
        List<IssueItem> items = [];
        foreach (LocalSyncIssue issue in await store.GetSyncIssuesAsync())
        {
            PageDto? page = await store.GetPageAsync(issue.EntityId);
            string name = page?.Title ?? spaces.FirstOrDefault(space => space.Id == issue.EntityId)?.Title ?? issue.EntityId.ToString();
            items.Add(new IssueItem(issue, LocalizationService.Instance.Format("issues.label", LocalizationService.Instance["issue." + issue.Status], name)));
        }

        knownIssueCount = items.Count;
        IssuesList.ItemsSource = items;
        IssuesStatus.Text = items.Count == 0 ? LocalizationService.Instance["issues.none"]
            : LocalizationService.Instance["issues.instructions"];
        UpdateIssueActions();
    }

    /// <summary>Updates the explicit resolution commands when a durable issue is selected.</summary>
    private void IssueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateIssueActions();
    }

    /// <summary>Enables resolution only when the installation owner selected a known issue.</summary>
    private void UpdateIssueActions()
    {
        bool enabled = initialized && !busy && IssuesList.SelectedItem is IssueItem;
        RetryDraftButton.IsEnabled = enabled;
        UseServerButton.IsEnabled = enabled;
    }

    /// <summary>Lets the owner inspect existing conflicts without contacting a server.</summary>
    private async void RefreshIssuesClicked(object sender, RoutedEventArgs e)
    {
        if (initialized && !busy)
        {
            await RunSafelyAsync(RefreshIssuesAsync);
        }
    }

    /// <summary>Queues a new conditional intent only after an explicit choice to keep the local draft.</summary>
    private async void RetryDraftClicked(object sender, RoutedEventArgs e)
    {
        if (busy || IssuesList.SelectedItem is not IssueItem selected)
        {
            return;
        }

        if (MessageBox.Show(this, LocalizationService.Instance["dialog.retryDraft"], LocalizationService.Instance["app.title"], MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunExclusiveAsync(async () =>
        {
            await SaveCurrentAsync(true);
            await store.ResolveSyncIssueAsync(selected.Issue.EntityId, true);
            await RefreshIssuesAsync();
            ConnectionStatus.Text = LocalizationService.Instance["status.draftRetained"];
        });
    }

    /// <summary>Writes a durable recovery JSON before restoring the last downloaded server version.</summary>
    private async void UseServerClicked(object sender, RoutedEventArgs e)
    {
        if (busy || IssuesList.SelectedItem is not IssueItem selected)
        {
            return;
        }

        if (MessageBox.Show(this, LocalizationService.Instance["dialog.useServer"], LocalizationService.Instance["app.title"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunExclusiveAsync(async () =>
        {
            await SaveCurrentAsync(true);
            Guid entityId = selected.Issue.EntityId;
            object entity = (object?)await store.GetPageAsync(entityId)
                ?? (await store.GetSpacesAsync()).FirstOrDefault(space => space.Id == entityId)
                ?? throw new InvalidOperationException("The local item cannot be exported for recovery.");
            string recovery = Path.Combine(settings.DataDirectory, "recovery");
            PrivateDirectoryPolicy.Ensure(recovery);
            string path = Path.Combine(recovery, entityId.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".json");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { format = "studydesk-conflict-recovery", formatVersion = 1, entity }, JsonOptions);
            await using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(true);
            }

            await store.ResolveSyncIssueAsync(entityId, false);
            await ReloadSpacesAsync();
            if (current is not null && await store.GetPageAsync(current.Id) is PageDto page)
            {
                await OpenPageAsync(page, false);
                await ReloadPagesAsync();
            }

            await RefreshIssuesAsync();
            IssuesStatus.Text = LocalizationService.Instance["status.serverRestored"];
        });
    }
}
