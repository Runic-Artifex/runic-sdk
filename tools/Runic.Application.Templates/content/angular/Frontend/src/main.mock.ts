(globalThis as { __runicToolkitMock?: boolean }).__runicToolkitMock = true;
const { bootstrapCounterApplication } = await import("./application");
await bootstrapCounterApplication();
export {};
