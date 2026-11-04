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

// The ReactiveUI flavor: a blob property raised with RaiseAndSetIfChanged and a ReactiveCommand that takes an upload.
public sealed class ReactiveBlobViewModel : ReactiveUI.ReactiveObject
{
    private RunicBlob? _preview;

    public ReactiveBlobViewModel(Runic.Navigation.IRunicModelContext modelContext)
    {
        var scheduler = Runic.Application.Views.ReactiveUI.ReactiveSchedulers.For(modelContext);
        AttachCommand = ReactiveUI.ReactiveCommand.CreateFromTask<RunicUpload, string>(async upload =>
        {
            await using var stream = await upload.OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            Preview = RunicBlob.FromBytes(Encoding.UTF8.GetBytes(text.ToUpperInvariant()), "text/plain", upload.FileName);
            return upload.FileName;
        }, outputScheduler: scheduler);
    }

    public RunicBlob? Preview
    {
        get => _preview;
        set => ReactiveUI.IReactiveObjectExtensions.RaiseAndSetIfChanged(this, ref _preview, value);
    }

    public ReactiveUI.ReactiveCommand<RunicUpload, string> AttachCommand { get; }
}

public sealed partial class ReactiveBlobWindow : RunicWindow<ReactiveBlobViewModel>;

// A gallery: a keyed collection of thumbnail rows, more than the window's cap of route-held blobs.
public sealed record Thumbnail(int Id, RunicBlob Image);

public sealed partial class BlobGalleryViewModel : ObservableObject
{
    [RunicCollection(nameof(Thumbnail.Id))]
    public System.Collections.ObjectModel.ObservableCollection<Thumbnail> Thumbnails { get; } = [];
}

public sealed partial class BlobGalleryWindow : RunicWindow<BlobGalleryViewModel>;
