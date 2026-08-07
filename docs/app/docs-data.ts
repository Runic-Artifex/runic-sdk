export type Product = {
  slug: string;
  name: string;
  shortName: string;
  mark: string;
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
};

export const products: Product[] = [
  {
    slug: "runic-toolkit", name: "Runic Toolkit", shortName: "Toolkit", mark: "RT",
    kicker: "Application composition",
    summary: "A NativeAOT-first toolbelt for lifecycle, hosting, desktop contracts, frontend builds, and explicit application bridges.",
    description: "Runic Toolkit composes the same application model across desktop windows, browser frontends, Generic Host, and framework-specific UI adapters. Its domain-oriented Application Bridge is being proven before the first public release.",
    version: "0.1.0-preview.4.1", source: "https://github.com/Runic-Artifex/runic-toolkit",
    bestFor: ["Composing .NET desktop and browser applications", "NativeAOT-safe application hosting", "Sharing frontend-neutral lifecycle and hosting contracts"],
    boundaries: ["Does not own UI languages, flow, command-line parsing, localization, or assets", "Uses named domain commands and events rather than exposing ViewModels as the public application contract", "Application Bridge packages remain outside the public train until their Setup vertical passes"],
    packages: ["RunicToolkit.Collections", "RunicToolkit.Desktop", "RunicToolkit.Frontend.Sdk", "RunicToolkit.Hosting.Abstractions", "RunicToolkit.Hosting.Build", "RunicToolkit.Hosting.CsWebUi", "RunicToolkit.Hosting.CsWebUi.App", "RunicToolkit.Hosting.Generators", "RunicToolkit.Hosting.GenericHost", "RunicToolkit.Hosting.WebUi", "RunicToolkit.Hosting", "RunicToolkit.DotNet.RunicToolkit"],
    install: ["dotnet add package RunicToolkit.Hosting.CsWebUi.App --version 0.1.0-preview.4.1"],
  },
  {
    slug: "cs-webui", name: "CsWebUi", shortName: "CsWebUi", mark: "CW",
    kicker: "Native host",
    summary: "Modern .NET bindings for WebUI with safe window ownership, callbacks, embedded assets, and NativeAOT support.",
    description: "CsWebUi uses an installed browser or supported WebView as a lightweight cross-platform desktop UI. The raw package mirrors WebUI's C ABI; the high-level package adds deterministic managed ownership and safer application APIs.",
    version: "2.5.0-beta.4.3", source: "https://github.com/Runic-Artifex/cs-webui",
    bestFor: ["Small cross-platform desktop hosts backed by web technology", "Direct WebUI interop without a larger application framework", "Dynamic or opt-in static NativeAOT deployment"],
    boundaries: ["Tracks the WebUI 2.5 beta ABI", "Remains independently useful without Runic Toolkit", "Raw and safe packages are intentionally separate"],
    packages: ["CsWebUi.Native", "CsWebUi"], install: ["dotnet add package CsWebUi --version 2.5.0-beta.4.3"],
  },
  {
    slug: "runic-flow", name: "Runic Flow", shortName: "Flow", mark: "RF",
    kicker: "Application mechanics",
    summary: "Typed navigation, dialogs, coordinated operations, workflows, presentation contracts, and official UI integrations.",
    description: "Runic Flow provides framework-neutral application mechanics. Its core targets trimming and NativeAOT; adapters translate Flow concepts into CommunityToolkit or Runic Toolkit without reversing the dependency direction.",
    version: "0.1.0-preview.4.1", source: "https://github.com/Runic-Artifex/runic-flow",
    bestFor: ["Typed navigation and dialog contracts", "Coordinated application operations and workflows", "Portable presentation mechanics shared across UI frameworks"],
    boundaries: ["Core has no UI-framework dependency", "Flow owns RunicFlow.RunicToolkit", "Adapters depend on Flow plus their target framework"],
    packages: ["RunicFlow", "RunicFlow.Generators", "RunicFlow.CommunityToolkit", "RunicFlow.RunicToolkit"], install: ["dotnet add package RunicFlow --version 0.1.0-preview.4.1"],
  },
  {
    slug: "runic-assets", name: "Runic Assets", shortName: "Assets", mark: "RA",
    kicker: "Shared infrastructure",
    summary: "Safe paths, immutable manifests, portable archives, development sources, and adapters for ASP.NET Core, CsWebUi, and Toolkit.",
    description: "Runic Assets defines a transport-neutral static-asset model. A canonical standard-ZIP format and strict path validation let the same manifest move through embedded, development, browser, and server hosts.",
    version: "0.1.0-preview.5.1", source: "https://github.com/Runic-Artifex/runic-assets",
    bestFor: ["Sharing static assets across hosts", "Deterministic embedded and development sources", "Portable, validated asset archives"],
    boundaries: ["Core has no UI or web framework dependency", "Host delivery lives in owned adapters", "Archive format is documented separately from host behavior"],
    packages: ["RunicAssets", "RunicAssets.CsWebUi", "RunicAssets.AspNetCore", "RunicAssets.RunicToolkit"], install: ["dotnet add package RunicAssets --version 0.1.0-preview.5.1"],
  },
  {
    slug: "runic-text-resources", name: "Runic Text Resources", shortName: "Text Resources", mark: "TR",
    kicker: "Localization",
    summary: "Language-neutral localization contracts with a deterministic compiler, NativeAOT runtime, source generator, MSBuild integration, and CLI.",
    description: "Runic Text Resources treats source schemas, message grammar, generated artifacts, and runtime ABI as portable contracts. The .NET implementation is the first runtime, not the boundary of the framework.",
    version: "0.1.0-preview.2.1", source: "https://github.com/Runic-Artifex/runic-text-resources",
    bestFor: ["Deterministic localization builds", "Generated strongly typed accessors", "Cross-language resource contracts"],
    boundaries: ["Independent of every UI framework", "Portable protocol family is runic.textresources/1", "Schema compatibility evolves separately from package versions"],
    packages: ["RunicTextResources", "RunicTextResources.Compiler", "RunicTextResources.Generator", "RunicTextResources.Build", "RunicTextResources.Tool"], install: ["dotnet add package RunicTextResources --version 0.1.0-preview.2.1"],
  },
  {
    slug: "runic-command-line", name: "Runic Command Line", shortName: "Command Line", mark: "CL",
    kicker: "Command applications",
    summary: "A parser-neutral, reflection-free framework for command catalogs, execution, deterministic output, hosting, and bounded processes.",
    description: "Runic Command Line exists because NativeAOT-focused applications need a smaller and more explicit command surface. Portable contracts stay separate from execution, hosting classification, and child-process support.",
    version: "0.1.0-preview.3.1", source: "https://github.com/Runic-Artifex/runic-command-line",
    bestFor: ["NativeAOT command applications", "Deterministic machine and human output", "Host-neutral command execution"],
    boundaries: ["No UI-framework dependency", "Parser-neutral abstractions are independently consumable", "A future Toolkit adapter remains owned by Command Line"],
    packages: ["RunicCommandLine.Abstractions", "RunicCommandLine", "RunicCommandLine.Hosting", "RunicCommandLine.Processes"], install: ["dotnet add package RunicCommandLine --version 0.1.0-preview.3.1"],
  },
];

export function getProduct(slug: string) {
  const product = products.find((candidate) => candidate.slug === slug);
  if (!product) throw new Error(`Unknown product '${slug}'.`);
  return product;
}
