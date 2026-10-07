import * as Effect from "effect/Effect";
import { ViewDomainFailure } from "./errors.js";

type DomainFailureOf<E> = E extends ViewDomainFailure<infer F> ? F : never;
type CaseFailure<E> = Extract<DomainFailureOf<E>, { readonly $case: string }>;
type AnyEffect = Effect.Effect<any, any, any>;
type CaseHandlers<E> = {
  readonly [K in CaseFailure<E>["$case"]]: (failure: Extract<CaseFailure<E>, { readonly $case: K }>) => AnyEffect;
};

/**
 * Handles every case of a declared \`$case\` failure in the error channel:
 * the \`ViewDomainFailure\` of a \`command\` or \`operation\` whose .NET command
 * declares a \`[RunicUnion]\` failure. A missing case is a type error; other
 * errors pass through.
 *
 * ```ts
 * const saved = catchCase(command(() => editor.save()), {
 *   titleRequired: () => Effect.succeed("A note needs a title."),
 *   titleTaken: taken => Effect.succeed(`"${taken.existingTitle}" exists.`),
 * });
 * ```
 */
export function catchCase<A, E, R, const H extends CaseHandlers<E>>(self: Effect.Effect<A, E, R>, cases: H): Effect.Effect<
  A | Effect.Success<ReturnType<H[keyof H]>>,
  Exclude<E, ViewDomainFailure<unknown>> | Effect.Error<ReturnType<H[keyof H]>>,
  R | Effect.Services<ReturnType<H[keyof H]>>> {
  const handlers = cases as unknown as Record<string, ((failure: unknown) => AnyEffect) | undefined>;
  return Effect.catch(self, (error: E) => {
    if (error instanceof ViewDomainFailure) {
      const tag = (error.failure as { readonly $case?: unknown } | null)?.$case;
      const handler = typeof tag === "string" && Object.hasOwn(handlers, tag) ? handlers[tag] : undefined;
      if (handler) return handler(error.failure);
    }
    return Effect.fail(error);
  }) as never;
}
