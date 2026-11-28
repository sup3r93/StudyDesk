using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CefSharp;
using CefSharp.Wpf;
using StudyDesk.Contracts;
using StudyDesk.Desktop.Configuration;
using StudyDesk.Desktop.Core.Storage;
using StudyDesk.Desktop.Editor;

namespace StudyDesk.Desktop.Runtime.Tests;

/// <summary>Runs the actual packaged CEF editor and native window against an isolated synthetic workspace.</summary>
public static class Program
{
    /// <summary>Executes a noninteractive offscreen smoke check and emits only flags and counters.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        string[] names = ["STUDYDESK_DESKTOP_ENV_FILE", "STUDYDESK_DESKTOP_DATA_DIR", "STUDYDESK_DESKTOP_TOKEN_FILE", "STUDYDESK_DESKTOP_CONNECTION_FILE", "STUDYDESK_DESKTOP_LANGUAGE", "STUDYDESK_DESKTOP_ALLOW_INSECURE_LOOPBACK"];
        Dictionary<string, string?> previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudyDeskRuntimeTests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("STUDYDESK_DESKTOP_ENV_FILE", Path.Combine(directory, ".env"));
        Environment.SetEnvironmentVariable("STUDYDESK_DESKTOP_DATA_DIR", Path.Combine(directory, "workspace"));
        Environment.SetEnvironmentVariable("STUDYDESK_DESKTOP_TOKEN_FILE", Path.Combine(directory, "device.bin"));
        Environment.SetEnvironmentVariable("STUDYDESK_DESKTOP_CONNECTION_FILE", Path.Combine(directory, "connection.json"));
        Environment.SetEnvironmentVariable("STUDYDESK_DESKTOP_LANGUAGE", "auto");
        Environment.SetEnvironmentVariable("STUDYDESK_DESKTOP_ALLOW_INSECURE_LOOPBACK", "false");
        bool rejectRegistration = args.Contains("--reject-editor-factory", StringComparer.Ordinal);
        int injectedRegistrarCalls = 0;
        int injectedRegistrarReturnedFalse = 0;
        int result = 1;
        WindowsCefBootstrap? bootstrap = null;
        try
        {
            // The runtime probe must exercise the same OS-provided FXC policy as the packaged host.
            if (Directory.EnumerateFiles(AppContext.BaseDirectory, "d3dcompiler_47.dll", SearchOption.AllDirectories).Any())
            {
                throw new InvalidOperationException("An optional app-local D3D compiler must not be redistributed.");
            }
            DesktopSettings settings = DesktopSettings.Load();
            using CefSettings browserSettings = new()
            {
                CachePath = Path.Combine(settings.DataDirectory, "browser-cache"),
                LogSeverity = LogSeverity.Disable,
                WindowlessRenderingEnabled = true,
            };
            string bundle = Path.Combine(AppContext.BaseDirectory, "editor");
            LocalEditorBrowserProcessHandler processHandler = rejectRegistration
                ? new LocalEditorBrowserProcessHandler(bundle, (_, _) =>
                {
                    Interlocked.Increment(ref injectedRegistrarCalls);
                    Interlocked.Exchange(ref injectedRegistrarReturnedFalse, 1);
                    return false;
                })
                : new LocalEditorBrowserProcessHandler(bundle);
            bootstrap = WindowsCefBootstrap.Prepare(browserSettings);
            if (!Cef.Initialize(browserSettings, performDependencyCheck: false, browserProcessHandler: processHandler))
            {
                throw new InvalidOperationException("The packaged CEF runtime could not initialize.");
            }

            Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/StudyDesk;component/Themes/StudyDeskTheme.xaml", UriKind.Relative) });
            AddImplicitStyles(application.Resources);
            MainWindow window = new(settings)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -16000,
                Top = -16000,
                Width = 1280,
                Height = 800,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            application.MainWindow = window;
            window.Loaded += ProbeLoaded;
            window.Show();
            application.Run();

            // Uses native document methods and real editor events without sending live keyboard or mouse input.
            async void ProbeLoaded(object sender, RoutedEventArgs eventArgs)
            {
                bool firstLaunchReady = false;
                bool registrationRejectionVerified = false;
                bool oversizedRejectedBeforeSubscriber = false;
                bool existingNotesPromptVerified = false;
                bool emptyStatesVerified = false;
                bool itemNamesVerified = false;
                bool documentScrollVerified = false;
                bool autosaveVerified = false;
                bool reopenedVerified = false;
                string stage = "readiness";
                string? failureType = null;
                try
                {
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(35));
                    Button create = (Button)window.FindName("NewSpaceButton");
                    PackagedEditorBrowser packagedBrowser = (PackagedEditorBrowser)window.FindName("EditorBrowser");
                    if (rejectRegistration)
                    {
                        stage = "registration-rejection";
                        bool gateRejected = false;
                        try
                        {
                            await LocalEditorBrowserProcessHandler.Registration.WaitAsync(timeout.Token);
                        }
                        catch (InvalidOperationException)
                        {
                            gateRejected = true;
                        }

                        FieldInfo subscriber = typeof(MainWindow).GetField("receivedBridgeEvents", BindingFlags.NonPublic | BindingFlags.Instance)
                            ?? throw new InvalidOperationException("The native subscriber counter is unavailable.");
                        registrationRejectionVerified = gateRejected && Volatile.Read(ref injectedRegistrarCalls) == 1
                            && Volatile.Read(ref injectedRegistrarReturnedFalse) == 1
                            && !LocalEditorBrowserProcessHandler.Registration.IsRegistered
                            && !create.IsEnabled && !LocalEditorPolicy.IsTrusted(packagedBrowser.Address)
                            && (int)(subscriber.GetValue(window) ?? -1) == 0;
                        result = registrationRejectionVerified ? 0 : 1;
                        return;
                    }

                    while (!create.IsEnabled)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        await Task.Delay(100, timeout.Token);
                    }

                    firstLaunchReady = true;
                    stage = "oversized-native-envelope";
                    long rejectedBefore = packagedBrowser.RejectedMessages;
                    FieldInfo received = typeof(MainWindow).GetField("receivedBridgeEvents", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? throw new InvalidOperationException("The native subscriber counter is unavailable.");
                    int subscriberBefore = (int)(received.GetValue(window) ?? -1);
                    JavascriptResponse oversized = await packagedBrowser.EvaluateScriptAsync("window.CefSharp.PostMessage('x'.repeat(" + (EditorMessagePolicy.MaximumMessageCharacters + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "))").WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
                    if (!oversized.Success)
                    {
                        throw new InvalidOperationException("The synthetic oversized message was not sent.");
                    }
                    while (packagedBrowser.RejectedMessages == rejectedBefore)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        await Task.Delay(50, timeout.Token);
                    }
                    oversizedRejectedBeforeSubscriber = (int)(received.GetValue(window) ?? -1) == subscriberBefore;
                    if (!oversizedRejectedBeforeSubscriber)
                    {
                        throw new InvalidOperationException("An oversized envelope reached the native subscriber.");
                    }
                    stage = "synthetic-document";
                    using SqliteLocalWorkspaceStore storage = new(settings.DataDirectory);
                    Guid spaceId = Guid.NewGuid();
                    Guid pageId = Guid.NewGuid();
                    await storage.SaveSpaceAsync(spaceId, "Synthetic runtime space", timeout.Token);
                    MethodInfo reloadSpaces = typeof(MainWindow).GetMethod("ReloadSpacesAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? throw new InvalidOperationException("The native space-refresh method was not found.");
                    await ((Task)(reloadSpaces.Invoke(window, new object?[] { spaceId }) ?? throw new InvalidOperationException("The native space list did not refresh."))).WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
                    Button emptyAction = (Button)window.FindName("EmptyStateAction");
                    emptyStatesVerified = Equals(emptyAction.Content, Localization.LocalizationService.Instance["empty.createNote"]);
                    if (!emptyStatesVerified)
                    {
                        throw new InvalidOperationException("An existing empty space must offer note creation.");
                    }
                    CheckBox trash = (CheckBox)window.FindName("ShowTrash");
                    trash.IsChecked = true;
                    while (!Equals(emptyAction.Content, Localization.LocalizationService.Instance["empty.backToNotes"]))
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        await Task.Delay(50, timeout.Token);
                    }
                    trash.IsChecked = false;
                    while (!Equals(emptyAction.Content, Localization.LocalizationService.Instance["empty.createNote"]))
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        await Task.Delay(50, timeout.Token);
                    }
                    const string shortDocument = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Synthetic short note remains visible.\"}]}]}";
                    await storage.SavePageAsync(pageId, spaceId, null, "Synthetic runtime note", shortDocument, timeout.Token);
                    PageDto page = await storage.GetPageAsync(pageId, timeout.Token) ?? throw new InvalidOperationException("The synthetic note was not persisted.");
                    MethodInfo refreshExisting = typeof(MainWindow).GetMethod("ReloadPagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? throw new InvalidOperationException("The native note refresh is unavailable.");
                    await ((Task)(refreshExisting.Invoke(window, null) ?? throw new InvalidOperationException("The native note list did not refresh."))).WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
                    existingNotesPromptVerified = ((TextBlock)window.FindName("EmptyStateHeading")).Text == Localization.LocalizationService.Instance["empty.chooseHeading"];
                    if (!existingNotesPromptVerified)
                    {
                        throw new InvalidOperationException("An existing note must offer selection, not a first-note message.");
                    }
                    MethodInfo open = typeof(MainWindow).GetMethod("OpenPageAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? throw new InvalidOperationException("The native document-opening method was not found.");
                    await ((Task)(open.Invoke(window, [page, true]) ?? throw new InvalidOperationException("The native document did not open."))).WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
                    MethodInfo reloadPages = typeof(MainWindow).GetMethod("ReloadPagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? throw new InvalidOperationException("The native note-refresh method was not found.");
                    await ((Task)(reloadPages.Invoke(window, null) ?? throw new InvalidOperationException("The native note list did not refresh."))).WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
                    ListBox noteList = (ListBox)window.FindName("PageList");
                    noteList.UpdateLayout();
                    if (noteList.ItemContainerGenerator.ContainerFromIndex(0) is not ListBoxItem noteContainer)
                    {
                        throw new InvalidOperationException("The native note item was not realized.");
                    }
                    itemNamesVerified = System.Windows.Automation.AutomationProperties.GetName(noteContainer) == "Synthetic runtime note";
                    if (!itemNamesVerified)
                    {
                        throw new InvalidOperationException("The native note name exposes metadata instead of its title.");
                    }
                    ChromiumWebBrowser browser = (ChromiumWebBrowser)window.FindName("EditorBrowser");
                    stage = "document-navigation-scroll";
                    documentScrollVerified = await VerifyDocumentScrollAsync(window, browser, storage, open, page, timeout.Token);
                    const string image = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
                    JavascriptResponse edit = await browser.EvaluateScriptAsync("window.studyDesk.insertImage(" + JsonSerializer.Serialize(image) + ")").WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
                    if (!edit.Success)
                    {
                        throw new InvalidOperationException("The synthetic editor operation failed.");
                    }

                    stage = "native-autosave";
                    while (!autosaveVerified)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        PageDto? saved = await storage.GetPageAsync(pageId, timeout.Token);
                        autosaveVerified = saved?.ContentJson.Contains("data:image/png;base64,", StringComparison.Ordinal) == true;
                        if (!autosaveVerified)
                        {
                            await Task.Delay(100, timeout.Token);
                        }
                    }

                    stage = "close-and-reopen";
                    TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    window.Closed += OnClosed;
                    window.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
                    using SqliteLocalWorkspaceStore reopened = new(settings.DataDirectory);
                    reopenedVerified = (await reopened.GetPageAsync(pageId, timeout.Token))?.ContentJson.Contains("data:image/png;base64,", StringComparison.Ordinal) == true;
                    result = reopenedVerified && emptyStatesVerified && itemNamesVerified && oversizedRejectedBeforeSubscriber && existingNotesPromptVerified && documentScrollVerified ? 0 : 1;

                    // Observes the real native close pipeline after its final durable save.
                    void OnClosed(object? eventSender, EventArgs eventArgs)
                    {
                        closed.TrySetResult();
                    }
                }
                catch (Exception error)
                {
                    failureType = error.GetType().Name;
                }
                finally
                {
                    typeof(MainWindow).GetMethod("WriteEditorDiagnostics", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(window, null);
                    object? diagnostics = null;
                    string path = Path.Combine(settings.DataDirectory, "editor-startup.json");
                    if (File.Exists(path))
                    {
                        diagnostics = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(path));
                    }

                    Console.WriteLine(JsonSerializer.Serialize(new { appLocalFxcAbsent = true, expectedRegistrationFailure = rejectRegistration, injectedRegistrarCalls = Volatile.Read(ref injectedRegistrarCalls), injectedRegistrarReturnedFalse = Volatile.Read(ref injectedRegistrarReturnedFalse) == 1, registrationRejectionVerified, firstLaunchReady, oversizedRejectedBeforeSubscriber, existingNotesPromptVerified, emptyStatesVerified, itemNamesVerified, documentScrollVerified, autosaveVerified, reopenedVerified, stage, failureType, diagnostics }));
                    if (window.IsVisible)
                    {
                        TaskCompletionSource cleanupClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        window.Closed += (_, _) => cleanupClosed.TrySetResult();
                        window.Close();
                        // Keep the dispatcher alive for the native final-snapshot pipeline, including a failed regression.
                        await cleanupClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    application.Shutdown(result);
                }
            }
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { startupFailure = error.GetType().Name }));
        }
        finally
        {
            try
            {
                if (Cef.IsInitialized == true)
                {
                    Cef.Shutdown();
                }
            }
            finally
            {
                bootstrap?.Dispose();
                foreach ((string name, string? value) in previous)
                {
                    Environment.SetEnvironmentVariable(name, value);
                }
            }
        }

        return result;
    }

    /// <summary>Exercises real multi-sheet navigation and clear while preserving content and non-navigation scroll position.</summary>
    private static async Task<bool> VerifyDocumentScrollAsync(MainWindow window, ChromiumWebBrowser browser,
        SqliteLocalWorkspaceStore storage, MethodInfo open, PageDto shortPage, CancellationToken cancellationToken)
    {
        string paragraph = string.Join(' ', Enumerable.Repeat("Synthetic pagination content for the scroll regression.", 8));
        string longDocument = JsonSerializer.Serialize(new
        {
            type = "doc",
            content = Enumerable.Range(1, 45).Select(index => new
            {
                type = "paragraph",
                content = new[] { new { type = "text", text = index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + paragraph } },
            }),
        });
        Guid longPageId = Guid.NewGuid();
        await storage.SavePageAsync(longPageId, shortPage.SpaceId, null, "Synthetic multi-sheet note", longDocument, cancellationToken);
        PageDto longPage = await storage.GetPageAsync(longPageId, cancellationToken)
            ?? throw new InvalidOperationException("The synthetic long note was not persisted.");
        const string scrollLongNote = """
            (() => {
                const viewport = document.getElementById('paper-scroll');
                if (document.getElementById('sheets').children.length < 3 || viewport.scrollHeight <= viewport.clientHeight) return false;
                viewport.scrollTop = viewport.scrollHeight;
                return viewport.scrollTop > 100;
            })()
            """;
        const string shortNoteAtTop = """
            (() => {
                const viewport = document.getElementById('paper-scroll');
                const paragraph = document.querySelector('.tiptap p');
                const snapshot = JSON.parse(window.studyDesk.snapshot());
                const top = paragraph.getBoundingClientRect().top;
                const bounds = viewport.getBoundingClientRect();
                return viewport.scrollTop === 0 && viewport.scrollLeft === 0 &&
                    snapshot.content.content[0].content[0].text === 'Synthetic short note remains visible.' &&
                    paragraph.textContent === 'Synthetic short note remains visible.' && top >= bounds.top && top < bounds.bottom;
            })()
            """;
        await OpenAsync(longPage);
        await RequireFlagAsync(scrollLongNote);
        string language = JsonSerializer.Serialize(Localization.LocalizationService.Instance.CurrentLanguage);
        string dictionary = JsonSerializer.Serialize(Localization.LocalizationService.Instance.Translations);
        await RequireFlagAsync("(() => { window.studyDesk.snapshot(); window.studyDesk.setLanguage(" + language + ", " + dictionary + "); return document.getElementById('paper-scroll').scrollTop > 100; })()");
        await OpenAsync(shortPage);
        await RequireFlagAsync(shortNoteAtTop);
        await OpenAsync(longPage);
        await RequireFlagAsync(scrollLongNote);
        await RequireFlagAsync("(() => { window.studyDesk.clear(); const viewport = document.getElementById('paper-scroll'); return viewport.scrollTop === 0 && viewport.scrollLeft === 0 && window.studyDesk.snapshot() === null; })()");
        await OpenAsync(shortPage);
        await RequireFlagAsync(shortNoteAtTop);
        return true;

        // Reuses the real native capture/save/load pipeline instead of bypassing the document bridge.
        async Task OpenAsync(PageDto page)
        {
            await ((Task)(open.Invoke(window, [page, true]) ?? throw new InvalidOperationException("The synthetic note did not open.")))
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        // Waits for Chromium's actual layout frames, with a bounded failure even when a viewport never resets.
        async Task RequireFlagAsync(string script)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                JavascriptResponse response = await browser.EvaluateScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
                if (!response.Success || response.Result is not bool flag)
                {
                    throw new InvalidOperationException("The synthetic document-scroll check could not run.");
                }
                if (flag)
                {
                    return;
                }
                await Task.Delay(50, deadline.Token);
            }
        }
    }

    /// <summary>Uses the production shared theme for the actual native controls without launching the application's normal entry point.</summary>
    private static void AddImplicitStyles(ResourceDictionary resources)
    {
        foreach ((Type type, string key) in new[]
        {
            (typeof(Button), "StudyDesk.SecondaryButton"), (typeof(TextBox), "StudyDesk.TextBox"),
            (typeof(ComboBox), "StudyDesk.ComboBox"), (typeof(CheckBox), "StudyDesk.CheckBox"),
            (typeof(TabControl), "StudyDesk.TabControl"), (typeof(TabItem), "StudyDesk.TabItem"),
            (typeof(ListBox), "StudyDesk.ListBox"), (typeof(ListBoxItem), "StudyDesk.ListBoxItem"),
        })
        {
            resources.Add(type, new Style(type, (Style)resources[key]));
        }
    }
}
