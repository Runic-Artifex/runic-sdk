import { useEffect, useState } from "react";
import type { WelcomePageReference, WelcomeState, WelcomeView } from "../generated/welcome.js";

export function WelcomePage({ page }: { page: WelcomePageReference }) {
  const [state, setState] = useState<WelcomeState>();
  const [error, setError] = useState<string>();
  useEffect(() => {
    let active = true;
    let unsubscribe = () => {};
    let client: WelcomeView | undefined;
    void page.connect().then(connected => {
      if (!active) { connected.dispose(); return; }
      client = connected;
      unsubscribe = connected.subscribe(next => setState(next));
    }).catch(cause => setError(String(cause)));
    return () => { active = false; unsubscribe(); client?.dispose(); };
  }, [page]);
  return <section><h2>{state?.greeting ?? "Connecting…"}</h2>{error && <p role="alert">{error}</p>}</section>;
}
