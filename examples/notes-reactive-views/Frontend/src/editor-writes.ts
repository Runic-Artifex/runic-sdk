/** Keeps field writes ahead of commands in one mounted editor. */
export class EditorWrites {
  private tail: Promise<void> = Promise.resolve();
  private failure: unknown;

  constructor(private readonly report: (cause: unknown | undefined) => void) {}

  enqueue(write: () => Promise<unknown>): void {
    this.tail = this.tail.then(async () => {
      try {
        await write();
        this.failure = undefined;
        this.report(undefined);
      } catch (cause) {
        this.failure = cause;
        this.report(cause);
      }
    });
  }

  async run(command: () => Promise<unknown>): Promise<void> {
    await this.tail;
    if (this.failure !== undefined) throw this.failure;
    await command();
  }
}
