# CS-WebUI Window Bridge NativeAOT host smoke

This private Linux smoke publishes the joined internal CS-WebUI Window Bridge
host as a trimmed NativeAOT executable. Its fixture uses hand-written routes,
so the result covers the host and core ownership boundary without claiming a
generated post-MVVM browser or SDK application authoring gate. The older
`cdbb8e9c` native fixture linked a generated browser fixture whose route and
save protocol have since changed; that follow-up needs its own joined fixture.

From the SDK root with the locked development shell:

```sh
direnv exec . dotnet restore tests/native/Runic.Application.CsWebUi.WindowBridgeAotSmoke/Runic.Application.CsWebUi.WindowBridgeAotSmoke.csproj --disable-parallel -r linux-x64 -p:PublishAot=true -p:SelfContained=true
direnv exec . dotnet publish tests/native/Runic.Application.CsWebUi.WindowBridgeAotSmoke/Runic.Application.CsWebUi.WindowBridgeAotSmoke.csproj -c Release -r linux-x64 --no-restore -p:PublishAot=true -p:SelfContained=true -p:PublishTrimmed=true -p:TrimMode=full -p:EnableTrimAnalyzer=true -p:EnableAotAnalyzer=true -p:InvariantGlobalization=true -o /tmp/runic-joined-windowbridge-aot-smoke
RUNIC_WINDOW_BRIDGE_AOT_HOST=/tmp/runic-joined-windowbridge-aot-smoke/Runic.Application.CsWebUi.WindowBridgeAotSmoke direnv exec . node tests/native/Runic.Application.CsWebUi.WindowBridgeAotSmoke/browser-smoke.mjs
```

The browser checks document admission, two mounted presentations, escaped
title transport, exact presentation rejection, save, endpoint retirement,
and window scope disposal. The host remains internal; this is not a public
NativeAOT support promise for arbitrary CS-WebUI versions or generated routes.
