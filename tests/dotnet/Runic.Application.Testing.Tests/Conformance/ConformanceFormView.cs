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
internal sealed class FormViewModel : ReactiveValidationObject, IConformanceModel, IRunicPendingValidation
{
    internal const string NameTooLong = "Name must be at most 8 characters.";
    internal const string ApprovalRequired = "Approval is required.";
    // translation-text/1: the text reference of the name rule's message.
    internal const string NameTooLongText = """{"version":2,"catalog":"conformance","contractFingerprint":"sha256:0000000000000000000000000000000000000000000000000000000000000000","key":"name_too_long","arguments":{"max":{"type":"int64","value":"8"}},"fallbackText":"Name must be at most 8 characters."}""";

    private readonly NameRule _shortName = new();
    private readonly ValidationSignal _approval = new(true);
    // validation-pending/1: the asynchronous check of name, which check mutations start and end.
    private readonly RunicPendingValidation _pending = new();
    private IDisposable? _nameCheck;

    IReadOnlyCollection<string> IRunicPendingValidation.PendingMemberPaths => _pending.PendingMemberPaths;

    event EventHandler? IRunicPendingValidation.PendingChanged
    {
        add => _pending.PendingChanged += value;
        remove => _pending.PendingChanged -= value;
    }

    internal FormViewModel()
    {
        this.ValidationRule(vm => vm.Name, _shortName);
        this.ValidationRule(_approval, ApprovalRequired);
    }

    public string Name
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            _shortName.Holds = value.Length <= 8;
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
            case "check" when mutation["member"]!.GetValue<string>() == "name":
                if (mutation["running"]!.GetValue<bool>()) _nameCheck ??= _pending.Begin(nameof(Name));
                else
                {
                    _nameCheck?.Dispose();
                    _nameCheck = null;
                }
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
            var message = validation.Text?.ToSingleLine(" ") ?? string.Empty;
            var text = validation.Text is IRunicTextReferenceSource { Text: { } carried } ? carried : null;
            errors.Add(text is null ? new System.ComponentModel.DataAnnotations.ValidationResult(message, properties) : new RunicValidationResult(message, text, properties));
        }
        return errors;
    }

    // The name rule: while it does not hold, its text is the message with its text reference.
    private sealed class NameRule : IObservable<ReactiveUI.Validation.States.IValidationState>
    {
        private static readonly ReactiveUI.Validation.States.IValidationState Invalid =
            new ReactiveUI.Validation.States.ValidationState(false, new ReferenceText());
        private readonly List<IObserver<ReactiveUI.Validation.States.IValidationState>> _observers = [];
        private bool _holds = true;

        internal bool Holds
        {
            set
            {
                if (_holds == value) return;
                _holds = value;
                foreach (var observer in _observers.ToArray()) observer.OnNext(State);
            }
        }

        private ReactiveUI.Validation.States.IValidationState State =>
            _holds ? ReactiveUI.Validation.States.ValidationState.Valid : Invalid;

        public IDisposable Subscribe(IObserver<ReactiveUI.Validation.States.IValidationState> observer)
        {
            _observers.Add(observer);
            observer.OnNext(State);
            return new Subscription(() => _observers.Remove(observer));
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class ReferenceText : ReactiveUI.Validation.Collections.IValidationText, IRunicTextReferenceSource
    {
        public RunicTextReference? Text { get; } = RunicTextReference.Parse(NameTooLongText);
        public int Count => 1;
        public string this[int index] => index == 0 ? NameTooLong : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<string> GetEnumerator() { yield return NameTooLong; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public string ToSingleLine(string? separator) => NameTooLong;
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
            new("Name", "name", model => model.Name, (model, value) => model.Name = ConformanceWire.String(value))
                { TextMember = true, TextSetter = static (model, value) => model.Name = value },
        ],
        [], [], "conformance-form", content,
        dataSubscriptions: FormViewModel.Metadata,
        validationReader: FormViewModel.Reader);
