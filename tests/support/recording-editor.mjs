// A recording editor for open-in-editor tests: a script that LAUNCH_EDITOR names, which launch-editor calls (for an
// editor it does not know) with the file, line and column, and which writes them to a record file, one per line.
//
// The script writes `<record>.part` and renames it to `<record>`, so the record appears only once its arguments are in
// it (#1010): a shell redirection creates the file before it writes, and a reader that waits for the file to exist
// could read it empty. `readRecord` waits for a complete record, returns its lines and removes it for the next launch.
import { chmod, readFile, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { scaled } from "./timeouts.mjs";

/**
 * Writes the recording editor into `directory`. Returns its path, the record's path and the environment that makes
 * launch-editor use it (`LAUNCH_EDITOR`, and `RUNIC_TEST_EDITOR_RECORD` for the script).
 */
export async function recordingEditor(directory) {
  const editor = join(directory, "record-editor.sh");
  const record = join(directory, "editor-arguments.txt");
  await writeFile(editor,
    '#!/bin/sh\nprintf "%s\\n" "$@" > "$RUNIC_TEST_EDITOR_RECORD.part" && mv -f "$RUNIC_TEST_EDITOR_RECORD.part" "$RUNIC_TEST_EDITOR_RECORD"\n');
  await chmod(editor, 0o755);
  return { editor, record, environment: { LAUNCH_EDITOR: editor, RUNIC_TEST_EDITOR_RECORD: record } };
}

/**
 * The arguments of the next launch, `[file, line, column]`: waits until the record holds them, then removes it. Throws
 * when no editor was launched within `timeout`.
 */
export async function readRecord(record, timeout = scaled(10_000)) {
  const deadline = Date.now() + timeout;
  for (;;) {
    const text = await readFile(record, "utf8").catch(() => "");
    // The rename makes a record complete when it appears; an empty read is still not taken as one.
    if (text.endsWith("\n")) {
      await rm(record, { force: true });
      return text.trimEnd().split("\n");
    }
    if (Date.now() > deadline) throw new Error(`No editor was launched: ${record} holds no arguments.`);
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

/** Whether an editor was launched since the record was last read. */
export function wasLaunched(record) {
  return stat(record).then(() => true, () => false);
}
