# Secret storage

An application keeps a secret, such as an access or refresh token, in the
user's credential store instead of a file, and reads, replaces and removes it
by service and account, with typed outcomes for a locked or missing store
(planning item W315-008). `IDesktopSecretStore` in Runic.Platform is the
contract; `AddRunicPlatformServices()` registers the provider for the operating
system.

## Delivery matrix — 2026-10-17

| Platform | Delivery state | Validation |
| --- | --- | --- |
| Linux | Implemented: the Secret Service API (`org.freedesktop.secrets`) over D-Bus in Runic.Platform.Linux.Portal, with the `dh-ietf1024-sha256-aes128-cbc-pkcs7` session and a `plain` fallback. | Tests against a fake service on a private bus for every outcome (encrypted and plain sessions, locked items and collections, prompt completed, dismissed, unshowable and cancelled, no default collection, no service). CI-verified against GNOME Keyring 46.1 and libsecret 0.21.4 on Ubuntu 24.04, JIT and NativeAOT: the encrypted session is required, `secret-tool` reads the stored item, and a locked keyring without a prompter reports `UserDismissed` (GNOME Keyring completes the prompt as dismissed). KeePassXC and KWallet are unverified. |
| Windows | Implemented: Credential Manager generic credentials (`CredWriteW`, `CredReadW`, `CredDeleteW`) in Runic.Platform.Windows. | Portable tests for target names and error mapping. CI-verified on the windows-latest runner's vault, JIT and NativeAOT. |
| macOS | Implemented: generic passwords in the default keychain (`SecItemAdd`, `SecItemCopyMatching`, `SecItemUpdate`, `SecItemDelete`) in Runic.Platform.MacOS. | Portable tests for status mapping. CI-verified on the macos-26 runner's login keychain, JIT and NativeAOT. The locked-keychain, unlock and access prompts are unverified (manual checks). |
| Sandboxed Linux | Implemented where the sandbox allows the Secret Service: Flatpak `--talk-name=org.freedesktop.secrets`, the Snap `password-manager-service` interface. Otherwise unavailable with the `secret-service-unavailable` diagnostic. | Deferred to 1.x: the Secret portal (`org.freedesktop.portal.Secret`) with a local encrypted file, for sandboxes without Secret Service access. |

## Contract

```csharp
public interface IDesktopSecretStore
{
    ValueTask<PlatformResult<string?>> GetAsync(string service, string account, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<PlatformUnit>> SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<bool>> DeleteAsync(string service, string account, CancellationToken cancellationToken = default);
}
```

- Services and accounts are 1 to 256 characters of valid Unicode text without
  NUL; anything else is an `ArgumentException` before any native call.
- Secrets are text, stored as UTF-8: the form `secret-tool`, keytar, Keychain
  Access and Git Credential Manager read. A stored value that is not UTF-8 is
  `Failed(InvalidData)`. An empty secret is a secret, distinct from none.
- Secrets are at most 2560 UTF-8 bytes on every platform, the Credential
  Manager limit (`CRED_MAX_CREDENTIAL_BLOB_SIZE`). A longer one is
  `Failed(TooLarge)` and nothing is stored, so a limit hit on Windows shows on
  the developer's Linux or macOS machine too.
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
  session and the `secret-service-plain-session` diagnostic. Key material and
  plaintext buffers are cleared after use.
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
user on this computer, across logons, not roamed. The calls do not prompt.

## macOS

A generic password (`kSecClassGenericPassword`) in the default keychain with
`kSecAttrService`, `kSecAttrAccount` and the label set to the service.
`SetAsync` updates an existing item, keeping the access the user granted it,
and adds one otherwise. The Keychain may ask the user to unlock it or to allow
this application; the calls run on the thread pool and wait for the answer,
which cancellation does not interrupt. The data-protection keychain
(`kSecUseDataProtectionKeychain`) needs a signed bundle with a
`keychain-access-groups` entitlement and is not used.

## Out of scope for 1.0

- The Secret portal and a local encrypted file for sandboxes without Secret
  Service access.
- Listing accounts, attributes beyond service and account, binary secrets and
  secrets above 2560 bytes.
- A non-interactive mode that fails instead of prompting, and parenting
  prompts to a window.
- The macOS data-protection keychain, iCloud Keychain synchronisation and
  access groups; Windows enterprise roaming and DPAPI files.
