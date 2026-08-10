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
      '/products/runic-translations' | '/products/runic-translations-editor';
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
      'A NativeAOT-first toolbelt for lifecycle, hosting, desktop contracts, frontend builds, and explicit application bridges.',
    description:
      'Runic Toolkit composes the same application model across desktop windows, browser frontends, Generic Host, and framework-owned UI state. Its official Application Bridge carries named domain commands and validated events through one NativeAOT-safe host boundary.',
    version: 'Candidate refresh pending',
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
  },
  {
    slug: 'cs-webui',
    name: 'CsWebUi',
    shortName: 'CsWebUi',
    icon: '/products/cs-webui.png',
    kicker: 'Native host',
    summary:
      'Modern .NET bindings for WebUI with safe window ownership, callbacks, embedded assets, and NativeAOT support.',
    description:
      "CsWebUi uses an installed browser or supported WebView as a lightweight cross-platform desktop UI. The raw package mirrors WebUI's C ABI; the high-level package adds deterministic managed ownership and safer application APIs.",
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
      'A deterministic, headless runtime for application processes and coordinated backend operations.',
    description:
      'Runic Flow provides typed command decisions, serialized state commits, process-local versions, opaque checkpoints, concurrency slots, timeouts, progress, cancellation, and terminal outcomes without owning UI state.',
    version: 'First headless candidate pending',
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
  },
  {
    slug: 'runic-assets',
    name: 'Runic Assets',
    shortName: 'Assets',
    icon: '/products/runic-assets.png',
    kicker: 'Shared infrastructure',
    summary:
      'Safe paths, immutable manifests, portable archives, development sources, and adapters for ASP.NET Core, CsWebUi, and Toolkit.',
    description:
      'Runic Assets defines a transport-neutral static-asset model. A canonical standard-ZIP format and strict path validation let the same manifest move through embedded, development, browser, and server hosts.',
    version: 'Candidate refresh pending',
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
  },
  {
    slug: 'runic-translations',
    name: 'Runic Translations',
    shortName: 'Translations',
    icon: '/products/runic-translations.png',
    kicker: 'Localization',
    summary:
      'Language-neutral localization contracts with a deterministic compiler, NativeAOT runtime, authoring API, generators, build integration, and CLI.',
    description:
      'Runic Translations treats source schemas, message grammar, generated artifacts, and runtime ABI as portable contracts. The .NET implementation is the first runtime, not the boundary of the framework; package and protocol identifiers now use the canonical Translations naming.',
    version: 'Candidate refresh pending',
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
    related: {
      href: '/products/runic-translations-editor',
      label: 'Open the editor product',
    },
  },
  {
    slug: 'runic-translations-editor',
    name: 'Runic Translations Editor',
    shortName: 'Translations Editor',
    icon: '/products/runic-translations-editor.png',
    kicker: 'Translation authoring',
    summary:
      'A focused desktop editor for creating, translating, reviewing, and validating Runic Translations workspaces.',
    description:
      'Runic Translations Editor is the human-facing authoring environment for Runic Translations. Translators work with natural text, variables, variants, workflow status, and validation while the application preserves the deterministic resource model underneath.',
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
      href: '/products/runic-translations',
      label: 'Explore the underlying translation system',
    },
  },
  {
    slug: 'runic-command-line',
    name: 'Runic Command Line',
    shortName: 'Command Line',
    icon: '/products/runic-command-line.png',
    kicker: 'Command applications',
    summary:
      'A parser-neutral, reflection-free framework for command catalogs, execution, deterministic output, hosting, and bounded processes.',
    description:
      'Runic Command Line exists because NativeAOT-focused applications need a smaller and more explicit command surface. Portable contracts stay separate from execution, hosting classification, and child-process support.',
    version: '0.1.0-preview.3.1',
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
      'dotnet add package RunicCommandLine --version 0.1.0-preview.3.1',
    ],
  },
];

export function getProduct(slug: string) {
  return products.find((candidate) => candidate.slug === slug);
}
