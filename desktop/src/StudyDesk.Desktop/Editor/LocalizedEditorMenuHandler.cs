using CefSharp;
using CefSharp.Handler;
using StudyDesk.Desktop.Localization;

namespace StudyDesk.Desktop.Editor;

/// <summary>Supplies only ordinary editing commands, labelled in the current native interface language.</summary>
public sealed class LocalizedEditorMenuHandler : ContextMenuHandler
{
    /// <summary>Rebuilds the native CEF menu each time so changing language cannot leave English commands behind.</summary>
    protected override void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters, IMenuModel model)
    {
        model.Clear();
        if (frame.IsDisposed || !frame.IsMain || !LocalEditorPolicy.IsTrusted(frame.Url))
        {
            return;
        }

        foreach (EditorMenuItem item in CreateItems(parameters.EditStateFlags, parameters.IsEditable, LocalizationService.Instance))
        {
            if (item.SeparatorBefore)
            {
                model.AddSeparator();
            }

            model.AddItem(item.Command, item.Label);
            model.SetEnabled(item.Command, item.Enabled);
        }
    }

    /// <summary>Uses CEF's supported-command flags and prevents mutating commands in viewing mode.</summary>
    public static IReadOnlyList<EditorMenuItem> CreateItems(ContextMenuEditState capabilities, bool editable, LocalizationService language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return
        [
            new(CefMenuCommand.Undo, language["editor.undo"], editable && capabilities.HasFlag(ContextMenuEditState.CanUndo), false),
            new(CefMenuCommand.Redo, language["editor.redo"], editable && capabilities.HasFlag(ContextMenuEditState.CanRedo), false),
            new(CefMenuCommand.Cut, language["context.cut"], editable && capabilities.HasFlag(ContextMenuEditState.CanCut), true),
            new(CefMenuCommand.Copy, language["context.copy"], capabilities.HasFlag(ContextMenuEditState.CanCopy), false),
            new(CefMenuCommand.Paste, language["context.paste"], editable && capabilities.HasFlag(ContextMenuEditState.CanPaste), false),
            new(CefMenuCommand.SelectAll, language["context.selectAll"], capabilities.HasFlag(ContextMenuEditState.CanSelectAll), true),
        ];
    }
}

/// <summary>Describes one ordinary native editor command without carrying any selected document text.</summary>
/// <param name="Command">The standard CEF command executed by Chromium.</param>
/// <param name="Label">Its current localized caption.</param>
/// <param name="Enabled">Whether CEF and the note's edit mode permit the command.</param>
/// <param name="SeparatorBefore">Whether a native separator groups the command.</param>
public sealed record EditorMenuItem(CefMenuCommand Command, string Label, bool Enabled, bool SeparatorBefore);
