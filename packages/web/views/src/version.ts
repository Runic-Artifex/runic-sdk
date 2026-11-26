// The release of @runic-artifex/views, compared with the `release` a Runic producer names in its hello reply, or else
// with the version of a producer under one of Runic's names (specs/application, "Handshake"). `bun run version:bump`
// rewrites it with the package version.
export const viewsVersion = "0.24.0";

/** The Views protocol this runtime implements (specs/application/README.md). */
export const bridgeProtocol = 4;

/** The `client.name` this runtime sends in `hello`. */
export const clientName = "@runic-artifex/views";

/**
 * The names of Runic's producer: today's, and the one the rename of 0.25 gives it. During the rename window
 * (specs/application, "Rename window") a client treats both as Runic's producer. Without a `release` in the hello reply,
 * only such a producer's `implementation.version` is compared with {@link viewsVersion}.
 */
export const runicProducerNames: readonly string[] = ["Runic.Application.Views", "RunicArtifex.Application.Views"];
