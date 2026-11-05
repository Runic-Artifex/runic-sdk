// @vitest-environment happy-dom
// appearance/1: useAppearance follows the host's pushes while its owner lives.
import { flushSync } from "svelte";
import { expect, test } from "vitest";
import { connectView } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { useAppearance, type AppearanceHandle } from "../src/views/index.js";

test("useAppearance follows the host's appearance while its owner lives", async () => {
  const bridge = installMockBridge(createMockBridge());
  bridge.view("shell", { state: {} });
  await connectView({ contract: "Tests.Shell:fp-1", route: "shell", members: {} });
  let look!: AppearanceHandle;
  const seen: (number | null)[] = [];
  const destroy = $effect.root(() => {
    look = useAppearance();
    $effect(() => { seen.push(look.current.textScale); });
  });
  flushSync();
  bridge.setAppearance({ textScale: 1.5, contrast: "more" });
  await bridge.flush();
  flushSync();
  expect([look.current.textScale, look.current.host?.contrast]).toEqual([1.5, "more"]);
  expect(seen.at(-1)).toBe(1.5);
  destroy();
  bridge.setAppearance({ textScale: 2 });
  await bridge.flush();
  flushSync();
  expect(look.current.textScale, "a destroyed owner stops following").toBe(1.5);
});
