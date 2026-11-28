using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Shell;
using StudyDesk.Desktop.Localization;

namespace StudyDesk.Desktop.Dialogs;

/// <summary>Provides a compact themed caption and predictable keyboard dismissal for native dialogs.</summary>
public class DialogWindow : Window
{
    private readonly ContentControl body = new();

    /// <summary>Creates a modal shell centered on the owning workspace without any external content.</summary>
    protected DialogWindow(Window owner, string title, double width)
    {
        SetResourceReference(StyleProperty, "StudyDesk.WindowStyle");
        Owner = owner;
        Icon = owner.Icon;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Language = XmlLanguage.GetLanguage(LocalizationService.Instance.Culture.IetfLanguageTag);
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });
        Border frame = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9) };
        frame.SetResourceReference(Border.BorderBrushProperty, "StudyDesk.BorderBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "StudyDesk.WindowBrush");
        Grid grid = new();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid caption = new() { Margin = new Thickness(20, 0, 8, 0) };
        caption.ColumnDefinitions.Add(new ColumnDefinition());
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        caption.Children.Add(new TextBlock { Text = LocalizationService.Instance["app.title"], FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        Button close = new() { Content = "✕", IsCancel = true, Width = 34, Height = 32 };
        close.SetResourceReference(StyleProperty, "StudyDesk.IconButton");
        close.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding("[window.close]") { Source = LocalizationService.Instance, Mode = BindingMode.OneWay });
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        Grid.SetColumn(close, 1);
        caption.Children.Add(close);
        grid.Children.Add(caption);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        frame.Child = grid;
        Content = frame;
    }

    /// <summary>Places the dialog-specific form below the shared native caption.</summary>
    protected void SetBody(UIElement content)
    {
        body.Content = content;
    }
}
