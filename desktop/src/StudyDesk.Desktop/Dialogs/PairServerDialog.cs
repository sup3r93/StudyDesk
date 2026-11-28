using System.Windows;
using System.Windows.Controls;
using StudyDesk.Desktop.Configuration;
using StudyDesk.Desktop.Localization;
using StudyDesk.Desktop.Synchronization;

namespace StudyDesk.Desktop.Dialogs;

/// <summary>Lets the installation owner explicitly pair this workspace with a chosen data server.</summary>
public sealed class PairServerDialog : DialogWindow, IDisposable
{
    private readonly TextBox address = new() { Margin = new Thickness(0, 6, 0, 14) };
    private readonly PasswordBox token = new() { MaxLength = 64, Margin = new Thickness(0, 6, 0, 14), Padding = new Thickness(8) };
    private readonly CheckBox consent = new() { Content = LocalizationService.Instance["dialog.pairConsent"], Margin = new Thickness(0, 6, 0, 14) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Button connect = new() { Content = LocalizationService.Instance["dialog.pairButton"], IsDefault = true };
    private readonly DesktopSettings settings;
    private bool disposed;
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>Creates a native pairing dialog whose secret field is never part of the editor.</summary>
    public PairServerDialog(Window owner, DesktopSettings settings)
        : base(owner, Localization.LocalizationService.Instance["dialog.pairTitle"], 530)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        StackPanel panel = new() { Margin = new Thickness(24, 12, 24, 24) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap });
        TextBlock description = new() { Text = Localization.LocalizationService.Instance["dialog.pairingDescription"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 24) };
        description.SetResourceReference(TextBlock.ForegroundProperty, "StudyDesk.MutedTextBrush");
        panel.Children.Add(description);
        panel.Children.Add(new TextBlock { Text = LocalizationService.Instance["dialog.serverAddress"], FontWeight = FontWeights.SemiBold });
        panel.Children.Add(address);
        panel.Children.Add(new TextBlock { Text = LocalizationService.Instance["dialog.deviceToken"], FontWeight = FontWeights.SemiBold });
        token.SetResourceReference(StyleProperty, "StudyDesk.PasswordBox");
        panel.Children.Add(token);
        consent.Content = new TextBlock { Text = LocalizationService.Instance["dialog.pairConsent"], TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(consent);
        panel.Children.Add(status);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(new Button { Content = LocalizationService.Instance["common.cancel"], IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        connect.SetResourceReference(StyleProperty, "StudyDesk.PrimaryButton");
        buttons.Children.Add(connect);
        panel.Children.Add(buttons);
        SetBody(panel);
        System.Windows.Automation.AutomationProperties.SetName(address, LocalizationService.Instance["dialog.serverAddressAccessible"]);
        System.Windows.Automation.AutomationProperties.SetName(token, LocalizationService.Instance["dialog.deviceToken"]);
        connect.Click += ConnectClicked;
        Closed += DialogClosed;
        Loaded += FocusAddress;
    }

    /// <summary>Places initial keyboard focus at the first field in the native pairing form.</summary>
    private void FocusAddress(object sender, RoutedEventArgs e)
    {
        address.Focus();
    }

    /// <summary>Gets the verified association after the user has approved and completed pairing.</summary>
    public ServerConnection? Connection { get; private set; }

    /// <summary>Cancels in-flight pairing when the user closes or cancels the dialog.</summary>
    private void DialogClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    /// <summary>Cancels and releases the private dialog lifetime without retaining typed credentials.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        token.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>Validates HTTPS identity before storing the token using Windows protection.</summary>
    private async void ConnectClicked(object sender, RoutedEventArgs e)
    {
        if (consent.IsChecked != true)
        {
            status.Text = LocalizationService.Instance["dialog.pairConsentRequired"];
            return;
        }

        connect.IsEnabled = false;
        CancellationToken cancellationToken = lifetime.Token;
        try
        {
            Uri origin = ServerEndpointPolicy.Validate(address.Text.Trim(), settings.AllowInsecureLoopback);
            string value = token.Password.Trim();
            ConnectionStore.ValidateToken(value);
            using DataServerClient client = new(origin, value, settings.AllowInsecureLoopback);
            var info = await client.GetInfoAsync(cancellationToken);
            if (info.ProtocolVersion != 1 || info.ServerId == Guid.Empty || info.Generation == Guid.Empty)
            {
                throw new InvalidOperationException("This server uses an unsupported synchronization protocol.");
            }

            // Verify access without making pairing depend on the size of the owner's workspace.
            var authorized = await client.VerifyAccessAsync(cancellationToken);
            if (authorized.ServerId != info.ServerId || authorized.Generation != info.Generation || authorized.ProtocolVersion != info.ProtocolVersion)
            {
                throw new InvalidOperationException("The server identity changed during pairing.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Connection = new ServerConnection(origin, info.ServerId, info.Generation);
            new ConnectionStore(settings).Save(Connection, value);
            token.Clear();
            DialogResult = true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or System.IO.IOException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
        {
            status.Text = LocalizationService.Instance["dialog.pairFailed"];
        }
        finally
        {
            connect.IsEnabled = true;
        }
    }
}
