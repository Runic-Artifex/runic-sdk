// Declarations of the JavaScript workspace resolver (runic-frontend-graph.mjs), the `./frontend-graph` export.

/** The format of the graph: `rax.frontend-graph/1`. */
export declare const SCHEMA: "rax.frontend-graph/1";

export interface FrontendGraphPackage {
  /** The package folder, absolute and canonical. */
  readonly directory: string;
  /** The package's `name`, or null when its package.json has none. */
  readonly package: string | null;
}

/** A frontend package: the frontend itself (`application`) or one that it depends on (`packages`). */
export interface FrontendPackage extends FrontendGraphPackage {
  /** `compiled` when its package.json has a `build` script, else `source`. */
  readonly kind: "compiled" | "source";
  /** The command that runs its `build` script with the workspace's package manager, or null. */
  readonly build: string | null;
  /** The command that runs its `watch` script, or null. */
  readonly watch: string | null;
  /** The files that its `exports`, `main`, `module` and `types` name, relative (`./dist/index.js`); patterns are left out. */
  readonly exports: readonly string[];
  /** The folder of its `./generated` export's types (else default), relative, or null. */
  readonly generatedExport: string | null;
  /** The folders that a compiled package's build writes: the first folder of each export other than `src`. */
  readonly outputs: readonly string[];
}

export interface FrontendGraph {
  readonly schema: "rax.frontend-graph/1";
  /** The workspace root, or the frontend's own folder when it is not in a workspace. */
  readonly workspaceRoot: string;
  readonly packageManager: string | null;
  /** The lock file at the workspace root, or null. */
  readonly lockFile: string | null;
  /** Whether the workspace's patterns include the frontend (always true without a workspace). */
  readonly member: boolean;
  readonly application: FrontendPackage;
  /** The workspace's member packages, in pattern order. */
  readonly members: readonly FrontendGraphPackage[];
  /**
   * The local frontend packages that the frontend depends on, dependencies first: workspace members that a dependency
   * names (by `workspace:`, or by name) and folders (`file:`, `link:`, `portal:`), transitively.
   */
  readonly packages: readonly FrontendPackage[];
}

/** The graph of one frontend; without `root`, the nearest folder above it that declares a workspace. */
export declare function resolveFrontendGraph(options: { frontend: string; root?: string; packageManager?: string }): FrontendGraph;
/** The nearest folder above `directory` whose package.json has `workspaces` or that has pnpm-workspace.yaml. */
export declare function findWorkspaceRoot(directory: string): string | undefined;
/** The patterns of a workspace root: package.json `workspaces`, else pnpm-workspace.yaml `packages`. */
export declare function workspacePatterns(root: string): string[];
/** The member folders of the workspace at `root`, absolute and canonical, in pattern order. */
export declare function workspaceMembers(root: string): string[];
/** A frontend package as the graph lists it; `packageManager` selects the build and watch commands. */
export declare function describePackage(directory: string, packageManager: string): FrontendPackage;
/** The local frontend packages that `frontend` depends on, dependencies first, given the workspace's members. */
export declare function dependencyPackages(frontend: string, members: readonly FrontendGraphPackage[], packageManager: string): FrontendPackage[];
/** The command that runs a package script with a package manager, as the Views targets run it. */
export declare function scriptCommand(packageManager: string, script: string): string;
