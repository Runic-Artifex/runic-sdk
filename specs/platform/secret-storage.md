# Secret storage

An application keeps a secret, such as an access or refresh token, in the
user's credential store instead of a file, and reads, replaces and removes it
by service and account, with typed outcomes for a locked or missing store
(planning item W315-008). `IDesktopSecretStore` in Runic.Platform is the
contract; `AddRunicPlatformServices()` registers the provider for the operating
system.

## Delivery matrix — 2026-10-23

| Platform | Delivery state | Validation |
| --- | --- | --- |
| Linux | Implemented: the Secret Service API (`org.freedesktop.secrets`) over D-Bus in Runic.Platform.Linux.Portal, with the `dh-ietf1024-sha256-aes128-cbc-pkcs7` session and a `plain` fallback. | Tests against a fake service on a private bus for every outcome (encrypted and plain sessions, locked items and collections, prompt completed, dismissed, unshowable and cancelled, no default collection, no service). CI-verified against GNOME Keyring 46.1 and libsecret 0.21.4 on Ubuntu 24.04, JIT and NativeAOT: the encrypted session is required, `secret-tool` reads the stored item, and a locked keyring without a prompter reports `UserDismissed` (GNOME Keyring completes the prompt as dismissed). KeePassXC and KWallet are unverified. |
| Windows | Implemented: Credential Manager generic credentials (`CredWriteW`, `CredReadW`, `CredDeleteW`) in Runic.Platform.Windows; binary secrets above 2560 bytes in a DPAPI-protected file referenced from the credential. | Portable tests for target names, error mapping and the file reference against a fake vault (round trip, replace, damaged and missing file, delete). CI-verified on the windows-latest runner's vault and DPAPI, JIT and NativeAOT: 2560-byte blobs, 64 KiB files, a damaged file, delete removing both. |
| macOS | Implemented: generic passwords in the default keychain (`SecItemAdd`, `SecItemCopyMatching`, `SecItemUpdate`, `SecItemDelete`) in Runic.Platform.MacOS; the data-protection keychain with an option. | Portable tests for status mapping. CI-verified on the macos-26 runner, JIT and NativeAOT: the login keychain; 64 KiB binary secrets and `Locked` without user interaction on a locked temporary keychain; `PermissionDenied` (-34018) from the data-protection keychain for the unsigned test host. Unlock and access prompts and the data-protection keychain from a team-signed bundle are unverified (manual checks). |
| Sandboxed Linux | Implemented where the sandbox allows the Secret Service: Flatpak `--talk-name=org.freedesktop.secrets`, the Snap `password-manager-service` interface. Otherwise unavailable with the `secret-service-unavailable` diagnostic. | Deferred to 1.x: the Secret portal (`org.freedesktop.portal.Secret`) with a local encrypted file, for sandboxes without Secret Service access. |

## Contract

```csharp
public interface IDesktopSecretStore
{
    ValueTask<PlatformResult<string?>> GetAsync(string service, string account, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<PlatformUnit>> SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<bool>> DeleteAsync(string service, string account, CancellationToken cancellationToken = default);
    // Default implementations: argument checks, then Unavailable(BackendUnavailable).
    ValueTask<PlatformResult<byte[]?>> GetBytesAsync(string service, string account, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<PlatformUnit>> SetBytesAsync(string service, string account, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default);
}

public sealed record DesktopSecretStoreOptions
{
    public bool RequireEncryption { get; init; }             // Linux Secret Service: no plain session
    public bool AllowUserInteraction { get; init; } = true;  // false: Failed(Locked) instead of a prompt
    public bool UseDataProtectionKeychain { get; init; }     // macOS only
}
```

Each provider has a `CreateSecretStore(DesktopSecretStoreOptions)` overload, and
`DesktopPlatformServices.CreateSecretStore(applicationId, options)` picks the
one for the operating system. A store holds no native resources between
requests, so an application creates one per set of options.

- Services and accounts are 1 to 256 characters of valid Unicode text without
  NUL; anything else is an `ArgumentException` before any native call.
- Secrets are text, stored as UTF-8: the form `secret-tool`, keytar, Keychain
  Access and Git Credential Manager read. A stored value that is not UTF-8 is
  `Failed(InvalidData)`. An empty secret is a secret, distinct from none.
- Text secrets are at most 2560 UTF-8 bytes on every platform, the Credential
  Manager limit (`CRED_MAX_CREDENTIAL_BLOB_SIZE`). Binary secrets are at most
  65 536 bytes (`DesktopSecretStore.MaximumBinarySecretBytes`) on every
  platform. A longer one is `Failed(TooLarge)` and nothing is stored, so a limit
  hit on one platform shows on the developer's machine too.
- A service and account hold one item, text or binary. `GetAsync` returns a
  binary item that is UTF-8 and is `Failed(InvalidData)` otherwise;
  `GetBytesAsync` returns a text item's UTF-8 bytes. Returned arrays belong to
  the caller; the store clears its own buffers.
- `AllowUserInteraction = false` makes a request that would show an unlock or
  access prompt `Failed(Locked)` instead. It is best effort where the
  platform has no such flag (the Linux Secret portal).
- `GetAsync` returns `Success(null)` when nothing is stored; `DeleteAsync`
  returns whether it removed something.
- The store needs no window. `DesktopSecretStore.Unavailable(reason)` serves
  hosts without a provider; CS-WebUI registers it with `ProviderNotConfigured`.

### Outcomes

| Situation | Result | Linux | Windows | macOS |
| --- | --- | --- | --- | --- |
| No store reachable | `Unavailable(BackendUnavailable)` | No owner and no activatable service for `org.freedesktop.secrets`, including a sandbox that hides it | `ERROR_NO_SUCH_LOGON_SESSION` (no vault in a service or network logon) | `errSecNoSuchKeychain`, `errSecNotAvailable`, `errSecNoDefaultKeychain`, `errSecInvalidKeychain` |
| Locked, and the store cannot ask the user | `Failed(Locked)` | `Prompt` fails (no prompter), or `org.freedesktop.Secret.Error.IsLocked` after `Unlock` | — (no locked state) | `errSecInteractionNotAllowed` |
| The user dismissed an unlock, access or create prompt | `Failed(UserDismissed)` | `Prompt.Completed` with `dismissed`; GNOME Keyring also completes a prompt it cannot show (no prompter, as in a headless session) this way | `ERROR_CANCELLED` | `errSecUserCanceled` |
| Access refused | `Failed(PermissionDenied)` | `org.freedesktop.DBus.Error.AccessDenied` | `ERROR_ACCESS_DENIED` | `errSecAuthFailed`, `errSecReadOnly`, `errSecWrPerm`, `errSecMissingEntitlement` |
| Other native errors | `Failed(Other)` with the native code | D-Bus error name | Win32 code | `OSStatus` |

Diagnostics carry the native error name or code only: never the secret, the
service or the account.

## Linux

The Secret Service API, as libsecret uses it:

- Each request opens its own connection, starts the service if it is
  activatable, and opens a session; it closes both when done, so a restarted
  service needs no recovery. A service replaced during a request ends it with
  `Unavailable(BackendUnavailable)`.
- The session is `dh-ietf1024-sha256-aes128-cbc-pkcs7`: Diffie-Hellman over the
  1024-bit MODP group of RFC 2409, HKDF-SHA256 without salt or info to a
  128-bit key, AES-128-CBC with PKCS#7 padding and a random IV per secret. A
  service that answers `org.freedesktop.DBus.Error.NotSupported` gets a `plain`
  session and the `secret-service-plain-session` diagnostic, unless the store
  was created with `RequireEncryption`: then the request is `Failed(Other)` with
  that D-Bus error name and the `secret-service-encryption-required`
  diagnostic. It is opt-in (D037.10): a plain session is visible only to
  processes of the same user, which can read the keyring anyway, and libsecret
  falls back the same way. Key material and plaintext buffers are cleared after
  use.
- Items carry libsecret's generic schema: attributes
  `xdg:schema=org.freedesktop.Secret.Generic`, `service` and `account`, and the
  label `<service> (<account>)`, which Seahorse, KeePassXC and KWallet show.
  Reads and deletes search by `service` and `account` only, so they also find
  items `secret-tool store` wrote without the schema attribute.
- `SetAsync` stores into the collection behind the `default` alias with
  `CreateItem(replace: true)`. Without that alias it calls
  `CreateCollection(alias: "default")`, whose prompt asks the user, as libsecret
  does; reads and deletes report nothing stored instead.
- A locked item or collection is unlocked with `Unlock` and its prompt, shown
  without a parent window (`window-id` empty). Cancellation calls
  `Prompt.Dismiss`. `DeleteAsync` removes every matching item.

## Windows

A generic credential (`CRED_TYPE_GENERIC`) whose target is
`<service>/<account>`, with `%` and `/` in the service written as `%25` and
`%2F` so no two pairs share a target, whose user name is the account and whose
blob is the UTF-8 secret. It persists with `CRED_PERSIST_LOCAL_MACHINE`: this
user on this computer, across logons, not roamed. The calls do not prompt, so
`AllowUserInteraction` has no effect, and nothing crosses a process boundary in
the clear, so neither has `RequireEncryption`.

A binary secret of up to 2560 bytes is the blob as is, unless it starts with
the reference marker. A larger one, or one with the marker, is written to
`%LOCALAPPDATA%\Runic\Secrets\<id>.bin`: a temporary file renamed into place,
holding `CryptProtectData` output for the current user
(`CRYPTPROTECT_UI_FORBIDDEN`, the target's UTF-8 as entropy). The blob then is
the reference: the byte `0xFF` (never in UTF-8, so no text secret looks like
one), `runic-secret-file`, a version byte `1`, the file's 16-byte random id and
the SHA-256 of the protected file. A missing, replaced or damaged file, or one
that does not decrypt for this target, is `Failed(InvalidData)`. A write
removes the file the credential referred to before; `DeleteAsync` removes the
credential, then its file. Concurrent writers may leave an unreferenced file
behind, never a reference without its file.

## macOS

A generic password (`kSecClassGenericPassword`) in the default keychain with
`kSecAttrService`, `kSecAttrAccount` and the label set to the service.
`SetAsync` updates an existing item, keeping the access the user granted it,
and adds one otherwise. The Keychain may ask the user to unlock it or to allow
this application; the calls run on the thread pool and wait for the answer,
which cancellation does not interrupt. Binary secrets are the item's data as
is.

- `AllowUserInteraction = false`: before each query the store checks the
  default keychain's lock state (`SecKeychainGetStatus`) and reports a locked
  one as `Failed(Locked)` with `errSecInteractionNotAllowed`; the query itself
  carries `kSecUseAuthenticationUI = kSecUseAuthenticationUIFail`, so an access
  prompt fails the same way. With the data-protection keychain the query
  carries an `LAContext` whose `interactionNotAllowed` is set instead.
- `UseDataProtectionKeychain = true`: every query carries
  `kSecUseDataProtectionKeychain`. The keychain needs a bundle signed with a
  team ID and the `keychain-access-groups` entitlement; without them the
  request is `Failed(PermissionDenied)` with `errSecMissingEntitlement` (-34018)
  and a diagnostic message naming the entitlement. Items of the file-based
  keychain are not migrated: read them with a store without the option and
  write them with one that has it. CI covers the missing-entitlement path; the
  success path needs a team-signed bundle (manual check).

## Out of scope for 1.0

- The Secret portal and a local encrypted file for sandboxes without Secret
  Service access.
- Listing accounts, attributes beyond service and account, and secrets above
  64 KiB.
- Parenting prompts to a window.
- iCloud Keychain synchronisation and shared access groups; Windows enterprise
  roaming.
