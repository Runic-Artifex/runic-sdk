// Types for tests/support/timeouts.mjs.
export declare const variable: "RUNIC_TEST_TIMEOUT_SCALE";
export declare function readScale(value: string | undefined): number;
export declare const timeoutScale: number;
export declare function scaled(milliseconds: number): number;
export declare function reportIfSlow(elapsed: number, unscaled: number, label?: string): void;
export interface ScaledWait {
  readonly unscaled: number;
  readonly limit: number;
  elapsed(): number;
  remaining(): number;
  expired(): boolean;
  succeeded(): void;
}
export declare function startWait(unscaled: number, label?: string): ScaledWait;
export declare function withinScaled<T>(promise: PromiseLike<T> | T, unscaled: number, label?: string): Promise<T>;
