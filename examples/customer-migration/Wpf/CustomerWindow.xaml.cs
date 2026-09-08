using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices;
using CustomerMigration.Domain;
using System.Windows;
using System.Windows.Controls;
using CustomerMigration.Before;
using Microsoft.Win32;
namespace CustomerMigration.Wpf;

public partial class CustomerWindow : Window
{
    private readonly CustomerEditorViewModel _model;
    private bool _closing;
    public CustomerWindow(CustomerEditorViewModel model) { InitializeComponent(); DataContext = _model = model; Closing += ConfirmClose; }
    private bool Discard() => !_model.IsDirty || MessageBox.Show(this, "Discard your unsaved changes?", "Unsaved changes", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    private void SelectCustomer(object sender, RoutedEventArgs e)
    {
        if (_model.SaveCommand.IsRunning || !Discard()) return;
        _model.Select((Guid)((Button)sender).Tag, discard: true);
    }
    private void ImportContact(object sender, RoutedEventArgs e)
    {
        if (_model.SaveCommand.IsRunning) return;
        var picker = new OpenFileDialog { Filter = "Contact JSON|*.json", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 4096) throw new IOException("Choose a file smaller than 4 KB.");
            using var stream = File.OpenRead(picker.FileName);
            byte[] bytes = new byte[4097];
            int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count > 4096) throw new InvalidDataException();
            ReviewContact(new UTF8Encoding(false, true).GetString(bytes, 0, count));
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException or DecoderFallbackException)
        { MessageBox.Show(this, "Could not read the contact. Use a JSON object with name, email and company text fields."); }
    }
    private void ReviewContact(string text)
    {
        var contact = ContactCodec.Parse(text);
        if (MessageBox.Show(this, $"Apply {contact.Name} ({contact.Email}) to the current draft?", "Review contact", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _model.ApplyContact(text);
    }
    private void PasteContact(object sender, RoutedEventArgs e)
    {
        if (_model.SaveCommand.IsRunning) return;
        try
        {
            if (!Clipboard.ContainsText()) { MessageBox.Show(this, "The clipboard contains no text."); return; }
            ReviewContact(Clipboard.GetText());
        }
        catch (Exception error) when (error is ExternalException or JsonException or InvalidDataException)
        { MessageBox.Show(this, "Could not paste a contact. Use valid contact JSON up to 4096 UTF-8 bytes; the clipboard may be busy."); }
    }
    private string? ConfirmExport()
    {
        if (_model.SaveCommand.IsRunning) return null;
        string text = _model.ExportSavedContact();
        return MessageBox.Show(this, $"Export or copy this saved revision? Unsaved draft edits are excluded.\n{text}", "Confirm saved contact", MessageBoxButton.YesNo) == MessageBoxResult.Yes ? text : null;
    }
    private void CopyContact(object sender, RoutedEventArgs e)
    {
        string? text = ConfirmExport();
        if (text is null) return;
        try { Clipboard.SetText(text); }
        catch (ExternalException) { MessageBox.Show(this, "The clipboard is busy. Try again."); }
    }
    private void ExportContact(object sender, RoutedEventArgs e)
    {
        string? text = ConfirmExport();
        if (text is null) return;
        var picker = new SaveFileDialog { Filter = "Contact JSON|*.json", FileName = "contact.json" };
        if (picker.ShowDialog(this) != true) return;
        string staging = Path.Combine(Path.GetDirectoryName(picker.FileName)!, ".contact-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(staging, text, new UTF8Encoding(false));
            File.Move(staging, picker.FileName, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "Could not export the contact. Check destination permissions."); }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    private async void ConfirmClose(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        if (_model.SaveCommand.IsRunning)
        {
            e.Cancel = true;
            if (MessageBox.Show(this, "Cancel the save and close?", "Save in progress", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _model.SaveCommand.Cancel();
            if (_model.SaveCommand.ExecutionTask is { } task) await task;
            _closing = true; Close();
        }
        else e.Cancel = !Discard();
    }
}
