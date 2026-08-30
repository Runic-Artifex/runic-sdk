#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { mkdir, readFile, realpath, writeFile } from 'node:fs/promises';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import prettier from 'prettier';
import { authorityRevision } from './release-authority.mjs';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const defaultManifest = resolve(
  process.env.RUNIC_RELEASE_MANIFEST ?? '../.github/runic.release.json',
);
const configuredCompatibilitySet = process.env.RUNIC_COMPATIBILITY_SET ?? null;
const defaultOutput = resolve(
  repositoryRoot,
  'src/lib/generated/release-data.ts',
);
const defaultAuthorityRevision =
  process.env.RUNIC_RELEASE_AUTHORITY_REVISION ?? authorityRevision;
const run = promisify(execFile);

function usage() {
  return 'Usage: node scripts/generate-release-data.mjs [--check] [--manifest <path>] [--compatibility-set <path>] [--authority-revision <commit>] [--output <path>]';
}

function parseArguments(arguments_) {
  const options = {
    check: false,
    manifest: defaultManifest,
    compatibilitySet: configuredCompatibilitySet
      ? resolve(configuredCompatibilitySet)
      : null,
    authorityRevision: defaultAuthorityRevision,
    output: defaultOutput,
  };

  for (let index = 0; index < arguments_.length; index += 1) {
    const argument = arguments_[index];
    if (argument === '--check') options.check = true;
    else if (
      argument === '--manifest' ||
      argument === '--compatibility-set' ||
      argument === '--output' ||
      argument === '--authority-revision'
    ) {
      const value = arguments_[index + 1];
      if (!value || value.startsWith('--')) throw new Error(usage());
      if (argument === '--authority-revision')
        options.authorityRevision = value;
      else options[argument.slice(2)] = resolve(value);
      index += 1;
    } else throw new Error(usage());
  }

  options.compatibilitySet ??= resolve(
    dirname(options.manifest),
    'runic.compatibility-set.json',
  );

  return options;
}

function sha256(source) {
  return createHash('sha256').update(source).digest('hex');
}

async function authorityRepository(path) {
  try {
    const { stdout } = await run('git', ['rev-parse', '--show-toplevel'], {
      cwd: dirname(path),
    });
    return resolve(stdout.trim());
  } catch {
    throw new Error(
      `release authority source is not in a Git repository: ${path}`,
    );
  }
}

async function authorityBlob(repository, revision, path) {
  const repositoryPath = relative(repository, path).replaceAll('\\', '/');
  if (
    !repositoryPath ||
    repositoryPath === '..' ||
    repositoryPath.startsWith('../')
  ) {
    throw new Error(
      `release authority source is outside its repository: ${path}`,
    );
  }

  try {
    const { stdout } = await run(
      'git',
      ['show', `${revision}:${repositoryPath}`],
      { cwd: repository },
    );
    return stdout;
  } catch {
    throw new Error(
      `release authority revision ${revision} does not contain ${repositoryPath}`,
    );
  }
}

async function verifyAuthorityCommit(repository, revision) {
  try {
    await run(
      'git',
      ['rev-parse', '--verify', '--quiet', `${revision}^{commit}`],
      {
        cwd: repository,
      },
    );
  } catch {
    throw new Error(
      `release authority revision must resolve to a Git commit: ${revision}`,
    );
  }
}

async function verifyAuthoritySources(revision, sources) {
  if (!/^[0-9a-f]{40}$/.test(revision)) {
    throw new Error(
      'release authority revision must be a full lowercase Git SHA',
    );
  }

  const canonicalSources = await Promise.all(
    sources.map(async (source) => ({
      ...source,
      path: await realpath(source.path),
    })),
  );
  const repository = await authorityRepository(canonicalSources[0].path);
  await verifyAuthorityCommit(repository, revision);
  for (const source of canonicalSources) {
    if ((await authorityRepository(source.path)) !== repository) {
      throw new Error(
        'release authority sources must come from one Git repository',
      );
    }
    if (
      (await authorityBlob(repository, revision, source.path)) !==
      source.contents
    ) {
      throw new Error(
        `release authority source does not match ${revision}: ${relative(repository, source.path)}`,
      );
    }
  }
}

function docsProjection(manifest, compatibilitySet, source) {
  return {
    schemaVersion: manifest.schemaVersion,
    source,
    repositories: manifest.repositories.map(
      ({ id, currentIdentity, v02Identity }) => ({
        id,
        currentIdentity,
        v02Identity,
      }),
    ),
    products: manifest.products.map(
      ({ id, stableOwner, repository, support, documentation, archive }) => ({
        id,
        stableOwner,
        repository,
        support,
        documentation,
        ...(archive ? { archive } : {}),
      }),
    ),
    canonicalPackages: manifest.canonicalPackages.map(
      ({ identity, ecosystem, product, state, installKind }) => ({
        identity,
        ecosystem,
        product,
        state,
        installKind,
      }),
    ),
    currentPackages: manifest.currentPackages.map(
      ({
        identity,
        ecosystem,
        product,
        stableOwner,
        support,
        disposition,
        target,
        migration,
      }) => ({
        identity,
        ecosystem,
        product,
        stableOwner,
        support,
        disposition,
        target,
        migration,
      }),
    ),
    compatibilityTrains: manifest.compatibilityTrains,
    distributions: manifest.distributions,
    compatibilitySet: {
      id: compatibilitySet.id,
      releaseTrainVersion: compatibilitySet.releaseTrainVersion,
      publication: compatibilitySet.publication,
      toolchain: compatibilitySet.toolchain,
      languageProfiles: compatibilitySet.languageProfiles,
      platformProfiles: compatibilitySet.platformProfiles,
      packages: compatibilitySet.packages,
    },
  };
}

async function render(data, outputPath) {
  const config = await prettier.resolveConfig(outputPath);
  return prettier.format(
    `// Generated by scripts/generate-release-data.mjs from the release authority. Do not edit.\nexport const releaseData = ${JSON.stringify(data, null, 2)} as const;\n`,
    { ...config, filepath: outputPath },
  );
}

async function main() {
  const {
    check,
    manifest: manifestPath,
    compatibilitySet: compatibilitySetPath,
    authorityRevision,
    output,
  } = parseArguments(process.argv.slice(2));
  const verifierPath = resolve(
    dirname(manifestPath),
    'eng/verify-release-manifest.mjs',
  );
  const compatibilitySchemaPath = resolve(
    dirname(compatibilitySetPath),
    'runic.compatibility-set.schema.json',
  );
  const compatibilityVerifierPath = resolve(
    dirname(compatibilitySetPath),
    'eng/verify-compatibility-set.mjs',
  );
  const [manifestSource, compatibilitySetSource] = await Promise.all([
    readFile(manifestPath, 'utf8'),
    readFile(compatibilitySetPath, 'utf8'),
  ]);
  const manifest = JSON.parse(manifestSource);
  const compatibilitySet = JSON.parse(compatibilitySetSource);
  const schemaReference = manifest.$schema;
  if (typeof schemaReference !== 'string' || !schemaReference) {
    throw new Error('release manifest must declare a schema path');
  }
  const schemaPath = resolve(dirname(manifestPath), schemaReference);
  const [
    schemaSource,
    verifierSource,
    compatibilitySchemaSource,
    compatibilityVerifierSource,
  ] = await Promise.all([
    readFile(schemaPath, 'utf8'),
    readFile(verifierPath, 'utf8'),
    readFile(compatibilitySchemaPath, 'utf8'),
    readFile(compatibilityVerifierPath, 'utf8'),
  ]);
  await verifyAuthoritySources(authorityRevision, [
    { path: manifestPath, contents: manifestSource },
    { path: schemaPath, contents: schemaSource },
    { path: verifierPath, contents: verifierSource },
    { path: compatibilitySetPath, contents: compatibilitySetSource },
    { path: compatibilitySchemaPath, contents: compatibilitySchemaSource },
    { path: compatibilityVerifierPath, contents: compatibilityVerifierSource },
  ]);
  const verifier = await import(
    `data:text/javascript;base64,${Buffer.from(verifierSource).toString('base64')}`
  );
  const schema = JSON.parse(schemaSource);
  const errors = verifier.verify(manifest, schema);
  if (errors.length)
    throw new Error(
      `release manifest validation failed:\n${errors.join('\n')}`,
    );
  const compatibilityVerifier = await import(
    `data:text/javascript;base64,${Buffer.from(compatibilityVerifierSource).toString('base64')}`
  );
  const compatibilityErrors = compatibilityVerifier.verifyCompatibilitySet(
    compatibilitySet,
    JSON.parse(compatibilitySchemaSource),
    manifest,
  );
  if (compatibilityErrors.length)
    throw new Error(
      `compatibility-set validation failed:\n${compatibilityErrors.join('\n')}`,
    );

  const expected = await render(
    docsProjection(manifest, compatibilitySet, {
      authorityRevision,
      manifestSha256: sha256(manifestSource),
      schemaSha256: sha256(schemaSource),
      verifierSha256: sha256(verifierSource),
      compatibilitySetSha256: sha256(compatibilitySetSource),
      compatibilitySchemaSha256: sha256(compatibilitySchemaSource),
      compatibilityVerifierSha256: sha256(compatibilityVerifierSource),
    }),
    output,
  );
  let actual;
  try {
    actual = await readFile(output, 'utf8');
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
  }

  if (check) {
    if (actual !== expected) {
      throw new Error(
        `generated release data is stale: ${relative(repositoryRoot, output)}`,
      );
    }
    return;
  }

  await mkdir(dirname(output), { recursive: true });
  await writeFile(output, expected);
}

main().catch((error) => {
  process.stderr.write(`${error.message}\n`);
  process.exitCode = 1;
});
