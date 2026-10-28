using System.Reflection;
using System.Text.Json;
using ReactiveUi25AotProof;
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using Runic.Application.Views.Generated.Bridges;
using Runic.Navigation;

await using var context = new RunicModelContext();
var model = new AotProofViewModel(context);
using var modelLease = RunicModelContextRegistry.Shared.Bind(context, model);
var transport = new AotTransport();
using var content = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = model });
using var bridge = new AotProofBridge(transport, model, content: content);
Require(await transport.RequestAsync("""{"kind":"hello","protocol":4,"session":"aot","client":{"name":"aot-proof","version":"1.0.0"}}"""), "hello");
using var snapshot = JsonDocument.Parse(Require(await transport.RequestAsync("""{"kind":"snapshot","route":"aotProof"}"""), "snapshot"));
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

// The operation contract the generated client sends: {ViewModel full name}:{fingerprint}. The generated Bridge holds
// the fingerprint; computing it again would reflect over the model, which NativeAOT does not keep.
var fingerprint = (string)typeof(ViewModelBridge<AotProofViewModel>)
    .GetField("_contractFingerprint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
var contract = JsonEncodedText.Encode($"{typeof(AotProofViewModel).FullName}:{fingerprint}").ToString();
var input = """{"documentId":"aot","expectedVersion":"9007199254740993","amount":"12.50","when":"2026-09-28T10:11:12.0000000"}""";
using var admission = JsonDocument.Parse(Require(await transport.RequestAsync(
    $$"""{"kind":"start","route":"aotProof","member":"save","operation":"aot-proof","contract":"{{contract}}","input":{{input}}}"""), "start"));
if (admission.RootElement.GetProperty("admission").GetString() != "accepted")
    throw new InvalidOperationException($"The operation was not admitted: {admission.RootElement}");
var status = "";
for (var attempt = 0; attempt < 500 && !status.Contains("\"succeeded\"", StringComparison.Ordinal); attempt++)
{
    if (attempt > 0) await Task.Delay(10);
    status = Require(await transport.RequestAsync(
        $$"""{"kind":"status","route":"aotProof","member":"save","operation":"aot-proof","contract":"{{contract}}"}"""), "status");
}
if (!status.Contains("9007199254740994", StringComparison.Ordinal))
    throw new InvalidOperationException($"Generated typed command result was not retained: {status}");
Console.WriteLine("ReactiveUI 25 generated codec NativeAOT proof passed.");

static string Require(string reply, string request) => reply.StartsWith("{\"ok\":true", StringComparison.Ordinal)
    ? reply : throw new InvalidOperationException($"The {request} request failed: {reply}");
