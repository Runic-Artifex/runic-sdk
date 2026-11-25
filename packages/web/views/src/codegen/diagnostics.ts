// The errors the emitter reports, with the RUNICBRIDGE code of the .NET generator's diagnostic for the same problem.

/** An error in the IR that the emitter cannot write a client for. */
export class ContractIrDiagnostic extends Error {
  /**
   * @param code The diagnostic code, such as `RUNICBRIDGE004`.
   * @param source The `sources` id of the member it concerns, which a producer maps to its source location, when known.
   */
  constructor(readonly code: string, message: string, readonly source?: string) {
    super(`${code}: ${message}`);
    this.name = "ContractIrDiagnostic";
  }
}

/** A generated module declares a name twice. */
export const NAME_COLLISION = "RUNICBRIDGE004";
/** The Views that implement a shared client publish one of its members differently. */
export const SHARED_CLIENT = "RUNICBRIDGE036";
/** A region collection's client method collides with another member of the client. */
export const REGION_COLLECTION_NAME = "RUNICBRIDGE071";
