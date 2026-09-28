using System.Text.Json;
using ReactiveUi25AotProof;
using Runic.Application.Views;

await using var context = new RunicModelContext();
var model = new AotProofViewModel(context);
using var modelLease = RunicModelContextRegistry.Shared.Bind(context, model);
var transport = new AotTransport();
using var content = new WindowContentSession(transport, rootModel: model);
using var bridge = new AotProofBridge(transport, model, content: content);
using var snapshot = JsonDocument.Parse(transport.Call("aotProofSnapshot"));
var state = snapshot.RootElement.GetProperty("state");
if (state.GetProperty("exactId").GetString() != "9007199254740993"
    || state.GetProperty("amount").GetString() != "1234567890.123456789"
    || state.GetProperty("optional").ValueKind != JsonValueKind.Null)
    throw new InvalidOperationException("Generated NativeAOT codec state was not exact.");

var validationError = state.GetProperty("validation").GetProperty("errors")[0];
if (validationError.GetProperty("code").GetString() != "required"
    || validationError.GetProperty("path")[0].GetString() != "validationItem"
    || validationError.GetProperty("path")[1].GetString() != "text")
    throw new InvalidOperationException("Generated NativeAOT nested validation was not projected.");

using var admission = JsonDocument.Parse(transport.Call("aotProofStartSave", "{\"requestId\":\"aot-proof\",\"input\":{\"documentId\":\"aot\",\"expectedVersion\":\"9007199254740993\",\"amount\":\"12.50\",\"when\":\"2026-09-28T10:11:12.0000000\"}}"));
var contract = admission.RootElement.GetProperty("contract").GetString() ?? throw new InvalidOperationException("Missing operation contract.");
var waitPayload = "{\"contract\":\"" + JsonEncodedText.Encode(contract).ToString() + "\",\"requestId\":\"aot-proof\"}";
var reply = await transport.CallAsync("__runicOperationWait", waitPayload);
if (!reply.Contains("9007199254740994", StringComparison.Ordinal))
    throw new InvalidOperationException("Generated typed command result was not retained.");
Console.WriteLine("ReactiveUI 25 generated codec NativeAOT proof passed.");
