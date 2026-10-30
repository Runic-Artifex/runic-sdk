using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Generated;

namespace Runic.Application.Testing.Tests.Conformance;

// The wizard View (specs/application/conformance/README.md, "Test Views"): pages that are presentations of one route.
// next and back are scoped to their presentation and renew the route with RunicPresentation.Renew, as a wizard's Next
// and Back do; ping is not scoped and changes nothing. The producer checks the epoch of every command that carries one,
// so the hand-written Bridge needs no mark of the scoped commands.
internal sealed class WizardViewModel : ConformanceModel
{
    private int _page = 1;

    private WizardViewModel()
    {
        NextCommand = new(_ => true, _ =>
        {
            Page++;
            RunicPresentation.Renew(this);
        });
        BackCommand = new(_ => Page > 1, _ =>
        {
            Page--;
            RunicPresentation.Renew(this);
        });
        PingCommand = new(_ => true, _ => { });
    }

    public int Page
    {
        get => _page;
        private set
        {
            _page = value;
            Raise();
            BackCommand.Raise();
        }
    }

    [RunicPresentationScoped]
    public ConformanceCommand NextCommand { get; }

    [RunicPresentationScoped]
    public ConformanceCommand BackCommand { get; }

    public ConformanceCommand PingCommand { get; }

    internal static WizardViewModel Create(JsonObject model)
    {
        var vm = new WizardViewModel();
        if (model["page"] is { } page) vm._page = page.GetValue<int>();
        return vm;
    }

    // renew and waitPresented are applied by the window (DotNetConformanceViews), outside the model's turn.
    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit) =>
        throw new NotSupportedException($"The wizard View has no mutation {mutation.ToJsonString()}.");

    internal override JsonNode? Read(string member) => member switch
    {
        "page" => Page,
        _ => throw new NotSupportedException($"The wizard View has no member {member}."),
    };
}

internal sealed class WizardBridge(IBridgeTransport transport, WizardViewModel vm, WindowContentSession content)
    : ViewModelBridge<WizardViewModel>(transport, vm, "wizard",
        [
            new("page", (writer, model) => writer.WriteNumberValue(model.Page)),
            new("canNext", ConformanceWire.Boolean<WizardViewModel>(model => model.NextCommand.CanExecute(null))),
            new("canBack", ConformanceWire.Boolean<WizardViewModel>(model => model.BackCommand.CanExecute(null))),
            new("canPing", ConformanceWire.Boolean<WizardViewModel>(model => model.PingCommand.CanExecute(null))),
        ],
        [new("Page", "page", model => model.Page, null)],
        [],
        [
            new CommandDescriptor<WizardViewModel>("Next", model => model.NextCommand),
            new CommandDescriptor<WizardViewModel>("Back", model => model.BackCommand),
            new CommandDescriptor<WizardViewModel>("Ping", model => model.PingCommand),
        ],
        "conformance-wizard", content);
