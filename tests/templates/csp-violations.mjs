// Collects Content-Security-Policy violations of a Playwright page: the securitypolicyviolation events the page's
// documents dispatch, reported through a binding as they happen, and Chromium's console and page errors about the
// policy ("Refused to ...", "... violates the following Content Security Policy directive ..."). The init script and
// binding run outside the page's policy, so collecting adds no violation of its own.

const binding = "__runicReportCspViolation";

// Runs in every document of the page before its own scripts.
function reportViolations(name) {
  const report = globalThis[name];
  document.addEventListener("securitypolicyviolation", event => {
    report({
      documentURI: event.documentURI,
      violatedDirective: event.violatedDirective,
      effectiveDirective: event.effectiveDirective,
      blockedURI: event.blockedURI,
      sample: event.sample,
      sourceFile: event.sourceFile,
      lineNumber: event.lineNumber,
      columnNumber: event.columnNumber,
      disposition: event.disposition,
      originalPolicy: event.originalPolicy,
    });
  }, true);
}

const cspMessage = /Content[- ]Security[- ]Policy|\bRefused to\b/i;

/** Whether a console or page error message reports a Content-Security-Policy violation. */
export function isCspMessage(text) {
  return cspMessage.test(text ?? "");
}

/** One line per violation: the directive, what was blocked and where. */
export function formatCspViolation(violation) {
  const directive = violation.effectiveDirective && violation.effectiveDirective !== violation.violatedDirective
    ? `${violation.violatedDirective} (effective ${violation.effectiveDirective})`
    : violation.violatedDirective;
  const blocked = violation.blockedURI || "(none)";
  const sample = violation.sample ? ` sample ${JSON.stringify(violation.sample)}` : "";
  const source = violation.sourceFile
    ? `${violation.sourceFile}:${violation.lineNumber ?? 0}:${violation.columnNumber ?? 0}`
    : violation.documentURI ?? "(unknown source)";
  const disposition = violation.disposition && violation.disposition !== "enforce" ? ` [${violation.disposition}]` : "";
  return `${directive}${disposition}: blocked ${blocked}${sample} at ${source}; policy: ${violation.originalPolicy}`;
}

/** One line per console or page error message about the policy. */
export function formatCspMessage(message) {
  const location = message.location?.url
    ? ` at ${message.location.url}:${message.location.lineNumber ?? 0}:${message.location.columnNumber ?? 0}`
    : "";
  return `${message.type}: ${message.text}${location}`;
}

/**
 * Starts collecting before the page navigates. Returns the violation events and policy messages seen so far, and a
 * report that is empty when there are none.
 */
export async function watchCspViolations(page) {
  const violations = [];
  const messages = [];
  await page.exposeBinding(binding, (_source, violation) => { violations.push(violation); });
  await page.addInitScript(reportViolations, binding);
  page.on("console", message => {
    if (isCspMessage(message.text())) messages.push({ type: `console.${message.type()}`, text: message.text(), location: message.location() });
  });
  page.on("pageerror", error => {
    if (isCspMessage(error.message)) messages.push({ type: "pageerror", text: error.message });
  });
  return {
    violations,
    messages,
    report() {
      if (violations.length === 0 && messages.length === 0) return "";
      return [
        `${violations.length} Content-Security-Policy violation event(s):`,
        ...violations.map(violation => `  - ${formatCspViolation(violation)}`),
        `${messages.length} console message(s) about the policy:`,
        ...messages.map(message => `  - ${formatCspMessage(message)}`),
      ].join("\n");
    },
  };
}
