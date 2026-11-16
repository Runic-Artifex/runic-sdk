// How the Runic DevTools extension reads the inspected page: through `chrome.devtools.inspectedWindow.eval` of Runic
// Inspect's development API, `globalThis[Symbol.for("runic.vite.inspect")]`, which vite-plugin-runic's development
// client installs under `vite dev` (a production build has none). Its methods return promises and `eval` returns only
// plain values, so a call starts in the page and keeps its JSON result on a registered symbol until the extension
// takes it. The API is read-only apart from `open`, which asks the development server to open a source location in the
// editor, as Runic Inspect's own links do. No Chrome API here, so the expressions are testable outside DevTools.

const inspect = 'globalThis[Symbol.for("runic.vite.inspect")]';
const results = 'Symbol.for("runic.devtools.results")';

/** The longest an answer may take, such as a `describe` that waits for the source map and the component's source. */
export const defaultTimeout = 15_000;

/**
 * The expression that starts `method(...args)` on Runic Inspect's API and keeps its result under `id`. `args` are
 * JavaScript expressions evaluated in the page, such as `$0` (the Elements panel's selection) or a JSON literal. It
 * evaluates to `"started"`, `"missing"` (no Runic development client on the page) or `"unsupported"` (an older
 * vite-plugin-runic whose API lacks the method).
 */
export function startExpression(id, method, args = []) {
  if (!/^[A-Za-z]+$/u.test(method)) throw new TypeError(`Not an API method: ${method}`);
  return `(() => {
  const api = ${inspect};
  if (typeof api !== "object" || api === null) return "missing";
  if (typeof api.${method} !== "function") return "unsupported";
  const results = (globalThis[${results}] ??= new Map());
  const key = ${JSON.stringify(id)};
  Promise.resolve().then(() => api.${method}(${args.join(", ")})).then(
    (value) => results.set(key, { value: JSON.stringify(value ?? null) }),
    (error) => results.set(key, { error: String(error && error.message || error) }));
  return "started";
})()`;
}

/** The expression that takes the result kept under `id`: `null` while the call runs, else `{ value }` (JSON) or `{ error }`. */
export function takeExpression(id) {
  return `(() => {
  const results = globalThis[${results}];
  const key = ${JSON.stringify(id)};
  const result = results instanceof Map ? results.get(key) : undefined;
  if (result === undefined) return null;
  results.delete(key);
  return result;
})()`;
}

/** The expression that tells whether the page has Runic Inspect and which of its methods. */
export const probeExpression = `(() => {
  const api = ${inspect};
  if (typeof api !== "object" || api === null) return { available: false };
  return { available: true, version: api.version, methods: Object.keys(api).filter((name) => typeof api[name] === "function") };
})()`;

/**
 * An evaluator over `evaluate(expression)`, which resolves to the expression's value or rejects with the page's
 * exception (`chrome.devtools.inspectedWindow.eval`, or `page.evaluate` in tests). `call` resolves to
 * `{ status: "ok", value }`, `{ status: "missing" | "unsupported" }`, or `{ status: "error", error }`.
 */
export function createEvaluator(evaluate, { interval = 40, timeout = defaultTimeout, now = () => Date.now(),
  sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms)) } = {}) {
  let sequence = 0;
  const prefix = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
  return {
    probe: () => evaluate(probeExpression),
    async call(method, args = []) {
      const id = `${prefix}-${++sequence}`;
      let started;
      try {
        started = await evaluate(startExpression(id, method, args));
      } catch (error) {
        return { status: "error", error: errorText(error) };
      }
      if (started === "missing" || started === "unsupported") return { status: started };
      if (started !== "started") return { status: "error", error: `Unexpected answer: ${String(started)}` };
      const deadline = now() + timeout;
      while (now() < deadline) {
        let result;
        try {
          result = await evaluate(takeExpression(id));
        } catch (error) {
          return { status: "error", error: errorText(error) };
        }
        if (result !== null && typeof result === "object") {
          if (typeof result.error === "string") return { status: "error", error: result.error };
          try {
            return { status: "ok", value: JSON.parse(result.value) };
          } catch {
            return { status: "error", error: "The page's answer is not JSON." };
          }
        }
        await sleep(interval);
      }
      return { status: "error", error: `No answer within ${Math.round(timeout / 1000)} s.` };
    },
  };
}

function errorText(error) {
  if (typeof error === "string") return error;
  if (error && typeof error.value === "string") return error.value;
  if (error && typeof error.description === "string") return error.description;
  if (error && typeof error.message === "string") return error.message;
  return String(error);
}

/**
 * `chrome.devtools.inspectedWindow.eval` as a promise of the value, in the page's main world. Its exception info is
 * `{ isError, code, description }` for an extension error and `{ isException, value }` for a page exception.
 */
export function devtoolsEvaluate(devtools) {
  return (expression) => new Promise((resolve, reject) => {
    devtools.inspectedWindow.eval(expression, (result, exception) => {
      if (exception && (exception.isException || exception.isError)) reject(exception);
      else resolve(result);
    });
  });
}
