# Shared desktop sample assets

These assets are copied into each CsWebUi sample at build and publish time.
They are served by the local virtual file system; the applications do not
depend on a CDN.

| Asset | Pinned version | Source |
| --- | --- | --- |
| Bootstrap | 5.3.8 | `bootstrap` npm package |
| Font Awesome Free | 7.3.1 | `@fortawesome/fontawesome-free` npm package |
| HTMX | 2.0.10 | `htmx.org` npm package |

The corresponding upstream license texts are retained in `licenses/`.
Bootstrap and Font Awesome are sample defaults only. Runic Toolkit runtime
packages do not depend on either styling system.
