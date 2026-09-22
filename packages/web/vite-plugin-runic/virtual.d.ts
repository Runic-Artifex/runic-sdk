declare module "virtual:runic/client" {
  export {
    createRunicDiagnosticReporter,
    disposeRunicHmrResource,
    preserveRunicHmrResource,
    reportRunicDiagnostic,
    reportRunicState,
    type RunicDiagnosticDetail,
    type RunicDiagnosticDetailValue,
    type RunicDiagnosticEntry,
    type RunicDiagnosticReporter,
    type RunicDiagnosticSource,
    type RunicRuntimeState,
    type RunicTraceKind,
  } from "@runic-artifex/vite-plugin-runic/client";
}
