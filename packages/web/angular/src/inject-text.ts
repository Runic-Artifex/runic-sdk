import {
  assertInInjectionContext, ChangeDetectorRef, computed, DestroyRef, inject, Injector, Pipe, PLATFORM_ID, signal,
  type OnDestroy, type PipeTransform, type Signal,
} from "@angular/core";
import {
  onWindowLocale, resolveText, subscribeText, textLocale, textVersion, windowLocale, type FieldError, type RunicInstance,
  type TextReference,
} from "@runic-artifex/views";
import { contextInstance, instanceOf } from "./instance.js";

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
 * A field's `FieldError` shows the application's text for it (`configureFieldErrorText`), or its English message.
 * Under server rendering it does not observe a language switch. In templates, the `runicText` pipe does the same.
 *
 * ```ts
 * readonly status = injectText(() => this.view.state()?.status);
 * // <p>{{ status() }}</p>
 * ```
 */
export function injectText(
  source: () => TextReference | FieldError | string | null | undefined,
  fallback?: () => string | undefined,
  { injector: provided }: InjectTextOptions = {},
): Signal<string> {
  if (!provided) assertInInjectionContext(injectText);
  const injector = provided ?? inject(Injector);
  const instance = instanceOf(injector);
  const version = signal(0);
  if (injector.get(PLATFORM_ID, null) !== "server") {
    const stop = instance.run(() => subscribeText(() => version.update(value => value + 1)));
    injector.get(DestroyRef).onDestroy(stop);
  }
  return computed(() => {
    version();
    const value = source(), otherwise = fallback?.();
    return instance.run(() => resolveText(value, otherwise));
  });
}

/**
 * The window's locale that the application set, or `null` while it sets none, as a signal for the lifetime of the
 * injection context, for example to bind `lang`.
 */
export function injectWindowLocale({ injector: provided }: InjectTextOptions = {}): Signal<string | null> {
  if (!provided) assertInInjectionContext(injectWindowLocale);
  const injector = provided ?? inject(Injector);
  const instance = instanceOf(injector);
  const locale = signal<string | null>(instance.run(windowLocale));
  if (injector.get(PLATFORM_ID, null) !== "server") {
    const stop = instance.run(() => onWindowLocale(next => locale.set(next)));
    injector.get(DestroyRef).onDestroy(stop);
  }
  return locale.asReadonly();
}

const textLocales = new WeakMap<RunicInstance, Signal<string | undefined>>();

/**
 * The locale that text is shown in (`textLocale`: the instance's Translations locale, which follows the window's
 * locale), as a signal. It is one signal per Runic instance and needs no injection context, so the page's locale
 * resolver can read it: a template that calls `m.*()` reads the signal through the resolver and renders again on a
 * language switch. `undefined` while the instance has no Translations runtime and the application sets no window
 * locale. The instance is `instance`, else that of the injection context, else the page instance.
 *
 * ```ts
 * const textLocale = textLocaleSignal();
 * configureLocaleResolver(() => textLocale() ?? locale.getLocale());
 * ```
 */
export function textLocaleSignal(instance: RunicInstance = contextInstance()): Signal<string | undefined> {
  const existing = textLocales.get(instance);
  if (existing) return existing;
  const current = signal<string | undefined>(instance.run(textLocale));
  instance.run(() => subscribeText(() => current.set(instance.run(textLocale))));
  const locale = current.asReadonly();
  textLocales.set(instance, locale);
  return locale;
}

/**
 * Shows a text reference in the window's language in a template, and marks the view for check when the language
 * changes: `{{ error.text | runicText: error.message }}`, `{{ state.status | runicText }}`. A string shows as it is;
 * `null` and `undefined` show the fallback argument; a field's error, `{{ name.error() | runicText }}`, the application's
 * text for it (`configureFieldErrorText`). The pipe is impure so that it sees a language switch; it formats
 * again only when the value, the fallback or the language changed, and returns its last text otherwise.
 */
@Pipe({ name: "runicText", standalone: true, pure: false })
export class RunicTextPipe implements PipeTransform, OnDestroy {
  private readonly changes = inject(ChangeDetectorRef);
  private readonly instance = instanceOf(inject(Injector));
  private readonly stop = this.instance.run(() => subscribeText(() => this.changes.markForCheck()));

  private last: { value: unknown; fallback: string | undefined; version: number; text: string } | undefined;

  transform(value: TextReference | FieldError | string | null | undefined, fallback?: string): string {
    const version = this.instance.run(textVersion);
    const last = this.last;
    if (last && last.value === value && last.fallback === fallback && last.version === version) return last.text;
    const text = this.instance.run(() => resolveText(value, fallback));
    this.last = { value, fallback, version, text };
    return text;
  }

  ngOnDestroy(): void {
    this.stop();
  }
}
