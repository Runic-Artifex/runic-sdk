// The runtime API of generated clients. Application code imports
// `@runic-artifex/views` instead; this entry may change with the generator.
//
// Interactions, operations and keyed collections are separate exports that a
// generated client passes to `connectView` only when it has them, so a View
// without them does not bundle their protocol code. The wire decoders are
// the separate `@runic-artifex/views/generated/wire` entry, which generated
// code imports as a namespace (`import * as bridgeWire`), so bundlers keep
// only the decoders a module calls.

export { decodeBridgeValidation } from "./validation.js";
export { connectView, viewReferences, type ViewConnection, type ViewConnectOptions } from "./connection.js";
export type { BridgeOperationRuntimeHandle } from "./operations.js";
export { defineCollection, defineCollections, type BridgeCollectionDefinition, type BridgeCollections } from "./collections.js";
export {
  defineInteractions,
  type BridgeInteractions,
  type InteractionDefinition,
  type InteractionScope,
  type InteractionSession,
} from "./interactions.js";
export { bridgeOperations, type BridgeOperations, type OperationScope } from "./operations.js";
