import { useCommand, useView } from "@runic-artifex/react";
import { describeBridgeError } from "@runic-artifex/views";
import { aboutHeading } from "runic-library-starter";
import type { AboutPageReference } from "runic-library-starter/generated";

// The RunicCoreLibrary library's View. Its client, AboutPageReference, and aboutHeading come from the
// library's frontend package, which the library's build generates and builds.
export function AboutPanel({ page }: { page: AboutPageReference }) {
  const { state, client, error: connection } = useView(page);
  const toggle = useCommand(() => client?.toggleDetails());
  const error = toggle.error ?? connection;

  return <aside className="about">
    <h2>{state ? aboutHeading(state) : "Connecting…"}</h2>
    {state?.expanded && <p>Shared ViewModels and Views, with their TypeScript clients in the library's frontend package.</p>}
    <button disabled={!client || toggle.pending} onClick={() => void toggle.run()}>{state?.expanded ? "Hide details" : "Show details"}</button>
    {error !== undefined && <p role="alert">{describeBridgeError(error)}</p>}
  </aside>;
}
