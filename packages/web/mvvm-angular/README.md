# @runic-artifex/mvvm-angular

Angular signals and a standalone DataContext-style directive over the frozen
`@runic-artifex/mvvm` projection contract.

`AngularMvvmStore` replaces one signal for each accepted protocol snapshot and
caches computed member signals. `AngularMvvmStoreDirective` can own that store
for exactly the host directive lifetime. Projection ownership is opt-in.

Generated handles can be passed directly to the signal accessors:

```ts
readonly amount = this.mvvm.property(contract.amount);
readonly items = this.mvvm.collection(contract.items);
readonly submit = this.mvvm.command(contract.submit);
readonly amountErrors = this.mvvm.validation(contract.amount);
```

The resulting signals preserve the generated property and collection types.
Numeric member identifiers remain supported for dynamic scenarios, and
commands retain their typed `execute` method on the generated handle.
`commandFacade` adds signal-native result, error, cancellation, and transition
state. Generated `{Contract}ContractService` classes aggregate all member
signals, and `provide{Contract}Contract` makes each service injectable in a
standalone application.

The supported peer range is Angular 20.3.26 through Angular 22.x. G6 verifies
both endpoints with the Angular compiler in production mode and executes the
shared browser fixtures in Chrome and Firefox.

The package is technically approved by G6 but remains private while ADR 0004
holds publication.
