using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Xml.Linq;
using StudyDesk.Desktop.Localization;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Executes the real title binding through WPF without starting Chromium or opening a window.</summary>
public sealed partial class NativeBindingTests
{
    /// <summary>Every localized source is explicitly read-only, including controls whose default binding mode is TwoWay.</summary>
    [Fact]
    public void NativeResourceBindingsExplicitlyUseOneWayMode()
    {
        XDocument source = LoadSource();
        var bindings = source.Descendants().Attributes().Where(attribute => attribute.Value.Contains("x:Static loc:LocalizationService.Instance", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, binding => Assert.Contains("Mode=OneWay", binding.Value, StringComparison.Ordinal));
    }

    /// <summary>The actual title binding parses and evaluates against the readonly resource indexer on an STA thread.</summary>
    [Fact]
    public void ActualTitleBindingLoadsThroughWpfOnStaThread()
    {
        RunOnSta(() =>
        {
            TextBox title = LoadActualTitleBinding();
            Binding? binding = BindingOperations.GetBinding(title, TextBox.TextProperty);
            Assert.NotNull(binding);
            Assert.Equal(BindingMode.OneWay, binding.Mode);
            Assert.Equal(LocalizationService.Instance["empty.title"], title.Text);
        });
    }

    /// <summary>Loading a real user title detaches the empty-state resource so language changes cannot overwrite a draft.</summary>
    [Fact]
    public void UserTitleIsPreservedAfterChangingTheInterfaceLanguage()
    {
        RunOnSta(() =>
        {
            LocalizationService service = LocalizationService.Instance;
            string previous = service.CurrentLanguage;
            try
            {
                service.SetLanguage("en");
                TextBox title = LoadActualTitleBinding();
                title.Text = "My personal untranslatable draft title";
                Assert.Null(BindingOperations.GetBinding(title, TextBox.TextProperty));
                service.SetLanguage("it");
                Assert.Equal("My personal untranslatable draft title", title.Text);
            }
            finally
            {
                service.SetLanguage(previous);
            }
        });
    }

    /// <summary>Reads the production XAML source copied into test output, not a duplicate test-only binding.</summary>
    private static XDocument LoadSource()
    {
        return XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestSources", "MainWindow.xaml"));
    }

    /// <summary>Extracts the current production title binding and evaluates it with WPF's actual markup parser.</summary>
    private static TextBox LoadActualTitleBinding()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement actual = LoadSource().Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == "TitleBox");
        string text = (string?)actual.Attribute("Text") ?? throw new InvalidOperationException("The production title has no initial text binding.");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement isolated = new(presentation + "TextBox",
            new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "loc", "clr-namespace:StudyDesk.Desktop.Localization;assembly=" + typeof(LocalizationService).Assembly.GetName().Name),
            new XAttribute("Text", text));
        return (TextBox)XamlReader.Parse(isolated.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>Uses a bounded dedicated WPF thread without creating an interactive surface or changing global culture.</summary>
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The noninteractive WPF binding check timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
