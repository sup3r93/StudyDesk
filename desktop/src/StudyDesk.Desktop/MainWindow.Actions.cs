using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using CefSharp;
using Microsoft.Win32;
using StudyDesk.Desktop.Localization;

namespace StudyDesk.Desktop;

/// <summary>Contains explicit local file actions and the external issue-report link.</summary>
public partial class MainWindow
{
    /// <summary>Opens the repository's issue form in the system browser without transmitting note data.</summary>
    private void ReportIssueClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/sup3r93/StudyDesk/issues/new/choose") { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, LocalizationService.Instance["dialog.issueFallback"], LocalizationService.Instance["app.title"], MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Embeds one bounded PNG or JPEG chosen by the installation owner.</summary>
    private async void InsertImageClicked(object sender, RoutedEventArgs e)
    {
        if (current is null || !editing || busy)
        {
            return;
        }
        OpenFileDialog dialog = new() { Filter = LocalizationService.Instance["file.imageFilter"], Multiselect = false, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await RunSafelyAsync(async () =>
        {
            FileInfo info = new(dialog.FileName);
            if (info.Length is < 8 or > 262144)
            {
                throw new InvalidOperationException("Choose an image smaller than 256 KiB.");
            }
            byte[] bytes = await File.ReadAllBytesAsync(dialog.FileName);
            string mime = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
                : bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) ? "image/jpeg" : throw new InvalidOperationException("Choose a PNG or JPEG image.");
            string argument = JsonSerializer.Serialize("data:" + mime + ";base64," + Convert.ToBase64String(bytes));
            JavascriptResponse response = await EditorBrowser.EvaluateScriptAsync("window.studyDesk.insertImage(" + argument + ")");
            if (!response.Success)
            {
                throw new InvalidOperationException("The image could not be inserted.");
            }
            await SaveCurrentAsync(true);
            Sections.SelectedIndex = 0;
        });
    }

    /// <summary>Exports the selected note as a portable structured document without connection credentials.</summary>
    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (current is null || busy)
        {
            return;
        }
        SaveFileDialog dialog = new() { Filter = LocalizationService.Instance["file.jsonFilter"], DefaultExt = ".studydesk.json", FileName = "note.studydesk.json", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await RunSafelyAsync(async () =>
        {
            await SaveCurrentAsync(true);
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(new { format = "studydesk-note", formatVersion = 1, page = current }, JsonOptions);
            await File.WriteAllBytesAsync(dialog.FileName, content);
            SaveStatus.Text = LocalizationService.Instance["status.exportedJson"];
        });
    }

    /// <summary>Uses the local Chromium print engine to export the current note to PDF.</summary>
    private async void PdfClicked(object sender, RoutedEventArgs e)
    {
        if (current is null || busy)
        {
            return;
        }
        SaveFileDialog dialog = new() { Filter = LocalizationService.Instance["file.pdfFilter"], DefaultExt = ".pdf", FileName = "note.pdf", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await RunSafelyAsync(async () =>
        {
            await SaveCurrentAsync(true);
            bool success = await EditorBrowser.PrintToPdfAsync(dialog.FileName, new PdfPrintSettings { PreferCssPageSize = true, PrintBackground = true });
            if (!success)
            {
                throw new InvalidOperationException("The PDF could not be written.");
            }
            SaveStatus.Text = LocalizationService.Instance["status.exportedPdf"];
        });
    }

    /// <summary>Moves a note to reversible local trash or restores a previously trashed note.</summary>
    private async void TrashClicked(object sender, RoutedEventArgs e)
    {
        if (current is null || busy)
        {
            return;
        }
        bool trashed = !current.IsTrashed;
        if (trashed && MessageBox.Show(this, LocalizationService.Instance["dialog.trashConfirm"], LocalizationService.Instance["app.title"], MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        await RunSafelyAsync(async () =>
        {
            await SaveCurrentAsync(true);
            Guid id = current.Id;
            await store.SetPageTrashAsync(id, trashed);
            var updated = await store.GetPageAsync(id) ?? throw new InvalidOperationException("The note could not be reloaded.");
            await OpenPageAsync(updated, false);
            await ReloadPagesAsync();
        });
    }
}
