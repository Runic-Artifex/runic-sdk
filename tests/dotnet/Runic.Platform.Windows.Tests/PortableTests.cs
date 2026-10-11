using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// Portable deterministic checks of pure classification, XML and buffer helpers and of the clipboard over a fake
// native backend. Nothing here calls native code, so they run on every OS.
internal sealed class PortableTests
{
    [Test]
    public void NotificationSettingClassification()
    {
#pragma warning disable CA1416 // Portable checks of pure classification and XML helpers; nothing here calls native code.
        Check(WindowsDesktopNotifications.NotificationSettingResult(unchecked((int)0x80070490), 0) is PlatformResult<PlatformUnit>.Success);
        Check(WindowsDesktopNotifications.NotificationSettingResult(0, 0) is PlatformResult<PlatformUnit>.Success);
        foreach (var setting in new[] { 1, 2, 3, 4, 99 })
            Check(WindowsDesktopNotifications.NotificationSettingResult(0, setting) is PlatformResult<PlatformUnit>.Failed
            {
                Code: PlatformFailureCode.PermissionDenied,
                Diagnostic.Domain: "Windows.UI.Notifications.NotificationSetting",
            } failed && failed.Diagnostic!.Code == setting);
        Check(WindowsDesktopNotifications.NotificationSettingResult(unchecked((int)0x80070005), 0) is PlatformResult<PlatformUnit>.Failed
            {
                Code: PlatformFailureCode.PermissionDenied,
                Diagnostic: { Domain: "HRESULT", Code: 0x80070005L },
            });
        Check(WindowsDesktopNotifications.NotificationSettingResult(unchecked((int)0x80004005), 0) is PlatformResult<PlatformUnit>.Unavailable);
#pragma warning restore CA1416
        Console.WriteLine("PASS notification first-use eligibility, denial and backend failure classification");
    }

    [Test]
    public void ToastXml()
    {
#pragma warning disable CA1416 // Portable checks of pure classification and XML helpers; nothing here calls native code.
        var xml = System.Xml.Linq.XElement.Parse(WindowsDesktopNotifications.ToastXml(new("saved", "<Title>", "A & B")
        { Actions = [new("open", "Open <result>")], ActivationUri = new Uri("runic-test://result/1") }));
#pragma warning restore CA1416
        Check(xml.Descendants("text").First().Value == "<Title>");
        Check(xml.Descendants("action").Single().Attribute("activationType")?.Value == "protocol");
        Check(xml.Descendants("action").Single().Attribute("arguments")?.Value.Contains("runic-action=open") == true);
    }

    [Test]
    public async Task ClipboardConformanceAsync()
    {
        var owner = new Owner();
        var native = new FakeClipboard();
        var clipboard = new WindowsTextClipboard(owner, native);
        Check(await clipboard.ReadTextAsync(0) is PlatformResult<string?>.Success { Value: null });
        native.Text = "";
        Check(await clipboard.ReadTextAsync(0) is PlatformResult<string?>.Success { Value: "" });
        native.Text = "long";
        Check(await clipboard.ReadTextAsync(2) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await clipboard.WriteTextAsync("ignored", cancellation.Token); throw new InvalidOperationException("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Check(native.Writes == 0);
        using var late = new CancellationTokenSource();
        native.OnWrite = late.Cancel;
        Check(await clipboard.WriteTextAsync("committed", late.Token) is PlatformResult<PlatformUnit>.Success);
        Check(native.Text == "committed" && native.Writes == 1);
        Check(await clipboard.WriteTextAsync("invalid\0text") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData });
        Check(native.Writes == 1);
        native.Reads = 0;
        foreach (var code in new[] { PlatformFailureCode.PermissionDenied, PlatformFailureCode.ResourceBusy, PlatformFailureCode.IoError })
        {
            native.Failure = code;
            Check(await clipboard.ReadTextAsync(10) is PlatformResult<string?>.Failed failed && failed.Code == code);
        }
        native.Failure = null;
        Check(native.Reads == 2 + WindowsTextClipboard.BusyAttempts);
        native.BusyReads = 2;
        native.Text = "after contention";
        Check(await clipboard.ReadTextAsync(30) is PlatformResult<string?>.Success { Value: "after contention" } && native.BusyReads == 0);
        using (var waiting = new CancellationTokenSource())
        {
            native.BusyReads = int.MaxValue;
            native.OnBusy = waiting.Cancel;
            try { await clipboard.ReadTextAsync(30, waiting.Token); throw new InvalidOperationException("Busy retry ignored cancellation"); }
            catch (OperationCanceledException) { }
            native.BusyReads = 0; native.OnBusy = null;
        }
        owner.BeforeInvoke = () => owner.Generation = Guid.NewGuid();
        Check(await clipboard.WriteTextAsync("stale") is PlatformResult<PlatformUnit>.Unavailable);
        Check(native.Writes == 1);
        owner.BeforeInvoke = null;
        Check(await clipboard.WriteTextAsync("retry") is PlatformResult<PlatformUnit>.Success);
        owner.IsAvailable = false;
        Check(await clipboard.ReadTextAsync(10) is PlatformResult<string?>.Unavailable);
        Console.WriteLine("PASS Windows clipboard portable cancellation, bounds, outcomes, generation, retry conformance");
    }

    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Clipboard conformance assertion failed"); }
}

internal sealed class Owner : INativePickerOwner
{
    public Guid Generation { get; set; } = Guid.NewGuid();
    public bool IsAvailable { get; set; } = true;
    internal Action? BeforeInvoke;
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeInvoke?.Invoke();
        action(1);
        return ValueTask.CompletedTask;
    }
}
internal sealed class FakeClipboard : IWindowsClipboard
{
    internal string? Text;
    internal int Writes;
    internal Action? OnWrite;
    internal PlatformFailureCode? Failure;
    internal int Reads, BusyReads;
    internal Action? OnBusy;
    public PlatformResult<string?> Read(nint owner, int maximumCharacters)
    {
        Reads++;
        if (BusyReads > 0) { BusyReads--; OnBusy?.Invoke(); return new PlatformResult<string?>.Failed(PlatformFailureCode.ResourceBusy); }
        return Failure is { } failure ? new PlatformResult<string?>.Failed(failure)
            : Text?.Length > maximumCharacters ? new PlatformResult<string?>.Failed(PlatformFailureCode.TooLarge)
            : new PlatformResult<string?>.Success(Text);
    }
    public PlatformResult<PlatformUnit> Write(nint owner, string text)
    { Writes++; Text = text; OnWrite?.Invoke(); return new PlatformResult<PlatformUnit>.Success(new()); }
}
