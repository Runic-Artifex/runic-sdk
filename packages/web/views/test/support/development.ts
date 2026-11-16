// The development build (dist/development/, the `development` export condition; README, "Development build"), with
// the declarations it shares with the production build (dist/*.d.ts). Tests of the development tools import it from
// here; every other test runs against the production build in dist/.
const load = (entry: string): Promise<unknown> => import(new URL(`../../dist/development/${entry}`, import.meta.url).href);

export const development = await load("index.js") as typeof import("../../dist/index.js");
export const developmentGenerated = await load("generated.js") as typeof import("../../dist/generated.js");
export const developmentWire = await load("wire.js") as typeof import("../../dist/wire.js");
export const developmentMock = await load("mock.js") as typeof import("../../dist/mock.js");
export const developmentBindings = await load("bindings.js") as typeof import("../../dist/bindings.js");
