import {readFileSync, writeFileSync} from 'node:fs';
import {bindTemplateLock, readNpmCandidates, verifyTemplateLock} from '../../eng/release/template-locks.mjs';

// Checks a generated template's package-lock.json, pnpm-lock.yaml or bun.lock against the candidate archives, then
// binds it to the acceptance registry (RUNIC_TEMPLATE_NPM_REGISTRY) and to the train candidates of the independently
// released Runic packages (bindTemplateLock).
const [lockPath, ...archives] = process.argv.slice(2);
if (!lockPath || !archives.length) throw new Error('Usage: bind-template-candidate-integrities.mjs <lockfile> <npm-archive>...');
const text = readFileSync(lockPath, 'utf8');
const candidates = readNpmCandidates(archives);
verifyTemplateLock(text, lockPath, candidates);
const bound = bindTemplateLock(text, lockPath, candidates, process.env.RUNIC_TEMPLATE_NPM_REGISTRY);
if (bound !== text) writeFileSync(lockPath, bound);
