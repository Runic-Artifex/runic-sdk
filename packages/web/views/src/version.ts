// The release of @runic-artifex/views, compared with the Runic.Application.Views version a .NET host
// reports (specs/application). `bun run version:bump` rewrites it with the package version.
export const viewsVersion = "0.12.1";

/** The Views protocol this runtime implements (specs/application/README.md). */
export const bridgeProtocol = 4;

/** The `client.name` this runtime sends in `hello`. */
export const clientName = "@runic-artifex/views";

/** The producer this release ships with; only its `implementation.version` is compared with {@link viewsVersion}. */
export const runicProducerName = "Runic.Application.Views";
