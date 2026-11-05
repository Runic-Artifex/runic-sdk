// appearance/1: appearances is the page's appearance now, then after each host push.
import { connectView } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { Effect, Fiber, Stream } from "effect";
import { expect, test } from "vitest";
import { appearances } from "../src/index.js";

test("is the appearance now, then the appearance after each push", async () => {
  const bridge = installMockBridge(createMockBridge());
  bridge.view("shell", { state: {} });
  bridge.setAppearance({ textScale: 1.25 });
  await connectView({ contract: "Tests.Shell:fp-1", route: "shell", members: {} });
  await bridge.flush();
  const seen: (number | null)[] = [];
  const fiber = Effect.runFork(Stream.runForEach(Stream.take(appearances(), 2), look => Effect.sync(() => { seen.push(look.textScale); })));
  await Effect.runPromise(Effect.yieldNow);
  await new Promise(resolve => setTimeout(resolve, 0));
  bridge.setAppearance({ textScale: 2 });
  await bridge.flush();
  await Effect.runPromise(Fiber.join(fiber));
  expect(seen).toEqual([1.25, 2]);
});
