import { useView } from "@runic-artifex/react";
import { describeBridgeError } from "@runic-artifex/views";
import type { WelcomePageReference } from "../generated/welcome.js";

export function WelcomePage({ page }: { page: WelcomePageReference }) {
  const { state, error } = useView(page);
  return <section><h2>{state?.greeting ?? "Connecting…"}</h2>{error !== undefined && <p role="alert">{describeBridgeError(error)}</p>}</section>;
}
