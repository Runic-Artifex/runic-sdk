const DEFAULT_MAX_FRAME_BYTES = 1_048_576;

/**
 * CsWebUi implementation of the `FrameChannel` contract from `@webuitoolkit/mvvm`.
 *
 * The host installs one binary binding. Host frames are pushed into one global receiver
 * with `WebUiEvent.SendRaw`; ViewModel commands never become native WebUI bindings.
 */
export class CsWebUiFrameChannel {
  #bindingName;
  #receiveFunctionName;
  #maxFrameBytes;
  #observer;
  #closed = false;
  #previousReceiver;

  constructor(options = {}) {
    this.#bindingName = options.bindingName ?? "__webuitoolkit_mvvm_send";
    this.#receiveFunctionName =
      options.receiveFunctionName ?? "__webuitoolkit_mvvm_receive";
    this.#maxFrameBytes = options.maxFrameBytes ?? DEFAULT_MAX_FRAME_BYTES;

    assertIdentifier(this.#bindingName, "bindingName");
    assertIdentifier(this.#receiveFunctionName, "receiveFunctionName");
    if (
      !Number.isSafeInteger(this.#maxFrameBytes) ||
      this.#maxFrameBytes < 1 ||
      this.#maxFrameBytes > DEFAULT_MAX_FRAME_BYTES
    ) {
      throw new RangeError("maxFrameBytes is outside the protocol v1 bounds.");
    }

    this.#previousReceiver = globalThis[this.#receiveFunctionName];
    if (this.#previousReceiver !== undefined) {
      throw new Error(
        `The CsWebUi MVVM receiver '${this.#receiveFunctionName}' is already installed.`,
      );
    }

    globalThis[this.#receiveFunctionName] = (value) => {
      if (this.#closed) return;
      try {
        const frame = asFrame(value);
        if (frame.byteLength > this.#maxFrameBytes) {
          throw new RangeError("A CsWebUi host frame exceeds the configured limit.");
        }
        this.#observer?.frame(frame.slice());
      } catch (cause) {
        this.#observer?.error(cause);
      }
    };
  }

  async send(frame) {
    if (this.#closed) throw new Error("The CsWebUi MVVM channel is closed.");
    if (!(frame instanceof Uint8Array)) {
      throw new TypeError("CsWebUi MVVM frames must be Uint8Array values.");
    }
    if (frame.byteLength > this.#maxFrameBytes) {
      throw new RangeError("A CsWebUi client frame exceeds the configured limit.");
    }

    const binding = globalThis[this.#bindingName];
    if (typeof binding !== "function") {
      throw new Error(`The CsWebUi binding '${this.#bindingName}' is unavailable.`);
    }
    await binding(frame);
  }

  subscribe(observer) {
    if (
      observer === null ||
      typeof observer !== "object" ||
      typeof observer.frame !== "function" ||
      typeof observer.close !== "function" ||
      typeof observer.error !== "function"
    ) {
      throw new TypeError("A complete FrameChannel observer is required.");
    }
    if (this.#observer !== undefined) {
      throw new Error("CsWebUiFrameChannel supports one active observer.");
    }
    if (this.#closed) {
      observer.close();
      return () => {};
    }

    this.#observer = observer;
    return () => {
      if (this.#observer === observer) this.#observer = undefined;
    };
  }

  close(reason) {
    if (this.#closed) return;
    this.#closed = true;
    delete globalThis[this.#receiveFunctionName];
    const observer = this.#observer;
    this.#observer = undefined;
    observer?.close(reason);
  }
}

function asFrame(value) {
  if (value instanceof Uint8Array) return value;
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  if (ArrayBuffer.isView(value)) {
    return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
  }
  throw new TypeError("CsWebUi delivered a non-binary host frame.");
}

function assertIdentifier(value, name) {
  if (
    typeof value !== "string" ||
    !/^[$A-Z_a-z][$0-9A-Z_a-z]*$/.test(value)
  ) {
    throw new TypeError(`${name} must be one simple ASCII JavaScript identifier.`);
  }
}
