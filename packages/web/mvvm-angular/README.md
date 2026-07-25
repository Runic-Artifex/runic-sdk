# @webuitoolkit/mvvm-angular

Angular signals and a standalone DataContext-style directive over the frozen
`@webuitoolkit/mvvm` projection contract.

`AngularMvvmStore` replaces one signal for each accepted protocol snapshot and
caches computed member signals. `AngularMvvmStoreDirective` can own that store
for exactly the host directive lifetime. Projection ownership is opt-in.

The supported peer range is Angular 20.3.26 through Angular 22.x. G6 verifies
both endpoints with the Angular compiler in production mode and executes the
shared browser fixtures in Chrome and Firefox.

The package is technically approved by G6 but remains private while ADR 0004
holds publication.
