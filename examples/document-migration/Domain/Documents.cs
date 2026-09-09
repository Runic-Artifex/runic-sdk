using System.Text;
using Runic.Platform;
namespace DocumentMigration.Domain;

public sealed record DocumentResult(string Status, string? Text = null, string? Name = null, bool CleanupFailed = false);

// Portable business policy shared by the MAUI-derived view model and Runic commands.
public sealed class DocumentService(IFileDialogs files, IDesktopFileLauncher? launcher = null, IDesktopNotifications? notifications = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _resultGate = new(1, 1);
    private ISaveFileLease? _saved;
    public bool HasResult => Volatile.Read(ref _saved) is not null;
    private bool _listening;
    private bool _disposed;
    private readonly string _notificationId = "document-" + Guid.NewGuid().ToString("N");
    public async ValueTask<DocumentResult> LaunchResultAsync(DesktopFileOperation operation, CancellationToken token)
    {
        await _resultGate.WaitAsync(token);
        try
        {
            if (_disposed || _saved is not ILaunchableFileLease file || launcher is null) return new("result-unavailable");
            var outcome = await file.LaunchAsync(launcher, operation, token);
            return new(outcome switch
            {
                PlatformResult<Unit>.Success => "handoff-requested",
                PlatformResult<Unit>.Failed failure => $"failed:{failure.Code}",
                PlatformResult<Unit>.Unavailable unavailable => $"unavailable:{unavailable.Reason}",
                _ => "result-unavailable"
            }, Name: _saved.DisplayName);
        }
        finally { _resultGate.Release(); }
    }
    private async ValueTask RetainResultAsync(ISaveFileLease lease)
    {
        await _resultGate.WaitAsync();
        try
        {
            if (_disposed) { await lease.DisposeAsync(); throw new ObjectDisposedException(nameof(DocumentService)); }
            var old = _saved;
            _saved = lease;
            if (old is not null) await old.DisposeAsync();
        }
        finally { _resultGate.Release(); }
    }
    private async ValueTask NotifyAsync()
    {
        if (notifications is null) return;
        if (!_listening) { notifications.Activated += OnActivated; _listening = true; }
        if (await notifications.RequestPermissionAsync() is not PlatformResult<Unit>.Success) return;
        await notifications.ShowAsync(new(_notificationId, "Document saved", "Your document is ready to open.")
        { Actions = [new("open", "Open result"), new("reveal", "Show in folder")] });
    }
    private void OnActivated(object? sender, DesktopNotificationActivation activation)
    {
        if (activation.NotificationId != _notificationId || activation.ActionId is not ("default" or "open" or "reveal")) return;
        _ = LaunchActivatedAsync(activation.ActionId == "reveal" ? DesktopFileOperation.Reveal : DesktopFileOperation.Open);
    }
    private async Task LaunchActivatedAsync(DesktopFileOperation operation)
    {
        try { await LaunchResultAsync(operation, CancellationToken.None); }
        catch (Exception error) { Console.Error.WriteLine($"Could not open notification result: {error.Message}"); }
    }
    public async ValueTask DisposeAsync()
    {
        await _resultGate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (notifications is not null && _listening) notifications.Activated -= OnActivated;
            if (_saved is not null) await _saved.DisposeAsync();
            _saved = null;
        }
        finally { _resultGate.Release(); }
        if (notifications is not null && _listening)
            try { await notifications.RemoveAsync(_notificationId); } catch (ObjectDisposedException) { }
    }
    public const int MaximumBytes = 32 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] Encode(string text)
    {
        if (text.Contains('\0')) throw new InvalidDataException("Text documents cannot contain NUL.");
        var bytes = Utf8.GetBytes(text);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Document exceeds 32 KiB.");
        return bytes;
    }
    public async ValueTask<DocumentResult> OpenAsync(CancellationToken token)
    {
        var selection = await files.OpenFileAsync(new(), token);
        if (selection is not PickerResult<IReadFileLease>.Selected picked) return new(PickerStatus(selection));
        DocumentResult result;
        await using (var lease = picked.Value)
        {
            var stream = await lease.OpenReadAsync(token);
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (content.Length + read > MaximumBytes) return new("too-large");
                content.Write(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
            var text = Utf8.GetString(content.ToArray());
            if (text.StartsWith('\uFEFF')) text = text[1..];
            Encode(text);
            result = new("opened", text, lease.DisplayName);
        }
        // Disposal can await access-grant cleanup. Cancellation during it must not apply a late open.
        token.ThrowIfCancellationRequested();
        return result;
    }
    public async ValueTask<DocumentResult> SaveAsync(string text, CancellationToken token)
    {
        var bytes = Encode(text); // Validate before showing a picker or modifying a destination.
        var selection = await files.SaveFileAsync(new("document.txt"), token);
        if (selection is not PickerResult<ISaveFileLease>.Selected picked) return new(PickerStatus(selection));
        DocumentResult? knownOutcome = null;
        var lease = picked.Value;
        bool retained = false;
        try
        {
            try
            {
                var displayName = lease.DisplayName;
                var begun = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace, token);
                if (begun is PlatformResult<IFileWriteTransaction>.Unavailable unavailable) return knownOutcome = new($"unavailable:{unavailable.Reason}");
                if (begun is PlatformResult<IFileWriteTransaction>.Failed failed) return knownOutcome = new($"failed:{failed.Code}");
                await using var transaction = ((PlatformResult<IFileWriteTransaction>.Success)begun).Value;
                await transaction.Content.WriteAsync(bytes, token);
                var commit = await transaction.CommitAsync(token);
                knownOutcome = commit switch
                {
                    FileCommitResult.Committed => new("saved", text, displayName),
                    FileCommitResult.CommitUnknown unknown => new($"commit-unknown:{unknown.Code}"),
                    FileCommitResult.NotCommitted failedCommit => new($"not-committed:{failedCommit.Code}"),
                    _ => throw new InvalidOperationException()
                };
                if (commit is FileCommitResult.Committed && launcher is not null)
                {
                    // The platform presentation also owns this lease and drains it on window closure.
                    retained = true;
                    await RetainResultAsync(lease);
                    try { await NotifyAsync(); }
                    catch (Exception error) { Console.Error.WriteLine($"Document saved; notification unavailable: {error.Message}"); }
                }
                return knownOutcome;
            }
            finally { if (!retained) await lease.DisposeAsync(); }
        }
        catch (Exception) when (knownOutcome is not null)
        {
            // Cleanup cannot turn an acknowledged or uncertain native write into a retryable failure.
            return knownOutcome with { CleanupFailed = true };
        }
    }
    private static string PickerStatus<T>(PickerResult<T> result) where T : IAsyncDisposable => result switch
    {
        PickerResult<T>.Dismissed => "dismissed",
        PickerResult<T>.Unavailable value => $"unavailable:{value.Reason}",
        PickerResult<T>.Failed value => $"failed:{value.Code}",
        _ => throw new InvalidOperationException()
    };
}
