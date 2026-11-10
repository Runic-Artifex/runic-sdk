import { useText, useTextLocale, useView } from "@runic-artifex/react";
import { describeBridgeError } from "@runic-artifex/views";
import type { WelcomePageReference } from "../generated/welcome.js";
import { m } from "../translations";

export function WelcomePage({ page }: { page: WelcomePageReference }) {
  const { state, error } = useView(page);
  // A text reference of the catalog, shown in the window's language.
  useTextLocale();
  const greeting = useText(state?.greeting, m.welcome_loading());
  return <section><h2>{greeting}</h2>{error !== undefined && <p role="alert">{describeBridgeError(error)}</p>}</section>;
}
