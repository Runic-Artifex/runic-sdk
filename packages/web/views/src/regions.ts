// Region collections (specs/application/README.md, "Region collections", feature `region-collections/1`): a .NET
// `[RunicRegionCollection]` NavigationSelection, such as document tabs that each keep their own back stack. The state
// member lists every region with a stable key and its current content, and the selected index; the page selects and
// closes regions by key with the generated `select{Name}(key)` and `close{Name}(key)`.
import * as wire from "./wire.js";

/** `region-collections/1`: region collection members, and `region` requests that select or close a region by key. */
export const regionCollectionsFeature = "region-collections/1";

/**
 * One region of a {@link RegionCollection}. `key` stays the same while the region exists and is never reused in the
 * window, so frameworks key a region's outlet by it and keep each region's component, and its DOM, across states.
 */
export interface RegionSlot<R> {
  /** Identifies the region in the window: opaque, compared only for equality. */
  readonly key: string;
  /** The region's current View, or `null` while the region is empty. */
  readonly content: R | null;
}

/**
 * A region collection member of generated state, such as `state.documents`: the regions in order and the selected
 * index. `selected` changes only when a selection or close commits, so a tab bar that highlights `selected` shows the
 * kept tab after a departure guard vetoed a switch.
 */
export interface RegionCollection<R> {
  readonly regions: readonly RegionSlot<R>[];
  /** The index in `regions` of the selected region, or `null` when the collection has no region. */
  readonly selected: number | null;
}

/**
 * How a `select{Name}(key)` or `close{Name}(key)` ended: the .NET `NavigationOutcome` of the request. A `"Rejected"`
 * outcome with rejection `"Guard"` means a departure guard kept the region.
 */
export interface RegionOutcome {
  readonly kind: "Committed" | "Rejected" | "Superseded" | "Failed" | (string & {});
  /** Why the request was rejected, such as `"Guard"`, `"Busy"` or `"Cancelled"`; `null` unless `kind` is `"Rejected"`. */
  readonly rejection: string | null;
  /** How the arrival hooks of the entry the commit made current ended; `"None"` unless `kind` is `"Committed"`. */
  readonly arrival: "None" | "Completed" | "Failed" | "Cancelled" | (string & {});
}

/** One region of a collection with its position, as {@link regionEntries} lists it for an outlet or a tab bar. */
export interface RegionEntry<R> extends RegionSlot<R> {
  /** The region's index in `regions`. */
  readonly index: number;
  /** Whether it is the selected region. */
  readonly selected: boolean;
}

/** The regions of `collection` with their index and whether each is selected; empty for `null` or `undefined`. */
export function regionEntries<R>(collection: RegionCollection<R> | null | undefined): readonly RegionEntry<R>[] {
  if (!collection) return [];
  return collection.regions.map((region, index) => ({ key: region.key, content: region.content, index, selected: index === collection.selected }));
}

/** The selected region of `collection`, or `undefined` when it has none. */
export function selectedRegion<R>(collection: RegionCollection<R> | null | undefined): RegionSlot<R> | undefined {
  if (!collection || collection.selected === null) return undefined;
  return collection.regions[collection.selected];
}

/** Decodes a region collection member; `content` decodes one region's non-null content reference. Generated code only. */
export function regionCollection<R>(value: unknown, content: (reference: unknown) => R): RegionCollection<R> {
  return wire.object(value, collection => {
    const regions = wire.array(collection["regions"], item => wire.object(item, region => ({
      key: wire.string(region["key"]),
      content: region["content"] === null ? null : content(region["content"]),
    })));
    const selected = collection["selected"] === null ? null : wire.integer(collection["selected"], 0, regions.length - 1);
    return { regions, selected };
  });
}

/** Reads a region reply's `outcome`. */
export function regionOutcome(value: unknown): RegionOutcome {
  return wire.object(value, outcome => ({
    kind: wire.string(outcome["kind"]),
    rejection: outcome["rejection"] === null || outcome["rejection"] === undefined ? null : wire.string(outcome["rejection"]),
    arrival: outcome["arrival"] === undefined ? "None" : wire.string(outcome["arrival"]),
  }));
}
