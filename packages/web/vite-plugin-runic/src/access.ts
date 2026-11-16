// Who may read Runic Inspect's development data from another machine (D066.27 #14): the development server answers
// pages of this machine (loopback) and, when configured, other hosts and networks, such as a Tailscale network or the
// laptop whose browser shows a page of an agent's dev server. `runic({ allow })` and `RUNIC_DEV_ALLOW` name them; Runic
// Translations' in-page editing API reads the same variable (vite-plugin-runic-translations, "Who may edit"), so one
// setting admits a client to both. Entries are parsed exactly as that plugin parses them.
//
// The client address is the TCP connection's, never a forwarded header. The open action (`/__runic/open`) does not use
// the list: it launches an editor on this machine, so it stays loopback-only.

import { lookup as dnsLookup } from "node:dns/promises";
import type { IncomingMessage } from "node:http";
import { BlockList, isIP } from "node:net";

/** Hosts and networks, besides loopback, that may read the development data: comma- or space-separated. */
export const allowVariable = "RUNIC_DEV_ALLOW";
/** How long a resolved host name of the allow list is trusted. */
const hostTtl = 30_000;

/** One entry of the allow list, validated. */
export type AccessRule =
  | { readonly kind: "address"; readonly text: string; readonly address: string; readonly family: "ipv4" | "ipv6" }
  | { readonly kind: "network"; readonly text: string; readonly address: string; readonly prefix: number; readonly family: "ipv4" | "ipv6" }
  | { readonly kind: "host"; readonly text: string; readonly name: string };

const hostName = /^(?=.{1,253}$)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.?$/u;

/** Validates allow-list entries; throws a TypeError naming the entry and `source` for an invalid one. */
export function parseAllowList(entries: unknown, source: string): AccessRule[] {
  if (!Array.isArray(entries)) throw new TypeError(`${source} must be an array of addresses, networks and host names.`);
  return entries.map((entry: unknown) => {
    if (typeof entry !== "string" || entry.trim().length === 0) throw new TypeError(`${source}: every entry must be a non-empty string.`);
    const text = entry.trim();
    const slash = text.indexOf("/");
    if (slash >= 0) {
      const address = unbracket(text.slice(0, slash)), prefixText = text.slice(slash + 1);
      const version = isIP(address);
      const prefix = /^\d{1,3}$/u.test(prefixText) ? Number(prefixText) : NaN;
      if (version === 0 || !Number.isInteger(prefix) || prefix > (version === 4 ? 32 : 128))
        throw new TypeError(`${source}: '${text}' is not a network; use an IPv4 or IPv6 CIDR such as 100.64.0.0/10 or fd7a:115c:a1e0::/48.`);
      return { kind: "network", text, address, prefix, family: version === 4 ? "ipv4" : "ipv6" };
    }
    const address = unbracket(text);
    const version = isIP(address);
    if (version !== 0) return { kind: "address", text, address, family: version === 4 ? "ipv4" : "ipv6" };
    if (!hostName.test(text) || /^[\d.]+$/u.test(text))
      throw new TypeError(`${source}: '${text}' is not an IP address, a CIDR network or a host name.`);
    return { kind: "host", text, name: text.toLowerCase().replace(/\.$/u, "") };
  });
}

/** The entries of the environment variable: separated by commas or whitespace. */
export function allowListFromEnvironment(value: string | undefined): string[] {
  return (value ?? "").split(/[\s,]+/u).filter((entry) => entry.length > 0);
}

function unbracket(value: string): string {
  return value.startsWith("[") && value.endsWith("]") ? value.slice(1, -1) : value;
}

/** An IPv4-mapped IPv6 address (`::ffff:192.0.2.1`) as its IPv4 address; any other address unchanged, without a zone. */
export function normalizeAddress(address: string): string {
  const unzoned = address.replace(/%.*$/u, "");
  const mapped = /^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/iu.exec(unzoned);
  return mapped ? mapped[1]! : unzoned;
}

/** Whether a client address is loopback: 127.0.0.0/8 or ::1 (also IPv4-mapped). */
export function isLoopbackAddress(address: string | undefined): boolean {
  if (typeof address !== "string") return false;
  const normalized = normalizeAddress(address);
  if (isIP(normalized) === 4) return normalized.startsWith("127.");
  return isIP(normalized) === 6 && loopback6.check(normalized, "ipv6");
}

const loopback6 = new BlockList();
loopback6.addAddress("::1", "ipv6");

/** Decides whether a client address may read the development data: loopback, or one of the rules. */
export class AccessList {
  readonly rules: readonly AccessRule[];
  readonly #addresses = new BlockList();
  readonly #hosts: readonly string[];
  readonly #resolved = new Map<string, { readonly at: number; readonly addresses: Promise<readonly string[]> }>();
  readonly #lookup: (name: string) => Promise<readonly string[]>;

  constructor(rules: readonly AccessRule[] = [], lookup?: (name: string) => Promise<readonly string[]>) {
    this.rules = rules;
    for (const rule of rules) {
      if (rule.kind === "address") this.#addresses.addAddress(rule.address, rule.family);
      else if (rule.kind === "network") this.#addresses.addSubnet(rule.address, rule.prefix, rule.family);
    }
    this.#hosts = rules.flatMap((rule) => rule.kind === "host" ? [rule.name] : []);
    this.#lookup = lookup ?? (async (name) => (await dnsLookup(name, { all: true, verbatim: true })).map((item) => item.address));
  }

  /** Whether the list admits more than loopback. */
  get remote(): boolean {
    return this.rules.length > 0;
  }

  async allows(address: string | undefined): Promise<boolean> {
    if (!address) return false;
    const normalized = normalizeAddress(address);
    const version = isIP(normalized);
    if (version === 0) return false;
    if (isLoopbackAddress(normalized)) return true;
    if (this.#addresses.check(normalized, version === 4 ? "ipv4" : "ipv6")) return true;
    for (const name of this.#hosts) {
      let entry = this.#resolved.get(name);
      if (!entry || Date.now() - entry.at > hostTtl) {
        entry = { at: Date.now(), addresses: this.#lookup(name).catch(() => []) };
        this.#resolved.set(name, entry);
      }
      if ((await entry.addresses).some((candidate) => normalizeAddress(candidate) === normalized)) return true;
    }
    return false;
  }

  /** Whether the request's client (its connection's address) is admitted. */
  allowsRequest(request: IncomingMessage): Promise<boolean> {
    return this.allows(request.socket?.remoteAddress);
  }
}

/**
 * Whether a `Host` header may be answered when Vite's own host check is off (`server.allowedHosts: true`): an IP literal,
 * `localhost` or a name below it, or a host name of the allow list. A name that DNS-rebinding points at loopback is
 * none of these. A request without `Host` (HTTP/1.0, a tool) did not come from a browser and passes.
 */
export function isTrustedHost(host: string | undefined, access: AccessList): boolean {
  if (host === undefined) return true;
  const text = host.trim().toLowerCase();
  if (text.startsWith("[")) {
    const end = text.indexOf("]");
    if (end < 0 || !/^(?::\d{1,5})?$/u.test(text.slice(end + 1))) return false;
    return isIP(text.slice(1, end)) === 6;
  }
  const port = /:\d{1,5}$/u.exec(text);
  const name = (port === null ? text : text.slice(0, port.index)).replace(/\.$/u, "");
  if (name.length === 0) return false;
  if (isIP(name) === 4) return true;
  if (name === "localhost" || name.endsWith(".localhost")) return true;
  return access.rules.some((rule) => rule.kind === "host" && rule.name === name);
}

/** The request's `Host` (or HTTP/2 `:authority`), the value `isTrustedHost` judges. */
export function requestHost(request: IncomingMessage): string | undefined {
  const value = request.headers.host ?? request.headers[":authority"];
  return typeof value === "string" ? value : undefined;
}

/** Loopback only: what the endpoints accept without configuration. */
export const loopbackOnly = new AccessList();

/**
 * The access list of a development server: the option's entries (validated when the plugin is created), then
 * `RUNIC_DEV_ALLOW`'s, each validated under its own name.
 */
export function resolveAccessList(option: readonly AccessRule[], environment: Readonly<Record<string, string | undefined>> = process.env): AccessList {
  return new AccessList([...option, ...parseAllowList(allowListFromEnvironment(environment[allowVariable]), allowVariable)]);
}
