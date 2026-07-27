# WebUIToolkit.Hosting.CsWebUi.Mvvm

This package carries the existing `webuitoolkit.mvvm/1` protocol over CsWebUi.
It deliberately exposes one native binary binding for the whole MVVM session;
properties and commands stay numeric protocol members rather than becoming
individually callable WebUI bindings.

## Host

Open a retained `IMvvmSession`, attach it while configuring the CsWebUi window,
and dispose the bridge before the window:

```csharp
IMvvmSession session = await sessionFactory.OpenAsync(new MvvmContract("todo"));
CsWebUiMvvmBridge? bridge = null;

var hostOptions = new CsWebUiAdapterOptions(
    "wwwroot",
    CsWebUiPresentationMode.WebView,
    ConfigureWindow: window =>
    {
        bridge = CsWebUiMvvmBridge.Attach(window, session);
    });

// Run the browser host...
await bridge!.DisposeAsync();
```

The bridge pins the first valid CsWebUi client ID, records its physical
connection ID, validates every bounded UTF-8/JSON frame with
`MvvmMessageCodec`, routes admitted work through `MvvmWebUiTransport`, and
pushes host frames only to that callback's client using `WebUiEvent.SendRaw`.
A later connection ID is accepted only when the same client starts a protocol
reconnect handshake.

`CsWebUiMvvmBridge` owns the supplied session after `Attach` succeeds. A
protocol `close` disposes it immediately; `DisposeAsync` is idempotent and
also handles host/window teardown.

## Browser

The NuGet package includes
`contentFiles/any/any/wwwroot/webuitoolkit-mvvm-cswebui.mjs`. Copy or bundle
that file with the application and compose it with `@webuitoolkit/mvvm`:

```js
import { MvvmClient, ProtocolTransport } from "@webuitoolkit/mvvm";
import { CsWebUiFrameChannel } from "./webuitoolkit-mvvm-cswebui.mjs";

const channel = new CsWebUiFrameChannel();
const transport = new ProtocolTransport(channel);
const client = new MvvmClient(transport);

await client.start("todo", crypto.randomUUID());
```

`CsWebUiFrameChannel` implements the package's structural `FrameChannel`
contract. It accepts only `Uint8Array` client frames, applies the 1 MiB
protocol ceiling before invoking the one host binding, and copies bounded
binary frames received through the one host-push function.

Custom names must match on both sides:

```csharp
new CsWebUiMvvmBridgeOptions
{
    BindingName = "appMvvmSend",
    ReceiveFunctionName = "appMvvmReceive",
};
```

```js
new CsWebUiFrameChannel({
  bindingName: "appMvvmSend",
  receiveFunctionName: "appMvvmReceive",
});
```

Cancellation is the normal MVVM `cancel` frame and never creates another
native binding. Calling `MvvmClient.close()` sends the authenticated protocol
close; call `ProtocolTransport.close()` afterward when the application also
wants to detach the local JavaScript channel.

## Shared application builder

React, Vue, Svelte, and Angular contribute C# 14 extension properties and
methods to the same frontend-neutral `WebUiAppBuilder`:

```csharp
var builder = WebUiApp.CreateBuilder(args);
builder.UseReact(options);
// Equivalent framework surface: builder.React.Use(options)
return await builder.RunAsync();
```

The corresponding members are `React`/`UseReact`, `Vue`/`UseVue`,
`Svelte`/`UseSvelte`, and `Angular`/`UseAngular`. Framework packages can add
future configuration members without changing or downcasting the shared base.
