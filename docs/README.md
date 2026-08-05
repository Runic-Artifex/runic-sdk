# Runic Artifex Documentation

The documentation portal for the independent Runic Artifex projects and the
explicit integration seams between them.

The repository is private while the first public preview train is prepared. A
successful build is not authorization to publish packages or make the portal
public.

## Develop locally

Use Node.js 24 and npm:

```bash
npm ci
npm run dev
```

The local portal is served at `http://localhost:3000`.

## Verify

```bash
npm audit --omit=dev --audit-level=high
npm run lint
npm test
```

`npm test` creates the production build and verifies important rendered routes
and release information.

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
