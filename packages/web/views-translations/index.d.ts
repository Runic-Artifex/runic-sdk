import type { FieldError } from "@runic-artifex/views";
import type { m } from "./dist/translations/runic-views.esm-v5/messages.js";
import type * as transport from "./dist/translations/runic-views.esm-v5/transport.js";

/** The runic-views messages, as the generated `m` of a catalog. */
export declare const runicViewsMessages: typeof m;

/** The runic-views catalog's generated transport module. */
export declare const runicViewsTransport: typeof transport;

/**
 * The catalog as an entry of a generated transport's `createReferenceResolver`, so the page resolves the validation texts
 * of Runic.Application.Views.Translations together with the application's references:
 * `createReferenceResolver([{ transport, messages: m }, runicViewsCatalog])`.
 */
export declare const runicViewsCatalog: Readonly<{ transport: typeof transport; messages: typeof m }>;

/**
 * Formats a `runic-views` text reference, such as a validation error's `text`, in `locale` (the page's when omitted);
 * `undefined` for a reference of another catalog, or one that does not match this catalog.
 */
export declare function resolveRunicViewsText(reference: unknown, locale?: string): string | undefined;

/**
 * The runic-views text of a field error in `locale`, as `configureFieldErrorText` takes it: for an `invalid-text` error
 * with a reason Runic's parsers report; `undefined` for any other error, so it shows its English message. Lists (the
 * choices, the date and time forms) are joined in the locale.
 */
export declare function runicViewsFieldErrorText(error: FieldError, locale: string | undefined): string | undefined;

/**
 * Shows field errors (`FieldState.error`, `resolveText`, `describeBridgeError`) with the runic-views text in the window's
 * language: `configureFieldErrorText(runicViewsFieldErrorText)`. Returns a function that removes it.
 */
export declare function configureRunicViewsText(): () => void;
