import { expect, test } from "bun:test";
import { programPolicy, selfCheck } from "../templates/check-template-page.mjs";
import { formatCspMessage, formatCspViolation, isCspMessage } from "../templates/csp-violations.mjs";

test("policy messages are told apart from other console output", () => {
  expect(isCspMessage("Refused to apply inline style because it violates the following Content Security Policy directive: \"default-src 'self'\".")).toBe(true);
  expect(isCspMessage("EvalError: Evaluating a string as JavaScript violates the following Content Security Policy directive")).toBe(true);
  expect(isCspMessage("Runic template rebuild check")).toBe(false);
  expect(isCspMessage(undefined)).toBe(false);
});

test("a violation names its directive, what was blocked and where", () => {
  const line = formatCspViolation({
    documentURI: "http://127.0.0.1:5000/index.html",
    violatedDirective: "style-src-attr",
    effectiveDirective: "style-src-attr",
    blockedURI: "inline",
    sample: "position: absolute",
    sourceFile: "http://127.0.0.1:5000/_app/immutable/start.js",
    lineNumber: 3,
    columnNumber: 14,
    disposition: "enforce",
    originalPolicy: "default-src 'self'",
  });
  expect(line).toBe('style-src-attr: blocked inline sample "position: absolute" at '
    + "http://127.0.0.1:5000/_app/immutable/start.js:3:14; policy: default-src 'self'");
  expect(formatCspViolation({ violatedDirective: "default-src", effectiveDirective: "img-src", blockedURI: "",
    documentURI: "http://x/", disposition: "report", originalPolicy: "p" }))
    .toBe("default-src (effective img-src) [report]: blocked (none) at http://x/; policy: p");
  expect(formatCspMessage({ type: "console.error", text: "Refused to load", location: { url: "http://x/a.js", lineNumber: 1, columnNumber: 2 } }))
    .toBe("console.error: Refused to load at http://x/a.js:1:2");
});

test("the expected header is the one policy of a generated Program.cs", () => {
  const policy = "default-src 'self'; script-src 'self'; object-src 'none'";
  expect(programPolicy(`var x = 1;\nconst string contentSecurityPolicy = "${policy}";\n`)).toBe(policy);
  expect(() => programPolicy("var x = 1;")).toThrow("found 0");
});

// The templates job runs the same self-check in Chromium before its pages.
test.skipIf(!process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH)("the collector reports a blocked inline style", async () => {
  const csp = await selfCheck();
  expect(csp.violations.some(violation => violation.effectiveDirective === "style-src-attr")).toBe(true);
  expect(csp.report()).toContain("style-src-attr");
}, 60_000);
