import { cp, readFile } from 'node:fs/promises';
import { join } from 'node:path';

const root = process.cwd();
const backup = process.env.RUNNER_TEMP ? join(process.env.RUNNER_TEMP, 'runic-candidate-npm-inputs') : join(root, '.ci', 'npm-inputs');
const { inputs } = JSON.parse(await readFile(join(backup, 'manifest.json'), 'utf8'));
for (const input of inputs) await cp(join(backup, input), join(root, input));
