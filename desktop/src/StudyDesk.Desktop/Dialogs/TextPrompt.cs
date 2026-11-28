using System.Windows;
using System.Windows.Controls;
using StudyDesk.Desktop.Localization;

namespace StudyDesk.Desktop.Dialogs;

/// <summary>Requests a short title using native, keyboard-accessible Windows controls.</summary>
public sealed class TextPrompt : DialogWindow
{
    private readonly TextBox value = new() { MaxLength = 200, Margin = new Thickness(0, 8, 0, 12) };

    /// <summary>Creates a modal input window owned by the current workspace.</summary>
    public TextPrompt(Window owner, string title, string label)
        : base(owner, title, 460)
    {
        StackPanel panel = new() { Margin = new Thickness(24, 12, 24, 24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        TextBlock description = new() { Text = Localization.LocalizationService.Instance["dialog.nameDescription"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 24) };
        description.SetResourceReference(TextBlock.ForegroundProperty, "StudyDesk.MutedTextBrush");
        panel.Children.Add(description);
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        System.Windows.Automation.AutomationProperties.SetName(value, label);
        panel.Children.Add(value);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Button cancel = new() { Content = LocalizationService.Instance["common.cancel"], IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        Button create = new() { Content = LocalizationService.Instance["common.create"], IsDefault = true };
        create.SetResourceReference(StyleProperty, "StudyDesk.PrimaryButton");
        create.Click += CreateClicked;
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        panel.Children.Add(buttons);
        SetBody(panel);
        Loaded += PromptLoaded;
    }

    /// <summary>Gets the title approved by the user.</summary>
    public string Value => value.Text.Trim();

    /// <summary>Accepts a nonempty title and keeps invalid input available for correction.</summary>
    private void CreateClicked(object sender, RoutedEventArgs e)
    {
        if (Value.Length > 0)
        {
            DialogResult = true;
        }
    }

    /// <summary>Places keyboard focus in the input field when the dialog appears.</summary>
    private void PromptLoaded(object sender, RoutedEventArgs e)
    {
        value.Focus();
    }
}
