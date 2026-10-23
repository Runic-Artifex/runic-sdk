using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// Secrets inside a sandbox without Secret Service access, as libsecret keeps them there: the Secret portal
// (org.freedesktop.portal.Secret.RetrieveSecret) hands the application a per-application secret through a pipe, and
// items live in libsecret's file format (LibsecretKeyring) at $XDG_DATA_HOME/keyrings/default.keyring, which inside
// Flatpak is the application's own data directory. secret-tool and libsecret-based libraries in the same sandbox share
// the items.
//
// The portal secret and the key derived from it are kept for the store's lifetime in pinned arrays, as libsecret keeps
// them per process. Writers serialise on an advisory lock file next to the keyring and replace the file through a
// temporary file and rename, so readers never see a partial file. The portal has no non-interactive flag: its backend
// may ask once to unlock the login keyring.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SemaphoreSlim never allocates a wait handle (AvailableWaitHandle is unused), so it holds nothing to dispose.")]
internal sealed partial class SecretPortalStore : IDesktopSecretStore
{
    internal const string PortalName = "org.freedesktop.portal.Desktop";
    private const string SecretInterface = "org.freedesktop.portal.Secret";
    // libsecret accepts only a 64-byte portal secret (secret-file-backend.c, PASSWORD_SIZE).
    internal const int PortalSecretSize = 64;
    private const uint MaximumIterations = 10_000_000;
    private static readonly TimeSpan PortalTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly string? _address;
    private readonly string _destination;
    private readonly PortalApplication? _application;
    private readonly string _path;
    // Tests supply the secret instead of the portal.
    private readonly Func<CancellationToken, Task<byte[]>>? _secretSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _secret;
    private byte[]? _key;
    private byte[]? _keySalt;
    private uint _keyIterations;

    internal SecretPortalStore(string? address = null, string destination = PortalName, PortalApplication? application = null,
        string? path = null, Func<CancellationToken, Task<byte[]>>? secretSource = null)
    {
        _address = address;
        _destination = destination;
        _application = application;
        _path = path ?? DefaultPath();
        _secretSource = secretSource;
    }

    internal string KeyringPath => _path;

    // libsecret's g_get_user_data_dir(): $XDG_DATA_HOME, else ~/.local/share.
    internal static string DefaultPath()
    {
        string? data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(data) || !Path.IsPathRooted(data))
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "keyrings", "default.keyring");
    }

    public async ValueTask<PlatformResult<string?>> GetAsync(string service, string account, CancellationToken cancellationToken = default) =>
        SecretStoreValidation.DecodeResult(await GetBytesAsync(service, account, cancellationToken).ConfigureAwait(false));

    public async ValueTask<PlatformResult<byte[]?>> GetBytesAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        return await RunAsync<byte[]?>(async token =>
        {
            var keyring = Read();
            if (keyring is null) return new PlatformResult<byte[]?>.Success(null);
            byte[] key = await KeyAsync(keyring, token).ConfigureAwait(false);
            var search = Search(service, account);
            // libsecret's replace matches the whole attribute set, so secret-tool may add an item next to Runic's for the
            // same service and account: the most recently modified one is the secret, the later one in the file on a tie.
            LibsecretKeyring.Item? newest = null;
            foreach (var (attributes, blob) in keyring.Items)
            {
                if (!LibsecretKeyring.Matches(attributes, key, search)) continue;
                var item = LibsecretKeyring.Decrypt(key, blob);
                if (item is null)
                {
                    if (newest is not null) CryptographicOperations.ZeroMemory(newest.Secret);
                    return Invalid<byte[]?>();
                }
                if (newest is null || item.Modified >= newest.Modified)
                {
                    if (newest is not null) CryptographicOperations.ZeroMemory(newest.Secret);
                    newest = item;
                }
                else CryptographicOperations.ZeroMemory(item.Secret);
            }
            return new PlatformResult<byte[]?>.Success(newest?.Secret);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<PlatformUnit>> SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        byte[]? encoded = SecretEncoding.Encode(secret);
        if (encoded is null) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.TooLarge);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await SetCoreAsync(service, account, encoded, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    public async ValueTask<PlatformResult<PlatformUnit>> SetBytesAsync(string service, string account, ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        if (!SecretStoreValidation.ValidateBinary(service, account, secret.Span))
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.TooLarge);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] copy = secret.ToArray();
        try { return await SetCoreAsync(service, account, copy, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    private Task<PlatformResult<PlatformUnit>> SetCoreAsync(string service, string account, byte[] secret, CancellationToken cancellationToken) =>
        RunAsync<PlatformUnit>(async token =>
        {
            // The secret first: the portal may ask the user, which must not hold the lock.
            await PortalSecretAsync(token).ConfigureAwait(false);
            await using var held = await LockAsync(token).ConfigureAwait(false);
            if (held is null) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.ResourceBusy);
            var keyring = Read() ?? LibsecretKeyring.Create();
            byte[] key = await KeyAsync(keyring, token).ConfigureAwait(false);
            var search = Search(service, account);
            ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), created = now;
            for (int i = keyring.Items.Count - 1; i >= 0; i--)
            {
                if (!LibsecretKeyring.Matches(keyring.Items[i].Attributes, key, search)) continue;
                // The replaced item keeps its creation time, as libsecret's replace does.
                if (LibsecretKeyring.Decrypt(key, keyring.Items[i].Blob) is { } old)
                {
                    created = old.Created;
                    CryptographicOperations.ZeroMemory(old.Secret);
                }
                keyring.Items.RemoveAt(i);
            }
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xdg:schema"] = SecretServiceStore.GenericSchema, ["service"] = service, ["account"] = account,
            };
            keyring.Items.Add((LibsecretKeyring.Hash(key, attributes),
                LibsecretKeyring.Encrypt(key, new(attributes, service + " (" + account + ")", created, now, secret))));
            keyring.UsageCount++;
            keyring.Modified = now;
            Write(keyring);
            return new PlatformResult<PlatformUnit>.Success(default);
        }, cancellationToken);

    public async ValueTask<PlatformResult<bool>> DeleteAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        return await RunAsync<bool>(async token =>
        {
            if (!File.Exists(_path)) return new PlatformResult<bool>.Success(false);
            await PortalSecretAsync(token).ConfigureAwait(false);
            await using var held = await LockAsync(token).ConfigureAwait(false);
            if (held is null) return new PlatformResult<bool>.Failed(PlatformFailureCode.ResourceBusy);
            var keyring = Read();
            if (keyring is null) return new PlatformResult<bool>.Success(false);
            byte[] key = await KeyAsync(keyring, token).ConfigureAwait(false);
            var search = Search(service, account);
            int removed = keyring.Items.RemoveAll(item => LibsecretKeyring.Matches(item.Attributes, key, search));
            if (removed == 0) return new PlatformResult<bool>.Success(false);
            keyring.Modified = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Write(keyring);
            return new PlatformResult<bool>.Success(true);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> Search(string service, string account) =>
        new(StringComparer.Ordinal) { ["service"] = service, ["account"] = account };

    private async Task<PlatformResult<T>> RunAsync<T>(Func<CancellationToken, Task<PlatformResult<T>>> body, CancellationToken cancellationToken)
    {
        try { return await body(cancellationToken).ConfigureAwait(false); }
        catch (SecretServiceFailure failure) { return new PlatformResult<T>.Failed(failure.Code, failure.Diagnostic); }
        catch (FormatException) { return Invalid<T>(); }
        catch (UnauthorizedAccessException error)
        { return new PlatformResult<T>.Failed(PlatformFailureCode.PermissionDenied, new PlatformDiagnostic("HRESULT", unchecked((uint)error.HResult))); }
        catch (IOException error)
        { return new PlatformResult<T>.Failed(PlatformFailureCode.IOError, new PlatformDiagnostic("HRESULT", unchecked((uint)error.HResult))); }
        catch (SecretPortalUnavailable unavailable)
        {
            // The D-Bus error name or exception type only, as the other portal diagnostics.
            _application?.Diagnose("secret-portal-unavailable",
                "Inside the sandbox neither the Secret Service nor the Secret portal answered: " + unavailable.Reason + ".",
                "Allow talking to org.freedesktop.secrets (Flatpak --talk-name=org.freedesktop.secrets, the Snap password-manager-service " +
                "interface), or run a desktop portal with a Secret backend, such as GNOME Keyring's.");
            return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
    }

    private PlatformResult<T> Invalid<T>()
    {
        _application?.Diagnose("secret-file-invalid", "The sandbox's keyring file is damaged or not in libsecret's format.",
            "Move the keyrings/default.keyring file out of the application's data directory; secrets stored there are lost.");
        return new PlatformResult<T>.Failed(PlatformFailureCode.InvalidData);
    }

    // The keyring, or null when there is none yet.
    /// <exception cref="FormatException">The file is damaged.</exception>
    private LibsecretKeyring? Read()
    {
        byte[] file;
        try { file = File.ReadAllBytes(_path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        var keyring = LibsecretKeyring.Parse(file);
        if (keyring.Iterations > MaximumIterations) throw new FormatException("The keyring's iteration count is out of range.");
        return keyring;
    }

    private void Write(LibsecretKeyring keyring)
    {
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = _path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                stream.Write(keyring.Serialize());
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    // The key for the keyring's salt, derived once per salt.
    private async Task<byte[]> KeyAsync(LibsecretKeyring keyring, CancellationToken cancellationToken)
    {
        byte[] secret = await PortalSecretAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_key is null || _keyIterations != keyring.Iterations || !_keySalt.AsSpan().SequenceEqual(keyring.Salt))
            {
                byte[] derived = keyring.DeriveKey(secret);
                _key ??= GC.AllocateArray<byte>(LibsecretKeyring.KeySize, pinned: true);
                derived.CopyTo(_key, 0);
                CryptographicOperations.ZeroMemory(derived);
                _keySalt = keyring.Salt.ToArray();
                _keyIterations = keyring.Iterations;
            }
            return _key;
        }
        finally { _gate.Release(); }
    }

    private async Task<byte[]> PortalSecretAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_secret is not null) return _secret;
            byte[] retrieved = _secretSource is not null ? await _secretSource(cancellationToken).ConfigureAwait(false)
                : await RetrieveSecretOnceMoreAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (retrieved.Length != PortalSecretSize) throw new SecretServiceFailure(PlatformFailureCode.InvalidData);
                _secret = GC.AllocateArray<byte>(PortalSecretSize, pinned: true);
                retrieved.CopyTo(_secret, 0);
                return _secret;
            }
            finally { CryptographicOperations.ZeroMemory(retrieved); }
        }
        finally { _gate.Release(); }
    }

    // xdg-desktop-portal 1.22 takes its bus name before it exports org.freedesktop.portal.Secret: it creates the
    // backend's proxy asynchronously (secret.c, init_secret) and exports Secret only when that completes. The call that
    // activates the portal is delivered as soon as the name is taken, so it can find no Secret interface yet (I-198).
    // One more attempt after a short delay; a portal that really has no Secret interface still reports it.
    internal static readonly TimeSpan ActivationRetryDelay = TimeSpan.FromMilliseconds(250);

    private async Task<byte[]> RetrieveSecretOnceMoreAsync(CancellationToken cancellationToken)
    {
        try { return await RetrieveSecretAsync(cancellationToken).ConfigureAwait(false); }
        catch (SecretPortalUnavailable error) when (error.Reason is "org.freedesktop.DBus.Error.UnknownMethod" or "org.freedesktop.DBus.Error.UnknownInterface")
        {
            await Task.Delay(ActivationRetryDelay, cancellationToken).ConfigureAwait(false);
            return await RetrieveSecretAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // org.freedesktop.portal.Secret.RetrieveSecret(h fd, a{sv} options): the portal writes the secret into the pipe's
    // write end and answers through a Request.
    private async Task<byte[]> RetrieveSecretAsync(CancellationToken cancellationToken)
    {
        PortalConnection session;
        try { session = await PortalConnection.OpenAsync(_address, _destination, _application, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is NativeBackendUnavailableException or TimeoutException) { throw new SecretPortalUnavailable(error.GetType().Name); }
        catch (DBusException error) when (IsMissing(error.ErrorName)) { throw new SecretPortalUnavailable(error.ErrorName); }
        string? request = null;
        Task<DBusObjectPath>? asked = null;
        SafeFileHandle? read = null, write = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.OwnerChanged);
            deadline.CancelAfter(PortalTimeout);
            var connection = session.Connection;
            string prefix = "/org/freedesktop/portal/desktop/request/" + connection.UniqueName.TrimStart(':').Replace('.', '_') + "/";
            var responses = Channel.CreateUnbounded<(string Path, uint Code)>();
            await using var subscription = await connection.SubscribeAsync(new DBusSignalFilter
            {
                Sender = session.Destination, PathNamespace = new DBusObjectPath(prefix.TrimEnd('/')),
                Interface = "org.freedesktop.portal.Request", Member = "Response",
            }, deadline.Token).ConfigureAwait(false);
            _ = PortalValues.PumpAsync(subscription, signal =>
            {
                if (PortalValues.TryReadResponse(signal, out uint code, out _)) responses.Writer.TryWrite((signal.Path.Value, code));
            }, error => responses.Writer.TryComplete(error));

            (read, write) = Pipe();
            string token = "runic_" + Guid.NewGuid().ToString("N");
            var options = new PortalOptions { ["handle_token"] = DBusValue.From(token) };
            request = prefix + token;
            asked = PortalOptions.RequestAsync(new DBusProxy(connection, session.Destination, new DBusObjectPath("/org/freedesktop/portal/desktop")),
                SecretInterface, "RetrieveSecret", [DBusValue.UnixFd(write), options.ToValue()]);
            string handle = (await asked.WaitAsync(deadline.Token).ConfigureAwait(false)).Value;
            // The portal has its own copy now; the pipe reaches end of file once it closes it.
            write.Dispose();
            if (!handle.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("Invalid secret request handle.");
            request = handle;
            while (true)
            {
                var response = await responses.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (response.Path != handle) continue;
                request = null;
                if (response.Code != 0)
                    throw new SecretServiceFailure(response.Code == 1 ? PlatformFailureCode.UserDismissed : PlatformFailureCode.Other, PortalErrors.Response(response.Code));
                return await ReadToEndAsync(read, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw new SecretPortalUnavailable(error.GetType().Name); }
        catch (DBusException error) when (IsMissing(error.ErrorName)) { throw new SecretPortalUnavailable(error.ErrorName); }
        catch (DBusException error) when (error.ErrorName is "org.freedesktop.portal.Error.NotAllowed" or "org.freedesktop.DBus.Error.AccessDenied")
        { throw new SecretServiceFailure(PlatformFailureCode.PermissionDenied, PortalErrors.Reply(error)); }
        catch (DBusException error) when (error.ErrorName != DBusException.Disconnected)
        { throw new SecretServiceFailure(PlatformFailureCode.Other, PortalErrors.Reply(error)); }
        catch (DBusException error) { throw new SecretPortalUnavailable(error.ErrorName); }
        finally
        {
            write?.Dispose();
            read?.Dispose();
            if (request is not null) _ = PortalRequests.CloseAbandonedAsync(session, request, asked);
        }
    }

    private static bool IsMissing(string error) => error is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner"
        or "org.freedesktop.DBus.Error.UnknownMethod" or "org.freedesktop.DBus.Error.UnknownInterface" or "org.freedesktop.DBus.Error.UnknownObject"
        || error.StartsWith("org.freedesktop.DBus.Error.Spawn.", StringComparison.Ordinal);

    // Reads the non-blocking pipe until every writer closed it, at most a few kilobytes.
    private static async Task<byte[]> ReadToEndAsync(SafeFileHandle read, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length) throw new SecretServiceFailure(PlatformFailureCode.InvalidData);
                int count = ReadPipe(read, buffer.AsSpan(length));
                if (count > 0) { length += count; continue; }
                if (count == 0) return buffer.AsSpan(0, length).ToArray();
                int error = Marshal.GetLastPInvokeError();
                if (error is not (EAGAIN or EINTR)) throw new IOException("Reading the portal's secret failed.", error);
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private const int EAGAIN = 11, EINTR = 4, O_CLOEXEC = 0x80000, O_NONBLOCK = 0x800, O_RDWR = 2, O_CREAT = 0x40;

    private static unsafe (SafeFileHandle Read, SafeFileHandle Write) Pipe()
    {
        int* fds = stackalloc int[2];
        if (Pipe2(fds, O_CLOEXEC) != 0) throw new IOException("Creating a pipe for the portal's secret failed.", Marshal.GetLastPInvokeError());
        var read = new SafeFileHandle(fds[0], ownsHandle: true);
        var write = new SafeFileHandle(fds[1], ownsHandle: true);
        if (SetFlags(fds[0], 4 /* F_SETFL */, O_NONBLOCK) != 0)
        {
            read.Dispose();
            write.Dispose();
            throw new IOException("Configuring the portal's pipe failed.", Marshal.GetLastPInvokeError());
        }
        return (read, write);
    }

    private static unsafe int ReadPipe(SafeFileHandle handle, Span<byte> buffer)
    {
        bool added = false;
        handle.DangerousAddRef(ref added);
        try { fixed (byte* bytes = buffer) return (int)Read((int)handle.DangerousGetHandle(), bytes, (nuint)buffer.Length); }
        finally { if (added) handle.DangerousRelease(); }
    }

    // An advisory lock on <keyring>.lock that serialises Runic's writers; null when another writer held it too long.
    private async Task<IAsyncDisposable?> LockAsync(CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // open(2) directly: a FileStream would take its own flock on the descriptor.
        int fd = Open(_path + ".lock", O_RDWR | O_CREAT | O_CLOEXEC, 0x180);
        if (fd < 0) throw new IOException("Opening the keyring's lock file failed.", Marshal.GetLastPInvokeError());
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        var started = DateTime.UtcNow;
        while (Flock(fd, 2 /* LOCK_EX */ | 4 /* LOCK_NB */) != 0)
        {
            if (DateTime.UtcNow - started > LockTimeout) { handle.Dispose(); return null; }
            try { await Task.Delay(20, cancellationToken).ConfigureAwait(false); }
            catch { handle.Dispose(); throw; }
        }
        // Closing the descriptor releases the lock.
        return new Held(handle);
    }

    private sealed class Held(SafeFileHandle handle) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { handle.Dispose(); return ValueTask.CompletedTask; }
    }

    [LibraryImport("libc", EntryPoint = "pipe2", SetLastError = true)] private static unsafe partial int Pipe2(int* fds, int flags);
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static partial int SetFlags(int fd, int command, int flags);
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)] private static unsafe partial nint Read(int fd, byte* buffer, nuint count);
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int Open(string path, int flags, int mode);
    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)] private static partial int Flock(int fd, int operation);
}

// Neither the Secret portal nor its backend answered.
internal sealed class SecretPortalUnavailable(string reason) : Exception
{
    internal string Reason { get; } = reason;
}
