using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CustomerMigration.Domain;

namespace CustomerMigration.Before;

// This baseline is the only application layer that depends on CommunityToolkit.
// WPF hosts it; its portable logic can also be tested without a UI dispatcher.
public sealed partial class CustomerEditorViewModel : ObservableValidator
{
    private readonly CustomerService _service;
    private Customer _original;
    public CustomerEditorViewModel(CustomerService service)
    {
        _service = service;
        Customers = new(service.Read());
        _original = Customers[0];
        name = _original.Name; email = _original.Email; company = _original.Company;
    }
    public ObservableCollection<Customer> Customers { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilteredCustomers))] private string search = "";
    public IEnumerable<Customer> FilteredCustomers => Customers.Where(c => $"{c.Name} {c.Email} {c.Company}".Contains(Search, StringComparison.OrdinalIgnoreCase));
    [ObservableProperty, NotifyDataErrorInfo, CustomerName, NotifyPropertyChangedFor(nameof(IsDirty)), NotifyCanExecuteChangedFor(nameof(SaveCommand))] private string name;
    [ObservableProperty, NotifyDataErrorInfo, CustomerEmail, NotifyPropertyChangedFor(nameof(IsDirty)), NotifyCanExecuteChangedFor(nameof(SaveCommand))] private string email;
    [ObservableProperty, NotifyDataErrorInfo, MaxLength(100), NotifyPropertyChangedFor(nameof(IsDirty)), NotifyCanExecuteChangedFor(nameof(SaveCommand))] private string company;
    [ObservableProperty] private int progress;
    [ObservableProperty] private string status = "Ready";
    public bool IsDirty => Name != _original.Name || Email != _original.Email || Company != _original.Company;
    private bool CanSave() => IsDirty && !HasErrors;
    public CustomerDraft Draft => new(_original.Id, Name, Email, Company, _original.Version);
    public bool Select(Guid id, bool discard = false)
    {
        if (SaveCommand.IsRunning || (IsDirty && !discard)) return false;
        _original = Customers.Single(c => c.Id == id);
        Name = _original.Name; Email = _original.Email; Company = _original.Company;
        ClearErrors(); OnPropertyChanged(nameof(IsDirty)); SaveCommand.NotifyCanExecuteChanged();
        return true;
    }
    [RelayCommand(CanExecute = nameof(CanSave), IncludeCancelCommand = true)]
    private async Task SaveAsync(CancellationToken token)
    {
        ValidateAllProperties();
        if (HasErrors) return;
        Status = "Saving"; Progress = 0;
        try
        {
            var saved = await _service.SaveAsync(Draft, value => { Progress = value; return ValueTask.CompletedTask; }, token);
            int index = Customers.IndexOf(_original);
            Customers[index] = saved; _original = saved;
            Name = saved.Name; Email = saved.Email; Company = saved.Company;
            OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(FilteredCustomers));
            Status = "Saved"; Progress = 100;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Status = "Cancelled"; }
        catch (CustomerProblem problem) { Status = problem.Message; }
        catch (IOException) { Status = "Could not write the customer file."; }
        finally { SaveCommand.NotifyCanExecuteChanged(); }
    }
}
