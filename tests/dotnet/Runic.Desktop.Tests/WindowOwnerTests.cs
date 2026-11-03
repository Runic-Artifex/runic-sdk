using TUnit.Core;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// B6: DesktopWindowOptions.Owner and Modality.
public sealed class WindowOwnerTests
{
    [Test]
    public async Task AModalWindowNeedsAnOwner()
    {
        await using var host = await DesktopHost.StartAsync();
        var validation = host.Validate(new DesktopWindowOptions { Modality = DesktopWindowModality.Window });
        var error = Assert.Single(validation.Errors, static item => item.Code == "window-option-invalid");
        Assert.Contains("Owner", error.Message, StringComparison.Ordinal);
        Assert.Contains(host.Validate(new DesktopWindowOptions { Modality = (DesktopWindowModality)7 }).Errors,
            static item => item.Code == "window-option-invalid");
    }

    [Test]
    public async Task ABrowserIgnoresTheOwnerWithAWarning()
    {
        await using var host = await DesktopHost.StartAsync();
        var owner = DesktopWindowOwner.FromNativeHandle(42);
        var validation = host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Chrome, Owner = owner, Modality = DesktopWindowModality.Window });
        var warning = Assert.Single(validation.Warnings, static item => item.Option == "DesktopWindowOptions.Owner");
        Assert.Equal("window-option-unsupported", warning.Code);
        Assert.DoesNotContain(validation.Errors, static item => item.Option == "DesktopWindowOptions.Owner");
    }

    [Test]
    public void ACustomHostWithoutOwnershipIgnoresTheOwner()
    {
        List<DesktopDiagnostic> diagnostics = [];
        DesktopWindowOptionValidation.AddEmbeddedChecks(new DesktopWindowOptions { Owner = DesktopWindowOwner.FromNativeHandle(42) },
            new Factory(DesktopWindowCapabilities.NativeHandle), null, diagnostics);
        Assert.Single(diagnostics, static item => item.Option == "DesktopWindowOptions.Owner");
        diagnostics.Clear();
        DesktopWindowOptionValidation.AddEmbeddedChecks(new DesktopWindowOptions { Owner = DesktopWindowOwner.FromNativeHandle(42) },
            new Factory(DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Ownership), null, diagnostics);
        Assert.DoesNotContain(diagnostics, static item => item.Option == "DesktopWindowOptions.Owner");
    }

    [Test]
    public void ANativeOwnerNeedsAHandle()
    {
        Assert.Throws<ArgumentException>(() => DesktopWindowOwner.FromNativeHandle(0));
        Assert.Throws<ArgumentNullException>(() => DesktopWindowOwner.FromWindow(null!));
        Assert.Null(DesktopWindowOwner.FromNativeHandle(7).Window);
    }

    // The owner takes input again only when the last window modal to it closed.
    [Test]
    public void AnOwnerIsBlockedUntilItsLastModalWindowCloses()
    {
        const nint owner = 0x5150;
        Assert.True(ModalOwners.Acquire(owner));
        Assert.False(ModalOwners.Acquire(owner));
        Assert.Equal(2, ModalOwners.Count(owner));
        Assert.False(ModalOwners.Release(owner));
        Assert.True(ModalOwners.Release(owner));
        Assert.False(ModalOwners.Release(owner));
        Assert.Equal(0, ModalOwners.Count(owner));
    }

    [Test]
    public async Task AnOwnedWindowReceivesItsOwnerAndFollowsIt()
    {
        var factory = new OwningFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        await using var ownerSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("owner") });
        await using var ownedSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("owned") });
        var owner = await ownerSurface.OpenWindowAsync();
        var owned = await ownedSurface.OpenWindowAsync(new DesktopWindowOptions
        {
            Owner = DesktopWindowOwner.FromWindow(owner),
            Modality = DesktopWindowModality.Window,
        });
        var ownedHost = factory.Hosts[1];
        Assert.Equal(owner.NativeHandle, ownedHost.Options!.OwnerHandle);
        Assert.Equal(DesktopWindowModality.Window, ownedHost.Options.Modality);
        Assert.Equal(0, factory.Hosts[0].Options!.OwnerHandle);
        Assert.Same(owner, owned.Owner!.Window);
        Assert.Equal(DesktopWindowModality.Window, owned.Modality);
        Assert.Same(owned, Assert.Single(owner.OwnedWindows));

        // Hiding the owner hides the owned window first; showing it shows it again after.
        await owner.SetVisibleAsync(false);
        await owner.SetVisibleAsync(true);
        Assert.Equal([false, true], ownedHost.Visibility);

        // Closing the owner closes the owned window without asking.
        await owner.CloseAsync();
        await owned.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(owned.IsOpen);
        Assert.Empty(owner.OwnedWindows);
    }

    [Test]
    public async Task AnOwnerThatIsNotOpenIsIgnored()
    {
        var factory = new OwningFactory();
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<DesktopDiagnostic>();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
            DiagnosticSink = diagnostics.Enqueue,
        });
        await using var ownerSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("owner") });
        await using var ownedSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("owned") });
        var owner = await ownerSurface.OpenWindowAsync();
        await owner.CloseAsync();
        var owned = await ownedSurface.OpenWindowAsync(new DesktopWindowOptions
        {
            Owner = DesktopWindowOwner.FromWindow(owner),
            Modality = DesktopWindowModality.Window,
        });
        Assert.True(owned.IsOpen);
        Assert.Null(owned.Owner);
        Assert.Equal(0, factory.Hosts[1].Options!.OwnerHandle);
        Assert.Equal(DesktopWindowModality.None, factory.Hosts[1].Options!.Modality);
        Assert.Contains(diagnostics, static item => item.Code == "window-owner-unavailable");
    }

    // Review 4: an owner's confirmed close asks the windows it owns first, innermost first; a refusal keeps the owner, and a
    // modal window that agrees closes before the owner is asked. A quit asks an owned window once, through its owner.
    [Test]
    public async Task AnOwnersCloseAsksItsWindowsFirst()
    {
        var factory = new OwningFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var asked = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var dialogAllows = false;
        DesktopWindow? dialog = null;
        var dialogOpenWhenOwnerAsked = true;
        DesktopWindow? owner = null;
        async Task<DesktopWindow> OpenAsync(string name, DesktopWindow? ownedBy, Func<bool> allow)
        {
            var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(name) });
            return await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Owner = ownedBy is null ? null : DesktopWindowOwner.FromWindow(ownedBy),
                Modality = ownedBy is null || name == "palette" ? DesktopWindowModality.None : DesktopWindowModality.Window,
                ConfirmCloseAsync = _ =>
                {
                    asked.Enqueue(name);
                    if (name == "owner") dialogOpenWhenOwnerAsked = dialog!.IsOpen;
                    return ValueTask.FromResult(allow());
                },
            });
        }
        owner = await OpenAsync("owner", null, static () => true);
        var palette = await OpenAsync("palette", owner, static () => true);
        dialog = await OpenAsync("dialog", owner, () => Volatile.Read(ref dialogAllows));
        var nested = await OpenAsync("nested", dialog, static () => true);

        // The nested dialog first, then the dialog, which refuses: the owner stays open and is not asked.
        Assert.False(await owner.RequestCloseAsync());
        Assert.Equal(["nested", "dialog"], asked.ToArray());
        Assert.True(owner.IsOpen && dialog.IsOpen && palette.IsOpen);
        // The nested dialog agreed, and a modal window that agrees closes at once.
        await nested.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // The user's close asks the same way; now the dialog agrees and closes before the owner is asked.
        Volatile.Write(ref dialogAllows, true);
        asked.Clear();
        var hostOfOwner = factory.Hosts[0];
        hostOfOwner.CloseByUser();
        await owner.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["dialog", "palette", "owner"], asked.ToArray());
        Assert.False(dialogOpenWhenOwnerAsked);
        await palette.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task AQuitAsksAnOwnedWindowThroughItsOwner()
    {
        var factory = new OwningFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
            HandleShutdownSignals = false,
        });
        var asked = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var ownerSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("owner") });
        var owner = await ownerSurface.OpenWindowAsync();
        var dialogSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("dialog") });
        var dialog = await dialogSurface.OpenWindowAsync(new DesktopWindowOptions
        {
            Owner = DesktopWindowOwner.FromWindow(owner),
            ConfirmCloseAsync = _ => { asked.Enqueue("dialog"); return ValueTask.FromResult(false); },
        });
        Assert.Equal(Internal.DesktopQuitOutcome.Refused, await Internal.DesktopQuitCoordinator.QuitAsync(DesktopQuitSource.Menu, [host]));
        Assert.Equal(["dialog"], asked.ToArray());
        Assert.True(owner.IsOpen && dialog.IsOpen);
        await owner.CloseAsync();
    }

    private sealed class OwningFactory : IDesktopWindowHostFactory
    {
        internal List<OwningHost> Hosts { get; } = [];
        public bool IsSupported => true;
        public DesktopWindowCapabilities Capabilities => OwningHost.Supported;
        public IDesktopWindowHost Create()
        {
            var created = new OwningHost(Hosts.Count + 100);
            Hosts.Add(created);
            return created;
        }
    }

    private sealed class OwningHost(nint handle) : IDesktopWindowHost
    {
        internal const DesktopWindowCapabilities Supported = DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Focus |
            DesktopWindowCapabilities.Ownership | DesktopWindowCapabilities.CloseConfirmation;
        public DesktopWindowCapabilities Capabilities => Supported;
        public bool SupportsCloseConfirmation => true;
        // The user closes the window: its close confirmation decides.
        internal void CloseByUser() => Options?.CloseRequested?.Invoke();
        public event EventHandler? Closed;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? handle : 0;
        internal DesktopWindowHostOptions? Options { get; private set; }
        internal List<bool> Visibility { get; } = [];

        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        {
            Options = options;
            IsOpen = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            if (!IsOpen) return ValueTask.CompletedTask;
            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
            return ValueTask.CompletedTask;
        }

        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        {
            lock (Visibility) Visibility.Add(visible);
            return ValueTask.CompletedTask;
        }

        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => CloseAsync();
    }

    private sealed class Factory(DesktopWindowCapabilities capabilities) : IDesktopWindowHostFactory
    {
        public bool IsSupported => true;
        public DesktopWindowCapabilities Capabilities => capabilities;
        public IDesktopWindowHost Create() => throw new NotSupportedException();
    }
}
