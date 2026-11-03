// The blobs/1 wire values (specs/application/values.md, "Blobs and uploads") and the hello reply's upload grant. No
// runtime imports: generated codecs reach these through `@runic-artifex/views/generated/wire`.

/** The feature name of blobs and uploads. */
export const blobsFeature = "blobs/1";

/**
 * Binary content that a ViewModel exposes: a .NET `RunicBlob`. Use `url` directly, as `<img src>`, `<a href download>`
 * or with `fetch`; the host serves it with its type and file name. The URL works on this window only, and stops
 * working when the ViewModel replaces the blob, when its View is released or when the window closes.
 */
export interface RunicBlob {
  /** The URL the host serves the content at; empty when the host serves no blobs. */
  readonly url: string;
  /** The media type, such as `image/png`. */
  readonly type: string;
  /** The length in bytes, or `null` when .NET knows it only when it serves the content. */
  readonly size: number | null;
  /** The file name a download saves the content as, or `null`. */
  readonly name: string | null;
}

/**
 * A file the page uploaded with `upload()`: pass it to a command whose .NET parameter is a `RunicUpload`. It is
 * valid on the connection that uploaded it until a command claims it, for at most 10 minutes.
 */
export interface BlobRef {
  /** The upload's id. */
  readonly id: string;
  /** The file name the host stored, without directories. */
  readonly name: string;
  /** The media type the page reported, or `application/octet-stream`. */
  readonly type: string;
  /** The number of bytes the host received. */
  readonly size: number;
}

/** The upload grant of a connection that uses blobs/1: the `blobs` member of the producer's `hello` reply. */
export interface BlobGrant {
  readonly upload: string;
  readonly token: string;
  readonly maximumBytes: number;
}

/** Reads a grant from a hello reply's `blobs` member; undefined when it is missing or malformed. */
export function readBlobGrant(value: unknown): BlobGrant | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return undefined;
  const { upload: url, token, maximumBytes } = value as Record<string, unknown>;
  if (typeof url !== "string" || typeof token !== "string" || typeof maximumBytes !== "number" ||
    !Number.isSafeInteger(maximumBytes) || maximumBytes < 0) return undefined;
  return { upload: url, token, maximumBytes };
}

function record(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new TypeError("Expected an object.");
  return value as Record<string, unknown>;
}

function size(value: unknown): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) throw new RangeError("Expected a size in bytes.");
  return value;
}

/** `blob`: `{url, type, size, name}`; `size` and `name` may be null. Other members are ignored. */
export function blob(value: unknown): RunicBlob {
  const { url, type, size: length, name } = record(value);
  if (typeof url !== "string" || typeof type !== "string") throw new TypeError("Expected a blob.");
  if (name !== null && name !== undefined && typeof name !== "string") throw new TypeError("Expected a blob name.");
  return { url, type, size: length === null || length === undefined ? null : size(length), name: typeof name === "string" ? name : null };
}

/** `upload`: `{id, name, type, size}`. Other members are ignored. */
export function blobRef(value: unknown): BlobRef {
  const { id, name, type, size: length } = record(value);
  if (typeof id !== "string" || id === "") throw new TypeError("Expected an upload id.");
  return { id, name: typeof name === "string" ? name : "", type: typeof type === "string" ? type : "", size: length === undefined ? 0 : size(length) };
}

/** The wire form of a blob the page sends back, such as through a mock: its members as they are. */
export function encodeBlob(value: RunicBlob): Record<string, unknown> {
  return { url: value.url, type: value.type, size: value.size, name: value.name };
}

/** The wire form of an upload reference: `{id, name, type, size}`. */
export function encodeBlobRef(value: BlobRef): Record<string, unknown> {
  return { id: value.id, name: value.name, type: value.type, size: value.size };
}
