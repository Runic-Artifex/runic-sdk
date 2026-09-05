using System.ComponentModel;
using System.IO;
using System.Text.Json;
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
            using var json = JsonDocument.Parse(File.ReadAllText(picker.FileName));
            string name = json.RootElement.GetProperty("name").GetString() ?? "";
            string email = json.RootElement.GetProperty("email").GetString() ?? "";
            string company = json.RootElement.GetProperty("company").GetString() ?? "";
            _model.Name = name; _model.Email = email; _model.Company = company;
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        { MessageBox.Show(this, "Could not read the contact. Use a JSON object with name, email and company text fields."); }
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
