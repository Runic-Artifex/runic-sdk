// The route both fixtures connect; the test's mock Bridges register it.
import { connectView } from "@runic-artifex/views/generated";
import * as wire from "@runic-artifex/views/generated/wire";

const members = { count: (value) => wire.integer(value, -2147483648, 2147483647) };
export const counter = { connect: () => connectView({ contract: "Fixtures.Counter:fp-1", route: "counter", members }) };
