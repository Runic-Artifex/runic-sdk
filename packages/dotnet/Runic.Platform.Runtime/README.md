# Runic.Platform.Runtime

Shared scoped lifetime, file leases, atomic writes and native provider infrastructure.

Preview API targeting .NET 10. Native providers are explicitly selected; this package has no Desktop or ASP.NET Core dependency.

## Single instance and replies to later launches

`ApplicationInstance.ClaimAsync` makes the first launch of an application the
running instance and forwards later launches to it over a Unix domain socket in a
directory only the user can use (on Linux, macOS and Windows). By default a later
launch hands over its arguments and exits, and the running instance raises
`IApplicationInstance.Launched`.

With `ApplicationInstanceOptions.ReplyToLaunches`, a later launch, such as a
command line, also waits for the running instance's reply: messages (output lines
or JSON events, as UTF-8 strings of at most 1 MiB) and an exit status. Set it in
every process of the application, so whichever process is the running instance
answers.

```csharp
var options = new ApplicationInstanceOptions { ReplyToLaunches = true };
var claim = await ApplicationInstance.ClaimAsync("org.example.Manager", ApplicationLaunch.FromProcess(args), options);

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
  messages end and `WaitForExitAsync` reports `Failed(IoError)`.
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
