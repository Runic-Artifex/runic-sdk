# WebUIToolkit.Desktop public API

The package exposes:

- immutable capability descriptors and a complete host/platform report;
- window, focus, and UI-dispatch services;
- text clipboard, bounded file-dialog, and drag/drop contracts;
- external-URI and notification services; and
- immutable browser profile/storage policy;
- guarded application lifetime and stopping cancellation; and
- deterministic application-owned secondary windows.

The contracts are frontend-neutral and Native-AOT-safe. They do not expose WPF,
CsWebUi, browser, DOM, or operating-system handle types.
