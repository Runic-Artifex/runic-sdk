import { defineConfig } from "vitest/config";
// Harness bounds only, scaled by RUNIC_TEST_TIMEOUT_SCALE on slow runners (eng/ci/README.md, "Test timeouts").
import { scaled } from "../../../tests/support/timeouts.mjs";

export default defineConfig({
  test: {
    environment: "node",
    testTimeout: scaled(5_000),
    hookTimeout: scaled(10_000),
    include: ["test/**/*.test.ts"],
  },
});
