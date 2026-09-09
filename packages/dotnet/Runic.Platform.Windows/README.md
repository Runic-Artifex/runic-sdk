# Runic.Platform.Windows

Explicit Windows x64 file dialog and Unicode text clipboard provider for Runic SDK
`0.2.0-preview.1`. Select `WindowsPlatformProvider.CreateFileDialogs(owner)` and
`CreateTextClipboard(owner)` with the presentation's verified native owner.
The owner dispatches file dialogs on the window's STA and drains native callbacks
before disposal. This package does not discover providers or depend on Desktop.

File dialogs require an owner, use filesystem-only Common Item Dialogs and retain
the shared runtime's atomic replacement policy. Clipboard reads distinguish absent
text (`null`) from empty text, reject over-limit payloads and return typed busy,
permission and I/O failures. Text containing NUL is invalid. Cancellation before
native execution prevents the operation; after execution starts, the actual write
outcome is returned. Win32 can clear the previous clipboard before a subsequent
write fails; failures must not be interpreted as preservation of previous text.

Native interop uses static imports and raw COM vtables for NativeAOT. Windows JIT,
NativeAOT and actual interactive selection acceptance are release gates; portable
conformance alone does not certify the Windows environment.

## Desktop services

Application-scoped appearance and notification providers, and owned file opening,
application choice and reveal, are described in the
[desktop services guide](../../../docs/guides/desktop-services.md). It includes
composition, native API choices, installation/activation requirements, retained
file access and the per-platform verification status.
