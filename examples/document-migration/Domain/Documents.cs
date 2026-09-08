using System.Text;
using Runic.Platform;
namespace DocumentMigration.Domain;

public sealed record DocumentResult(string Status, string? Text = null, string? Name = null, bool CleanupFailed = false);

// Portable business policy shared by the MAUI-derived view model and Runic commands.
public sealed class DocumentService(IFileDialogs files)
{
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
        try
        {
            await using var lease = picked.Value;
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
            return knownOutcome;
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
