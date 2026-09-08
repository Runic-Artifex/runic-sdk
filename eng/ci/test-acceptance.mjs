import { run } from "../run.mjs";

// Keep the current behavior suite explicit; historical receipt directories must
// never become CI gates merely because their names start with "current-".
const tests = [
  "./tests/engineering/current/size-command.test.mjs",
];
run("bun", ["test", "--timeout", "180000", ...tests]);
