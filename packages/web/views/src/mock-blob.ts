// A fake blobs/1 value for frontend tests and mocks: a `RunicBlob` whose URL the page can load.
import type { RunicBlob } from "./blob-wire.js";

/** Options of {@link mockBlob}. */
export interface MockBlobOptions {
  /** The media type; defaults to the Blob's type, or `application/octet-stream`. */
  readonly type?: string;
  /** The file name, or `null` (the default). */
  readonly name?: string | null;
}

/**
 * A `RunicBlob` for a mock's state or a command result, as .NET's `RunicBlob` arrives on the page. Its URL is an
 * object URL of the content (`URL.createObjectURL`), or a `data:` URL where object URLs are missing, so an
 * `<img src>` or `fetch` in a test loads the content.
 */
export function mockBlob(content: Blob | string | Uint8Array, options: MockBlobOptions = {}): RunicBlob {
  const type = options.type ?? (content instanceof Blob && content.type !== "" ? content.type : "application/octet-stream");
  const bytes = typeof content === "string" ? new TextEncoder().encode(content) : content;
  const blob = bytes instanceof Blob ? bytes : new Blob([bytes as Uint8Array<ArrayBuffer>], { type });
  const create = (globalThis as { URL?: { createObjectURL?: (blob: Blob) => string } }).URL?.createObjectURL;
  const url = typeof create === "function" ? create(blob) : dataUrl(bytes instanceof Blob ? undefined : bytes, type);
  return { url, type, size: blob.size, name: options.name ?? null };
}

function dataUrl(bytes: Uint8Array | undefined, type: string): string {
  if (bytes === undefined) return `data:${type},`;
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return `data:${type};base64,${btoa(binary)}`;
}
