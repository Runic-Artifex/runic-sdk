![Runic Artifex Documentation banner](.github/assets/brand/banner.png)

# Runic Artifex Documentation

The documentation portal for the independent Runic Artifex projects and the
explicit integration seams between them.

The portal source may become public while the first package preview train is
still being prepared. A successful build is not authorization to publish
packages or claim that registry candidates are ready.

The portal is built with SvelteKit and Svelte 5. It targets Cloudflare through
`@sveltejs/adapter-cloudflare`; hosting identity remains a separate launch
decision.

## Develop locally

Use Node.js 24 and npm:

```bash
npm ci
npm run dev
```

The terminal prints the local portal URL (normally `http://localhost:5173`).

## Verify

```bash
npm audit --omit=dev --audit-level=high
npm run lint
npm run check
npm test
```

`npm test` creates the production build and verifies important rendered routes
and release information.

## Deploy to Cloudflare

The production build is emitted to `.svelte-kit/cloudflare` and works with
Cloudflare Pages or Workers. The repository intentionally does not commit a
Wrangler deployment file yet: a Pages/Worker project name, Cloudflare account,
and final hostname have not been selected. For Pages Git integration, use the
SvelteKit preset, `npm run build`, and `.svelte-kit/cloudflare` as the output
directory, then enable the `nodejs_als` compatibility flag.

## Content model

- `/getting-started` introduces the product family.
- `/products` owns product and integration documentation.
- `/architecture` records dependency direction and ownership rules.
- `/packages` is the NuGet/npm catalog.
- `/releases` describes the coordinated first preview train.

## Publication gate

The portal and packages may go public only after:

1. every product's exact release workflow succeeds on `main`;
2. NuGet and npm trusted publishers are configured;
3. the npm organization/scope is under Runic Artifex control;
4. documentation links and install commands are verified against public
   registries; and
5. the launch is explicitly approved.

## License

The portal source and documentation are licensed under the MIT License.
