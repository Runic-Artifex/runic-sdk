# npm inventory fixtures

These synthetic npm lockfile v2/v3 trees are fully offline. The tracked `installed` templates are materialized as temporary `node_modules` trees by the executable tests because the repository intentionally ignores that directory name. They contain only inert `package.json` and attribution text files; no lifecycle script, executable, registry, or network input is used. `_modules_` inside a template represents a nested `node_modules` directory.

`basic` covers exact integrity, scoped and transitive packages, all supported scopes, bundled packages, deterministic ordering, and multiple evidence candidates. `workspace` proves explicit workspace selection, while `alias-v2` covers lockfile version 2 and npm aliases. The remaining fixtures exercise unsupported formats, lock drift, absent restores, duplicate properties, escaped lone surrogates in names and values, unsafe paths and links, invalid integrity, malformed license metadata, and URL-only metadata.
