// Where a Runic Translations message is defined, for Runic Inspect's "open in editor" (#956). The open action never takes
// a path from the page for a message: it asks the Translations dev server's message API (`GET
// /__runic-translations/message`, vite-plugin-runic-translations 0.12 in project mode) for the message's locale, on the
// same development server, and opens the `.rmf2`/`.mf2` source that answer names. So it opens only a source the
// Translations catalog lists.

import { request as httpRequest } from "node:http";
import { request as httpsRequest } from "node:https";
import type { AddressInfo } from "node:net";
import type { RunicSourceLocation } from "./sources.js";

/** A message as Runic Inspect names it: its catalog, key and the locale whose text the page shows. */
export interface MessageReference {
  readonly catalog: string;
  readonly key: string;
  /** Empty when the page's Translations runtime did not tell it; the base locale is used then. */
  readonly locale: string;
}

/** The development server, as the request to its message API needs it. */
export interface TranslationsServer {
  readonly address: AddressInfo | string | null | undefined;
  readonly https: boolean;
  /** Vite's `base`, a path or a URL. */
  readonly base: string;
}

const maximumReplyBytes = 4 * 1024 * 1024;
const messagePath = "__runic-translations/message";

/** The `{ "message": { catalog, key, locale } }` of an open request, when it is well formed. */
export function parseMessageReference(value: unknown): MessageReference | undefined {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return undefined;
  const { catalog, key, locale } = value as Record<string, unknown>;
  if (typeof catalog !== "string" || !/^[a-z][a-z0-9.-]{0,127}$/u.test(catalog)) return undefined;
  if (typeof key !== "string" || !/^[A-Za-z_][A-Za-z0-9_.]{0,511}$/u.test(key)) return undefined;
  if (typeof locale !== "string" || !/^(?:[A-Za-z0-9]{1,8}(?:-[A-Za-z0-9]{1,8}){0,7})?$/u.test(locale)) return undefined;
  return { catalog, key, locale };
}

/**
 * The source of a message's locale in the message API's reply (`message.locales[].source`), when it is a `.rmf2` or
 * `.mf2` file. A message without a locale takes the base locale, which the reply lists first.
 */
export function messageLocation(reply: unknown, message: MessageReference): RunicSourceLocation | undefined {
  const locales = (reply as { message?: { locales?: unknown } } | null | undefined)?.message?.locales;
  if (!Array.isArray(locales)) return undefined;
  const entry = locales.find((item) => (item as { locale?: unknown } | null)?.locale === message.locale)
    ?? (message.locale === "" ? locales[0] : undefined);
  const source = (entry as { source?: unknown } | null | undefined)?.source as Record<string, unknown> | null | undefined;
  const { path, line, column } = source ?? {};
  if (typeof path !== "string" || !/\.(?:rmf2|mf2)$/iu.test(path) || !Number.isSafeInteger(line) || (line as number) < 1) return undefined;
  return { file: path, line: line as number, column: Number.isSafeInteger(column) && (column as number) > 0 ? column as number : 1 };
}

/** The message API's URL on this server: loopback, the server's port, below its base. */
export function messageUrl(server: TranslationsServer, message: MessageReference): URL | undefined {
  const address = server.address;
  if (address === null || address === undefined || typeof address === "string") return undefined;
  const host = address.family === "IPv6" || address.family as unknown === 6
    ? (address.address === "::" ? "127.0.0.1" : `[${address.address}]`)
    : (address.address === "0.0.0.0" ? "127.0.0.1" : address.address);
  let path = /^https?:/iu.test(server.base) ? new URL(server.base).pathname : server.base;
  if (!path.startsWith("/")) path = `/${path}`;
  if (!path.endsWith("/")) path += "/";
  const url = new URL(`${server.https ? "https" : "http"}://${host}:${address.port}${path}${messagePath}`);
  url.searchParams.set("key", message.key);
  url.searchParams.set("catalog", message.catalog);
  return url;
}

/**
 * Asks this development server's Translations message API where a message's locale is defined. Undefined when the API
 * is not there (an earlier plugin, manifest mode, `inPageEditing: false`), refuses, or names no `.rmf2`/`.mf2` source.
 */
export async function findMessageSource(server: TranslationsServer, message: MessageReference): Promise<RunicSourceLocation | undefined> {
  const url = messageUrl(server, message);
  if (url === undefined) return undefined;
  return messageLocation(await getJson(url), message);
}

// The request goes to this process's own listening socket. With HTTPS that is the dev server's own certificate, often
// self-signed, so it is not verified; the connection never leaves the machine.
function getJson(url: URL): Promise<unknown> {
  return new Promise((resolve) => {
    const https = url.protocol === "https:";
    const request = (https ? httpsRequest : httpRequest)(url, {
      method: "GET", headers: { Accept: "application/json" }, timeout: 15_000, ...(https ? { rejectUnauthorized: false } : {}),
    }, (response) => {
      const chunks: Buffer[] = [];
      let size = 0;
      response.on("data", (chunk: Buffer) => {
        size += chunk.byteLength;
        if (size > maximumReplyBytes) request.destroy();
        else chunks.push(chunk);
      });
      response.on("end", () => {
        if (response.statusCode !== 200) return resolve(undefined);
        try { resolve(JSON.parse(Buffer.concat(chunks).toString("utf8"))); } catch { resolve(undefined); }
      });
      response.on("error", () => resolve(undefined));
    });
    request.on("timeout", () => request.destroy());
    request.on("error", () => resolve(undefined));
    request.end();
  });
}
