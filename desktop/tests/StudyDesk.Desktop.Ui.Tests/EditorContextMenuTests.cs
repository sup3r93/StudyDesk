using CefSharp;
using StudyDesk.Desktop.Editor;
using StudyDesk.Desktop.Localization;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Checks the essential context menu without reading or changing the system clipboard.</summary>
public sealed class EditorContextMenuTests
{
    /// <summary>Opening the menu uses the current language instead of Chromium's startup locale.</summary>
    [Fact]
    public void EssentialContextMenuFollowsTheCurrentLanguage()
    {
        LocalizationService language = new("it");
        var italian = LocalizedEditorMenuHandler.CreateItems(ContextMenuEditState.CanCopy, false, language);
        Assert.Equal("Copia", italian.Single(item => item.Command == CefMenuCommand.Copy).Label);
        Assert.Equal(6, italian.Count);
        language.SetLanguage("en");
        var english = LocalizedEditorMenuHandler.CreateItems(ContextMenuEditState.CanCopy, false, language);
        Assert.Equal("Copy", english.Single(item => item.Command == CefMenuCommand.Copy).Label);
        Assert.DoesNotContain(english, item => item.Label.StartsWith('['));
    }

    /// <summary>Viewing mode can copy or select text but cannot undo, redo, cut or paste.</summary>
    [Fact]
    public void ViewingModeDisablesEveryMutatingContextCommand()
    {
        ContextMenuEditState flags = ContextMenuEditState.CanUndo | ContextMenuEditState.CanRedo | ContextMenuEditState.CanCut
            | ContextMenuEditState.CanCopy | ContextMenuEditState.CanPaste | ContextMenuEditState.CanSelectAll;
        var menu = LocalizedEditorMenuHandler.CreateItems(flags, false, new LocalizationService("en"));
        Assert.All(menu.Where(item => item.Command is not (CefMenuCommand.Copy or CefMenuCommand.SelectAll)), item => Assert.False(item.Enabled));
        Assert.True(menu.Single(item => item.Command == CefMenuCommand.Copy).Enabled);
        Assert.True(menu.Single(item => item.Command == CefMenuCommand.SelectAll).Enabled);
        Assert.All(LocalizedEditorMenuHandler.CreateItems(flags, true, new LocalizationService("en")), item => Assert.True(item.Enabled));
    }
}
