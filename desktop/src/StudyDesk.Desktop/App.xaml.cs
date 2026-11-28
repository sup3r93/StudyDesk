using System.IO;
using System.Windows;
using CefSharp;
using CefSharp.Wpf;
using StudyDesk.Desktop.Configuration;
using StudyDesk.Desktop.Editor;
using StudyDesk.Desktop.Localization;

namespace StudyDesk.Desktop;

/// <summary>Starts the offline workspace and its isolated, locally supplied editor.</summary>
public partial class App : Application
{
    private WindowsCefBootstrap? bootstrap;

    /// <summary>Creates private local configuration before starting the embedded browser.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LocalizationService.Instance.SetLanguage(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en");
        try
        {
            DesktopSettings settings = DesktopSettings.Load();
            LocalizationService.Instance.SetLanguage(settings.EffectiveLanguage);
            string editorDirectory = Path.Combine(AppContext.BaseDirectory, "editor");
            if (!File.Exists(Path.Combine(editorDirectory, "index.html")))
            {
                MessageBox.Show(LocalizationService.Instance["error.initialEditorMissing"], LocalizationService.Instance["app.title"], MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            CefSettings browserSettings = new()
            {
                CachePath = Path.Combine(settings.DataDirectory, "browser-cache"),
                LogSeverity = LogSeverity.Disable,
                WindowlessRenderingEnabled = true,
            };
            try
            {
                bootstrap = WindowsCefBootstrap.Prepare(browserSettings);
            }
            catch (Exception error) when (error is InvalidOperationException or DllNotFoundException or BadImageFormatException)
            {
                MessageBox.Show(LocalizationService.Instance["error.initialEditorFailed"], LocalizationService.Instance["app.title"], MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            // The equivalent full check above permits only the verified, retained Windows FXC module.
            if (!Cef.Initialize(browserSettings, performDependencyCheck: false, browserProcessHandler: new LocalEditorBrowserProcessHandler(editorDirectory)))
            {
                MessageBox.Show(LocalizationService.Instance["error.initialEditorFailed"], LocalizationService.Instance["app.title"], MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            MainWindow window = new(settings);
            MainWindow = window;
            window.Show();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(LocalizationService.Instance["error.initialWorkspaceOpen"], LocalizationService.Instance["app.title"], MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Stops Chromium after the window has disposed its editor control.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Cef.Shutdown();
        }
        finally
        {
            bootstrap?.Dispose();
            base.OnExit(e);
        }
    }
}
