using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Navigation;
using Runic.Platform;

namespace HostedInstaller;

// The installer's wizard: Welcome, System check and Finish, one step at a time in a navigation region that only C#
// sees. The check step shows the check's Window in the page's region "check" and asks it before the wizard leaves.
public sealed class InstallerViewModel : ReactiveObject, IDisposable
{
    private readonly IRunicHostedApplications _applications;
    private readonly IRunicModelContext _context;
    private readonly InstallerOptions _options;
    private readonly NavigationRegion<InstallerStep> _steps;
    private readonly WelcomeStep _welcome = new();
    private readonly CheckStep _check = new();
    private readonly FinishStep _finish = new();
    private RunicHostedApplication? _checkApplication;
    private string _step = "welcome";
    private string _checkState = "stopped";
    private string _checkTitle = "";
    private string _checkEvent = "";
    private string _design = "system";
    // The latest choice, which the policy and the region read off the model context.
    private volatile string _chosenDesign = "system";

    public InstallerViewModel(RunicNavigator navigator, IRunicHostedApplications applications, IRunicModelContext context, InstallerOptions options)
    {
        _applications = applications;
        _context = context;
        _options = options;
        _steps = navigator.CreateRegion<InstallerStep>(this, NavigationTarget.Borrow<InstallerStep>(_welcome));
        _steps.PropertyChanged += (_, _) => Step = _steps.Current?.Name ?? "welcome";
        // Each window of the check: in a region, RunicHostedWindow.Region follows it.
        _applications.WindowOpened += OnWindowOpened;

        NextCommand = ReactiveCommand.CreateFromTask(async token => { await NextAsync(token); },
            this.WhenAnyValue(model => model.Step, step => step != "finish"));
        BackCommand = ReactiveCommand.CreateFromTask(async token => { await BackAsync(token); },
            this.WhenAnyValue(model => model.Step, step => step != "welcome"));
        RestartCheckCommand = ReactiveCommand.CreateFromTask(StartCheckAsync);
        ChooseDesignCommand = ReactiveCommand.CreateFromTask<string>(async (design, token) => { await ChooseDesignAsync(design, token); });
    }

    // The installer's design menu, which the check in its step shows too: "system", "light", "dark" or "high-contrast".
    public static readonly IReadOnlyList<string> Designs = ["system", "light", "dark", "high-contrast"];

    // The design the user chose. The installer's page applies it to itself (Frontend/src/app.ts), and the check's page
    // receives it as its appearance (AppearanceOf).
    public string Design { get => _design; private set => this.RaiseAndSetIfChanged(ref _design, value); }

    // "welcome", "check" or "finish".
    public string Step { get => _step; private set => this.RaiseAndSetIfChanged(ref _step, value); }

    // "stopped", "starting", the region's state ("waiting", "loading", "attached", "detached", "ended") or
    // "failed: <code>" when the check could not start.
    public string CheckState { get => _checkState; private set => this.RaiseAndSetIfChanged(ref _checkState, value); }

    // The title of the check's page, which the step indicator shows, such as "System check: passed".
    public string CheckTitle { get => _checkTitle; private set => this.RaiseAndSetIfChanged(ref _checkTitle, value); }

    // The latest attach or detach, with the reason of a detach, such as "detached (removed)".
    public string CheckEvent { get => _checkEvent; private set => this.RaiseAndSetIfChanged(ref _checkEvent, value); }

    // Why the page shows no check while it is detached: the fallback content says it (Frontend/index.html).
    public string CheckNote => _options.Elevated && OperatingSystem.IsMacOS()
        // An elevated check refuses regions on macOS, where Runic cannot check which process connects to it (event 2026,
        // refused). It would show only in a window of its own.
        ? "The system check runs as administrator, and on macOS such a check cannot show inside the installer's window."
        : "The system check is not shown right now. It shows again when the installer attaches it.";

    public ReactiveCommand<RxVoid, RxVoid> NextCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BackCommand { get; }

    // Starts the check again after it ended, such as after a crash or after the user closed it.
    public ReactiveCommand<RxVoid, RxVoid> RestartCheckCommand { get; }

    // Chooses a design of the menu.
    public ReactiveCommand<string, RxVoid> ChooseDesignCommand { get; }

    // A design as the check's appearance override. Each member that is set replaces the desktop's value for the check's
    // page; the rest stay the desktop's, such as its text scale and accent colour. "High contrast" is the applications' own
    // palette, the light design with stronger contrast rules, which the desktop never reports. "System" is no override
    // (null): the check shows the desktop's appearance, as DesktopColorScheme.NoPreference would.
    internal static RunicHostedAppearance? AppearanceOf(string design) => design switch
    {
        "light" => new RunicHostedAppearance { ColorScheme = DesktopColorScheme.Light },
        "dark" => new RunicHostedAppearance { ColorScheme = DesktopColorScheme.Dark },
        "high-contrast" => new RunicHostedAppearance { ColorScheme = DesktopColorScheme.Light, Contrast = DesktopContrast.More },
        _ => null,
    };

    // The check's appearance for the current design, which the policy gives its window from its open (CheckPolicy).
    internal RunicHostedAppearance? CheckAppearance => AppearanceOf(_chosenDesign);

    // The region that shows the check's Window, while one does; the probe follows it.
    internal RunicHostedRegion? CheckRegion => _check.Region;

    internal RunicHostedApplication? CheckApplication => _checkApplication;

    // Next and Back replace the step. Leaving the check step asks the check's close guard first (CheckStep): when the user
    // keeps the check running, the wizard stays.
    internal async Task<bool> NextAsync(CancellationToken cancellationToken = default) =>
        await GoAsync(_steps.Current switch { WelcomeStep => _check, CheckStep => _finish, _ => null }, cancellationToken);

    internal async Task<bool> BackAsync(CancellationToken cancellationToken = default) =>
        await GoAsync(_steps.Current switch { FinishStep => _check, CheckStep => _welcome, _ => null }, cancellationToken);

    // The installer window's close guard (InstallerWindow): the current step departs as for Back or Next.
    internal async ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken) =>
        await _steps.ConfirmDepartureAsync(NavigationDepartureKind.Retire, cancellationToken: cancellationToken) is NavigationResult<object>.Committed;

    // Switches the design. The installer's page follows from the state; the check's page follows through its region's
    // override, also while the region is detached: its next page shows it first.
    internal async Task<bool> ChooseDesignAsync(string design, CancellationToken cancellationToken = default)
    {
        if (!Designs.Contains(design)) return false;
        _chosenDesign = design;
        _context.TryPost(() => Design = design);
        // False for an ended region, which the next check's open gets the design for, and for a check older than 0.19,
        // which shows the desktop's appearance (CheckPolicy).
        return _check.Region is { } region && await region.SetAppearanceAsync(AppearanceOf(design), cancellationToken);
    }

    private async Task<bool> GoAsync(InstallerStep? target, CancellationToken cancellationToken)
    {
        if (target is null) return false;
        var result = await _steps.ReplaceAsync(NavigationTarget.Borrow(target), cancellationToken: cancellationToken);
        if (result is not NavigationResult<InstallerStep>.Committed) return false;
        if (target == _check) await StartCheckAsync(cancellationToken);
        return true;
    }

    // Starts the check unless it runs. Its Window then opens in the page's region "check" (CheckPolicy).
    private async Task StartCheckAsync(CancellationToken cancellationToken)
    {
        if (_checkApplication is { Exited.IsCompleted: false }) return;
        _context.TryPost(() => CheckState = "starting");
        try
        {
            var check = await _applications.StartAsync(Guests.Check(_options.Elevated),
                new RunicHostedApplicationOptions { HostName = "Hosted region installer", Policy = new CheckPolicy(() => CheckAppearance) }, cancellationToken);
            _checkApplication = check;
            _ = WatchAsync(check);
        }
        catch (RunicHostedApplicationException error)
        {
            // Such as guest-refused with guest-elevated for a check that runs elevated under an installer without
            // --elevated, or host-not-elevated for --elevated without administrator rights.
            _context.TryPost(() => CheckState = $"failed: {error.Code}");
        }
    }

    private void OnWindowOpened(object? sender, RunicHostedWindowEventArgs e)
    {
        // The installer hosts only the check, and shows only its main window, in a region (CheckPolicy).
        if (e.Window.Region is not { } region) return;
        _check.Region = region;
        // A design chosen between the policy's decision and this event reached no region yet. An unchanged override
        // sends nothing.
        _ = region.SetAppearanceAsync(CheckAppearance);
        // The region's events arrive in order, off the model context.
        region.StateChanged += (_, _) => _context.TryPost(() => CheckState = region.State.ToString().ToLowerInvariant());
        region.TitleChanged += (_, _) => _context.TryPost(() => CheckTitle = region.Title ?? "");
        region.Attached += (_, _) => _context.TryPost(() => CheckEvent = "attached");
        region.Detached += (_, args) => _context.TryPost(() => CheckEvent = $"detached ({args.Reason})");
        _context.TryPost(() => CheckState = region.State.ToString().ToLowerInvariant());
    }

    // The check exits when its Window closed, such as after the wizard left its step, or when it crashes. The region then
    // shows its "ended" content, with a button that starts the check again.
    private async Task WatchAsync(RunicHostedApplication check)
    {
        var exitCode = await check.Exited;
        _context.TryPost(() =>
        {
            if (_checkApplication != check) return;
            CheckState = "ended";
            CheckEvent = $"the check exited with {exitCode}";
        });
    }

    public void Dispose()
    {
        _applications.WindowOpened -= OnWindowOpened;
        NextCommand.Dispose();
        BackCommand.Dispose();
        RestartCheckCommand.Dispose();
        ChooseDesignCommand.Dispose();
    }
}

// A step of the wizard. The steps are the wizard's own (borrowed): the navigator never disposes them.
public abstract class InstallerStep(string name)
{
    public string Name => name;
}

public sealed class WelcomeStep() : InstallerStep("welcome");

public sealed class FinishStep() : InstallerStep("finish");

// The check step asks the check before the wizard leaves it: RunicHostedRegion.AsDepartureGuard sends the check a close
// request, which its close guard answers, and lets the departure go ahead once the check allowed and its Window closed.
public sealed class CheckStep() : InstallerStep("check"), INavigationDepartureGuard
{
    private volatile RunicHostedRegion? _region;

    public RunicHostedRegion? Region { get => _region; set => _region = value; }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        Region is { State: not RunicHostedRegionState.Ended } region
            ? region.AsDepartureGuard().CanDepartAsync(departure, cancellationToken)
            : ValueTask.FromResult(true);
}
