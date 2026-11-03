using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// blobs/1: a ViewModel that shows a blob, returns one from a command, and takes uploads.
public sealed record BlobAttachment(string Name, RunicBlob Preview);

public sealed partial class BlobViewModel : ObservableObject
{
    [RunicIgnore] public string? LastText { get; private set; }
    [RunicIgnore] public string? LastName { get; private set; }

    // A public setter: still read-only for the page, since it holds a blob.
    [ObservableProperty]
    public partial RunicBlob? Avatar { get; set; } = RunicBlob.FromBytes("avatar-1"u8.ToArray(), "image/png", "avatar.png");

    [ObservableProperty]
    public partial IReadOnlyList<BlobAttachment> Attachments { get; private set; } = [];

    [RelayCommand]
    private async Task AttachAsync(RunicUpload file, CancellationToken cancellationToken)
    {
        await using var stream = await file.OpenReadAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        LastText = await reader.ReadToEndAsync(cancellationToken);
        LastName = file.FileName;
        Attachments = [.. Attachments, new BlobAttachment(file.FileName, RunicBlob.FromBytes(Encoding.UTF8.GetBytes(LastText), file.ContentType))];
    }

    [RelayCommand]
    private Task<RunicBlob> ExportAsync() =>
        Task.FromResult(RunicBlob.FromBytes(Encoding.UTF8.GetBytes(LastText ?? "report"), "text/csv", "report.csv", RunicBlobDisposition.Attachment));
}

public sealed partial class BlobWindow : RunicWindow<BlobViewModel>;
