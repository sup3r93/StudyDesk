using System.Windows;
using StudyDesk.Contracts;
using StudyDesk.Desktop.Dialogs;
using StudyDesk.Desktop.Localization;
using StudyDesk.Desktop.Synchronization;

namespace StudyDesk.Desktop;

/// <summary>Coordinates explicit native server synchronization while preserving local-first behavior.</summary>
public partial class MainWindow
{
    /// <summary>Pairs the local workspace only after an explicit native confirmation and token check.</summary>
    private async void PairServerClicked(object sender, RoutedEventArgs e)
    {
        if (!initialized || busy || navigating)
        {
            return;
        }
        await RunSafelyAsync(async () =>
        {
            await SaveCurrentAsync(true);
            PairServerDialog dialog = new(this, settings);
            if (dialog.ShowDialog() == true)
            {
                connection = dialog.Connection;
                UpdateConnectionStatus();
            }
        });
    }

    /// <summary>Pushes operations in order, checks the server generation and applies fresh snapshots.</summary>
    private async void SynchronizeClicked(object sender, RoutedEventArgs e)
    {
        if (!initialized || busy || navigating || connection is not ServerConnection paired)
        {
            return;
        }
        busy = true;
        synchronization = new CancellationTokenSource();
        CancellationToken cancellationToken = synchronization.Token;
        CancelSyncButton.Visibility = Visibility.Visible;
        CancelSyncButton.IsEnabled = true;
        UpdateActions();
        try
        {
            await SetEditorReadOnlyAsync(true);
            await SaveCurrentAsync(true);
            using DataServerClient client = new(paired.ServerUri, connections.LoadToken(paired), settings.AllowInsecureLoopback);
            ServerInfoResponse info = await client.GetInfoAsync(cancellationToken);
            if (info.ProtocolVersion != 1 || info.ServerId != paired.ServerId || info.Generation != paired.Generation)
            {
                throw new InvalidOperationException("The paired server identity changed. Local work has been preserved.");
            }

            int sent = 0;
            bool conflict = false;
            while (true)
            {
                var pending = await store.GetPendingOperationsAsync(cancellationToken);
                if (pending.Count == 0)
                {
                    break;
                }
                // A single ordered operation makes each dependency's new version visible locally.
                SyncPushResponse push = await client.PushAsync(new SyncPushRequest(paired.Generation, [pending[0]]), cancellationToken);
                SyncSnapshotResponse snapshot = await client.GetSnapshotAsync(cancellationToken);
                ValidateSnapshot(paired, snapshot, push);
                if (push.Receipts.Count != 1 || push.Receipts[0].OperationId != pending[0].OperationId)
                {
                    throw new InvalidOperationException("The synchronization receipt does not match the submitted operation.");
                }

                if (push.Receipts[0].Status is not ("accepted" or "duplicate" or "conflict" or "invalid" or "forbidden"))
                {
                    throw new InvalidOperationException("The server returned an unsupported receipt status.");
                }

                await store.ApplySyncAsync(snapshot, push, cancellationToken);
                sent++;
                if (sent >= 10000)
                {
                    break;
                }
                if (push.Receipts[0].Status is not ("accepted" or "duplicate"))
                {
                    conflict = true;
                    continue;
                }

                ConnectionStatus.Text = LocalizationService.Instance.Format("status.syncProgress", sent);
            }

            if (!conflict)
            {
                SyncSnapshotResponse snapshot = await client.GetSnapshotAsync(cancellationToken);
                SyncPushResponse empty = new(paired.Generation, snapshot.Cursor, []);
                ValidateSnapshot(paired, snapshot, empty);
                await store.ApplySyncAsync(snapshot, empty, cancellationToken);
            }

            Guid? selected = current?.Id;
            await ReloadSpacesAsync();
            if (selected.HasValue && await store.GetPageAsync(selected.Value) is PageDto updated)
            {
                await OpenPageAsync(updated, editing);
                await ReloadPagesAsync();
            }

            await RefreshIssuesAsync();
            conflict = (await store.GetSyncIssuesAsync()).Count > 0;
            var remaining = await store.GetPendingOperationsAsync(cancellationToken);
            ConnectionStatus.Text = conflict ? LocalizationService.Instance["status.syncConflict"]
                : remaining.Count > 0 ? LocalizationService.Instance["status.syncPending"] : LocalizationService.Instance["status.syncComplete"];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConnectionStatus.Text = LocalizationService.Instance["status.syncCanceled"];
        }
        catch (System.Net.Http.HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            ConnectionStatus.Text = LocalizationService.Instance["status.syncChanged"];
        }
        catch (Exception error) when (IsExpectedError(error))
        {
            ConnectionStatus.Text = LocalizationService.Instance["status.syncFailed"];
            ShowFailure();
        }
        finally
        {
            synchronization.Dispose();
            synchronization = null;
            CancelSyncButton.Visibility = Visibility.Collapsed;
            busy = false;
            UpdateActions();
            await RunSafelyAsync(() => SetEditorReadOnlyAsync(!editing));
        }
    }

    /// <summary>Stops synchronization without removing pending or unacknowledged local operations.</summary>
    private void CancelSyncClicked(object sender, RoutedEventArgs e)
    {
        synchronization?.Cancel();
        CancelSyncButton.IsEnabled = false;
        ConnectionStatus.Text = LocalizationService.Instance["status.syncStopping"];
    }

    /// <summary>Refuses snapshots from a replaced server or history generation.</summary>
    private static void ValidateSnapshot(ServerConnection paired, SyncSnapshotResponse snapshot, SyncPushResponse push)
    {
        if (snapshot.ServerId != paired.ServerId || snapshot.Generation != paired.Generation || push.Generation != paired.Generation || snapshot.Cursor < push.Cursor)
        {
            throw new InvalidOperationException("The server synchronization history changed.");
        }
    }

    /// <summary>Shows association state without rendering any credential in the editor.</summary>
    private void UpdateConnectionStatus()
    {
        ConnectionStatus.Text = knownIssueCount > 0 ? LocalizationService.Instance.Format("status.issuesCount", knownIssueCount)
            : connection is null ? LocalizationService.Instance["status.offline"] : LocalizationService.Instance.Format("status.connectedServer", connection.ServerUri.Host);
        UpdateActions();
    }
}
