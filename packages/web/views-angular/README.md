# Angular outlet source package

The headless `RunicViewOutlet` renders a generated View reference through a
statically typed `ViewRegistry`. A changed reference remounts the selected
component, so its existing `pageSignal` connection and cleanup run at the
correct View lifetime. Missing kinds render an error. Reactive Notes imports
this source package directly and tests main and nested outlets.

This is source-package evidence, not a published Angular library. It does not
yet own `pageSignal`, loading/retry UI, operation acceptance, lazy components,
or a native dialog policy.
