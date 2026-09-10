using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Shortcuts;

/// <summary>Local shell-link operations, executed on an owned COM apartment without a desktop owner.</summary>
public sealed class WindowsShellLinkClient : IWindowsShellLinkClient
{
    private static readonly Guid ClassId = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid InterfaceId = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid PersistenceId = new("0000010b-0000-0000-C000-000000000046");
    private const int TextCapacity = 32768;

    /// <summary>Reads without resolving or displaying UI. Returns null only if the link or its directory is absent.</summary>
    public Task<ShellLinkSnapshot?> FindAsync(string path, CancellationToken cancellationToken = default)
    {
        path = ValidatePath(path);
        return ComApartment.RunAsync<ShellLinkSnapshot?>(() => WithErrors("Read shell link", () =>
        {
            if (!Exists(path)) return null;
            using var link = Load(path);
            return Read(link);
        }), cancellationToken);
    }

    /// <summary>Creates a link; replacement requires explicit opt-in. A failed staged save retains the destination.</summary>
    public Task CreateAsync(string path, ShellLinkSpecification specification, bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        path = ValidatePath(path);
        ArgumentNullException.ThrowIfNull(specification);
        var update = new ShellLinkUpdate
        {
            TargetPath = specification.TargetPath, Arguments = specification.Arguments,
            WorkingDirectory = specification.WorkingDirectory, Description = specification.Description,
            Icon = specification.Icon, ShowState = specification.ShowState, Hotkey = specification.Hotkey
        };
        NativeError.Text(specification.TargetPath, nameof(specification));
        ArgumentNullException.ThrowIfNull(specification.Icon);
        Validate(update);
        return ComApartment.RunAsync(() => WithErrors("Create shell link", () =>
        {
            using var link = ComObject.Create(ClassId, InterfaceId);
            Apply(link, update);
            Save(link, path, replaceExisting);
            return true;
        }), cancellationToken);
    }

    /// <summary>Updates only supplied fields, retaining other shell-link metadata. Missing links are errors.</summary>
    public Task UpdateAsync(string path, ShellLinkUpdate update, CancellationToken cancellationToken = default)
    {
        path = ValidatePath(path);
        ArgumentNullException.ThrowIfNull(update);
        Validate(update);
        return ComApartment.RunAsync(() => WithErrors("Update shell link", () =>
        {
            using var link = Load(path);
            Apply(link, update);
            Save(link, path, true);
            return true;
        }), cancellationToken);
    }

    /// <summary>Explicitly resolves a target without UI or writing the source link. The timeout is passed to the shell.</summary>
    /// <remarks>The shell timeout limits its no-UI resolution work; it cannot guarantee a hard deadline for every network provider.</remarks>
    public Task<ShellLinkSnapshot> ResolveAsync(string path, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        path = ValidatePath(path);
        if (timeout.TotalMilliseconds < 1 || timeout.TotalMilliseconds > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Use a timeout between 1 and 65535 milliseconds.");
        var flags = 1u | 8u | ((uint)Math.Ceiling(timeout.TotalMilliseconds) << 16);
        return ComApartment.RunAsync(() => WithErrors("Resolve shell link", () =>
        {
            using var link = Load(path);
            Resolve(link, flags);
            return Read(link);
        }), cancellationToken);
    }

    private static unsafe void Resolve(ComObject link, uint flags) =>
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)link.Slot(19))(link.Pointer, 0, flags), "Resolve shell link");

    private static string ValidatePath(string path)
    {
        NativeError.Text(path, nameof(path));
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute shell-link path.", nameof(path));
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use a .lnk file.", nameof(path));
        return Path.GetFullPath(path);
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static T WithErrors<T>(string operation, Func<T> action)
    {
        try { return action(); }
        catch (UnauthorizedAccessException error) { throw new WindowsAdministrationException(operation, AdministrationErrorCategory.AccessDenied, NativeErrorDomain.HResult, error.HResult, operation + " was denied.", error); }
        catch (IOException error) { throw NativeError.HResult(operation, error.HResult, error); }
    }

    private static void Validate(ShellLinkUpdate update)
    {
        if (update.TargetPath is not null) NativeError.Text(update.TargetPath, nameof(update.TargetPath));
        foreach (var value in new[] { update.Arguments, update.Description, update.WorkingDirectory, update.Icon?.Path })
            if (value is not null) NativeError.Text(value, nameof(update), true);
        if (update.Icon is not null) ArgumentNullException.ThrowIfNull(update.Icon.Path);
        if (update.ShowState is { } state && !Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(update));
    }

    private static unsafe ComObject Load(string path)
    {
        var link = ComObject.Create(ClassId, InterfaceId);
        try
        {
            using var file = link.Query(PersistenceId);
            fixed (char* text = path)
                NativeError.Check(((delegate* unmanaged[Stdcall]<nint, char*, uint, int>)file.Slot(5))(file.Pointer, text, 0), "Load shell link");
            return link;
        }
        catch { link.Dispose(); throw; }
    }

    private static unsafe void Save(ComObject link, string path, bool replace)
    {
        var staging = Path.Combine(Path.GetDirectoryName(path)!, ".runic-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            using var file = link.Query(PersistenceId);
            fixed (char* text = staging)
                NativeError.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)file.Slot(6))(file.Pointer, text, 0), "Save shell link");
            File.Move(staging, path, replace);
        }
        finally
        {
            // Cleanup must not mask the original operation outcome.
            try { File.Delete(staging); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static unsafe void SetText(ComObject link, int slot, string value)
    {
        fixed (char* text = value)
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)link.Slot(slot))(link.Pointer, text), "Set shell-link field");
    }

    private static unsafe void Apply(ComObject link, ShellLinkUpdate update)
    {
        if (update.TargetPath is { } target) SetText(link, 20, target);
        if (update.Description is { } description) SetText(link, 7, description);
        if (update.WorkingDirectory is { } workingDirectory) SetText(link, 9, workingDirectory);
        if (update.Arguments is { } arguments) SetText(link, 11, arguments);
        if (update.Icon is { } icon)
            fixed (char* text = icon.Path)
                NativeError.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)link.Slot(17))(link.Pointer, text, icon.Index), "Set shell-link icon");
        if (update.ShowState is { } state)
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, int>)link.Slot(15))(link.Pointer, (int)state), "Set shell-link show state");
        if (update.Hotkey is { } hotkey)
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, ushort, int>)link.Slot(13))(link.Pointer, hotkey), "Set shell-link hotkey");
    }

    private static unsafe string GetText(ComObject link, int slot)
    {
        var buffer = new char[TextCapacity];
        fixed (char* text = buffer)
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)link.Slot(slot))(link.Pointer, text, buffer.Length), "Read shell-link field");
        return FromBuffer(buffer);
    }

    private static string FromBuffer(char[] buffer)
    {
        var end = Array.IndexOf(buffer, '\0');
        if (end < 0 || end == buffer.Length - 1) throw NativeError.Win32("Read shell-link text without truncation", 13);
        return new string(buffer, 0, end);
    }

    private static unsafe ShellLinkSnapshot Read(ComObject link)
    {
        var path = new char[TextCapacity];
        var icon = new char[TextCapacity];
        int index, show;
        ushort hotkey;
        fixed (char* text = path)
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, nint, uint, int>)link.Slot(3))(link.Pointer, text, path.Length, 0, 4), "Read shell-link target");
        fixed (char* text = icon)
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int*, int>)link.Slot(16))(link.Pointer, text, icon.Length, &index), "Read shell-link icon");
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)link.Slot(14))(link.Pointer, &show), "Read shell-link show state");
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, ushort*, int>)link.Slot(12))(link.Pointer, &hotkey), "Read shell-link hotkey");
        return new(FromBuffer(path), GetText(link, 10), GetText(link, 8), GetText(link, 6), new(FromBuffer(icon), index), show, hotkey);
    }
}
