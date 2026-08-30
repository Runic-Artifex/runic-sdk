import { releaseData } from '$lib/generated/release-data';
import { activeVersionForProduct } from '$lib/release-docs';

type ReleaseProductId = (typeof releaseData.products)[number]['id'];
type ArchivedReleaseProduct = Extract<
  (typeof releaseData.products)[number],
  { readonly support: 'archived' }
>;
type ReleaseMetadata = {
  releaseProduct: ReleaseProductId;
  version: string | null;
  versionState: 'published' | 'unassigned';
  availability: 'active' | 'archived';
  archive?: ArchivedReleaseProduct['archive'];
};

export type Product = {
  slug: string;
  name: string;
  shortName: string;
  icon: string;
  kicker: string;
  summary: string;
  description: string;
  releaseProduct: ReleaseProductId | null;
  version: string | null;
  versionState: 'published' | 'unassigned';
  source: string;
  bestFor: string[];
  boundaries: string[];
  availability?: 'active' | 'archived' | 'independent';
  archive?: ArchivedReleaseProduct['archive'];
  kind?: 'package-family' | 'application';
  related?: {
    href:
      '/products/runic-translations/' | '/products/runic-translations-editor/';
    label: string;
  };
};

function releaseVersion(product: ReleaseProductId) {
  return (
    activeVersionForProduct(product) ?? {
      state: 'unassigned' as const,
      value: null,
    }
  );
}

function releaseMetadata(releaseProduct: ReleaseProductId): ReleaseMetadata {
  const version = releaseVersion(releaseProduct);
  const product = releaseData.products.find(
    (candidate) => candidate.id === releaseProduct,
  );
  return {
    releaseProduct,
    version: version.value,
    versionState: version.state,
    availability: product?.support === 'archived' ? 'archived' : 'active',
    archive: product?.support === 'archived' ? product.archive : undefined,
  };
}

export const products: Product[] = [
  {
    slug: 'runic-toolkit',
    name: 'Runic Toolkit',
    shortName: 'Toolkit',
    icon: '/products/runic-toolkit.png',
    kicker: 'Application composition',
    summary:
      'Compose desktop windows, browser frontends, and .NET hosting around one application model with NativeAOT-safe application contracts.',
    description:
      'Runic Toolkit connects desktop windows, browser frontends, and .NET hosting around one application model. Its Application Bridge carries named commands and validated events between a frontend and a NativeAOT-safe .NET host.',
    ...releaseMetadata('application'),
    source: 'https://github.com/Runic-Artifex/runic-toolkit',
    bestFor: [
      'Composing .NET desktop and browser applications',
      'NativeAOT-safe application hosting',
      'Effect Schema-first application contracts with generated C# dispatch',
    ],
    boundaries: [
      'Does not own UI languages, flow, command-line parsing, localization, or assets',
      'Uses named domain commands and events rather than exposing ViewModels as the public application contract',
      'Rendering frameworks own presentation state; the bridge owns validation, transport, sessions, revisions, and operations',
    ],
  },
  {
    slug: 'runic-desktop',
    name: 'Runic Desktop',
    shortName: 'Desktop',
    icon: '/products/runic-desktop.png',
    kicker: 'Native presentation',
    summary:
      'Present web-powered Runic applications in native windows through explicit browser and embedded-WebView policies.',
    description:
      'Runic Desktop owns native presentation hosting for Runic applications. Its C# backend and TypeScript+Effect frontend are native implementations of one shared presentation contract; neither package wraps the other language runtime.',
    ...releaseMetadata('desktop'),
    source: 'https://github.com/Runic-Artifex/runic-desktop',
    bestFor: [
      'Native-window presentation for C# application backends',
      'TypeScript+Effect frontends using the shared Desktop contract',
      'Explicit browser, embedded-WebView, availability, and fallback policies',
    ],
    boundaries: [
      'Owns presentation hosting and lifecycle, not application composition, assets, localization, or domain commands',
      'C# and TypeScript+Effect are peer implementations with deliberate language-specific APIs',
      'Does not depend on WebUI or CivetWeb; CS-WebUI remains a separate upstream WebUI compatibility product',
    ],
  },
  {
    slug: 'cs-webui',
    name: 'CS-WebUI',
    shortName: 'CS-WebUI',
    icon: '/products/cs-webui.png',
    kicker: 'Independent WebUI binding',
    summary:
      'Use upstream WebUI from .NET through a complete C-ABI binding and an ownership-safe managed API.',
    description:
      'CS-WebUI tracks unmodified upstream WebUI. CsWebUi.Native exposes the complete WebUI 2.5 C ABI, while CsWebUi adds deterministic managed ownership, UTF-8 conversion, error handling, and safer window and callback APIs.',
    releaseProduct: null,
    version: null,
    versionState: 'unassigned',
    availability: 'independent',
    source: 'https://github.com/Runic-Artifex/cs-webui',
    bestFor: [
      'Direct upstream WebUI interop from .NET',
      'Applications that intentionally choose WebUI’s native runtime and protocol',
      'Low-level C-ABI access or an ownership-safe managed wrapper',
    ],
    boundaries: [
      'Tracks the WebUI 2.5 beta ABI and unmodified upstream native source',
      'Is maintained and released independently of the Runic v1 compatibility set',
      'Is not the implementation underneath Runic Desktop',
    ],
  },
  {
    slug: 'runic-flow',
    name: 'Runic Flow',
    shortName: 'Flow',
    icon: '/products/runic-flow.png',
    kicker: 'Archived product',
    summary:
      'Runic Flow is archived and has no release-bearing packages or public replacement.',
    description:
      'Runic Flow is archived. Historical migration documentation remains available to guide removal, while current release authority records no package identity, public replacement, or forwarding package.',
    ...releaseMetadata('flow'),
    source: 'https://github.com/Runic-Artifex/runic-flow',
    bestFor: [
      'Reviewing archived package migrations',
      'Removing legacy Flow dependencies without adopting a replacement',
    ],
    boundaries: [
      'Has no canonical packages, install instructions, or compatibility lane',
      'Legacy identities survive only in clearly historical records, not current release authority',
      'Archive evidence records the authoritative retirement decision',
    ],
  },
  {
    slug: 'runic-assets',
    name: 'Runic Assets',
    shortName: 'Assets',
    icon: '/products/runic-assets.png',
    kicker: 'Shared infrastructure',
    summary:
      'Package static assets once and serve the same validated manifest from embedded, development, browser, or server hosts.',
    description:
      'Runic Assets lets one validated asset manifest travel through embedded, development, browser, and server hosts. Safe paths, immutable manifests, portable standard-ZIP archives, development sources, and host adapters stay separate from the transport-neutral core.',
    ...releaseMetadata('assets'),
    source: 'https://github.com/Runic-Artifex/runic-assets',
    bestFor: [
      'Sharing static assets across hosts',
      'Deterministic embedded and development sources',
      'Portable, validated asset archives',
    ],
    boundaries: [
      'Core has no UI or web framework dependency',
      'Host delivery lives in owned adapters',
      'Archive format is documented separately from host behavior',
    ],
  },
  {
    slug: 'runic-translations',
    name: 'Runic Translations',
    shortName: 'Translations',
    icon: '/products/runic-translations.png',
    kicker: 'Localization',
    summary:
      'Turn portable translation resources into strongly typed, NativeAOT-ready .NET APIs.',
    description:
      'Runic Translations defines source schemas, message grammar, generated artifacts, and its runtime ABI as portable contracts. Its deterministic compiler, authoring API, generators, build integration, CLI, and NativeAOT runtime begin with .NET without making .NET the boundary of the system.',
    ...releaseMetadata('translations'),
    source: 'https://github.com/Runic-Artifex/runic-translations',
    bestFor: [
      'Deterministic localization builds',
      'Generated strongly typed accessors',
      'Cross-language resource contracts',
      'Supported translation workspace tooling',
    ],
    boundaries: [
      'Independent of every UI framework',
      'The canonical protocol identifier is runic.translations/1',
      'The canonical .NET package family is Runic.Translations.*',
      'The desktop authoring experience and its releases belong to Runic Translations Editor',
    ],
    related: {
      href: '/products/runic-translations-editor/',
      label: 'Explore Translations Editor',
    },
  },
  {
    slug: 'runic-translations-editor',
    name: 'Runic Translations Editor',
    shortName: 'Translations Editor',
    icon: '/products/runic-translations-editor.png',
    kicker: 'Translation authoring',
    summary:
      'Create, translate, review, and validate Runic Translations workspaces in a focused desktop editor.',
    description:
      'Runic Translations Editor gives translators a focused workspace for natural text, variables, variants, workflow status, and validation. It preserves the deterministic Runic Translations resource model without requiring people to edit resource files directly.',
    ...releaseMetadata('editor'),
    source: 'https://github.com/Runic-Artifex/runic-translations-editor',
    bestFor: [
      'Translating and reviewing messages without editing JSON directly',
      'Managing locales, message structure, variables, and plural variants',
      'Validating a workspace before application builds consume it',
    ],
    boundaries: [
      'Consumes Runic Translations packages as an ordinary downstream application',
      'Owns the desktop UX, application packaging, and release cadence',
      'Does not own the compiler, schemas, runtime ABI, generators, or package releases',
    ],
    kind: 'application',
    related: {
      href: '/products/runic-translations/',
      label: 'Explore Runic Translations',
    },
  },
  {
    slug: 'runic-command-line',
    name: 'Runic Command Line',
    shortName: 'Command Line',
    icon: '/products/runic-command-line.png',
    kicker: 'Command applications',
    summary:
      'Build reflection-free NativeAOT command applications with parser-neutral contracts and predictable human and machine output.',
    description:
      'Runic Command Line separates portable command contracts from execution, hosting, and child-process support. It provides command catalogs and predictable output without requiring reflection or committing applications to one parser.',
    ...releaseMetadata('command-line'),
    source: 'https://github.com/Runic-Artifex/runic-command-line',
    bestFor: [
      'NativeAOT command applications',
      'Deterministic machine and human output',
      'Host-neutral command execution',
    ],
    boundaries: [
      'No UI-framework dependency',
      'Parser-neutral abstractions are independently consumable',
      'A future Toolkit adapter remains owned by Command Line',
    ],
  },
];

export const activeProducts = products.filter(
  (product) => product.availability !== 'archived',
);

export function getProduct(slug: string) {
  return products.find((candidate) => candidate.slug === slug);
}
