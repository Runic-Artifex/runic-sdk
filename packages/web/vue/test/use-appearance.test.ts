// appearance/1: useAppearance follows the host's pushes until its scope stops.
import { effectScope } from "vue";
import { expect, test } from "vitest";
import { connectView } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { useAppearance } from "../dist/index.js";

test("useAppearance follows the host's appearance until the scope stops", async () => {
  const bridge = installMockBridge(createMockBridge());
  bridge.view("shell", { state: {} });
  await connectView({ contract: "Tests.Shell:fp-1", route: "shell", members: {} });
  const scope = effectScope();
  const look = scope.run(() => useAppearance())!;
  bridge.setAppearance({ textScale: 1.5, reducedMotion: true });
  await bridge.flush();
  expect([look.value.textScale, look.value.reducedMotion]).toEqual([1.5, true]);
  scope.stop();
  bridge.setAppearance({ textScale: 2 });
  await bridge.flush();
  expect(look.value.textScale, "a stopped scope stops following").toBe(1.5);
});
