import { matchCase } from "@runic-artifex/views";
import type { SaveFailure } from "./generated/editor.js";

/** The text every Notes frontend shows for Save's declared failure; a new case is a compile error. */
export function describeSaveFailure(failure: SaveFailure): string {
  return matchCase(failure, {
    titleRequired: () => "A note needs a title.",
    titleTaken: taken => `Another note is already called "${taken.existingTitle}".`,
  });
}
