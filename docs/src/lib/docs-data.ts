export type Product = {
  slug: string;
  name: string;
  shortName: string;
  icon: string;
  kicker: string;
  summary: string;
  description: string;
  version: string;
  source: string;
  bestFor: string[];
  boundaries: string[];
  packages: string[];
  npmPackages?: string[];
  install?: string[];
  kind?: 'package-family' | 'application';
  artifacts?: string[];
  related?: {
    href:
      '/products/runic-translations/' | '/products/runic-translations-editor/';
    label: string;
  };
};

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
    version: '0.1.0-preview.30.1',
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
    packages: [
      'RunicToolkit.ApplicationBridge',
      'RunicToolkit.ApplicationBridge.Generators',
      'RunicToolkit.Collections',
      'RunicToolkit.Desktop',
      'RunicToolkit.Hosting.Abstractions',
      'RunicToolkit.Hosting.Build',
      'RunicToolkit.Hosting.CsWebUi',
      'RunicToolkit.Hosting.CsWebUi.App',
      'RunicToolkit.Hosting.CsWebUi.ApplicationBridge',
      'RunicToolkit.Hosting.Generators',
      'RunicToolkit.Hosting.GenericHost',
      'RunicToolkit.Hosting.WebUi',
      'RunicToolkit.Hosting',
      'RunicToolkit.DotNet.RunicToolkit',
      'RunicToolkit.Templates',
    ],
    npmPackages: ['@runic-artifex/application-bridge'],
    install: [
      'dotnet add package RunicToolkit.Hosting --version 0.1.0-preview.30.1',
      'npm install @runic-artifex/application-bridge@0.1.0-preview.30.1',
    ],
  },
  {
    slug: 'cs-webui',
    name: 'CS-WebUI',
    shortName: 'CS-WebUI',
    icon: '/products/cs-webui.png',
    kicker: 'Native host',
    summary:
      'Host a web-powered cross-platform desktop UI in a native window using an installed browser or supported WebView.',
    description:
      'CS-WebUI provides .NET bindings for WebUI. The raw package follows the WebUI C ABI, while the high-level package adds deterministic managed ownership, safer callbacks and window APIs, custom HTTP responses for asset integrations, and NativeAOT support.',
    version: '2.5.0-beta.4.4',
    source: 'https://github.com/Runic-Artifex/cs-webui',
    bestFor: [
      'Small cross-platform desktop hosts backed by web technology',
      'Direct WebUI interop without a larger application framework',
      'Dynamic or opt-in static NativeAOT deployment',
    ],
    boundaries: [
      'Tracks the WebUI 2.5 beta ABI',
      'Remains independently useful without Runic Toolkit',
      'Raw and safe packages are intentionally separate',
    ],
    packages: ['CsWebUi.Native', 'CsWebUi'],
    install: ['dotnet add package CsWebUi --version 2.5.0-beta.4.4'],
  },
  {
    slug: 'runic-flow',
    name: 'Runic Flow',
    shortName: 'Flow',
    icon: '/products/runic-flow.png',
    kicker: 'Headless orchestration',
    summary:
      'Coordinate predictable, UI-independent work with typed decisions, progress, cancellation, and deterministic state commits.',
    description:
      'Runic Flow coordinates headless application work through typed command decisions and serialized state commits. It tracks process-local versions, opaque checkpoints, concurrency slots, timeouts, progress, cancellation, and terminal outcomes without taking ownership of UI state.',
    version: '0.1.0-preview.19.1',
    source: 'https://github.com/Runic-Artifex/runic-flow',
    bestFor: [
      'Deterministic application processes',
      'Coordinated backend operations with progress and cancellation',
      'Headless orchestration shared across application surfaces',
    ],
    boundaries: [
      'Core has no Runic Toolkit or frontend-framework dependency',
      'Does not provide navigation, dialogs, presenters, ViewModel activation, routing, or a generic wire protocol',
      'Flow owns its Runic Toolkit Application Bridge integration',
    ],
    packages: ['RunicFlow', 'RunicFlow.ApplicationBridge'],
    install: ['dotnet add package RunicFlow --version 0.1.0-preview.19.1'],
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
    version: '0.1.0-preview.24.1',
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
    packages: [
      'RunicAssets',
      'RunicAssets.CsWebUi',
      'RunicAssets.AspNetCore',
      'RunicAssets.RunicToolkit',
    ],
    install: ['dotnet add package RunicAssets --version 0.1.0-preview.24.1'],
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
    version: '0.1.0-preview.8.1',
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
      'The canonical .NET package family is RunicTranslations.*',
      'The desktop authoring experience and its releases belong to Runic Translations Editor',
    ],
    packages: [
      'RunicTranslations',
      'RunicTranslations.Compiler',
      'RunicTranslations.Authoring',
      'RunicTranslations.Generator',
      'RunicTranslations.Build',
      'RunicTranslations.Tool',
      'RunicTranslations.Templates',
    ],
    npmPackages: ['@runic-artifex/vite-plugin-runic-translations'],
    install: [
      'dotnet add package RunicTranslations --version 0.1.0-preview.8.1',
      'npm install @runic-artifex/vite-plugin-runic-translations@0.1.0-preview.8.1',
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
    version: 'First preview pending',
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
    packages: [],
    kind: 'application',
    artifacts: [
      'Linux x64 self-contained archive',
      'macOS arm64 self-contained archive',
      'Windows x64 self-contained archive',
    ],
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
    version: '0.1.0-preview.5.1',
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
    packages: [
      'RunicCommandLine.Abstractions',
      'RunicCommandLine',
      'RunicCommandLine.Hosting',
      'RunicCommandLine.Processes',
    ],
    install: [
      'dotnet add package RunicCommandLine --version 0.1.0-preview.5.1',
    ],
  },
];

export function getProduct(slug: string) {
  return products.find((candidate) => candidate.slug === slug);
}
