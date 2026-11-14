using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Navigation;
using Runic.Platform;

namespace CheckTool;

// The check's root ViewModel: three checks of the computer, which run when the Window opens and again on request.
// Nothing in it knows whether an installer shows it: in a region its message box is the installer's, owned by the
// installer's window (dialogs/1).
public sealed class CheckViewModel : ReactiveObject, IDisposable
{
    // The least free space the installation needs.
    public const long RequiredBytes = 1L << 30;

    private readonly IRunicModelContext _context;
    private readonly IDesktopMessageBoxes _messageBoxes;
    private CancellationTokenSource? _run;
    private string _space = "pending";
    private string _spaceDetail = "";
    private string _folder = "pending";
    private string _folderDetail = "";
    private string _account = "pending";
    private string _accountDetail = "";
    private string _outcome = "pending";

    public CheckViewModel(IRunicModelContext context, IDesktopMessageBoxes messageBoxes)
    {
        _context = context;
        _messageBoxes = messageBoxes;
        RunCommand = ReactiveCommand.Create(Run, this.WhenAnyValue(model => model.Outcome, outcome => outcome != "running"));
        ReportAppearanceCommand = ReactiveCommand.Create<string>(ReportAppearance);
    }

    // Each check: "pending", "running", "passed", "failed" or "info", with a detail for the page.
    public string Space { get => _space; private set => this.RaiseAndSetIfChanged(ref _space, value); }

    public string SpaceDetail { get => _spaceDetail; private set => this.RaiseAndSetIfChanged(ref _spaceDetail, value); }

    public string Folder { get => _folder; private set => this.RaiseAndSetIfChanged(ref _folder, value); }

    public string FolderDetail { get => _folderDetail; private set => this.RaiseAndSetIfChanged(ref _folderDetail, value); }

    public string Account { get => _account; private set => this.RaiseAndSetIfChanged(ref _account, value); }

    public string AccountDetail { get => _accountDetail; private set => this.RaiseAndSetIfChanged(ref _accountDetail, value); }

    // "pending", "running", "passed" or "failed". The page names its title after it, which an installer shows.
    public string Outcome { get => _outcome; private set => this.RaiseAndSetIfChanged(ref _outcome, value); }

    public ReactiveCommand<RxVoid, RxVoid> RunCommand { get; }

    // The page reports the appearance it applied, such as "light more" (Frontend/src/app.ts).
    public ReactiveCommand<string, RxVoid> ReportAppearanceCommand { get; }

    // Starts the checks from outside the Window's model context, as Program.cs does when the Window opened.
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => _context.InvokeAsync(Run, cancellationToken);

    public void Run()
    {
        if (Outcome == "running") return;
        var run = new CancellationTokenSource();
        _run = run;
        Outcome = "running";
        Space = Folder = Account = "pending";
        SpaceDetail = FolderDetail = AccountDetail = "";
        _ = RunAsync(run.Token);
    }

    // Writes the page's appearance to standard error, where a Runic host logs a guest's lines (event 2022). The installer's
    // probe reads it to check that the page shows the installer's design.
    private static void ReportAppearance(string appearance)
    {
        if (appearance.Length <= 40) Console.Error.WriteLine($"check page appearance: {appearance}");
    }

    // The Window's close guard (CheckWindow): a check that runs is not given up without asking. In a region, the installer
    // asks it before Back, Next or its own close; the message box shows as the installer's, over its window.
    public async ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken)
    {
        if (Outcome != "running") return true;
        var stop = new DesktopMessageBoxButton("stop", "Stop the check") { Role = DesktopMessageBoxButtonRole.Destructive };
        var answer = await _messageBoxes.ShowAsync(new DesktopMessageBox("The system check is still running. Stop it?")
        {
            Kind = DesktopMessageBoxKind.Warning,
            Buttons = [stop, new DesktopMessageBoxButton("continue", "Keep checking") { Role = DesktopMessageBoxButtonRole.Cancel }],
        }, cancellationToken);
        if (answer is not PlatformResult<DesktopMessageBoxButton?>.Success { Value.Id: "stop" }) return false;
        _run?.Cancel();
        return true;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var failed = false;
            foreach (var (check, measure) in new (string, Func<(string Status, string Detail)>)[]
            {
                ("space", MeasureSpace),
                ("folder", MeasureFolder),
                ("account", MeasureAccount),
            })
            {
                await _context.InvokeAsync(() => Set(check, "running", ""), cancellationToken);
                // A pause, so that the page shows each check while it runs.
                await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
                var (status, detail) = measure();
                failed |= status == "failed";
                await _context.InvokeAsync(() => Set(check, status, detail), cancellationToken);
            }
            _context.TryPost(() => Outcome = failed ? "failed" : "passed");
        }
        catch (OperationCanceledException)
        {
            _context.TryPost(() => Outcome = "pending");
        }
    }

    private void Set(string check, string status, string detail)
    {
        switch (check)
        {
            case "space": (Space, SpaceDetail) = (status, detail); break;
            case "folder": (Folder, FolderDetail) = (status, detail); break;
            default: (Account, AccountDetail) = (status, detail); break;
        }
    }

    // Free space on the drive of the temporary folder, where a real installer would unpack.
    private static (string, string) MeasureSpace()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!);
            var free = $"{drive.AvailableFreeSpace / (double)(1L << 30):0.0} GB free on {drive.Name}";
            return (drive.AvailableFreeSpace >= RequiredBytes ? "passed" : "failed", free);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ("failed", $"The free space could not be read ({error.GetType().Name}).");
        }
    }

    private static (string, string) MeasureFolder()
    {
        try
        {
            var folder = Directory.CreateTempSubdirectory("runic-check-");
            File.WriteAllText(Path.Combine(folder.FullName, "probe.txt"), "check");
            folder.Delete(recursive: true);
            return ("passed", "The temporary folder can be written.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ("failed", $"The temporary folder cannot be written ({error.GetType().Name}).");
        }
    }

    // Informational: an installer that needs administrator rights starts the check elevated too (README, "Elevated").
    private static (string, string) MeasureAccount() =>
        ("info", Environment.IsPrivilegedProcess ? "Runs as administrator." : "Runs as a standard user.");

    public void Dispose()
    {
        _run?.Cancel();
        RunCommand.Dispose();
        ReportAppearanceCommand.Dispose();
    }
}
