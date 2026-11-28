using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CefSharp;
using Microsoft.Data.Sqlite;
using StudyDesk.Contracts;
using StudyDesk.Desktop.Configuration;
using StudyDesk.Desktop.Core.Storage;
using StudyDesk.Desktop.Dialogs;
using StudyDesk.Desktop.Editor;
using StudyDesk.Desktop.Localization;
using StudyDesk.Desktop.Synchronization;

namespace StudyDesk.Desktop;

/// <summary>Owns the native offline workspace, durable saves and isolated editor lifecycle.</summary>
public partial class MainWindow : Window, IDisposable
{
    private const string EmptyDocument = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly DesktopSettings settings;
    private readonly SqliteLocalWorkspaceStore store;
    private readonly ConnectionStore connections;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private TaskCompletionSource editorReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer autosave = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string bridgeChallenge = Guid.NewGuid().ToString("N");
    private bool initializing;
    private bool storageReady;
    private bool rendererApiReady;
    private bool postMessageAvailable;
    private int receivedBridgeEvents;
    private int rejectedBridgeOrigins;
    private int rejectedBridgePayloads;
    private int frameChecksPassed;
    private int payloadChecksPassed;
    private int queuedUiMessages;
    private int callbackFailures;
    private string callbackFailureStage = "none";
    private string? callbackFailureType;
    private int parsedUiMessages;
    private int rootObjectMessages;
    private int rootStringMessages;
    private int readyTypeMessages;
    private int matchingChannelMessages;
    private int bridgeParseFailures;
    private int acknowledgedReadyEvents;
    private int documentChangeEvents;
    private int capturedSnapshots;
    private int committedSaves;
    private CancellationTokenSource? synchronization;
    private int knownIssueCount;
    private ServerConnection? connection;
    private PageDto? current;
    private string contentJson = EmptyDocument;
    private string sessionId = string.Empty;
    private long sequence;
    private long revision;
    private long savedRevision;
    private bool initialized;
    private bool loading;
    private bool navigating;
    private bool busy;
    private bool editing;
    private bool disposed;
    private bool closeApproved;
    private bool closePending;

    /// <summary>Creates a workspace against its local database without requiring a server.</summary>
    public MainWindow(DesktopSettings settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        LocalizationService.Instance.SetLanguage(settings.EffectiveLanguage);
        store = new SqliteLocalWorkspaceStore(settings.DataDirectory);
        connections = new ConnectionStore(settings);
        InitializeComponent();
        ApplyInitialWindowBounds();
        InitializeLanguageSelection();
        TitleBox.MaxLength = 200;
        EditorBrowser.RequestHandler = new LocalEditorRequestHandler();
        EditorBrowser.MenuHandler = new LocalizedEditorMenuHandler();
        EditorBrowser.AccessibilityHandler = new CefSharp.Wpf.Experimental.Accessibility.AccessibilityHandler(EditorBrowser);
        EditorBrowser.PackagedMessageReceived += EditorMessageReceived;
        EditorBrowser.JavascriptObjectRepository.Settings.JavascriptBindingApiEnabled = true;
        EditorBrowser.JavascriptObjectRepository.Settings.JavascriptBindingApiAllowOrigins = ["https://studydesk.local"];
        autosave.Tick += AutosaveTick;
        // Navigation is deferred until native resource registration has succeeded.
        UpdateActions();
    }

    /// <summary>Initializes durable storage and opens the previous server association if present.</summary>
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeWorkspaceAsync();
    }

    /// <summary>Retries a failed initialization without enabling commands against an unavailable editor.</summary>
    private async void RetryEditorClicked(object sender, RoutedEventArgs e)
    {
        if (!initialized && !initializing)
        {
            await InitializeWorkspaceAsync();
        }
    }

    /// <summary>Requires a native-observed ready acknowledgement before enabling offline document commands.</summary>
    private async Task InitializeWorkspaceAsync()
    {
        if (initializing || initialized || disposed)
        {
            return;
        }

        initializing = true;
        RetryEditorButton.Visibility = Visibility.Collapsed;
        InitializationBanner.Visibility = Visibility.Visible;
        InitializationStatus.Text = LocalizationService.Instance["status.initializing"];
        SaveStatus.Text = LocalizationService.Instance["status.openingWorkspace"];
        bridgeChallenge = Guid.NewGuid().ToString("N");
        editorReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateActions();
        try
        {
            await store.InitializeAsync();
            storageReady = true;
            await LocalEditorBrowserProcessHandler.Registration.WaitAsync();
            EditorBrowser.Address = LocalEditorPolicy.EntryPoint;
            await WaitForEditorHandshakeAsync();
            await ApplyEditorLanguageAsync();
            // Browser and host wrappers share the control's lifetime and must not be disposed here.
            EditorBrowser.GetBrowser().GetHost().SetAccessibilityState(CefState.Enabled);

            initialized = true;
            await ReloadSpacesAsync();
            try
            {
                connection = connections.LoadConnection();
                if (connection is not null)
                {
                    ServerEndpointPolicy.Validate(connection.ServerUri.AbsoluteUri, settings.AllowInsecureLoopback);
                }
            }
            catch (Exception error) when (IsExpectedError(error))
            {
                connection = null;
            }

            await RefreshIssuesAsync();
            UpdateConnectionStatus();
            InitializationBanner.Visibility = Visibility.Collapsed;
            SaveStatus.Text = LocalizationService.Instance["status.ready"];
            autosave.Start();
        }
        catch (Exception error) when (IsExpectedError(error))
        {
            initialized = false;
            InitializationStatus.Text = storageReady
                ? LocalizationService.Instance["error.editorConnect"]
                : LocalizationService.Instance["error.workspaceOpen"];
            SaveStatus.Text = storageReady ? LocalizationService.Instance["status.editorUnavailable"] : LocalizationService.Instance["status.workspaceUnavailable"];
            RetryEditorButton.Visibility = Visibility.Visible;
        }
        finally
        {
            initializing = false;
            UpdateActions();
            WriteEditorDiagnostics();
        }
    }

    /// <summary>Repeats an idempotent challenge until the trusted main frame acknowledges native message delivery.</summary>
    private async Task WaitForEditorHandshakeAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));
        while (!editorReady.Task.IsCompletedSuccessfully)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (disposed)
            {
                throw new OperationCanceledException("The workspace window has closed.");
            }

            if (EditorBrowser.IsBrowserInitialized)
            {
                // This frame belongs to the browser; disposing it races the native IPC callback.
                IFrame frame = EditorBrowser.GetMainFrame();
                if (frame.IsValid && frame.IsMain && LocalEditorPolicy.IsTrusted(frame.Url))
                {
                    JavascriptResponse response = await frame.EvaluateScriptAsync(EditorProtocol.CreateHandshakeScript(bridgeChallenge)).WaitAsync(TimeSpan.FromSeconds(4), timeout.Token);
                    if (response.Success && response.Result is string state)
                    {
                        using JsonDocument document = JsonDocument.Parse(state);
                        rendererApiReady = document.RootElement.TryGetProperty("editorApiReady", out JsonElement api) && api.ValueKind == JsonValueKind.True;
                        postMessageAvailable = document.RootElement.TryGetProperty("postMessageAvailable", out JsonElement bridge) && bridge.ValueKind == JsonValueKind.True;
                    }
                }
            }

            if (!editorReady.Task.IsCompletedSuccessfully)
            {
                await Task.WhenAny(editorReady.Task, Task.Delay(150, timeout.Token));
            }
        }
    }

    /// <summary>Records only startup flags and event counters; never document text, identifiers or credentials.</summary>
    private void WriteEditorDiagnostics()
    {
        try
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(new
            {
                diagnosticVersion = 1,
                initialized,
                storageReady,
                rendererApiReady,
                postMessageAvailable,
                receivedBridgeEvents,
                rejectedBridgeOrigins,
                rejectedBridgePayloads,
                frameChecksPassed,
                payloadChecksPassed,
                queuedUiMessages,
                callbackFailures,
                callbackFailureStage,
                callbackFailureType,
                parsedUiMessages,
                rootObjectMessages,
                rootStringMessages,
                readyTypeMessages,
                matchingChannelMessages,
                bridgeParseFailures,
                acknowledgedReadyEvents,
                documentChangeEvents,
                capturedSnapshots,
                committedSaves,
            }, JsonOptions);
            File.WriteAllBytes(Path.Combine(settings.DataDirectory, "editor-startup.json"), data);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Optional content-free diagnostics must never prevent local document saving.
        }
    }

    /// <summary>Accepts only document events from the packaged editor's main frame.</summary>
    private void EditorMessageReceived(object? sender, PackagedEditorMessageEventArgs e)
    {
        Interlocked.Increment(ref receivedBridgeEvents);
        string stage = "frame";
        try
        {
            if (!e.IsMainFrame || !e.IsTrustedOrigin)
            {
                Interlocked.Increment(ref rejectedBridgeOrigins);
                return;
            }

            Interlocked.Increment(ref frameChecksPassed);
            stage = "payload";
            if (!EditorMessagePolicy.IsAllowed(e.Message))
            {
                Interlocked.Increment(ref rejectedBridgePayloads);
                return;
            }

            Interlocked.Increment(ref payloadChecksPassed);
            stage = "dispatch";
            Dispatcher.BeginInvoke(() => ReceiveEditorMessage(e.Message));
            Interlocked.Increment(ref queuedUiMessages);
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref callbackFailures);
            callbackFailureStage = stage;
            callbackFailureType = error.GetType().Name;
        }
    }

    /// <summary>Parses bounded bridge messages and marks matching local drafts for durable saving.</summary>
    private void ReceiveEditorMessage(string message)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            parsedUiMessages++;
            if (root.ValueKind == JsonValueKind.Object)
            {
                rootObjectMessages++;
            }
            if (root.ValueKind == JsonValueKind.String)
            {
                rootStringMessages++;
            }
            if (EditorProtocol.IsMatchingChannel(root, bridgeChallenge))
            {
                matchingChannelMessages++;
            }
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement kind))
            {
                return;
            }

            if (kind.ValueKind == JsonValueKind.String && kind.GetString() == "editorReady")

            {

                readyTypeMessages++;

            }
            if (kind.GetString() == "editorReady" && EditorProtocol.IsReadyAcknowledgement(root, bridgeChallenge))
            {
                acknowledgedReadyEvents++;
                editorReady.TrySetResult();
                return;
            }

            if (kind.GetString() == "documentChanged" && editing && !loading)
            {
                documentChangeEvents++;
                AcceptSnapshot(root);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            bridgeParseFailures++;
            SaveStatus.Text = LocalizationService.Instance["error.editorInvalidChange"];
        }
    }

    /// <summary>Rejects stale page sessions and keeps pagination artifacts out of stored content.</summary>
    private void AcceptSnapshot(JsonElement snapshot)
    {
        if (current is null || !EditorProtocol.IsMatchingChannel(snapshot, bridgeChallenge) || !snapshot.TryGetProperty("pageId", out JsonElement page) || !Guid.TryParse(page.GetString(), out Guid pageId) || pageId != current.Id
            || !snapshot.TryGetProperty("sessionId", out JsonElement session) || session.GetString() != sessionId
            || !snapshot.TryGetProperty("sequence", out JsonElement counter) || !counter.TryGetInt64(out long next) || next < sequence
            || !snapshot.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("type", out JsonElement type) || type.GetString() != "doc")
        {
            return;
        }

        string value = content.GetRawText();
        DocumentPolicy.Validate(value);

        sequence = next;
        if (!string.Equals(value, contentJson, StringComparison.Ordinal))
        {
            contentJson = value;
            revision++;
            SaveStatus.Text = LocalizationService.Instance["status.unsaved"];
        }
    }

    /// <summary>Reads a final renderer snapshot before navigation, export or shutdown.</summary>
    private async Task CaptureEditorAsync()
    {
        if (current is null || !editorReady.Task.IsCompletedSuccessfully)
        {
            return;
        }

        JavascriptResponse response = await EditorBrowser.EvaluateScriptAsync("window.studyDesk.snapshot()");
        if (!response.Success)
        {
            throw new InvalidOperationException("The editor snapshot could not be read.");
        }

        if (response.Result is string value)
        {
            using JsonDocument document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 });
            AcceptSnapshot(document.RootElement);
            capturedSnapshots++;
        }
    }

    /// <summary>Commits a revision to SQLite and only then acknowledges that it is saved.</summary>
    private async Task SaveCurrentAsync(bool capture)
    {
        await saveGate.WaitAsync();
        try
        {
            if (capture)
            {
                await CaptureEditorAsync();
            }

            if (current is null || savedRevision == revision)
            {
                return;
            }

            string title = TitleBox.Text.Trim();
            if (title.Length == 0)
            {
                throw new InvalidOperationException("Enter a note title before saving.");
            }

            PageDto target = current;
            long saved = revision;
            string body = contentJson;
            await store.SavePageAsync(target.Id, target.SpaceId, target.ParentId, title, body);
            PageDto? updated = await store.GetPageAsync(target.Id);
            if (current?.Id == target.Id)
            {
                current = updated ?? target;
                savedRevision = saved;
                SaveStatus.Text = savedRevision == revision ? LocalizationService.Instance["status.saved"] : LocalizationService.Instance["status.unsaved"];
                UpdateDetails();
                committedSaves++;
                WriteEditorDiagnostics();
            }
        }
        finally
        {
            saveGate.Release();
        }
    }

    /// <summary>Debounces changes without treating the server as a prerequisite for saving.</summary>
    private async void AutosaveTick(object? sender, EventArgs e)
    {
        if (initialized && !busy && !navigating && !closePending && (editing || revision != savedRevision))
        {
            try
            {
                await SaveCurrentAsync(editing);
            }
            catch (Exception error) when (IsExpectedError(error))
            {
                autosave.Stop();
                SaveStatus.Text = LocalizationService.Instance["status.autosavePaused"];
            }
        }
    }

    /// <summary>Queues title changes with the same durable save path as editor content.</summary>
    private void TitleChanged(object sender, TextChangedEventArgs e)
    {
        if (!loading && editing && current is not null)
        {
            revision++;
            SaveStatus.Text = LocalizationService.Instance["status.unsaved"];
        }
    }

    /// <summary>Refreshes spaces while preserving the user's current selection where possible.</summary>
    private async Task ReloadSpacesAsync(Guid? selected = null)
    {
        Guid? requested = selected ?? (SpacePicker.SelectedItem as SpaceDto)?.Id;
        var spaces = (await store.GetSpacesAsync()).Where(space => !space.IsTrashed).OrderBy(space => space.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        loading = true;
        SpacePicker.ItemsSource = spaces;
        SpacePicker.SelectedItem = spaces.FirstOrDefault(space => space.Id == requested) ?? spaces.FirstOrDefault();
        loading = false;
        CurrentSpaceText.Text = (SpacePicker.SelectedItem as SpaceDto)?.Title ?? LocalizationService.Instance["workspace.default"];
        await ReloadPagesAsync();
    }

    /// <summary>Refreshes the visible notes without mutating the document currently being edited.</summary>
    private async Task ReloadPagesAsync()
    {
        PageDto[] pages = SpacePicker.SelectedItem is SpaceDto space
            ? (await store.GetPagesAsync(space.Id)).Where(page => page.IsTrashed == (ShowTrash.IsChecked == true)).OrderBy(page => page.Title, StringComparer.CurrentCultureIgnoreCase).ToArray()
            : [];
        if (current is not null && !pages.Any(page => page.Id == current.Id))
        {
            await ClearPageAsync();
        }

        loading = true;
        PageList.ItemsSource = pages;
        PageList.SelectedItem = pages.FirstOrDefault(page => page.Id == current?.Id);
        loading = false;
        UpdateEmptyState();
    }

    /// <summary>Saves the previous document before loading a newly selected note.</summary>
    private async void PageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || navigating || busy || !initialized || PageList.SelectedItem is not PageDto selected || selected.Id == current?.Id)
        {
            return;
        }

        navigating = true;
        await RunExclusiveAsync(async () =>
        {
            await SaveCurrentAsync(true);
            await OpenPageAsync(selected, false);
        });
        navigating = false;
        UpdateActions();
    }

    /// <summary>Saves pending local work before switching the visible space.</summary>
    private async void SpaceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || loading || navigating || busy)
        {
            return;
        }

        navigating = true;
        await RunExclusiveAsync(async () =>
        {
            await SaveCurrentAsync(true);
            CurrentSpaceText.Text = (SpacePicker.SelectedItem as SpaceDto)?.Title ?? LocalizationService.Instance["workspace.default"];
            await ReloadPagesAsync();
        });
        navigating = false;
        UpdateActions();
    }

    /// <summary>Filters existing notes without deleting any content.</summary>
    private async void TrashFilterChanged(object sender, RoutedEventArgs e)
    {
        if (initialized && !loading && !busy)
        {
            await RunExclusiveAsync(async () => { await SaveCurrentAsync(true); await ReloadPagesAsync(); });
        }
    }

    /// <summary>Loads one note with an unpredictable session identifier and viewing as the default.</summary>
    private async Task OpenPageAsync(PageDto page, bool enableEditing)
    {
        DocumentPolicy.Validate(page.ContentJson);
        await editorReady.Task.WaitAsync(TimeSpan.FromSeconds(25));
        string newSession = Guid.NewGuid().ToString("N");
        string payload = JsonSerializer.Serialize(new { pageId = page.Id, sessionId = newSession, contentJson = page.ContentJson }, JsonOptions);
        JavascriptResponse loaded = await EditorBrowser.EvaluateScriptAsync("window.studyDesk.load(" + payload + ")");
        if (!loaded.Success)
        {
            throw new InvalidOperationException("This note could not be opened by the editor. Its saved content is preserved.");
        }

        current = page;
        contentJson = page.ContentJson;
        sessionId = newSession;
        sequence = 0;
        revision = 0;
        savedRevision = 0;
        loading = true;
        TitleBox.Text = page.Title;
        loading = false;
        editing = enableEditing && !page.IsTrashed;
        UpdateActions();
        await SetEditorReadOnlyAsync(!editing);
        Sections.SelectedIndex = 0;
        SaveStatus.Text = LocalizationService.Instance["status.saved"];
        UpdateDetails();
    }

    /// <summary>Creates a titled local space without server connectivity.</summary>
    private async void NewSpaceClicked(object sender, RoutedEventArgs e)
    {
        if (!initialized || busy || navigating)
        {
            return;
        }
        TextPrompt prompt = new(this, LocalizationService.Instance["dialog.newSpaceTitle"], LocalizationService.Instance["dialog.spaceTitle"]);
        if (prompt.ShowDialog() != true)
        {
            return;
        }
        await RunExclusiveAsync(async () =>
        {
            await SaveCurrentAsync(true);
            Guid id = Guid.NewGuid();
            await store.SaveSpaceAsync(id, prompt.Value);
            await ReloadSpacesAsync(id);
        });
    }

    /// <summary>Creates and durably stores an empty Tiptap note before editing starts.</summary>
    private async void NewNoteClicked(object sender, RoutedEventArgs e)
    {
        if (!initialized || busy || navigating)
        {
            return;
        }
        if (SpacePicker.SelectedItem is not SpaceDto space)
        {
            MessageBox.Show(this, LocalizationService.Instance["error.createSpaceFirst"], LocalizationService.Instance["app.title"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TextPrompt prompt = new(this, LocalizationService.Instance["dialog.newNoteTitle"], LocalizationService.Instance["dialog.noteTitle"]);
        if (prompt.ShowDialog() != true)
        {
            return;
        }
        await RunExclusiveAsync(async () =>
        {
            await SaveCurrentAsync(true);
            loading = true;
            ShowTrash.IsChecked = false;
            loading = false;
            Guid id = Guid.NewGuid();
            await store.SavePageAsync(id, space.Id, null, prompt.Value, EmptyDocument);
            PageDto page = await store.GetPageAsync(id) ?? throw new InvalidOperationException("The new note could not be read.");
            await OpenPageAsync(page, true);
            await ReloadPagesAsync();
        });
    }

    /// <summary>Toggles the explicit edit mode after preserving the current draft.</summary>
    private async void EditClicked(object sender, RoutedEventArgs e)
    {
        if (current is null || busy)
        {
            return;
        }
        await RunExclusiveAsync(async () =>
        {
            await SaveCurrentAsync(true);
            editing = !editing;
            UpdateActions();
            await SetEditorReadOnlyAsync(!editing);
        });
    }

    /// <summary>Updates accessible native controls to match the actual document capabilities.</summary>
    private void UpdateActions()
    {
        NewSpaceButton.IsEnabled = initialized && !busy && !navigating;
        NewNoteButton.IsEnabled = initialized && !busy && !navigating;
        PairServerButton.IsEnabled = initialized && !busy && !navigating;
        SpacePicker.IsEnabled = initialized && !busy && !navigating;
        PageList.IsEnabled = initialized && !busy && !navigating;
        ShowTrash.IsEnabled = initialized && !busy && !navigating;
        UpdateIssueActions();
        UpdateEmptyState();
        bool hasPage = current is not null;
        EmptyNoteState.Visibility = hasPage ? Visibility.Collapsed : Visibility.Visible;
        EditButton.Visibility = hasPage ? Visibility.Visible : Visibility.Collapsed;
        EditorBrowser.IsTabStop = hasPage;
        TitleBox.Focusable = editing;
        EditButton.SetResourceReference(StyleProperty, editing ? "StudyDesk.PrimaryButton" : "StudyDesk.SecondaryButton");
        TitleBox.IsReadOnly = !editing || busy;
        EditButton.IsEnabled = hasPage && current?.IsTrashed == false && !busy;
        EditButton.Content = editing ? LocalizationService.Instance["actions.finishEditing"] : LocalizationService.Instance["actions.edit"];
        SaveNowButton.IsEnabled = hasPage && !busy;
        ImageButton.IsEnabled = hasPage && editing && !busy;
        ExportButton.IsEnabled = hasPage && !busy;
        PdfButton.IsEnabled = hasPage && !busy;
        TrashButton.IsEnabled = hasPage && !busy;
        TrashButton.Content = current?.IsTrashed == true ? LocalizationService.Instance["actions.restore"] : LocalizationService.Instance["actions.trash"];
        SyncButton.IsEnabled = connection is not null && initialized && !busy;
    }

    /// <summary>Displays only the local metadata of the selected note.</summary>
    private void UpdateDetails()
    {
        if (current is not PageDto page)
        {
            return;
        }
        PageSummary.Text = LocalizationService.Instance.Format("details.summary", page.IsTrashed ? LocalizationService.Instance["state.trashed"] : editing ? LocalizationService.Instance["state.editing"] : LocalizationService.Instance["state.viewing"]);
        string space = (SpacePicker.ItemsSource as IEnumerable<SpaceDto>)?.FirstOrDefault(item => item.Id == page.SpaceId)?.Title ?? LocalizationService.Instance["details.localSpace"];
        DetailsText.Text = LocalizationService.Instance.Format("details.template", page.Title, space, page.UpdatedAt.LocalDateTime, page.Version);
    }

    /// <summary>Changes editor mutability through a fixed command with no native object binding.</summary>
    private async Task SetEditorReadOnlyAsync(bool readOnly)
    {
        if (editorReady.Task.IsCompletedSuccessfully)
        {
            JavascriptResponse response = await EditorBrowser.EvaluateScriptAsync("window.studyDesk.setReadOnly(" + (readOnly ? "true" : "false") + ")");
            if (!response.Success)
            {
                throw new InvalidOperationException("The editor could not change its viewing mode.");
            }
        }
    }

    /// <summary>Refreshes details when switching native tabs without changing saved content.</summary>
    private void SectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && e.Source == Sections)
        {
            UpdateDetails();
        }
    }

    /// <summary>Provides an explicit save action using the same SQLite transaction as autosave.</summary>
    private async void SaveNowClicked(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(() => SaveCurrentAsync(true));
    }

    /// <summary>Supports the standard keyboard save gesture.</summary>
    private async void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                NewSpaceClicked(this, new RoutedEventArgs());
            }
            else
            {
                NewNoteClicked(this, new RoutedEventArgs());
            }
        }

        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await RunSafelyAsync(() => SaveCurrentAsync(true));
        }
    }

    /// <summary>Blocks shutdown until the last renderer snapshot has been durably saved.</summary>
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closeApproved || !initialized)
        {
            return;
        }
        e.Cancel = true;
        if (closePending || busy)
        {
            synchronization?.Cancel();
            return;
        }
        closePending = true;
        autosave.Stop();
        try
        {
            await SetEditorReadOnlyAsync(true);
            await SaveCurrentAsync(true);
            closeApproved = true;
            Close();
        }
        catch (Exception error) when (IsExpectedError(error))
        {
            ShowFailure();
            await SetEditorReadOnlyAsync(!editing);
            closePending = false;
            autosave.Start();
        }
    }

    /// <summary>Disposes renderer and native scheduling resources after a successful close.</summary>
    private void WindowClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    /// <summary>Releases native resources exactly once after pending saves have completed.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        WriteEditorDiagnostics();
        autosave.Stop();
        EditorBrowser.PackagedMessageReceived -= EditorMessageReceived;
        EditorBrowser.Dispose();
        synchronization?.Cancel();
        synchronization?.Dispose();
        store.Dispose();
        saveGate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Stabilizes the renderer while a native operation saves or replaces the selected document.</summary>
    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        UpdateActions();
        try
        {
            await SetEditorReadOnlyAsync(true);
            await action();
        }
        catch (Exception error) when (IsExpectedError(error))
        {
            ShowFailure();
        }
        finally
        {
            busy = false;
            UpdateActions();
            await RunSafelyAsync(() => SetEditorReadOnlyAsync(!editing));
        }
    }

    /// <summary>Keeps expected I/O failures visible while retaining the in-memory draft for retry.</summary>
    private async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
            if (initialized && !closePending && !busy)
            {
                autosave.Start();
            }
        }
        catch (Exception error) when (IsExpectedError(error))
        {
            ShowFailure();
        }
    }

    /// <summary>Recognizes recoverable storage, renderer and transport errors without revealing secrets.</summary>
    private static bool IsExpectedError(Exception error)
    {
        return error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException
            or SqliteException or System.Net.Http.HttpRequestException or System.Security.Cryptography.CryptographicException or OperationCanceledException or TimeoutException;
    }

    /// <summary>Reports failure honestly; a failed save is never labelled as durable.</summary>
    private void ShowFailure()
    {
        SaveStatus.Text = LocalizationService.Instance["error.actionStatus"];
        MessageBox.Show(this, LocalizationService.Instance["error.actionMessage"], LocalizationService.Instance["app.title"], MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
