using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>
/// Thumbnails of files as the desktop's file manager shows them: the freedesktop.org thumbnail cache on Linux, the
/// shell's thumbnail providers on Windows and Quick Look on macOS.
/// </summary>
/// <remarks>
/// <para>
/// A path is a fully qualified local path, supplied by trusted C# application code, never directly by a web client.
/// The thumbnail is a PNG image, which a web view shows directly, for example as a <c>data:image/png</c> URL.
/// </para>
/// <para>
/// Outcomes common to every platform: a thumbnail is <see cref="PlatformResult{T}.Success"/> with a
/// <see cref="DesktopThumbnail"/>; a file without one, such as a type no thumbnailer handles, a file whose thumbnailing
/// failed or, on Linux, a desktop that runs no thumbnailer and has none cached, is <see cref="PlatformResult{T}.Success"/>
/// with <see langword="null"/>; a path that does not exist is <see cref="PlatformFailureCode.NotFound"/>; access refused
/// is <see cref="PlatformFailureCode.PermissionDenied"/>. An icon is never returned instead of a thumbnail.
/// </para>
/// </remarks>
public interface IDesktopThumbnails
{
    /// <summary>Gets a thumbnail of the file at <paramref name="path"/> for display at <paramref name="size"/> pixels.</summary>
    /// <param name="path">A fully qualified local path.</param>
    /// <param name="size">
    /// The largest edge to display, from 1 to <see cref="DesktopThumbnails.MaximumSize"/> pixels. The thumbnail keeps the
    /// file's aspect ratio and can differ in size: Linux returns the smallest cached size at least as large (128, 256,
    /// 512 or 1024 pixels), else the largest smaller one; Windows and macOS return at most <paramref name="size"/> pixels,
    /// fewer for a small image. <see cref="DesktopThumbnail.Width"/> and <see cref="DesktopThumbnail.Height"/> report the
    /// image's own size; scale it to display.
    /// </param>
    /// <param name="cancellationToken">Cancels the request, also while a thumbnailer generates the thumbnail.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty, relative, contains NUL or names a root directory.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is less than 1 or greater than <see cref="DesktopThumbnails.MaximumSize"/>.</exception>
    ValueTask<PlatformResult<DesktopThumbnail?>> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken = default);
}

/// <summary>A thumbnail: a PNG image and its size in pixels.</summary>
/// <param name="Png">The PNG file's bytes.</param>
/// <param name="Width">The image's width in pixels.</param>
/// <param name="Height">The image's height in pixels.</param>
public sealed record DesktopThumbnail(ImmutableArray<byte> Png, int Width, int Height)
{
    private readonly ImmutableArray<byte> _png = Png.IsDefaultOrEmpty ? throw new ArgumentException("Supply the thumbnail's PNG data.", nameof(Png)) : Png;
    private readonly int _width = Width > 0 ? Width : throw new ArgumentOutOfRangeException(nameof(Width));
    private readonly int _height = Height > 0 ? Height : throw new ArgumentOutOfRangeException(nameof(Height));

    /// <summary>The PNG file's bytes.</summary>
    public ImmutableArray<byte> Png { get => _png; init => _png = value.IsDefaultOrEmpty ? throw new ArgumentException("Supply the thumbnail's PNG data.", nameof(Png)) : value; }
    /// <summary>The image's width in pixels.</summary>
    public int Width { get => _width; init => _width = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(Width)); }
    /// <summary>The image's height in pixels.</summary>
    public int Height { get => _height; init => _height = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(Height)); }
}

/// <summary>Thumbnails for hosts and platforms that cannot provide them.</summary>
public static class DesktopThumbnails
{
    /// <summary>The largest size a request accepts, in pixels: the freedesktop.org <c>xx-large</c> thumbnail. A later
    /// release may raise it.</summary>
    public static int MaximumSize => 1024;

    /// <summary>Creates thumbnails that report every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>Requests validate their arguments as a provider does; otherwise nothing throws except caller cancellation.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not defined.</exception>
    public static IDesktopThumbnails Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableThumbnails(reason);
    }

    private sealed class UnavailableThumbnails(PlatformUnavailableReason reason) : IDesktopThumbnails
    {
        public ValueTask<PlatformResult<DesktopThumbnail?>> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken = default)
        {
            ItemPaths.Validate(path, nameof(path));
            ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaximumSize);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<DesktopThumbnail?>>(new PlatformResult<DesktopThumbnail?>.Unavailable(reason));
        }
    }
}
