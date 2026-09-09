# Manual checks for native changes

The first preview used a broader acceptance process. That process is retired for
future SDK releases; [release/README.md](release/README.md) is the current policy.
Historical results remain in the published release and Git history.

Use a brief manual session when a change affects native behavior that automated
checks cannot adequately cover. Choose the affected platform and scenario:

- File dialogs: open/save, dismiss and retry; verify edited data survives.
- Clipboard: copy/paste between the app and another application.
- Window lifecycle: close with unsaved work, keep editing, then discard/close.
- Focus/accessibility: keyboard navigation, focus return, or the specific screen
  reader, input method or scaling behavior changed by the implementation.

Note the platform and result in the PR or issue. Repeat affected checks after a
relevant fix. A translation parser or CLI change does not require repeating all
native demos. There is no candidate receipt format or routine manual release
sign-off. Do not claim untested platforms or accessibility behavior were tested.
