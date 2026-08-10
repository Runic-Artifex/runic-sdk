declare module "virtual:runic-translations/setup" {
  export interface MessageOptions {
    readonly locale?: "en" | "de" | string;
  }

  export function m$Application$Title(options?: MessageOptions): string;
  export function m$Application$Lead(options?: MessageOptions): string;
  export function m$Locale$English(options?: MessageOptions): string;
  export function m$Locale$German(options?: MessageOptions): string;
  export function m$Validation$Required(
    inputs: Readonly<{ field: string }>,
    options?: MessageOptions,
  ): string;
}

declare module "virtual:runic-translations/setup/runtime" {
  export type Locale = "en" | "de";
  export const catalog: "setup";
  export const contractFingerprint: `sha256:${string}`;
  export const locales: readonly Locale[];
  export function resolveLocale(requested: string): Locale;
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
