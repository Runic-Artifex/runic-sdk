---
order: 50
---
- Development endpoints hardened: vite-plugin-runic's `/__runic/*` endpoints
  and HMR events answer only admitted clients, the Linux debugging endpoints
  admit only the developer's user, and WebView2's debugging endpoint opens only
  in Development ([Behaviour and output changes](#behaviour-and-output-changes)).
