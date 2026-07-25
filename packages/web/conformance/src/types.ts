export const CONFORMANCE_FORMAT = "webuitoolkit.mvvm.conformance/1" as const;

export type FixtureData = string | Uint8Array;

export interface FixtureSource {
  read(path: string): Promise<FixtureData>;
}

export type ConformanceStatus = "passed" | "failed" | "skipped";

export interface ConformanceDiagnostic {
  readonly code: string;
  readonly message: string;
  readonly step?: number;
  readonly expected?: unknown;
  readonly actual?: unknown;
}

export interface ConformanceCaseResult {
  readonly id: string;
  readonly suite: string;
  readonly status: ConformanceStatus;
  readonly diagnostics: readonly ConformanceDiagnostic[];
}

export interface ConformanceTotals {
  readonly total: number;
  readonly passed: number;
  readonly failed: number;
  readonly skipped: number;
}

export interface ConformanceReport {
  readonly format: typeof CONFORMANCE_FORMAT;
  readonly protocolIdentity: "webuitoolkit.mvvm/1";
  readonly runtime: string;
  readonly success: boolean;
  readonly totals: ConformanceTotals;
  readonly cases: readonly ConformanceCaseResult[];
}

export interface CorpusManifestCase {
  readonly id: string;
  readonly file: string;
  readonly schema: "client" | "host";
  readonly documentMode: "single" | "eachItem";
  readonly valid: boolean;
  readonly reason: string;
}

export interface CorpusSemanticCase {
  readonly id: string;
  readonly file: string;
  readonly reason: string;
}

export interface ProtocolCorpusManifest {
  readonly protocolIdentity: "webuitoolkit.mvvm/1";
  readonly cases: readonly CorpusManifestCase[];
  readonly semanticCases: readonly CorpusSemanticCase[];
}

export interface ConformanceSuiteManifestEntry {
  readonly id: string;
  readonly file: string;
  readonly caseProperty: string;
  readonly caseCount: number;
}

export interface FixtureIntegrityEntry {
  readonly path: string;
  readonly sha256: string;
  readonly bytes: number;
}

export interface ConformanceFixtureManifest {
  readonly formatVersion: number;
  readonly protocolIdentity: "webuitoolkit.mvvm/1";
  readonly suites: readonly ConformanceSuiteManifestEntry[];
  readonly files?: readonly FixtureIntegrityEntry[];
}

export interface ScenarioStep {
  readonly action: string;
  readonly expect: Readonly<Record<string, unknown>>;
  readonly [key: string]: unknown;
}

export interface ConformanceScenario {
  readonly id: string;
  readonly initial: Readonly<Record<string, unknown>>;
  readonly steps: readonly ScenarioStep[];
}

export interface ScenarioDocument {
  readonly format: "webuitoolkit.mvvm.conformance-scenarios/1";
  readonly protocolIdentity: "webuitoolkit.mvvm/1";
  readonly category: string;
  readonly scenarios: readonly ConformanceScenario[];
}

export interface ScenarioContext {
  readonly suite: string;
  readonly scenario: ConformanceScenario;
}

export interface ScenarioDriver {
  perform(step: ScenarioStep, stepIndex: number): Promise<unknown> | unknown;
  close?(): Promise<void> | void;
}

export interface SemanticCaseContext {
  readonly id: string;
  readonly reason: string;
  readonly document: unknown;
}

export interface RuntimeCaseOutcome {
  readonly passed: boolean;
  readonly diagnostics?: readonly ConformanceDiagnostic[];
}

export interface HostileInputContext {
  readonly id: string;
  readonly bytes: Uint8Array;
  readonly expected: Readonly<Record<string, unknown>>;
}

export interface ConformanceRuntimeAdapter {
  readonly name: string;
  createScenarioDriver?(context: ScenarioContext): Promise<ScenarioDriver> | ScenarioDriver;
  runSemanticCase?(context: SemanticCaseContext): Promise<RuntimeCaseOutcome> | RuntimeCaseOutcome;
  testHostileInput?(context: HostileInputContext): Promise<unknown> | unknown;
}

export interface RunConformanceOptions {
  readonly source: FixtureSource;
  readonly runtime?: ConformanceRuntimeAdapter;
  readonly manifestPath?: string;
  readonly protocolManifestPath?: string;
}
