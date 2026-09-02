declare module "virtual:runic-translations/setup" {
  export interface MessageOptions {
    readonly locale?: "en" | "de" | string;
  }

  export const m: Readonly<{
    application_title: (options?: MessageOptions) => string;
    application_lead: (options?: MessageOptions) => string;
    locale_english: (options?: MessageOptions) => string;
    locale_german: (options?: MessageOptions) => string;
    validation_required: (
      inputs: Readonly<{ field: string }>,
      options?: MessageOptions,
    ) => string;
  }>;
}

declare module "virtual:runic-translations/setup/runtime" {
  export type Locale = "en" | "de";
  export const catalog: "setup";
  export const contractFingerprint: `sha256:${string}`;
  export const locales: readonly Locale[];
  export const baseLocale: Locale;
  export function resolveLocale(requested: string): Locale;
}

declare module "virtual:runic-translations/setup/server" {
  import type { Locale } from "virtual:runic-translations/setup/runtime";
  export function runWithLocale<T>(locale: Locale | string, operation: () => T): T;
  export function getRequestLocale(): Locale;
}

declare module "virtual:runic-translations/setup/transport" {
  export interface DecodedTextReference {
    readonly version: 1;
    readonly catalog: "setup";
    readonly contractFingerprint: string;
    readonly key: string;
    readonly arguments: Readonly<Record<string, unknown>>;
    readonly fallbackText?: string;
  }

  export type DecodeResult =
    | { readonly ok: true; readonly value: DecodedTextReference }
    | { readonly ok: false; readonly reason?: string };

  export function decodeTextReference(value: unknown): DecodeResult;
  export function formatTextReference(
    reference: DecodedTextReference,
    handlers: Readonly<Record<string, (inputs: Readonly<Record<string, unknown>>, options?: { locale?: string }) => string>>,
    options?: { locale?: string },
  ): string;
}

declare module "virtual:runic-translations/setup/dynamic" {
  export interface LocaleArtifactV2 {
    readonly artifactVersion: 2;
    readonly messageGrammarVersion: 2;
    readonly catalog: "setup";
    readonly locale: string;
    readonly contractFingerprint: string;
    readonly messages: Readonly<Record<string, unknown>>;
  }

  export type LocaleArtifactDecodeResult =
    | { readonly ok: true; readonly value: LocaleArtifactV2 }
    | { readonly ok: false; readonly reason: string };

  export function decodeLocaleArtifact(value: unknown): LocaleArtifactDecodeResult;
  export function formatDynamicMessage(
    artifact: LocaleArtifactV2,
    key: string,
    inputs?: Readonly<Record<string, unknown>>,
    options?: { locale?: string },
  ): string;
}
