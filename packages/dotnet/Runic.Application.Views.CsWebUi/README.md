# Runic Application CS-WebUI host adapter

This adapter connects the host-neutral View bridge to a CS-WebUI window. Its
browser script adapts `window.webui` to the generated TypeScript client's
transport contract. A window-local `CreateBridgeSession()` retains one native
binding for each route name and swaps the active managed handler as Views
change; an inactive route returns `disconnected`.

CS-WebUI cannot remove a native route registration before its window closes.
Retained page references reuse routes; new page identities still add routes.
The Microsoft DI window factory in this package owns the content session,
bridge attachment, and scoped ViewModel together.
The View core remains independent of CS-WebUI. This is the selected native
host for the current preview package graph.
