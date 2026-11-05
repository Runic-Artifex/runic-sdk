import {
  assertInInjectionContext, ChangeDetectorRef, computed, DestroyRef, inject, Injector, Pipe, PLATFORM_ID, signal,
  type OnDestroy, type PipeTransform, type Signal,
} from "@angular/core";
import {
  onWindowLocale, resolveText, subscribeText, textLocale, textVersion, windowLocale, type TextReference,
} from "@runic-artifex/views";

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

let pageTextLocale: Signal<string | undefined> | undefined;

/**
 * The locale that text is shown in (`textLocale`: the page's Translations locale, which follows the window's locale),
 * as a signal. It is one signal for the page and needs no injection context, so the page's locale resolver can read
 * it: a template that calls `m.*()` reads the signal through the resolver and renders again on a language switch.
 * `undefined` while the page has no Translations runtime and the application sets no window locale.
 *
 * ```ts
 * const textLocale = textLocaleSignal();
 * configureLocaleResolver(() => textLocale() ?? locale.getLocale());
 * ```
 */
export function textLocaleSignal(): Signal<string | undefined> {
  if (pageTextLocale) return pageTextLocale;
  const current = signal<string | undefined>(textLocale());
  subscribeText(() => current.set(textLocale()));
  return pageTextLocale = current.asReadonly();
}

/**
 * Shows a text reference in the window's language in a template, and marks the view for check when the language
 * changes: `{{ error.text | runicText: error.message }}`, `{{ state.status | runicText }}`. A string shows as it is;
 * `null` and `undefined` show the fallback argument. The pipe is impure so that it sees a language switch; it formats
 * again only when the value, the fallback or the language changed, and returns its last text otherwise.
 */
@Pipe({ name: "runicText", standalone: true, pure: false })
export class RunicTextPipe implements PipeTransform, OnDestroy {
  private readonly changes = inject(ChangeDetectorRef);
  private readonly stop = subscribeText(() => this.changes.markForCheck());

  private last: { value: unknown; fallback: string | undefined; version: number; text: string } | undefined;

  transform(value: TextReference | string | null | undefined, fallback?: string): string {
    const version = textVersion();
    const last = this.last;
    if (last && last.value === value && last.fallback === fallback && last.version === version) return last.text;
    const text = resolveText(value, fallback);
    this.last = { value, fallback, version, text };
    return text;
  }

  ngOnDestroy(): void {
    this.stop();
  }
}
