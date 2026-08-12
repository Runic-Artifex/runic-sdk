# Shared desktop sample assets

The desktop samples include these local assets so their UI works without a CDN
or an external network connection. They are copied into the consuming CS-WebUI
application at build and publish time and served from its virtual file system.

| Asset | Pinned version | Upstream package |
| --- | --- | --- |
| Bootstrap | 5.3.8 | [`bootstrap`](https://www.npmjs.com/package/bootstrap) |
| Font Awesome Free | 7.3.1 | [`@fortawesome/fontawesome-free`](https://www.npmjs.com/package/@fortawesome/fontawesome-free) |
| HTMX | 2.0.10 | [`htmx.org`](https://www.npmjs.com/package/htmx.org) |

The corresponding upstream license texts are in `licenses/`. Bootstrap and
Font Awesome are an accessible visual baseline for these examples only; Runic
Toolkit packages are styling-neutral and work with any consumer-owned design
system. Retain accessible text or labels whenever an icon carries meaning.

See the [sample learning path](../) for runnable applications and the
[Runic Toolkit documentation](https://docs.runic-artifex.eu/products/runic-toolkit)
for hosting guidance.
