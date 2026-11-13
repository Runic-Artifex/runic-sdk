// hosted-regions/1 (specs/application/README.md, "Hosted regions"): the wire members of the feature. The element that
// uses them is hosted-region.ts; the runtime only lists the feature and reads its pushes, which name no route.

/** `hosted-regions/1`: the page declares regions where the host shows a guest application's Window. */
export const hostedRegionsFeature = "hosted-regions/1";

/** The kinds of the feature's pushes. */
export const hostedRegionPushKinds = ["hosted-region-content", "hosted-region-update", "hosted-region-empty", "hosted-region-focus"] as const;

export type HostedRegionPushKind = typeof hostedRegionPushKinds[number];

/** A push of the feature as the runtime passes it on; members depend on `kind`. */
export type HostedRegionPush = Readonly<Record<string, unknown>> & { readonly kind: HostedRegionPushKind; readonly name: string; readonly route?: undefined };

/** At most this many regions per connection. */
export const maximumHostedRegions = 64;
const maximumSize = 32_767;
const maximumSource = 2_048;
const maximumTitle = 1_024;
const maximumAllow = 1_024;

/** A region name: 1 to 64 ASCII letters, digits, dots, underscores or hyphens. */
export function isHostedRegionName(value: unknown): value is string {
  return typeof value === "string" && /^[A-Za-z0-9._-]{1,64}$/.test(value);
}

const isOptionalTitle = (value: unknown) => value === null || typeof value === "string" && value.length <= maximumTitle;

/**
 * Checks a push of the feature; returns a reason when it is invalid. A content push of another embedding is valid:
 * the element shows its fallback for it.
 */
export function invalidHostedRegionPush(push: Readonly<Record<string, unknown>>): string | undefined {
  if (!isHostedRegionName(push["name"])) return "The hosted region push has no region name.";
  switch (push["kind"]) {
    case "hosted-region-content":
      if (typeof push["embedding"] !== "string") return "The hosted region content has no embedding.";
      if (push["embedding"] === "component") {
        // region-component/1: the guest's module, the WebSocket address of its surface and the component token.
        if (typeof push["module"] !== "string" || push["module"].length > maximumSource || !/^https?:\/\//.test(push["module"]))
          return "The hosted region content has no component module address.";
        if (typeof push["url"] !== "string" || push["url"].length > maximumSource || !/^wss?:\/\//.test(push["url"]))
          return "The hosted region content has no component connection address.";
        if (typeof push["token"] !== "string" || !/^[A-Za-z0-9_-]{1,256}$/.test(push["token"]))
          return "The hosted region content has no component token.";
        return undefined;
      }
      if (push["embedding"] !== "frame") return undefined;
      if (typeof push["src"] !== "string" || push["src"].length === 0 || push["src"].length > maximumSource || !/^https?:\/\//.test(push["src"]))
        return "The hosted region content has no frame address.";
      if (!isOptionalTitle(push["title"] ?? null)) return "The hosted region content's title is invalid.";
      if (typeof push["allow"] !== "string" || push["allow"].length > maximumAllow) return "The hosted region content's allow is invalid.";
      return undefined;
    case "hosted-region-update": {
      if (Object.hasOwn(push, "title") && !isOptionalTitle(push["title"])) return "The hosted region update's title is invalid.";
      const height = push["height"];
      if (height !== undefined && !(Number.isInteger(height) && (height as number) >= 0 && (height as number) <= maximumSize))
        return "The hosted region update's height is invalid.";
      return undefined;
    }
    case "hosted-region-empty":
      return typeof push["reason"] === "string" ? undefined : "The hosted region empty push has no reason.";
    default:
      return undefined;
  }
}
