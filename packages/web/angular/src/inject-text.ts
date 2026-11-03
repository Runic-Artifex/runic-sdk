import {
  assertInInjectionContext, ChangeDetectorRef, computed, DestroyRef, inject, Injector, Pipe, PLATFORM_ID, signal,
  type OnDestroy, type PipeTransform, type Signal,
} from "@angular/core";
import { onWindowLocale, resolveText, subscribeText, windowLocale, type TextReference } from "@runic-artifex/views";

export interface InjectTextOptions {
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

/**
 * A text reference that a ViewModel produced, such as a status line or a validation message's `text`, in the window's
 * language (`resolveText`), as a signal that follows a language switch for the lifetime of the injection context.
 * `source` and `fallback` are read in a computed signal, so they may read other signals:
 * `injectText(() => view.state()?.status)`. A string shows as it is; `null` and `undefined` show `fallback`. Configure
 * the page's Translations runtime once with `configureTranslatedText`; without one, a reference shows its fallback text.
 * Under server rendering it does not observe a language switch. In templates, the `runicText` pipe does the same.
 *
 * ```ts
 * readonly status = injectText(() => this.view.state()?.status);
 * // <p>{{ status() }}</p>
 * ```
 */
export function injectText(
  source: () => TextReference | string | null | undefined,
  fallback?: () => string | undefined,
  { injector: provided }: InjectTextOptions = {},
): Signal<string> {
  if (!provided) assertInInjectionContext(injectText);
  const injector = provided ?? inject(Injector);
  const version = signal(0);
  if (injector.get(PLATFORM_ID, null) !== "server") {
    const stop = subscribeText(() => version.update(value => value + 1));
    injector.get(DestroyRef).onDestroy(stop);
  }
  return computed(() => {
    version();
    return resolveText(source(), fallback?.());
  });
}

/**
 * The window's locale that the application set, or `null` while it sets none, as a signal for the lifetime of the
 * injection context, for example to bind `lang`.
 */
export function injectWindowLocale({ injector: provided }: InjectTextOptions = {}): Signal<string | null> {
  if (!provided) assertInInjectionContext(injectWindowLocale);
  const injector = provided ?? inject(Injector);
  const locale = signal<string | null>(windowLocale());
  if (injector.get(PLATFORM_ID, null) !== "server") {
    const stop = onWindowLocale(next => locale.set(next));
    injector.get(DestroyRef).onDestroy(stop);
  }
  return locale.asReadonly();
}

/**
 * Shows a text reference in the window's language in a template, and marks the view for check when the language
 * changes: `{{ error.text | runicText: error.message }}`, `{{ state.status | runicText }}`. A string shows as it is;
 * `null` and `undefined` show the fallback argument.
 */
@Pipe({ name: "runicText", standalone: true, pure: false })
export class RunicTextPipe implements PipeTransform, OnDestroy {
  private readonly changes = inject(ChangeDetectorRef);
  private readonly stop = subscribeText(() => this.changes.markForCheck());

  transform(value: TextReference | string | null | undefined, fallback?: string): string {
    return resolveText(value, fallback);
  }

  ngOnDestroy(): void {
    this.stop();
  }
}
