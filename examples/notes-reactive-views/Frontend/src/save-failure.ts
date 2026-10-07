import { matchCase } from "@runic-artifex/views";
import type { SaveFailure } from "./generated/editor.js";

/** The text the Reactive Notes frontends show for Save's declared failure; a new case is a compile error. */
export function describeSaveFailure(failure: SaveFailure): string {
  return matchCase(failure, {
    titleRequired: () => "A note needs a title.",
    titleTooLong: tooLong => `A title can have at most ${tooLong.maximumLength} characters.`,
  });
}
