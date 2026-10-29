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

        _ = host.Root.TakePublications();
        model.Server.SetErrors(nameof(ValidationErrorsOnly.Detail), "Server returned an error");
        // The change is a frame that sets the validation member.
        Require(await PublishedAsync(host.Root, publication => publication.Sets.Any(set => set.Field == "validation"
            && HasError(set.Value.GetProperty("errors"), ["server", "detail"], "Server returned an error"))),
            "An errors-only nested object did not publish its validation change.");
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

    // A rule that names no member changes neither ErrorsChanged nor HasErrors while another rule already fails; the
    // Bridge observes the ValidationContext and pushes a frame that sets validation, at the root and in nested objects.
    [Test]
    public async Task RuleOnlyChangesPushValidationFramesAsync()
    {
        var model = new ReactiveRulesViewModel { Name = "far too long" };
        using var host = ReactiveRulesHost(model);
        using (var snapshot = host.RawSnapshot())
        {
            var errors = snapshot.RootElement.GetProperty("state").GetProperty("validation").GetProperty("errors");
            RequireErrors(errors, (["name"], ReactiveRulesViewModel.NameTooLong, 1));
            Require(errors.GetArrayLength() == 1, $"The first state was not one error: {errors}");
        }
        _ = host.Root.TakePublications();
        var events = 0;
        model.ErrorsChanged += (_, _) => events++;
        model.PropertyChanged += (_, _) => events++;

        model.Approved = false;
        Require(events == 0, "The rule that names no member raised a model event; the test no longer covers the ValidationContext.");
        Require(await PublishedAsync(host.Root, publication => ValidationSet(publication) is { } validation
                && HasError(validation.GetProperty("errors"), [], ReactiveRulesViewModel.ApprovalRequired)
                && HasError(validation.GetProperty("errors"), ["name"], ReactiveRulesViewModel.NameTooLong)),
            "A rule that names no member did not push its validation.");

        model.Approved = true;
        Require(await PublishedAsync(host.Root, publication => ValidationSet(publication) is { } validation
                && !HasError(validation.GetProperty("errors"), [], ReactiveRulesViewModel.ApprovalRequired)),
            "The rule's recovery did not push its validation.");

        model.Address.Verified = false;
        Require(await PublishedAsync(host.Root, publication => ValidationSet(publication) is { } validation
                && HasError(validation.GetProperty("errors"), ["address"], ReactiveRulesAddress.Unverified)),
            "A nested object's rule that names no member did not push its validation.");

        model.Lines[0].Open = false;
        Require(await PublishedAsync(host.Root, publication => ValidationSet(publication) is { } validation
                && HasError(validation.GetProperty("errors"), ["lines", 0], ReactiveRulesLine.Blocked)),
            "A collection row's rule that names no member did not push its validation.");

        // A row added later is observed too, and a removed one no longer publishes.
        var added = new ReactiveRulesLine();
        model.Lines.Add(added);
        _ = await PublishedAsync(host.Root, publication => publication.Sets.Any(set => set.Field == "lines"));
        added.Open = false;
        Require(await PublishedAsync(host.Root, publication => ValidationSet(publication) is { } validation
                && HasError(validation.GetProperty("errors"), ["lines", 1], ReactiveRulesLine.Blocked)),
            "An added row's rule that names no member did not push its validation.");
        model.Lines.Remove(added);
        _ = await PublishedAsync(host.Root, publication => publication.Sets.Any(set => set.Field == "lines"));
        _ = host.Root.TakePublications();
        added.Open = true;
        Require(!await PublishedAsync(host.Root, _ => true, TimeSpan.FromMilliseconds(200)),
            "A removed row's validation still published.");
    }

    // Nested and collection DTOs with their own ValidationContext report at their paths, property rules once.
    [Test]
    public void NestedAndCollectionValidationContextsReportAtTheirPaths()
    {
        var model = new ReactiveRulesViewModel();
        model.Address.City = "";
        model.Lines[0].Amount = 0;
        model.Lines.Add(new ReactiveRulesLine { Amount = -1, Open = false });
        using var host = ReactiveRulesHost(model);
        using var snapshot = host.RawSnapshot();
        var errors = snapshot.RootElement.GetProperty("state").GetProperty("validation").GetProperty("errors");
        RequireErrors(errors,
            (["address", "city"], ReactiveRulesAddress.CityRequired, 1),
            (["address"], ReactiveRulesAddress.CityRequired, 0),
            (["lines", 0, "amount"], ReactiveRulesLine.AmountPositive, 1),
            (["lines", 1, "amount"], ReactiveRulesLine.AmountPositive, 1),
            (["lines", 1], ReactiveRulesLine.Blocked, 1));
        Require(errors.GetArrayLength() == 4, $"The nested summary was not four errors: {errors}");
    }

    // A checked write's applied receipt carries the member's messages after the write (applied.validation).
    [Test]
    public void CheckedWriteReceiptCarriesTheMembersValidation()
    {
        var model = new ReactiveRulesViewModel { Approved = false };
        using var host = ReactiveRulesHost(model);
        var invalid = host.Root.Write(vm => vm.Name, "far too long");
        Require(invalid.Kind == RunicFieldWriteReceiptKind.Applied, $"The write was not applied: {invalid}");
        Require(ReceiptValidation(invalid.Json) == ReactiveRulesViewModel.NameTooLong,
            $"The applied receipt did not carry the member's validation, and only it: {invalid}");
        var valid = host.Root.Write(vm => vm.Name, "short");
        Require(valid.Kind == RunicFieldWriteReceiptKind.Applied && ReceiptValidation(valid.Json) is null,
            $"A valid write's receipt carried validation: {valid}");
    }

    // A disposed ReactiveValidationObject keeps HasErrors true; the Bridge reads its ValidationContext instead.
    [Test]
    public void DisposedReactiveValidationObjectHasNoErrors()
    {
        var model = new ReactiveRulesViewModel { Name = "far too long", Approved = false };
        Require(model.HasErrors, "The fixture's rules did not fail.");
        using var host = ReactiveRulesHost(model);
        model.Dispose();
        Require(model.HasErrors, "A disposed ReactiveValidationObject no longer keeps HasErrors; the test covers nothing.");
        using var snapshot = host.RawSnapshot();
        var validation = snapshot.RootElement.GetProperty("state").GetProperty("validation");
        Require(!validation.GetProperty("hasErrors").GetBoolean() && validation.GetProperty("errors").GetArrayLength() == 0,
            $"A disposed ReactiveValidationObject reported errors: {validation}");
    }

    // A RunicValidationMessage member path may index lists and dictionaries: Items[0].Value, Lookup[slot].Value and
    // Groups[0][0].Value. A path that names no element reports at the object.
    [Test]
    public void ResolvesIndexedMemberPaths()
    {
        var root = new ManualRoot();
        root.SetErrors(null,
            new RunicValidationMessage("Indexed", MemberPaths: ["Items[0].Value"]),
            new RunicValidationMessage("Keyed", MemberPaths: ["Lookup[slot].Value"]),
            new RunicValidationMessage("Quoted key", MemberPaths: ["Lookup['slot']"]),
            new RunicValidationMessage("Nested", MemberPaths: ["Groups[0][0].Value"]),
            new RunicValidationMessage("After a member", MemberPaths: ["Holder.Items[0].Value"]),
            new RunicValidationMessage("Missing", MemberPaths: ["Items[5].Value"]),
            new RunicValidationMessage("Malformed", MemberPaths: ["Items[0"]));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) BridgeValidation.Write(writer, root, ManualMetadata());
        using var document = JsonDocument.Parse(buffer.ToArray());
        var errors = document.RootElement.GetProperty("errors");
        Require(HasError(errors, ["wire-items", 0, "wire-value"], "Indexed"), $"A list index did not resolve: {errors}");
        Require(HasError(errors, ["wire-lookup", "slot", "wire-value"], "Keyed"), $"A dictionary key did not resolve: {errors}");
        Require(HasError(errors, ["wire-lookup", "slot"], "Quoted key"), $"A quoted dictionary key did not resolve: {errors}");
        Require(HasError(errors, ["wire-groups", 0, 0, "wire-value"], "Nested"), $"A nested list index did not resolve: {errors}");
        Require(HasError(errors, ["wire-holder", "wire-items", 0, "wire-value"], "After a member"),
            $"An index after a plain member did not resolve: {errors}");
        Require(HasError(errors, [], "Missing"), $"An index of no element did not report at the object: {errors}");
        Require(HasError(errors, [], "Malformed"), $"A malformed index did not report at the object: {errors}");
    }

    // An empty message is the default text, in validation and in the member's error array.
    [Test]
    public void EmptyMessagesBecomeTheDefaultText()
    {
        var model = new ValidationViewModel();
        model.SetErrors(null, "", new System.ComponentModel.DataAnnotations.ValidationResult(" "));
        model.Profile.SetErrors(nameof(ValidationProfile.PostalCode), new RunicValidationMessage("",
            MemberPaths: [nameof(ValidationProfile.PostalCode)]));
        using var host = new RunicWindowTestHost<ValidationViewModel>(model,
            (transport, content, vm) => new ValidationBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "validation", ViewLocator = new TestViewLocator() });
        using var snapshot = host.RawSnapshot();
        var errors = snapshot.RootElement.GetProperty("state").GetProperty("validation").GetProperty("errors");
        RequireErrors(errors, ([], "Invalid value.", 2), (["model-profile", "postal-code"], "Invalid value.", 1));
        Require(errors.EnumerateArray().All(error => error.GetProperty("message").GetString() == "Invalid value."),
            $"An empty message was not the default text: {errors}");
    }

    // D053.3: a ViewModel with a ValidationContext but no INotifyDataErrorInfo has the validation member, with its
    // property rules at their paths and the others at the object's, and publishes their changes.
    [Test]
    public async Task ValidatableViewModelWithoutDataErrorInfoHasValidationAsync()
    {
        var model = new ValidatableOnlyViewModel { Name = "" };
        model.Address.City = "";
        using var host = new RunicWindowTestHost<ValidatableOnlyViewModel>(model,
            (transport, content, vm) => new ValidatableOnlyBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "validatableOnly", ViewLocator = new TestViewLocator() });
        using (var snapshot = host.RawSnapshot())
        {
            var state = snapshot.RootElement.GetProperty("state");
            Require(!state.TryGetProperty("validationContext", out _) && !state.TryGetProperty("nameErrors", out _)
                    && !state.GetProperty("address").TryGetProperty("validationContext", out _),
                $"The state exposed a ValidationContext or per-member errors: {state}");
            var validation = state.GetProperty("validation");
            Require(validation.GetProperty("hasErrors").GetBoolean(), $"hasErrors was false: {validation}");
            var errors = validation.GetProperty("errors");
            RequireErrors(errors,
                (["name"], ValidatableOnlyViewModel.NameRequired, 1),
                ([], ValidatableOnlyViewModel.NameRequired, 0),
                (["address", "city"], ValidatableOnlyAddress.CityRequired, 1));
            Require(errors.GetArrayLength() == 2, $"The summary was not two errors: {errors}");
        }

        _ = host.Root.TakePublications();
        model.Approved = false;
        Require(await PublishedAsync(host.Root, publication => ValidationSet(publication) is { } validation
                && HasError(validation.GetProperty("errors"), [], ValidatableOnlyViewModel.ApprovalRequired)),
            "A rule that names no member did not push its validation.");
        model.Address.City = "Wien";
        Require(await PublishedAsync(host.Root, publication => ValidationSet(publication) is { } validation
                && !HasError(validation.GetProperty("errors"), ["address", "city"], ValidatableOnlyAddress.CityRequired)),
            "A nested property rule did not push its validation.");

        var write = host.Root.Write(vm => vm.Name, "");
        Require(write.Kind == RunicFieldWriteReceiptKind.Applied && ReceiptValidation(write.Json) == ValidatableOnlyViewModel.NameRequired,
            $"The checked write did not report the property's rule: {write}");
    }

    private static RunicWindowTestHost<ReactiveRulesViewModel> ReactiveRulesHost(ReactiveRulesViewModel model) =>
        new(model, (transport, content, vm) => new ReactiveRulesBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "reactiveRules", ViewLocator = new TestViewLocator() });

    private static JsonElement? ValidationSet(RunicViewPublication publication) =>
        publication.Sets.FirstOrDefault(set => set.Field == "validation")?.Value;

    private static string? ReceiptValidation(string json)
    {
        using var document = JsonDocument.Parse(json);
        var receipt = document.RootElement.GetProperty("receipt");
        return receipt.TryGetProperty("validation", out var validation) ? validation.GetString() : null;
    }

    private static BridgeDataSubscriptionMember[] ManualMetadata()
    {
        var childMembers = new[]
        {
            new BridgeDataSubscriptionMember("wire-value", static owner => ((ManualChild)owner).Value,
                propertyName: nameof(ManualChild.Value)),
        };
        BridgeDataSubscriptionMember items = new("wire-items", static owner => ((ManualRoot)owner).Items, childMembers,
            enumerateChildren: static value => (IEnumerable)value,
            enumerateValidationChildren: static value => BridgeValidation.EnumerateIndexed((IEnumerable)value),
            propertyName: nameof(ManualRoot.Items));
        return
        [
            new("wire-holder", static owner => ((ManualRoot)owner).Holder, [items], propertyName: nameof(ManualRoot.Holder)),
            new("wire-items", static owner => ((ManualRoot)owner).Items, childMembers,
                enumerateChildren: static value => (IEnumerable)value,
                enumerateValidationChildren: static value => BridgeValidation.EnumerateIndexed((IEnumerable)value),
                propertyName: nameof(ManualRoot.Items)),
            new("wire-lookup", static owner => ((ManualRoot)owner).Lookup, childMembers,
                enumerateChildren: static value => ((Dictionary<string, ManualChild>)value).Values,
                enumerateValidationChildren: static value => BridgeValidation.EnumerateDictionary((Dictionary<string, ManualChild>)value),
                propertyName: nameof(ManualRoot.Lookup)),
            new("wire-groups", static owner => ((ManualRoot)owner).Groups,
                [new BridgeDataSubscriptionMember("$items", static owner => owner, childMembers,
                    enumerateChildren: static value => (IEnumerable)value,
                    enumerateValidationChildren: static value => BridgeValidation.EnumerateIndexed((IEnumerable)value),
                    isPathTransparent: true, propertyName: "$items")],
                enumerateChildren: static value => (IEnumerable)value,
                enumerateValidationChildren: static value => BridgeValidation.EnumerateIndexed((IEnumerable)value),
                propertyName: nameof(ManualRoot.Groups)),
        ];
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

    private static async Task<bool> PublishedAsync<TModel>(RunicViewDriver<TModel> driver, Func<RunicViewPublication, bool> predicate,
        TimeSpan? timeout = null)
        where TModel : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (driver.TakePublications().Any(predicate)) return true;
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
        internal ManualRoot Holder => field ??= new();
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
