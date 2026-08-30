import type { RequestEvent } from "@sveltejs/kit";
import { describe, expect, test, vi } from "vitest";
import {
  createRunicHostedSsrLoad,
  RUNIC_HOSTED_SESSION_PATH,
} from "../src/hosted/index.js";
import { createRunicLocaleRouting } from "../src/translations/index.js";

const routing = createRunicLocaleRouting({
  locales: ["en", "de"] as const,
  baseLocale: "en",
  baseLocalePath: "unprefixed",
});

function event(pathname: string, cookie?: string): RequestEvent {
  const url = new URL(pathname, "https://app.example.test");
  return {
    url,
    request: new Request(url, cookie ? { headers: { cookie } } : undefined),
    locals: { locale: pathname.startsWith("/de") ? "de" : "en" },
  } as unknown as RequestEvent;
}

describe("Runic hosted SvelteKit SSR", () => {
  test("forwards only the opaque host cookie and returns a bounded request bootstrap", async () => {
    const loadSession = vi.fn(async (request) => {
      expect(request).toEqual({
        path: RUNIC_HOSTED_SESSION_PATH,
        cookie: "__Host-runic-session=opaque-value",
      });
      return Response.json({ subject: "operator", displayName: "Operator", roles: ["operator"] });
    });
    const load = createRunicHostedSsrLoad(routing, { loadSession });
    const data = await load(event("/de/setup", "tracking=ignored; __Host-runic-session=opaque-value; runic_locale=de"));

    expect(data).toEqual({
      bootstrap: {
        schema: "runic.hosted-bootstrap/1",
        locale: "de",
        session: { subject: "operator", displayName: "Operator", roles: ["operator"] },
        fingerprint: expect.stringMatching(/^runic-hosted-[a-f0-9]{8}$/),
      },
    });
    expect(loadSession).toHaveBeenCalledOnce();
  });

  test("fails closed for missing, unauthorized, malformed, or noncanonical service projections", async () => {
    const unauthorized = createRunicHostedSsrLoad(routing, { loadSession: async () => new Response(null, { status: 401 }) });
    const malformed = createRunicHostedSsrLoad(routing, { loadSession: async () => Response.json({ subject: "operator", roles: [], token: "forged" }) });
    const unorderedRoles = createRunicHostedSsrLoad(routing, { loadSession: async () => Response.json({ subject: "operator", displayName: null, roles: ["viewer", "operator"] }) });

    await expect(createRunicHostedSsrLoad(routing, { loadSession: vi.fn() })(event("/setup")))
      .rejects.toMatchObject({ status: 401 });
    await expect(unauthorized(event("/setup", "__Host-runic-session=opaque")))
      .rejects.toMatchObject({ status: 401 });
    await expect(malformed(event("/setup", "__Host-runic-session=opaque")))
      .rejects.toMatchObject({ status: 502 });
    await expect(unorderedRoles(event("/setup", "__Host-runic-session=opaque")))
      .rejects.toMatchObject({ status: 502 });
  });

  test("accepts C# ordinal roles and rejects a noncanonical mixed-case Unicode order", async () => {
    const csharpCanonical = createRunicHostedSsrLoad(routing, {
      loadSession: async () => Response.json({ subject: "operator", displayName: null, roles: ["Z", "a", "Å", "ä"] }),
    });
    const noncanonical = createRunicHostedSsrLoad(routing, {
      loadSession: async () => Response.json({ subject: "operator", displayName: null, roles: ["a", "Z", "Å", "ä"] }),
    });

    await expect(csharpCanonical(event("/setup", "__Host-runic-session=opaque")))
      .resolves.toMatchObject({ bootstrap: { session: { roles: ["Z", "a", "Å", "ä"] } } });
    await expect(noncanonical(event("/setup", "__Host-runic-session=opaque")))
      .rejects.toMatchObject({ status: 502 });
  });

  test("keeps concurrent request locale and identity projections isolated", async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    const load = createRunicHostedSsrLoad(routing, {
      loadSession: async ({ cookie }) => {
        await gate;
        const subject = cookie.endsWith("=german") ? "german" : "english";
        return Response.json({ subject, displayName: subject, roles: ["operator"] });
      },
    });
    const english = load(event("/setup", "__Host-runic-session=english"));
    const german = load(event("/de/setup", "__Host-runic-session=german"));
    release();

    await expect(english).resolves.toMatchObject({ bootstrap: { locale: "en", session: { subject: "english" } } });
    await expect(german).resolves.toMatchObject({ bootstrap: { locale: "de", session: { subject: "german" } } });
  });
});
