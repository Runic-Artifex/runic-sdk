// The two ReactiveUI distributions get one Runic adapter API each (vrx-09, nrxr-03): the System.Reactive flavor
// of each adapter package declares the same public API as the default flavor, apart from its namespace and the
// ReactiveUI types that differ between the distributions.
import { test, expect } from 'bun:test';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { root } from './run.mjs';

// The declared public API after the pending removals: Shipped plus Unshipped, without *REMOVED* entries and the
// Shipped lines they name.
export function declaredApi(project) {
  const read = name => readFileSync(resolve(root, 'packages/dotnet', project, name), 'utf8').split(/\r?\n/)
    .map(line => line.trim()).filter(line => line && !line.startsWith('#'));
  const unshipped = read('PublicAPI.Unshipped.txt');
  const removed = new Set(unshipped.filter(line => line.startsWith('*REMOVED*')).map(line => line.slice('*REMOVED*'.length)));
  return [...read('PublicAPI.Shipped.txt'), ...unshipped].filter(line => !line.startsWith('*REMOVED*') && !removed.has(line));
}

// Maps a System.Reactive flavor line to the default flavor's spelling.
export function toDefaultFlavor(line) {
  return line
    .replaceAll('Runic.Application.Views.ReactiveUI.Reactive.', 'Runic.Application.Views.ReactiveUI.')
    .replaceAll('Runic.Navigation.ReactiveUI.Reactive.', 'Runic.Navigation.ReactiveUI.')
    .replaceAll('System.Reactive.Concurrency.IScheduler', 'ReactiveUI.Primitives.Concurrency.ISequencer')
    .replaceAll('System.Reactive.Unit', 'ReactiveUI.Primitives.RxVoid')
    .replaceAll('ReactiveUI.Primitives.Reactive.Disposables.ContainerDisposable', 'ReactiveUI.Primitives.Disposables.MultipleDisposable')
    .replaceAll('ReactiveUI.Binding.Reactive.', 'ReactiveUI.Binding.')
    .replaceAll('ReactiveUI.Reactive.', 'ReactiveUI.');
}

for (const project of ['Runic.Application.Views.ReactiveUI', 'Runic.Navigation.ReactiveUI']) {
  test(`${project}.Reactive declares the same API as ${project}`, () => {
    const primitives = declaredApi(project).sort();
    const reactive = declaredApi(`${project}.Reactive`).map(toDefaultFlavor).sort();
    expect(primitives.length).toBeGreaterThan(10);
    expect(reactive).toEqual(primitives);
  });
}

test('the flavor mapping changes only flavor spellings', () => {
  expect(toDefaultFlavor('static Runic.Navigation.ReactiveUI.Reactive.X.F(System.Reactive.Concurrency.IScheduler? s) -> ReactiveUI.Reactive.ReactiveCommand<System.Reactive.Unit, Runic.Navigation.NavigationOutcome!>!'))
    .toBe('static Runic.Navigation.ReactiveUI.X.F(ReactiveUI.Primitives.Concurrency.ISequencer? s) -> ReactiveUI.ReactiveCommand<ReactiveUI.Primitives.RxVoid, Runic.Navigation.NavigationOutcome!>!');
  expect(toDefaultFlavor('Runic.Navigation.NavigationArrival')).toBe('Runic.Navigation.NavigationArrival');
});

// I-004: the generated code's ReactiveUI flavor is inferred from the Runic ReactiveUI adapter the project compiles
// against, not set. The removed RunicApplicationFrontendReactiveUiFlavor property is reported as RUNICBRIDGE017.
test('the ReactiveUI flavor is inferred from the referenced adapter', () => {
  const views = readFileSync(resolve(root, 'packages/dotnet/Runic.Application.Views/build/Runic.Application.Views.targets'), 'utf8');
  expect(views).toMatch(/<_RunicBridgeReactiveUiReactiveAdapter Include="@\(ReferencePath\)" Condition="'%\(Filename\)' == 'Runic\.Application\.Views\.ReactiveUI\.Reactive'"/);
  expect(views).toMatch(/<_RunicApplicationFrontendRemovedName Include="RunicApplicationFrontendReactiveUiFlavor"/);
  expect(views).toContain('--reactiveui-flavor $(_RunicBridgeReactiveUiFlavor)');
  expect(views).not.toContain('--reactiveui-flavor &quot;$(RunicApplicationFrontendReactiveUiFlavor)');
  const reactive = readFileSync(resolve(root,
    'packages/dotnet/Runic.Application.Views.ReactiveUI.Reactive/buildTransitive/Runic.Application.Views.ReactiveUI.Reactive.targets'), 'utf8');
  expect(reactive).not.toContain('<RunicApplicationFrontendReactiveUiFlavor');
});
