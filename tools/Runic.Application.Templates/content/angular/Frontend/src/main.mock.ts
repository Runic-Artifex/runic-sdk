(globalThis as { __runicApplicationMock?: boolean }).__runicApplicationMock = true;
const { bootstrapCounterApplication } = await import("./application");
await bootstrapCounterApplication();
export {};
