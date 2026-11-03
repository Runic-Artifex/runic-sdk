// blobs/1 (specs/application/README.md, "Blobs and uploads"): binary content that .NET serves at a URL, and files the
// page uploads to the host over HTTP. Neither crosses the Bridge as JSON: a blob is a URL with its type, size and
// name, and an upload is a reference that a command takes as its input.
import { BridgeError } from "./errors.js";
import { throwIfAborted } from "./lifetime.js";
import { sharedRuntimeFor } from "./runtime.js";
import { waitForBridge } from "./transport.js";
import { blobRef, type BlobGrant, type BlobRef } from "./blob-wire.js";

export { blobsFeature, type BlobGrant, type BlobRef, type RunicBlob } from "./blob-wire.js";

/** The progress of an {@link upload}. */
export interface UploadProgress {
  /** Bytes sent so far. */
  readonly loaded: number;
  /** Bytes to send. */
  readonly total: number;
}

/** Options of {@link upload}. */
export interface UploadOptions {
  /** Aborts the upload: the call rejects with `signal.reason`, and the host deletes what it received. */
  readonly signal?: AbortSignal | undefined;
  /** Called as the content is sent, at most once per browser progress event, and once with everything sent. */
  readonly onProgress?: ((progress: UploadProgress) => void) | undefined;
  /** The file name to report; defaults to the `File`'s name, or `upload` for another `Blob`. */
  readonly name?: string | undefined;
  /** The media type to report; defaults to the `Blob`'s type, or `application/octet-stream`. */
  readonly type?: string | undefined;
}

/**
 * One upload as a host bridge sends it itself ({@link HostUploads}). The runtime has checked the size limit.
 */
export interface HostUploadRequest {
  readonly url: string;
  readonly token: string;
  readonly content: Blob;
  readonly name: string;
  readonly type: string;
  readonly signal?: AbortSignal | undefined;
  readonly onProgress?: ((progress: UploadProgress) => void) | undefined;
}

/**
 * An optional member of the host bridge (`globalThis.__runicBridge`): a host that sends uploads itself, such as the
 * mock Bridge, implements it. It resolves with the HTTP status and the JSON text of the host's answer.
 */
export interface HostUploads {
  upload(request: HostUploadRequest): Promise<{ readonly status: number; readonly body: string }>;
}

/**
 * Uploads a file to the window's .NET host and resolves with its reference, which a command whose .NET parameter is a
 * `RunicUpload` takes. The content streams over HTTP, not over the Bridge, so the Bridge input limit does not apply;
 * the window's upload limit does.
 *
 * Rejects with a BridgeError: `unavailable` when the host takes no uploads, `rejected` with reason `too-large` when the
 * file is larger than the host's limit, or `capacity` when the window has no room for it, `disconnected` without a
 * Bridge connection, and `failed` when the request fails. Rejects with `signal.reason` when `signal` aborts.
 */
export async function upload(content: Blob, options: UploadOptions = {}): Promise<BlobRef> {
  const { signal } = options;
  throwIfAborted(signal);
  // The runtime of the installed host bridge, as generated clients use it.
  const runtime = sharedRuntimeFor(await waitForBridge({ signal }));
  const grant = await runtime.blobGrant?.();
  throwIfAborted(signal);
  if (grant === undefined) {
    throw new BridgeError("unavailable", "This host takes no uploads: it does not implement blobs/1, or it serves no " +
      "upload endpoint for this window.");
  }
  if (content.size > grant.maximumBytes) {
    throw new BridgeError("rejected", `The file is larger than the window's upload limit of ${grant.maximumBytes} bytes.`,
      { reason: "too-large" });
  }
  const name = options.name ?? (typeof (content as Partial<File>).name === "string" && (content as File).name !== ""
    ? (content as File).name : "upload");
  const type = options.type ?? (content.type === "" ? "application/octet-stream" : content.type);
  const request: HostUploadRequest = { url: grant.upload, token: grant.token, content, name, type, signal, onProgress: options.onProgress };
  const host = runtime.bridge as Partial<HostUploads>;
  const answer = typeof host.upload === "function" ? await host.upload(request) : await send(request);
  return readAnswer(answer.status, answer.body);
}

// The host's answer: {"ok":true,"upload":{…}} or {"ok":false,"error":{"kind","reason","message"}}.
function readAnswer(status: number, body: string): BlobRef {
  let reply: unknown;
  try { reply = JSON.parse(body); }
  catch { reply = undefined; }
  const value = reply as { ok?: unknown; upload?: unknown; error?: { kind?: unknown; reason?: unknown; message?: unknown } } | undefined;
  if (value?.ok === true) {
    try { return blobRef(value.upload); }
    catch (cause) { throw new BridgeError("failed", "The host's answer to the upload is not valid.", { cause }); }
  }
  const error = value?.error;
  const kind = error?.kind === "released" ? "released" : error?.kind === "rejected" || status === 413 || status === 429 || status === 507
    ? "rejected" : "failed";
  const reason = typeof error?.reason === "string" ? error.reason : status === 413 ? "too-large" : undefined;
  const message = typeof error?.message === "string" ? error.message : `The host answered the upload with HTTP ${status}.`;
  throw new BridgeError(kind, message, reason === undefined ? {} : { reason });
}

// The HTTP request: XMLHttpRequest, which reports upload progress; fetch where it is missing.
function send(request: HostUploadRequest): Promise<{ readonly status: number; readonly body: string }> {
  const headers: Record<string, string> = {
    "X-Runic-Upload-Token": request.token,
    "X-Runic-Upload-Name": encodeURIComponent(request.name),
    "Content-Type": request.type,
  };
  const Request = (globalThis as { XMLHttpRequest?: typeof XMLHttpRequest }).XMLHttpRequest;
  if (Request === undefined) {
    return fetch(request.url, { method: "POST", headers, body: request.content, credentials: "include", signal: request.signal ?? null })
      .then(async response => {
        request.onProgress?.({ loaded: request.content.size, total: request.content.size });
        return { status: response.status, body: await response.text() };
      }, (cause: unknown) => {
        if (request.signal?.aborted) throw request.signal.reason;
        throw new BridgeError("failed", "The upload request failed.", { cause });
      });
  }
  return new Promise((resolve, reject) => {
    const xhr = new Request();
    const abort = () => xhr.abort();
    xhr.open("POST", request.url);
    xhr.withCredentials = true;
    for (const [name, value] of Object.entries(headers)) xhr.setRequestHeader(name, value);
    xhr.upload.onprogress = event => {
      try { request.onProgress?.({ loaded: event.loaded, total: event.lengthComputable ? event.total : request.content.size }); }
      catch (error) { reportError(error); }
    };
    xhr.onload = () => {
      request.signal?.removeEventListener("abort", abort);
      if (xhr.status >= 200 && xhr.status < 300) {
        try { request.onProgress?.({ loaded: request.content.size, total: request.content.size }); }
        catch (error) { reportError(error); }
      }
      resolve({ status: xhr.status, body: xhr.responseText });
    };
    xhr.onerror = () => {
      request.signal?.removeEventListener("abort", abort);
      reject(new BridgeError("failed", "The upload request failed."));
    };
    xhr.onabort = () => {
      request.signal?.removeEventListener("abort", abort);
      reject(request.signal?.reason ?? new DOMException("The upload was aborted.", "AbortError"));
    };
    request.signal?.addEventListener("abort", abort, { once: true });
    xhr.send(request.content);
  });
}

function reportError(error: unknown): void {
  const report = (globalThis as { reportError?: (error: unknown) => void }).reportError;
  if (typeof report === "function") report(error); else console.error(error);
}
