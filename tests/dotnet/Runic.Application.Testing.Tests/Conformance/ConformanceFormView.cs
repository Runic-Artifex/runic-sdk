using System.Text.Json.Nodes;
using ReactiveUI;
using ReactiveUI.Validation.Extensions;
using ReactiveUI.Validation.Helpers;
using Runic.Application.Views;
using Runic.Application.Views.Generated;

namespace Runic.Application.Testing.Tests.Conformance;

// The form View (specs/application/conformance/README.md, "Test Views"): a ReactiveValidationObject with a rule of
// name and the approval rule, which names no member. The ValidationContext is read and observed as the generated
// Bridge of such a ViewModel does.
internal sealed class FormViewModel : ReactiveValidationObject, IConformanceModel
{
    internal const string NameTooLong = "Name must be at most 8 characters.";
    internal const string ApprovalRequired = "Approval is required.";

    private readonly ValidationSignal _shortName = new(true);
    private readonly ValidationSignal _approval = new(true);

    internal FormViewModel()
    {
        this.ValidationRule(vm => vm.Name, _shortName, NameTooLong);
        this.ValidationRule(_approval, ApprovalRequired);
    }

    public string Name
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            _shortName.Value = value.Length <= 8;
        }
    } = "";

    internal static readonly BridgeDataSubscriptionMember[] Metadata =
        [new("name", static owner => ((FormViewModel)owner).Name, propertyName: nameof(Name))];

    internal static readonly BridgeValidationReader Reader = new(ReadErrors, Observe);

    internal static FormViewModel Create(JsonObject model)
    {
        var vm = new FormViewModel();
        if (model["name"] is { } name) vm.Name = name.GetValue<string>();
        return vm;
    }

    public void Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        switch (mutation["op"]!.GetValue<string>())
        {
            case "assign" when mutation["member"]!.GetValue<string>() == "name":
                Name = mutation["value"]!.GetValue<string>();
                break;
            case "rule" when mutation["rule"]!.GetValue<string>() == "approval":
                _approval.Value = mutation["holds"]!.GetValue<bool>();
                break;
            default:
                throw new NotSupportedException($"The form View has no mutation {mutation.ToJsonString()}.");
        }
    }

    public JsonNode? Read(string member) => member switch
    {
        "name" => Name,
        _ => throw new NotSupportedException($"The form View has no member {member}."),
    };

    public JsonNode? Provide(string id) => null;

    // As the generated reader: a validation with properties names them; every other one reports at the object.
    private static List<System.ComponentModel.DataAnnotations.ValidationResult>? ReadErrors(object owner)
    {
        if (owner is not ReactiveUI.Validation.Abstractions.IValidatableViewModel { ValidationContext: { } context }) return null;
        var errors = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        foreach (var validation in context.Validations.Items)
        {
            if (validation.IsValid) continue;
            var properties = validation is ReactiveUI.Validation.Components.Abstractions.IValidatesProperties { PropertyCount: > 0 } named
                ? named.Properties.ToArray() : null;
            errors.Add(new(validation.Text?.ToSingleLine(" ") ?? string.Empty, properties));
        }
        return errors;
    }

    private static IDisposable? Observe(object owner, Action changed) =>
        owner is ReactiveUI.Validation.Abstractions.IValidatableViewModel { ValidationContext: { } context }
            ? BridgeValidation.Observe(context.ValidationStatusChange, changed)
            : null;
}

internal sealed class FormBridge(IBridgeTransport transport, FormViewModel vm, WindowContentSession content)
    : ViewModelBridge<FormViewModel>(transport, vm, "form",
        [
            new("name", ConformanceWire.Value<FormViewModel>(model => model.Name)),
            new("nameErrors", (writer, model) => BridgeValidation.WriteMessages(writer, model.GetErrors(nameof(FormViewModel.Name)))),
            new("validation", (writer, model) => BridgeValidation.Write(writer, model, FormViewModel.Metadata, FormViewModel.Reader.ReadErrors)),
        ],
        [
            new("Name", "name", model => model.Name, (model, value) => model.Name = ConformanceWire.String(value)),
        ],
        [], [], "conformance-form", content,
        dataSubscriptions: FormViewModel.Metadata,
        validationReader: FormViewModel.Reader);
