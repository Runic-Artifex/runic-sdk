declare module "virtual:runic/client" {
  export {
    createRunicDevtoolsObserver,
    createRunicDiagnosticReporter,
    disposeRunicHmrResource,
    preserveRunicHmrResource,
    reportRunicDiagnostic,
    reportRunicState,
    traceRunicEvent,
    type RunicDevtoolsObserver,
    type RunicDiagnosticDetail,
    type RunicDiagnosticDetailValue,
    type RunicDiagnosticEntry,
    type RunicDiagnosticReporter,
    type RunicDiagnosticSource,
    type RunicRuntimeState,
    type RunicTraceEntry,
    type RunicTraceKind,
  } from "@runic-artifex/vite-plugin-runic/client";
}
