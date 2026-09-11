import { activeVersionForProduct } from '$lib/release-docs';

type ReleaseProductId = string;
type ReleaseMetadata = {
  releaseProduct: string;
  version: string | null;
  versionState: 'published' | 'unpublished' | 'unassigned';
  availability: 'active';
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
  versionState: 'published' | 'unpublished' | 'unassigned';
  source: string;
  bestFor: string[];
  boundaries: string[];
  availability?: 'active' | 'archived' | 'independent';
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
  return {
    releaseProduct,
    version: version.value,
    versionState: version.state,
    availability: 'active',
  };
}

export const products: Product[] = [
  {
    slug: 'runic-flow',
    name: 'Runic Flow',
    shortName: 'Flow',
    icon: '/products/runic-toolkit.png',
    kicker: 'Historical project',
    summary: 'Historical Runic Flow information and migration guidance.',
    description:
      'Runic Flow is retired. New applications should use Runic Application and its Application Bridge for frontend commands and events.',
    releaseProduct: null,
    version: null,
    versionState: 'unassigned',
    availability: 'archived',
    source:
      'https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/releases/preview-migration.md',
    bestFor: ['Understanding an older Runic integration before migrating'],
    boundaries: [
      'No current SDK package or forwarding package is published under the Runic Flow name',
    ],
  },
  {
    slug: 'runic-toolkit',
    name: 'Runic Application',
    shortName: 'Application',
    icon: '/products/runic-toolkit.png',
    kicker: 'Application composition',
    summary:
      'Compose desktop windows, browser frontends, and .NET hosting around one application model with NativeAOT-safe application contracts.',
    description:
      'Runic Application connects desktop windows, browser frontends, and .NET hosting around one application model. Its Application Bridge carries named commands and validated events between a frontend and a NativeAOT-safe .NET host.',
    ...releaseMetadata('application'),
    source:
      'https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/dotnet/Runic.Application',
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
      'Runic Desktop hosts browser and embedded-WebView presentations. Linux applications explicitly choose GTK3/WebKitGTK 4.1 or the optional GTK4/WebKitGTK 6 adapter. Windows NativeAOT embeds the WebView2 loader; the Edge WebView2 Runtime remains a prerequisite. Platform adapters add file dialogs, notifications, appearance settings, application handoff and inhibition.',
    ...releaseMetadata('desktop'),
    source:
      'https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/dotnet/Runic.Desktop',
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
      'Is maintained and released independently of the SDK release set',
      'Is not the implementation underneath Runic Desktop',
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
    source:
      'https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/dotnet/Runic.Assets',
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
      'Turn a conventional MessageFormat 2 project into typed C# and tree-shakable TypeScript APIs.',
    description:
      'Runic Translations discovers one translations/runic.json project with locale-scoped MessageFormat 2 files. Its deterministic compiler generates identifier-safe message calls, locale metadata, request-local SSR support, typed C# and tree-shakable ESM while keeping its runtime ABI portable and NativeAOT-ready.',
    ...releaseMetadata('translations'),
    source:
      'https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/dotnet/Runic.Translations',
    bestFor: [
      'Deterministic localization builds',
      'MessageFormat 2 authoring with generated m.message_id() calls',
      'Generated locale configuration and request-safe SSR',
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
      label: 'Explore the source-only Editor',
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
      'Runic Translations Editor opens the same runic.json and MessageFormat 2 files as the compiler. It gives translators a focused workspace for natural text, variables, variants, workflow status, and validation without defining a second authoring format.',
    ...releaseMetadata('editor'),
    source:
      'https://github.com/Runic-Artifex/runic-sdk/tree/main/apps/translations-editor',
    bestFor: [
      'Translating and reviewing MessageFormat 2 projects visually',
      'Managing locales, message structure, variables, and plural variants',
      'Validating a workspace before application builds consume it',
    ],
    boundaries: [
      'Consumes Runic Translations packages as an ordinary downstream application',
      'Owns desktop UX; standalone Editor distributions are outside the SDK preview',
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
      'Runic Command Line generates NativeAOT-ready commands from ordinary typed C# methods. Help, validation, completion, environment fallbacks and shared options work in standalone tools and hosted Runic applications. Add Runic.CommandLine.Spectre for styled help, progress and prompts; machine output remains structured and predictable.',
    ...releaseMetadata('command-line'),
    source:
      'https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/dotnet/Runic.CommandLine',
    bestFor: [
      'NativeAOT command applications',
      'Deterministic machine and human output',
      'The same command behavior in standalone tools and hosted applications',
      'Optional Spectre.Console help, progress and prompts',
    ],
    boundaries: [
      'The core has no Spectre.Console dependency; presentation is an optional package',
      'Parser-neutral abstractions are independently consumable',
    ],
  },
];

export const activeProducts = products.filter(
  (product) =>
    product.availability !== 'archived' && product.kind !== 'application',
);

export function getProduct(slug: string) {
  return products.find((candidate) => candidate.slug === slug);
}
