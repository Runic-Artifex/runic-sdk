using System.Collections;
using System.ComponentModel;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class ValidationTests
{
    [Test]
    public void WritesAliasedStructuredPaths()
    {
        var root = new ManualRoot();
        root.SetErrors(null, new RunicValidationMessage("Entity failure", "entity", "warning"));
        root.Child.SetErrors(null, "Nested entity failure",
            new RunicValidationMessage("Explicit nested entity failure", MemberPaths: [""]));
        root.Child.SetErrors(nameof(ManualChild.Value),
            new RunicValidationMessage("Aliased nested failure", "nested", "error", [nameof(ManualChild.Value)]));
        root.Child.SetErrors(nameof(ManualChild.Value),
            new RunicValidationMessage("Aliased nested failure", "nested", "error", [nameof(ManualChild.Value)]),
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
            "A RunicValidationMessage member path did not resolve CLR names through wire aliases.");
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

    [Test]
    public void BoundsTraversalWithoutLosingItsSignal()
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

    [Test]
    public async Task GeneratedBridgeWritesAndPublishesNestedValidationAsync()
    {
        var model = new ValidationViewModel();
        model.SetErrors(null, new RunicValidationMessage("Form invalid", "form", "warning"));
        model.Profile.SetErrors(nameof(ValidationProfile.PostalCode), new RunicValidationMessage(
            "Postal code required", "required", "error", [nameof(ValidationProfile.PostalCode)]));
        model.Items[0].SetErrors(nameof(ValidationItem.Name), "Item label required");
        model.Lookup["primary"].SetErrors(nameof(ValidationItem.Name), "Primary label required");

        using var host = new RunicWindowTestHost<ValidationViewModel>(model,
            (transport, content, vm) => new ValidationBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "validation", ViewLocator = new TestViewLocator() });
        using var snapshot = host.RawSnapshot();
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

        _ = host.Transport.TakePublications();
        model.Server.SetErrors(nameof(ValidationErrorsOnly.Detail), "Server returned an error");
        Require(await PublishedAsync(host.Transport, publication =>
        {
            using var state = JsonDocument.Parse(publication.Json);
            return HasError(state.RootElement.GetProperty("validation").GetProperty("errors"),
                ["server", "detail"], "Server returned an error");
        }), "An errors-only nested object did not publish its validation change.");
    }

    [Test]
    public void ToolkitObservableValidatorReportsEachMessageOnce()
    {
        var model = new ToolkitValidationViewModel();
        model.Validate();
        BridgeDataSubscriptionMember[] metadata =
        [
            new("name", static owner => ((ToolkitValidationViewModel)owner).Name, propertyName: nameof(ToolkitValidationViewModel.Name)),
            new("count", static owner => ((ToolkitValidationViewModel)owner).Count, propertyName: nameof(ToolkitValidationViewModel.Count)),
        ];
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) BridgeValidation.Write(writer, model, metadata);
        using var document = JsonDocument.Parse(buffer.ToArray());
        var validation = document.RootElement;
        Require(validation.GetProperty("hasErrors").GetBoolean(), "The Toolkit validator's errors did not set hasErrors.");
        var errors = validation.GetProperty("errors");
        RequireErrors(errors,
            ([], "Name is required.", 0),
            (["name"], "Name is required.", 1),
            ([], "Count must be between 1 and 10.", 0),
            (["count"], "Count must be between 1 and 10.", 1));
        Require(errors.GetArrayLength() == 2, $"The Toolkit validator's summary was not two errors: {errors}");
    }

    [Test]
    public void ReactiveValidationObjectReportsEachMessageOnce()
    {
        var model = new ReactivePasswordViewModel { NewPassword = "abc", ConfirmPassword = "abd" };
        // The fork keeps INotifyDataErrorInfo's unfiltered reading: every message.
        Require(model.GetErrors(null).Cast<object>().Count() == 3,
            "ReactiveValidationObject.GetErrors(null) no longer returns every message.");

        using var host = new RunicWindowTestHost<ReactivePasswordViewModel>(model,
            (transport, content, vm) => new ReactivePasswordBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "reactivePassword", ViewLocator = new TestViewLocator() });
        using var snapshot = host.RawSnapshot();
        var state = snapshot.RootElement.GetProperty("state");
        var validation = state.GetProperty("validation");
        Require(validation.GetProperty("hasErrors").GetBoolean(), "The ValidationContext's errors did not set hasErrors.");
        var errors = validation.GetProperty("errors");
        RequireErrors(errors,
            (["newPassword"], ReactivePasswordViewModel.TooShort, 1),
            ([], ReactivePasswordViewModel.TooShort, 0),
            (["confirmPassword"], ReactivePasswordViewModel.Mismatch, 1),
            ([], ReactivePasswordViewModel.Mismatch, 0),
            ([], ReactivePasswordViewModel.Common, 1));
        Require(errors.GetArrayLength() == 3, $"The summary was not three errors: {errors}");
        Require(state.GetProperty("newPasswordErrors").EnumerateArray().Single().GetString() == ReactivePasswordViewModel.TooShort,
            "The per-property errors changed.");

        model.NewPassword = "abcdefgh";
        model.ConfirmPassword = "abcdefgh";
        using var valid = host.RawSnapshot();
        var cleared = valid.RootElement.GetProperty("state").GetProperty("validation");
        Require(!cleared.GetProperty("hasErrors").GetBoolean() && cleared.GetProperty("errors").GetArrayLength() == 0,
            $"Valid rules still reported errors: {cleared}");
    }

    private static void RequireErrors(JsonElement errors, params (object[] Path, string Message, int Count)[] expected)
    {
        foreach (var (path, message, count) in expected)
        {
            var actual = errors.EnumerateArray().Count(error => HasError(JsonArray(error), path, message));
            Require(actual == count,
                $"'{message}' at [{string.Join(", ", path)}] appeared {actual} times, not {count}: {errors}");
        }
    }

    private static JsonElement JsonArray(JsonElement error)
    {
        using var document = JsonDocument.Parse("[" + error.GetRawText() + "]");
        return document.RootElement.Clone();
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

    private static async Task<bool> PublishedAsync(InMemoryBridgeTransport transport, Func<RunicViewPublication, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (transport.TakePublications().Any(predicate)) return true;
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
