// The RunicCoreLibrary frontend package. Its build (the build script, which dotnet build runs after the library's
// build generated src/generated) compiles it into dist/, which the package's exports name:
//   runic-library-starter            this module: code that every application's page shares
//   runic-library-starter/generated  the generated clients of the library's Views (AboutPageReference, AboutState, ...)
//   runic-library-starter/generated/mocks  their typed mocks, for the pages' tests
import type { AboutState } from "./generated/runic-clients.js";

/** The about panel's heading, such as "RunicCoreLibrary 1.0.0". */
export function aboutHeading(state: AboutState): string {
  return `${state.library} ${state.version}`;
}
