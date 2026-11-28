using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using StudyDesk.Contracts;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Protects assistive names from record.ToString metadata and document payloads.</summary>
public sealed partial class NativeBindingTests
{
    /// <summary>The actual source container binding exposes only a human title or issue label.</summary>
    [Theory]
    [InlineData("PageList", "Synthetic page")]
    [InlineData("SpacePicker", "Synthetic space")]
    [InlineData("IssuesList", "Synthetic issue")]
    public void ItemAutomationNamesContainOnlyTheirHumanCaption(string ownerName, string expected)
    {
        RunOnSta(() =>
        {
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement owner = LoadSource().Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == ownerName);
            XElement setter = owner.Descendants().Single(element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "AutomationProperties.Name");
            string binding = (string?)setter.Attribute("Value") ?? throw new InvalidOperationException("The item has no explicit accessible name.");
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XElement markup = new(presentation + (ownerName == "SpacePicker" ? "ComboBoxItem" : "ListBoxItem"), new XAttribute("AutomationProperties.Name", binding));
            ContentControl container = (ContentControl)XamlReader.Parse(markup.ToString(SaveOptions.DisableFormatting));
            object value = ownerName switch
            {
                "SpacePicker" => new SpaceDto(Guid.NewGuid(), expected, 1, false, DateTimeOffset.UtcNow),
                "PageList" => new PageDto(Guid.NewGuid(), Guid.NewGuid(), null, expected, "Synthetic private content must not enter the accessible name", 1, false, DateTimeOffset.UtcNow),
                _ => new { Label = expected, PrivateMetadata = "Synthetic metadata must not enter the accessible name" },
            };
            container.DataContext = value;
            container.Content = value;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            BindingOperations.GetBindingExpression(container, AutomationProperties.NameProperty)?.UpdateTarget();
            Assert.Equal(expected, AutomationProperties.GetName(container));
        });
    }
}
