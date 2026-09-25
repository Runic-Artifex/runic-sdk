# Svelte outlet source package

`ViewOutlet.svelte` renders a registered component for a generated View kind.
Its keyed block remounts a component when the canonical View reference changes;
the component's existing `pageState` helper owns the Bridge connection. The
`ViewRegistry` type checks each kind against the component's `page` prop.
Reactive Notes imports this source package for main and nested content.

This is source-package evidence, not a published Svelte library. It does not
yet own `pageState`, loading/retry UI, operation acceptance, lazy components,
or a native dialog policy.
