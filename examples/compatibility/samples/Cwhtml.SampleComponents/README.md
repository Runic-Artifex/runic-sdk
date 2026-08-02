# Compiled HTML sample components

This sample-only project demonstrates reusable components composed into
`.cwhtml` through `@render(...)`. It deliberately is not a runtime package.

`BootstrapFormGroup`, `BootstrapValidationMessage`, `BootstrapNavigation`,
`BootstrapModal`, and `BootstrapToast` encode the Bootstrap 5.3 conventions
used by repository samples. Applications still own their Bootstrap and Font
Awesome npm dependencies; this project references neither package and the core
WebUIToolkit runtime does not depend on them.

`AccessibleStatusRegion` is styling-neutral. It emits semantic HTML and ARIA
attributes without Bootstrap, Font Awesome, JavaScript, or framework classes.
The component test project composes it from a compiled `.cwhtml` view with
deliberately non-Bootstrap class names.

`StatusBadge` is authored in `.cwuix` and consumed by `SimpleTodo` across the
project boundary. It proves that stable `WebUIToolkit.CsharpMarkup.HtmlContent`
values can flow through reusable libraries.
