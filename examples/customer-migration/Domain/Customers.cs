using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomerMigration.Domain;

public sealed record Customer(Guid Id, string Name, string Email, string Company, int Version);
public sealed record CustomerDraft(Guid Id, string Name, string Email, string Company, int Version);
public sealed record FieldIssue(string Field, string Message);

public sealed class CustomerNameAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value is string text && text.Trim().Length is >= 2 and <= 100;
    public override string FormatErrorMessage(string name) => "Enter a name with 2–100 characters.";
}
public sealed class CustomerEmailAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value is string text && text.Length <= 200 && new EmailAddressAttribute().IsValid(text) && !string.IsNullOrWhiteSpace(text);
    public override string FormatErrorMessage(string name) => "Enter a valid email address (up to 200 characters).";
}
public static class CustomerRules
{
    public static FieldIssue[] Validate(CustomerDraft draft)
    {
        var errors = new List<FieldIssue>();
        if (!new CustomerNameAttribute().IsValid(draft.Name)) errors.Add(new("name", new CustomerNameAttribute().FormatErrorMessage("name")));
        if (!new CustomerEmailAttribute().IsValid(draft.Email)) errors.Add(new("email", new CustomerEmailAttribute().FormatErrorMessage("email")));
        if (draft.Company.Length > 100) errors.Add(new("company", "Use at most 100 characters."));
        return errors.ToArray();
    }
}
public sealed class CustomerProblem(string code, string message, FieldIssue[]? issues = null) : Exception(message)
{
    public string Code { get; } = code;
    public FieldIssue[] Issues { get; } = issues ?? [];
}

// A single-process sample store. A production repository can preserve this API
// while implementing database transactions and cross-process concurrency.
public sealed class CustomerDirectory(string? path = null) : IDisposable
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    private Customer[] _customers = path is not null && File.Exists(path)
        ? JsonSerializer.Deserialize(File.ReadAllText(path), CustomerJson.Default.CustomerArray) ?? []
        : Seeds();
    public Customer[] Read() => [.. Volatile.Read(ref _customers)];
    public async Task<Customer> SaveAsync(CustomerDraft draft, CancellationToken token)
    {
        var errors = CustomerRules.Validate(draft);
        if (errors.Length > 0) throw new CustomerProblem("Validation", "Correct the highlighted fields.", errors);
        await _writes.WaitAsync(token);
        try
        {
            var current = _customers.SingleOrDefault(c => c.Id == draft.Id)
                ?? throw new CustomerProblem("NotFound", "This customer no longer exists.");
            if (current.Version != draft.Version) throw new CustomerProblem("Conflict", "This customer changed. Reload before saving.");
            if (_customers.Any(c => c.Id != draft.Id && string.Equals(c.Email, draft.Email.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new CustomerProblem("Validation", "Email is already in use.", [new("email", "Another customer uses this email address.")]);
            var saved = new Customer(draft.Id, draft.Name.Trim(), draft.Email.Trim(), draft.Company.Trim(), checked(current.Version + 1));
            var next = _customers.Select(c => c.Id == saved.Id ? saved : c).ToArray();
            string? temporary = null;
            try
            {
                if (path is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next, CustomerJson.Default.CustomerArray), token);
                }
                token.ThrowIfCancellationRequested();
                // Commit point: cancellation after this point must report success.
                if (temporary is not null) File.Move(temporary, path!, overwrite: true);
                Volatile.Write(ref _customers, next);
                return saved;
            }
            finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _writes.Release(); }
    }
    public void Dispose() => _writes.Dispose();
    public static Customer[] Seeds() => [
        new(Guid.Parse("11111111-1111-4111-8111-111111111111"), "Alex Morgan", "alex@example.com", "Northstar Studio", 1),
        new(Guid.Parse("22222222-2222-4222-8222-222222222222"), "Sam Rivera", "sam@example.com", "Fieldwork", 1),
        new(Guid.Parse("33333333-3333-4333-8333-333333333333"), "Robin Chen", "robin@example.com", "Independent", 1)];
}
public sealed class CustomerService(CustomerDirectory directory, TimeSpan stepDelay)
{
    public Customer[] Read() => directory.Read();
    public async Task<Customer> SaveAsync(CustomerDraft draft, Func<int, ValueTask> progress, CancellationToken token)
    {
        var errors = CustomerRules.Validate(draft);
        if (errors.Length != 0) throw new CustomerProblem("Validation", "Correct the highlighted fields.", errors);
        // Visible staging for this demonstration; no simulated delay is required by Runic.
        foreach (int percent in new[] { 20, 60, 90 })
        {
            token.ThrowIfCancellationRequested();
            await progress(percent);
            await Task.Delay(stepDelay, token);
        }
        return await directory.SaveAsync(draft, token);
    }
}
[JsonSerializable(typeof(Customer[]))]
internal partial class CustomerJson : JsonSerializerContext;
