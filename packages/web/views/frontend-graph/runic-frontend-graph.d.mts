// Declarations of the JavaScript workspace resolver (runic-frontend-graph.mjs), the `./frontend-graph` export.

/** The format of the graph: `rax.frontend-graph/1`. */
export declare const SCHEMA: "rax.frontend-graph/1";

export interface FrontendGraphPackage {
  /** The package folder, absolute and canonical. */
  readonly directory: string;
  /** The package's `name`, or null when its package.json has none. */
  readonly package: string | null;
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
  readonly application: FrontendGraphPackage;
  /** The workspace's member packages, in pattern order. */
  readonly members: readonly FrontendGraphPackage[];
  /** The frontend packages that the application depends on (filled from 0.24's library package build on). */
  readonly packages: readonly unknown[];
}

/** The graph of one frontend; without `root`, the nearest folder above it that declares a workspace. */
export declare function resolveFrontendGraph(options: { frontend: string; root?: string; packageManager?: string }): FrontendGraph;
/** The nearest folder above `directory` whose package.json has `workspaces` or that has pnpm-workspace.yaml. */
export declare function findWorkspaceRoot(directory: string): string | undefined;
/** The patterns of a workspace root: package.json `workspaces`, else pnpm-workspace.yaml `packages`. */
export declare function workspacePatterns(root: string): string[];
/** The member folders of the workspace at `root`, absolute and canonical, in pattern order. */
export declare function workspaceMembers(root: string): string[];
