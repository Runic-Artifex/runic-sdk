## Upgrading from 0.20

Update every Runic NuGet and npm package to 0.21.0 together and rebuild, so that
the generated clients and Bridges are regenerated. The Views protocol and its
features are unchanged, so a 0.20 page and a 0.21 host still connect.

Most applications change only a few calls. Rename the .NET members of the
[API review](#breaking-changes) that the application uses; the compiler lists
each one, and saved navigation paths and every wire format load unchanged. A
hosted guest in trusted component mode imports it from `./experimental`
([Experimental features](#experimental-features-and-how-to-opt-in)). A tool or a
test that reads Runic's development endpoints from another machine, or reads
`sources.json`, checks the [behaviour changes](#behaviour-and-output-changes)
and the [format IDs](#breaking-changes). Runic's VS Code extension reads the
development source map's new format ID from the Runic editor extensions 0.21.0
on; update it with the SDK.
