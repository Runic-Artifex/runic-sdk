using System.Collections.Immutable;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Attachments;

/// <summary>An attachment the user uploaded, which the page offers as a download.</summary>
/// <param name="Name">The file name the page reported, reduced to a file name.</param>
/// <param name="Size">The number of bytes received.</param>
/// <param name="File">The stored file, served as a download.</param>
public sealed record Attachment(string Name, long Size, RunicBlob File);

/// <summary>
/// A profile with an avatar that the ViewModel draws, and attachments that the page uploads (Views feature blobs/1).
/// </summary>
public sealed partial class ProfileViewModel : ObservableObject, IDisposable
{
    private static readonly string[] Colors = ["#2f6f8f", "#8f2f5f", "#3f8f2f", "#8f6f2f"];
    // Uploads are stored here, one file per attachment, and deleted when the Window closes.
    private readonly string _folder = Directory.CreateTempSubdirectory("runic-attachments-example-").FullName;
    private int _color;

    public ProfileViewModel() => Avatar = Draw();

    /// <summary>The avatar, drawn by .NET; the page shows it with &lt;img src&gt;.</summary>
    [ObservableProperty]
    public partial RunicBlob Avatar { get; private set; }

    /// <summary>The attachments, newest last.</summary>
    [ObservableProperty]
    public partial ImmutableArray<Attachment> Attachments { get; private set; } = [];

    /// <summary>Draws the avatar in the next color: a new blob, so the page loads a new URL and the old one is released.</summary>
    [RelayCommand]
    private void Redraw()
    {
        _color = (_color + 1) % Colors.Length;
        Avatar = Draw();
    }

    /// <summary>Stores an upload as an attachment. Its name, type and content come from the page and are untrusted.</summary>
    [RelayCommand]
    private async Task AttachAsync(RunicUpload file, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
        await file.SaveAsAsync(path, cancellationToken: cancellationToken);
        Attachments = Attachments.Add(new Attachment(file.FileName, file.Length,
            RunicBlob.FromFile(path, "application/octet-stream", file.FileName, RunicBlobDisposition.Attachment)));
    }

    private RunicBlob Draw()
    {
        var svg = $"""<svg xmlns="http://www.w3.org/2000/svg" width="96" height="96" viewBox="0 0 96 96"><rect width="96" height="96" rx="48" fill="{Colors[_color]}"/><text x="48" y="60" font-family="sans-serif" font-size="40" text-anchor="middle" fill="white">RA</text></svg>""";
        return RunicBlob.FromBytes(Encoding.UTF8.GetBytes(svg), "image/svg+xml", "avatar.svg");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }
}
