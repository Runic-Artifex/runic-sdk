// Effect 4.0.0-rc.112 strips this @internal declaration while its public
// internal/command.d.ts still refers to it. Mirror the exact source signature
// until upgrading Effect; keep skipLibCheck=false for the rest of the SDK.
import type { Option } from "effect";
import "effect/unstable/cli/Param";
declare module "effect/unstable/cli/Param" {
  export function getParamMetadata<Kind extends ParamKind, A>(param: Param<Kind, A>): {
    readonly isOptional: boolean;
    readonly isVariadic: boolean;
    readonly variadicMin: Option.Option<number>;
    readonly variadicMax: Option.Option<number>;
  };
}
