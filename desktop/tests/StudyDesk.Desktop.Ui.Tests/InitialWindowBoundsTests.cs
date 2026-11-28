using System.Windows;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Protects first-launch proportions when Windows scaling reduces the available logical desktop.</summary>
public sealed class InitialWindowBoundsTests
{
    /// <summary>The initial window remains inside the work area and centered, including a scaled laptop desktop.</summary>
    [Theory]
    [InlineData(1920, 1040)]
    [InlineData(1280, 680)]
    [InlineData(1024, 650)]
    [InlineData(800, 1200)]
    public void WindowFitsAndCentersInsideLogicalWorkArea(double width, double height)
    {
        Rect workArea = new(40, 24, width, height);
        Rect bounds = MainWindow.CalculateInitialWindowBounds(workArea);
        Assert.True(workArea.Contains(bounds));
        Assert.InRange(bounds.Width, 1, 1320);
        Assert.InRange(bounds.Height, 1, 860);
        Assert.Equal(workArea.Left + (width / 2), bounds.Left + (bounds.Width / 2), precision: 6);
        Assert.Equal(workArea.Top + (height / 2), bounds.Top + (bounds.Height / 2), precision: 6);
    }
}
