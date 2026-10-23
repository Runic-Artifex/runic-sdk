// `@runic-artifex/views/mock`: an in-memory Bridge for frontend tests, and the typed mock
// shapes that generated `*.mock.ts` files return. The generated mocks build on
// `@runic-artifex/views/generated/mock`, which follows the generator.
export {
  createMockBridge,
  installMockBridge,
  mockFailure,
  type MockBridge,
  type MockBridgeOptions,
  type MockCall,
  type MockCollection,
  type MockCollectionChange,
  type MockDomainFailure,
  type MockErrorFailure,
  type MockFailure,
  type MockInteractionOptions,
  type MockInteractionReply,
  type MockOperation,
  type MockOperationHandler,
  type MockOperationKind,
  type MockOperationOutcome,
  type MockRoute,
  type MockScheduling,
  type MockState,
  type MockView,
  type MockViewDefinition,
} from "./mock-bridge.js";
export type {
  MockCommandHandler,
  MockFailureOf,
  MockReference,
  MockSetterHandler,
  MockTypedCollection,
  MockTypedInteraction,
  MockTypedOperation,
  MockTypedOperationHandler,
  MockTypedOperationOutcome,
  MockTypedView,
} from "./mock-typed.js";
export { installMockMenuBar, type MockMenuBar, type MockMenuBarOptions } from "./mock-menu-bar.js";
