using System.Windows;
using StudyDesk.Desktop.Localization;

namespace StudyDesk.Desktop;

/// <summary>Keeps native window commands accessible without bypassing document save safeguards.</summary>
public partial class MainWindow
{
    /// <summary>Fits the first window and its resize limits within the primary monitor's WPF work area.</summary>
    private void ApplyInitialWindowBounds()
    {
        Rect bounds = CalculateInitialWindowBounds(SystemParameters.WorkArea);
        MinWidth = Math.Min(1000, bounds.Width);
        MinHeight = Math.Min(700, bounds.Height);
        Width = bounds.Width;
        Height = bounds.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
    }

    /// <summary>Centers the preferred size inside the available device-independent work area, retaining a small margin.</summary>
    internal static Rect CalculateInitialWindowBounds(Rect workArea)
    {
        if (workArea.IsEmpty || !double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height)
            || workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentException("A finite, positive Windows work area is required.", nameof(workArea));
        }
        double margin = Math.Min(16, Math.Min(workArea.Width, workArea.Height) / 20);
        double width = Math.Min(1320, workArea.Width - (2 * margin));
        double height = Math.Min(860, workArea.Height - (2 * margin));
        return new Rect(workArea.Left + ((workArea.Width - width) / 2),
            workArea.Top + ((workArea.Height - height) / 2), width, height);
    }

    /// <summary>Minimizes the workspace using the normal Windows window state.</summary>
    private void MinimizeClicked(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>Toggles maximize and restore while retaining native resize behavior.</summary>
    private void MaximizeClicked(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>Uses the existing close pipeline so the final editor snapshot must be saved.</summary>
    private void CloseWindowClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
