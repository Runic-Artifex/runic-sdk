using System.Collections;
using System.ComponentModel;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

internal static class ValidationTests
{
    internal static async Task RunAsync()
    {
        WritesAliasedStructuredPaths();
        BoundsTraversalWithoutLosingItsSignal();
        await GeneratedBridgeWritesAndPublishesNestedValidationAsync();
    }

    private static void WritesAliasedStructuredPaths()
    {
        var root = new ManualRoot();
        root.SetErrors(null, new BridgeValidationMessage("Entity failure", "entity", "warning"));
        root.Child.SetErrors(null, "Nested entity failure",
            new BridgeValidationMessage("Explicit nested entity failure", MemberPaths: [""]));
        root.Child.SetErrors(nameof(ManualChild.Value),
            new BridgeValidationMessage("Aliased nested failure", "nested", "error", [nameof(ManualChild.Value)]));
        root.Child.SetErrors(nameof(ManualChild.Value),
            new BridgeValidationMessage("Aliased nested failure", "nested", "error", [nameof(ManualChild.Value)]),
            new System.ComponentModel.DataAnnotations.ValidationResult("ValidationResult member path", [nameof(ManualChild.Value)]));
        root.Items[0].SetErrors(nameof(ManualChild.Value), "First item failed");
        root.Lookup["slot"].SetErrors(nameof(ManualChild.Value), "Dictionary item failed");
        root.Groups[0][0].SetErrors(nameof(ManualChild.Value), "Nested list failed");

        var childMembers = new[]
        {
            new BridgeDataSubscriptionMember("wire-value", static owner => ((ManualChild)owner).Value,
                propertyName: nameof(ManualChild.Value)),
        };
        var metadata = new[]
        {
            new BridgeDataSubscriptionMember("wire-child", static owner => ((ManualRoot)owner).Child,
                childMembers, propertyName: nameof(ManualRoot.Child)),
            new BridgeDataSubscriptionMember("wire-secondary", static owner => ((ManualRoot)owner).Secondary,
                childMembers, propertyName: nameof(ManualRoot.Secondary)),
            new BridgeDataSubscriptionMember("wire-items", static owner => ((ManualRoot)owner).Items,
                childMembers,
                enumerateChildren: static value => (IEnumerable)value,
                enumerateValidationChildren: static value => BridgeValidation.EnumerateIndexed((IEnumerable)value),
                propertyName: nameof(ManualRoot.Items)),
            new BridgeDataSubscriptionMember("wire-lookup", static owner => ((ManualRoot)owner).Lookup,
                childMembers,
                enumerateChildren: static value => ((Dictionary<string, ManualChild>)value).Values,
                enumerateValidationChildren: static value => BridgeValidation.EnumerateDictionary((Dictionary<string, ManualChild>)value),
                propertyName: nameof(ManualRoot.Lookup)),
            new BridgeDataSubscriptionMember("wire-groups", static owner => ((ManualRoot)owner).Groups,
                [new BridgeDataSubscriptionMember("$items", static owner => owner, childMembers,
                    enumerateChildren: static value => (IEnumerable)value,
                    enumerateValidationChildren: static value => BridgeValidation.EnumerateIndexed((IEnumerable)value),
                    isPathTransparent: true, propertyName: "$items")],
                enumerateChildren: static value => (IEnumerable)value,
                enumerateValidationChildren: static value => BridgeValidation.EnumerateIndexed((IEnumerable)value),
                propertyName: nameof(ManualRoot.Groups)),
        };

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) BridgeValidation.Write(writer, root, metadata);
        using var document = JsonDocument.Parse(buffer.ToArray());
        var validation = document.RootElement;
        Require(validation.GetProperty("hasErrors").GetBoolean(), "Structured validation did not set hasErrors.");
        var errors = validation.GetProperty("errors");
        Require(HasError(errors, [], "Entity failure", "entity", "warning"),
            "Entity-level validation did not retain its empty path and metadata.");
        Require(HasError(errors, ["wire-child"], "Nested entity failure"),
            "A nested entity-level error did not retain its owner path.");
        Require(HasError(errors, ["wire-child"], "Explicit nested entity failure"),
            "An explicitly empty member path did not retain its nested owner path.");
        Require(HasError(errors, ["wire-child", "wire-value"], "Aliased nested failure", "nested", "error"),
            "A BridgeValidationMessage member path did not resolve CLR names through wire aliases.");
        Require(HasError(errors, ["wire-child", "wire-value"], "ValidationResult member path"),
            "ValidationResult.MemberNames did not resolve relative to its containing object.");
        Require(HasError(errors, ["wire-items", 0, "wire-value"], "First item failed"),
            "List validation did not retain an index path.");
        Require(HasError(errors, ["wire-lookup", "slot", "wire-value"], "Dictionary item failed"),
            "Dictionary validation did not retain its string key path.");
        Require(HasError(errors, ["wire-secondary", "wire-value"], "Aliased nested failure", "nested", "error"),
            "A shared nested reference was not reported for each reachable wire path.");
        Require(HasError(errors, ["wire-groups", 0, 0, "wire-value"], "Nested list failed"),
            "A nested collection leaked its synthetic metadata name into the validation path.");
    }

    private static void BoundsTraversalWithoutLosingItsSignal()
    {
        var root = new ManualRoot();
        for (var index = 0; index < 5_000; index++) root.Items.Add(new());
        var childMembers = new[]
        {
            new BridgeDataSubscriptionMember("wire-value", static owner => ((ManualChild)owner).Value,
                propertyName: nameof(ManualChild.Value)),
        };
        var metadata = new[]
        {
            new BridgeDataSubscriptionMember("wire-items", static owner => ((ManualRoot)owner).Items,
                childMembers, static value => (IEnumerable)value,
                enumerateValidationChildren: static value => BridgeValidation.EnumerateIndexed((IEnumerable)value),
                propertyName: nameof(ManualRoot.Items)),
        };
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) BridgeValidation.Write(writer, root, metadata);
        using var document = JsonDocument.Parse(buffer.ToArray());
        Require(document.RootElement.GetProperty("truncated").GetBoolean(),
            "A validation traversal exceeding its bounded entry budget did not disclose truncation.");
    }

    private static async Task GeneratedBridgeWritesAndPublishesNestedValidationAsync()
    {
        var model = new ValidationViewModel();
        model.SetErrors(null, new BridgeValidationMessage("Form invalid", "form", "warning"));
        model.Profile.SetErrors(nameof(ValidationProfile.PostalCode), new BridgeValidationMessage(
            "Postal code required", "required", "error", [nameof(ValidationProfile.PostalCode)]));
        model.Items[0].SetErrors(nameof(ValidationItem.Name), "Item label required");
        model.Lookup["primary"].SetErrors(nameof(ValidationItem.Name), "Primary label required");

        using var host = new RunicWindowTestHost<ValidationViewModel>(model, "validation",
            (transport, content, vm) => new ValidationBridge(transport, vm, content: content), new TestViewLocator());
        using var snapshot = host.Snapshot();
        var validation = snapshot.RootElement.GetProperty("state").GetProperty("validation");
        Require(validation.GetProperty("hasErrors").GetBoolean(), "Generated state omitted structured validation.");
        var errors = validation.GetProperty("errors");
        Require(HasError(errors, [], "Form invalid", "form", "warning"), "Generated entity error was omitted.");
        Require(HasError(errors, ["model-profile", "postal-code"], "Postal code required", "required", "error"),
            "Generated nested alias path was incorrect.");
        Require(HasError(errors, ["items", 0, "label"], "Item label required"),
            "Generated list validation path was incorrect.");
        Require(HasError(errors, ["lookup", "primary", "label"], "Primary label required"),
            "Generated dictionary validation path was incorrect.");

        _ = host.Transport.DrainPublications();
        model.Server.SetErrors(nameof(ValidationErrorsOnly.Detail), "Server returned an error");
        Require(await PublishedAsync(host.Transport, publication =>
        {
            using var state = JsonDocument.Parse(publication.StateJson);
            return HasError(state.RootElement.GetProperty("validation").GetProperty("errors"),
                ["server", "detail"], "Server returned an error");
        }), "An errors-only nested object did not publish its validation change.");
    }

    private static bool HasError(JsonElement errors, object[] path, string message,
        string? code = null, string? severity = null) => errors.EnumerateArray().Any(error =>
    {
        if (error.GetProperty("message").GetString() != message) return false;
        if (code is not null && (!error.TryGetProperty("code", out var actualCode) || actualCode.GetString() != code)) return false;
        if (severity is not null && (!error.TryGetProperty("severity", out var actualSeverity) || actualSeverity.GetString() != severity)) return false;
        var actualPath = error.GetProperty("path");
        if (actualPath.GetArrayLength() != path.Length) return false;
        for (var index = 0; index < path.Length; index++)
        {
            var segment = actualPath[index];
            if (path[index] is int number)
            {
                if (segment.ValueKind != JsonValueKind.Number || segment.GetInt32() != number) return false;
            }
            else if (segment.ValueKind != JsonValueKind.String || segment.GetString() != (string)path[index]) return false;
        }
        return true;
    });

    private static async Task<bool> PublishedAsync(InMemoryViewTransport transport, Func<ViewTestPublication, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (transport.DrainPublications().Any(predicate)) return true;
            await Task.Delay(10);
        }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ManualRoot : ManualNode
    {
        internal ManualChild Child { get; } = new();
        internal ManualChild Secondary => Child;
        internal List<ManualChild> Items { get; } = [new()];
        internal Dictionary<string, ManualChild> Lookup { get; } = new() { ["slot"] = new() };
        internal List<List<ManualChild>> Groups { get; } = [[new()]];
    }

    private sealed class ManualChild : ManualNode
    {
        internal string Value { get; set; } = string.Empty;
    }

    private abstract class ManualNode : INotifyDataErrorInfo
    {
        private readonly Dictionary<string, List<object>> _errors = new();
        public bool HasErrors => _errors.Values.Any(values => values.Count > 0);
        public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
        public IEnumerable GetErrors(string? propertyName) => _errors.TryGetValue(Key(propertyName), out var values) ? values : [];
        internal void SetErrors(string? propertyName, params object[] values)
        {
            _errors[Key(propertyName)] = [.. values];
            ErrorsChanged?.Invoke(this, new(propertyName));
        }

        private static string Key(string? propertyName) => propertyName ?? string.Empty;
    }
}
