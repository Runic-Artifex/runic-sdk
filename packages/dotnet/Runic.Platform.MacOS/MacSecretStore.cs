using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Runic.Platform.Runtime;

namespace Runic.Platform.MacOS;

// Generic passwords in the user's default (login) keychain through SecItem, or in the data-protection keychain with
// DesktopSecretStoreOptions.UseDataProtectionKeychain. The item's service and account are the secret's, and its label
// is the service, as Keychain Access shows it. The Keychain may ask the user to unlock it or to allow this application;
// the calls block until the user answers, so they run off the caller's thread. Without user interaction the file-based
// keychain's lock state is checked first and the query forbids authentication UI (kSecUseAuthenticationUIFail); the
// data-protection keychain gets an LAContext with interactionNotAllowed.
[SupportedOSPlatform("macos")]
internal sealed partial class MacSecretStore : IDesktopSecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LocalAuthentication = "/System/Library/Frameworks/LocalAuthentication.framework/LocalAuthentication";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    internal const int Success = 0;
    internal const int ItemNotFound = -25300;
    internal const int DuplicateItem = -25299;
    internal const int InteractionNotAllowed = -25308;
    internal const int UserCanceled = -128;
    internal const int AuthFailed = -25293;
    internal const int NoSuchKeychain = -25294;
    internal const int NotAvailable = -25291;
    internal const int NoDefaultKeychain = -25307;
    internal const int InvalidKeychain = -25295;
    internal const int ReadOnly = -25292;
    internal const int WritePermission = -61;
    internal const int MissingEntitlement = -34018;
    private const uint UnlockStateStatus = 1; // kSecUnlockStateStatus

    private readonly bool _interactive;
    private readonly bool _dataProtection;
    // A keychain file to use instead of the default keychain; tests only.
    private readonly string? _keychainPath;

    internal MacSecretStore(DesktopSecretStoreOptions? options = null, string? keychainPath = null)
    {
        _interactive = options?.AllowUserInteraction ?? true;
        _dataProtection = options?.UseDataProtectionKeychain ?? false;
        _keychainPath = keychainPath;
    }

    public async ValueTask<PlatformResult<string?>> GetAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => SecretStoreValidation.DecodeResult(Get(service, account)), CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<byte[]?>> GetBytesAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => Get(service, account), CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<PlatformUnit>> SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        byte[]? encoded = SecretEncoding.Encode(secret);
        if (encoded is null) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.TooLarge);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => Set(service, account, encoded), CancellationToken.None).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    public async ValueTask<PlatformResult<PlatformUnit>> SetBytesAsync(string service, string account, ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        if (!SecretStoreValidation.ValidateBinary(service, account, secret.Span))
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.TooLarge);
        byte[] copy = secret.ToArray();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => Set(service, account, copy), CancellationToken.None).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    public async ValueTask<PlatformResult<bool>> DeleteAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            if (LockedWithoutInteraction() is { } locked) return Failure<bool>(locked);
            nint query = Query(service, account, adding: false);
            try
            {
                int status = SecItemDelete(query);
                return status switch
                {
                    Success => new PlatformResult<bool>.Success(true),
                    ItemNotFound => new PlatformResult<bool>.Success(false),
                    _ => Failure<bool>(status),
                };
            }
            finally { MacSecurity.Release(query); }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private unsafe PlatformResult<byte[]?> Get(string service, string account)
    {
        if (LockedWithoutInteraction() is { } locked) return Failure<byte[]?>(locked);
        nint query = Query(service, account, adding: false);
        try
        {
            MacSecurity.DictionarySet(query, Constant("kSecReturnData"), MacSecurity.True);
            MacSecurity.DictionarySet(query, Constant("kSecMatchLimit"), Constant("kSecMatchLimitOne"));
            int status = SecItemCopyMatching(query, out nint data);
            if (status == ItemNotFound) return new PlatformResult<byte[]?>.Success(null);
            if (status != Success) return Failure<byte[]?>(status);
            try
            {
                // An item with empty data may return no data object.
                if (data == 0) return new PlatformResult<byte[]?>.Success([]);
                if (CFGetTypeID(data) != CFDataGetTypeID()) return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData);
                return new PlatformResult<byte[]?>.Success(new ReadOnlySpan<byte>(CFDataGetBytePtr(data), checked((int)CFDataGetLength(data))).ToArray());
            }
            finally { if (data != 0) MacSecurity.Release(data); }
        }
        finally { MacSecurity.Release(query); }
    }

    private unsafe PlatformResult<PlatformUnit> Set(string service, string account, byte[] secret)
    {
        if (LockedWithoutInteraction() is { } locked) return Failure<PlatformUnit>(locked);
        nint query = Query(service, account, adding: false), update = MacSecurity.CreateDictionary();
        nint data;
        fixed (byte* bytes = secret) data = CFDataCreate(0, bytes, secret.Length);
        try
        {
            MacSecurity.DictionarySet(update, Constant("kSecValueData"), data);
            // Update an existing item, which keeps the access the user granted it; add one otherwise. Another
            // process may add the item between the two calls. An update to empty data can leave the old data in a
            // file keychain, so an empty secret replaces the item instead.
            for (int attempt = 0; ; attempt++)
            {
                int status = secret.Length == 0 ? SecItemDelete(query) : SecItemUpdate(query, update);
                if (status == Success && secret.Length != 0) return new PlatformResult<PlatformUnit>.Success(default);
                if (status is not (Success or ItemNotFound)) return Failure<PlatformUnit>(status);
                nint add = Query(service, account, adding: true);
                try
                {
                    nint label = MacSecurity.CreateString(service);
                    MacSecurity.DictionarySet(add, Constant("kSecAttrLabel"), label);
                    MacSecurity.Release(label);
                    MacSecurity.DictionarySet(add, Constant("kSecValueData"), data);
                    status = SecItemAdd(add, 0);
                }
                finally { MacSecurity.Release(add); }
                if (status == Success) return new PlatformResult<PlatformUnit>.Success(default);
                if (status != DuplicateItem || attempt > 0) return Failure<PlatformUnit>(status);
            }
        }
        finally
        {
            MacSecurity.Release(data);
            MacSecurity.Release(update);
            MacSecurity.Release(query);
        }
    }

    // { kSecClass: kSecClassGenericPassword, kSecAttrService: service, kSecAttrAccount: account } with the options'
    // keychain and interaction keys.
    private unsafe nint Query(string service, string account, bool adding)
    {
        nint query = MacSecurity.CreateDictionary();
        MacSecurity.DictionarySet(query, Constant("kSecClass"), Constant("kSecClassGenericPassword"));
        nint cfService = MacSecurity.CreateString(service), cfAccount = MacSecurity.CreateString(account);
        MacSecurity.DictionarySet(query, Constant("kSecAttrService"), cfService);
        MacSecurity.DictionarySet(query, Constant("kSecAttrAccount"), cfAccount);
        MacSecurity.Release(cfService);
        MacSecurity.Release(cfAccount);
        if (_dataProtection) MacSecurity.DictionarySet(query, Constant("kSecUseDataProtectionKeychain"), MacSecurity.True);
        if (_keychainPath is not null && !_dataProtection && OpenKeychain() is var keychain and not 0)
        {
            if (adding) MacSecurity.DictionarySet(query, Constant("kSecUseKeychain"), keychain);
            else
            {
                nint list = CFArrayCreate(0, &keychain, 1, NativeLibrary.GetExport(CoreFoundationLibrary.Value, "kCFTypeArrayCallBacks"));
                MacSecurity.DictionarySet(query, Constant("kSecMatchSearchList"), list);
                MacSecurity.Release(list);
            }
            MacSecurity.Release(keychain);
        }
        if (!_interactive)
        {
            if (_dataProtection && CreateNonInteractiveContext() is var context and not 0)
            {
                MacSecurity.DictionarySet(query, Constant("kSecUseAuthenticationContext"), context);
                ObjcRelease(context);
            }
            else if (!_dataProtection)
            {
                MacSecurity.DictionarySet(query, Constant("kSecUseAuthenticationUI"), Constant("kSecUseAuthenticationUIFail"));
            }
        }
        return query;
    }

    // The status to report when a request may not ask the user and the file-based keychain it uses is locked.
    private int? LockedWithoutInteraction()
    {
        if (_interactive || _dataProtection) return null;
        nint keychain;
        int status = _keychainPath is not null ? SecKeychainOpen(_keychainPath, out keychain) : SecKeychainCopyDefault(out keychain);
        if (status != Success) return status is NoDefaultKeychain or NoSuchKeychain ? status : null;
        try
        {
            if (SecKeychainGetStatus(keychain, out uint state) != Success) return null;
            return (state & UnlockStateStatus) == 0 ? InteractionNotAllowed : null;
        }
        finally { MacSecurity.Release(keychain); }
    }

    private nint OpenKeychain() => SecKeychainOpen(_keychainPath!, out nint keychain) == Success ? keychain : 0;

    // [[LAContext alloc] init] with interactionNotAllowed = YES; the caller releases it.
    private static nint CreateNonInteractiveContext()
    {
        _ = LocalAuthenticationLibrary.Value;
        nint type = objc_getClass("LAContext");
        if (type == 0) return 0;
        nint context = Send(Send(type, sel_registerName("alloc")), sel_registerName("init"));
        if (context != 0) SendBool(context, sel_registerName("setInteractionNotAllowed:"), 1);
        return context;
    }

    private static void ObjcRelease(nint value) => Send(value, sel_registerName("release"));

    internal static PlatformResult<T> Failure<T>(int status)
    {
        var diagnostic = status == MissingEntitlement
            ? new PlatformDiagnostic("OSStatus", status,
                "The data-protection keychain needs a bundle signed with a team ID and the keychain-access-groups entitlement.")
            : new PlatformDiagnostic("OSStatus", status);
        return status switch
        {
            // The keychain is locked and the process cannot show the unlock dialog, as in an SSH session.
            InteractionNotAllowed => new PlatformResult<T>.Failed(PlatformFailureCode.Locked, diagnostic),
            UserCanceled => new PlatformResult<T>.Failed(PlatformFailureCode.UserDismissed, diagnostic),
            AuthFailed or ReadOnly or WritePermission or MissingEntitlement => new PlatformResult<T>.Failed(PlatformFailureCode.PermissionDenied, diagnostic),
            NoSuchKeychain or NotAvailable or NoDefaultKeychain or InvalidKeychain => new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            _ => new PlatformResult<T>.Failed(PlatformFailureCode.Other, diagnostic),
        };
    }

    private static readonly Lazy<nint> SecurityLibrary = new(() => NativeLibrary.Load(Security));
    private static readonly Lazy<nint> CoreFoundationLibrary = new(() => NativeLibrary.Load(CoreFoundation));
    private static readonly Lazy<nint> LocalAuthenticationLibrary = new(() => NativeLibrary.Load(LocalAuthentication));
    // An exported CFStringRef constant: the address of a variable that holds it.
    private static unsafe nint Constant(string name) => *(nint*)NativeLibrary.GetExport(SecurityLibrary.Value, name);

    [LibraryImport(Security)] private static partial int SecItemCopyMatching(nint query, out nint result);
    [LibraryImport(Security)] private static partial int SecItemAdd(nint attributes, nint result);
    [LibraryImport(Security)] private static partial int SecItemUpdate(nint query, nint attributesToUpdate);
    [LibraryImport(Security)] private static partial int SecItemDelete(nint query);
    [LibraryImport(Security)] private static partial int SecKeychainCopyDefault(out nint keychain);
    [LibraryImport(Security, StringMarshalling = StringMarshalling.Utf8)] private static partial int SecKeychainOpen(string path, out nint keychain);
    [LibraryImport(Security)] private static partial int SecKeychainGetStatus(nint keychain, out uint status);
    [LibraryImport(CoreFoundation)] private static unsafe partial nint CFDataCreate(nint allocator, byte* bytes, nint length);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataGetLength(nint data);
    [LibraryImport(CoreFoundation)] private static unsafe partial byte* CFDataGetBytePtr(nint data);
    [LibraryImport(CoreFoundation)] private static partial nuint CFDataGetTypeID();
    [LibraryImport(CoreFoundation)] private static partial nuint CFGetTypeID(nint value);
    [LibraryImport(CoreFoundation)] private static unsafe partial nint CFArrayCreate(nint allocator, nint* values, nint count, nint callbacks);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial nint objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial nint sel_registerName(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SendBool(nint receiver, nint selector, byte value);
}
