# Native-AOT smoke executable

This executable uses no reflection, dynamic code, serializer discovery,
suppression, or trim descriptors. It exercises range add/insert/remove/replace/
move, clear, snapshot isolation, Range and Reset policy, comparer/FIFO `UpdateTo`,
and keyed `UpdateTo` while validating deterministic content, identity, event, and
result behavior.

```console
dotnet build -c Release src/WebUIToolkit.Collections
dotnet restore --locked-mode tests/WebUIToolkit.Collections.AotSmoke
dotnet restore -r win-x64 -p:PublishAot=true -p:PublishTrimmed=true -p:NuGetLockFilePath=obj/aot.packages.lock.json -p:RestoreLockedMode=false tests/WebUIToolkit.Collections.AotSmoke
dotnet publish -c Release -r win-x64 --no-restore -p:PublishAot=true -p:PublishTrimmed=true -p:NuGetLockFilePath=obj/aot.packages.lock.json tests/WebUIToolkit.Collections.AotSmoke
```

The RID-specific smoke consumes that built shipping assembly directly. Its native
restore lock is generated below ignored `obj/`, keeping every committed lock
portable while still compiling the real shipping binary into the native executable.

Run the native executable from the publish directory. Success prints one stable
`PASS` line and exits zero; validation failures print one `FAIL` line and exit one.
Additional runtime identifiers should be validated with their own temporary native
restore before release evidence is accepted.
