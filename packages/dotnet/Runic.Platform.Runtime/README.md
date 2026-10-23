# Runic.Platform.Runtime

Single instance, requests to open files and URLs, and file transfers for Runic applications, and the contract for native platform providers.

Preview API targeting .NET 10. Native providers are explicitly selected; this package has no Desktop or ASP.NET Core dependency.

The package has two audiences:

- **Applications** use the [single instance](#single-instance-and-replies-to-later-launches)
  (`ApplicationInstance.ClaimAsync`, `ApplicationInstanceOptions`), the
  [requests to open files and URLs](#opening-files-and-urls)
  (`ApplicationInstance.CreateOpenRequests`) and the managed
  [file transfers](#file-transfers) (`LocalFileTransfers.Create()`). They need no
  window and no Desktop host.
- **Hosts and native providers** implement the [provider contract](#provider-contract).

The presentation lifetime, the argument checks the providers share and the
settings base class are internal to Runic's own providers and hosts.

## Provider contract

A host gives each window an `INativeWindowOwner`: the window's generation, whether it
is still available, and `InvokeAsync(Action<nint>)`, which runs a callback on the
window's native thread with its handle (an `HWND`, an `NSWindow*` or a `GtkWindow*`).
The handle must not escape the callback. Once the window has closed, `InvokeAsync`
throws `OwnerClosedException`.

A file-dialog provider is an `IPickerBackend`. `NativePickerBackend` turns an
`INativeFilePicker` into one: it passes each dialog's filters, initial folder and
multiplicity as a `NativeFileDialogRequest` to
`INativeFilePicker.SelectAsync(request, cancellationToken)`, and the initial folder
of a directory dialog to
`INativeDirectoryPicker.SelectDirectoryAsync(initialDirectory, cancellationToken)`.
A picker that cannot show a dialog throws `NativeBackendUnavailableException`, which
the backend reports as `Unavailable(BackendUnavailable)`. A picker that returns more
than one selection also returns `true` from `SupportsMultipleSelection`; otherwise
`OpenFilesAsync` reports `Unavailable(BackendUnavailable)` without showing a dialog.
A directory picker that implements
`INativeDirectoryPicker.SelectDirectoriesAsync(initialDirectory, cancellationToken)`
returns `true` from `SupportsMultipleDirectorySelection`, which `OpenDirectoriesAsync`
and the `platform.directories.openMultiple` capability follow the same way. The
request's `ShowsFilterChoice` asks for a filter choice where the dialog has none of
its own; a picker reports the filter the user left selected as the first
selection's `NativeFileSelection.SelectedFilter`, and the backend passes it on as
`PickerResult<T>.Selected.SelectedFilter` only when it is one of the request's
filters.
The backend releases the access of every selection it does not hand to a lease,
including after a failure or cancellation, and reports the native error of a failed
selection or commit in the `Diagnostic` of the result.

## Single instance and replies to later launches

`ApplicationInstance.ClaimAsync` makes the first launch of an application the
running instance and forwards later launches to it over a Unix domain socket in a
directory only the user can use (on Linux, macOS and Windows). By default a later
launch hands over its arguments and exits, and the running instance raises
`IApplicationInstance.Launched`.

```csharp
// In Main(string[] args): the same as ClaimAsync(id, ApplicationLaunch.FromProcess(args)).
var claim = await ApplicationInstance.ClaimAsync("org.example.Manager", args);
```

An MSIX-packaged application receives a protocol URI or the files of a file-type
activation through its activation, not `args`, so `ClaimAsync(id, args)` would
forward none of them. Pass the launch that
`DesktopPlatformServices.GetLaunch(args)` (Runic.Application.Views.Desktop) or
`WindowsPlatformProvider.GetLaunch(args)` (Runic.Platform.Windows) returns; both
are `ApplicationLaunch.FromProcess(args)` outside a package:

```csharp
var claim = await ApplicationInstance.ClaimAsync("org.example.Manager", DesktopPlatformServices.GetLaunch(args));
```

With `ApplicationInstanceOptions.ReplyToLaunches`, a later launch, such as a
command line, also waits for the running instance's reply: messages (output lines
or JSON events, as UTF-8 strings of at most 1 MiB) and an exit status. Set it in
every process of the application, so whichever process is the running instance
answers.

```csharp
var options = new ApplicationInstanceOptions { ReplyToLaunches = true };
var claim = await ApplicationInstance.ClaimAsync("org.example.Manager", args, options);

// A later launch: print the running instance's messages and exit with its status.
if (claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded { Reply: { } reply } })
{
    await using (reply)
    {
        // Ctrl+C asks the running instance to cancel; its last messages and exit status still arrive.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = reply.CancelAsync(); };
        await foreach (var line in reply.ReadMessagesAsync()) Console.WriteLine(line);
        return await reply.WaitForExitAsync() is PlatformResult<int>.Success(var code) ? code : 1;
    }
}

// The running instance: launches that wait carry a reply; complete each one.
instance.Launched += (_, launch) => _ = Task.Run(async () =>
{
    if (launch.Reply is not { } reply) { /* Only handed over: raise the window. */ return; }
    await reply.WriteAsync("working");
    await reply.CompleteAsync(reply.Cancelled.IsCancellationRequested ? 130 : 0);
});
```

- `IApplicationLaunchReply.Cancelled` is cancelled when the launching process
  asks to cancel, disconnects (disposing its reply, or cancelling a read), or when
  the running instance ends. `CancelRequested` is cancelled only for the request
  (`IForwardedLaunchReply.CancelAsync`, such as on Ctrl+C), and `Disconnected`
  only when the reply ends without an exit status; each is cancelled before
  `Cancelled`. Pass `CancelRequested` to work that should outlive the launching
  process, such as an operation started over an SSH session that drops. Writes
  after the launching process went away return false.
- A write waits while the launching process does not read, so large output is
  not buffered in memory. Pass a token to `WriteAsync` to bound the wait; a write
  cancelled that way ends the reply.
- When the running instance ends or crashes before the exit status, the
  messages end and `WaitForExitAsync` reports `Failed(IOError)`.
- With `ApplicationInstanceOptions.ForwardOnly`, the process never becomes the
  running instance: a command line that talks to a running daemon gets
  `ApplicationInstanceClaim.Forwarded`, or `ApplicationInstanceClaim.NotRunning`
  when no instance runs, and then does the work itself or reports it. It does not
  listen meanwhile, so a daemon that starts at the same moment does not wait for
  it. An instance that is starting is waited for, as
  with a claim.

  ```csharp
  var options = new ApplicationInstanceOptions { ReplyToLaunches = true, ForwardOnly = true };
  switch (await ApplicationInstance.ClaimAsync("org.example.Manager", ApplicationLaunch.FromProcess(args), options))
  {
      case PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded { Reply: { } reply } }:
          /* Read the reply as above. */ break;
      case PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.NotRunning }:
          /* No daemon: run here. */ break;
  }
  ```
- An instance claimed without `ReplyToLaunches`, or by an SDK before
  0.8.0-preview.3, refuses launches that wait: the claim fails with
  `ResourceBusy` and an `ApplicationInstance` diagnostic, and the launch is not
  handled. Launches without `ReplyToLaunches` keep the one-way protocol and reach
  any instance.

## Opening files and URLs

`ApplicationInstance.CreateOpenRequests(options, instance, launch)` turns this
launch (by default none) and each later launch of `instance` into
`IDesktopOpenRequests.Opened` requests: the arguments that are URLs with a
registered scheme and, with `IncludeFiles`, existing files. Linux desktop entries (`Exec=app %U`) and Windows registrations
(`"app.exe" "%1"`) pass them that way. The instance's own `Launched` handlers
still receive every launch. See the
[opening files and URLs design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/open-activation.md).

## File transfers

`LocalFileTransfers.Create()` is the `IDesktopFileTransfers` engine every
platform uses, and what `AddRunicPlatformServices()` registers on Runic
Desktop. It copies in 1 MiB blocks under hidden temporary names, renames into
place with an exclusive rename, and moves by renaming within a file system. A
CS-WebUI application can register it itself. See
[Copy and move](../Runic.Platform/README.md#copy-and-move) and the
[file transfer design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/file-transfers.md).
