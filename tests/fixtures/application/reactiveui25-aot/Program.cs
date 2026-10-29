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
// RunicArtifex.ReactiveUI.Validation: the property rule reports at its path, and a rule that names no member pushes a
// frame that sets validation (the ValidationContext is observed).
var rules = new AotRulesViewModel { Name = "far too long" };
using var rulesLease = RunicModelContextRegistry.Shared.Bind(context, rules);
var rulesTransport = new AotTransport();
using var rulesContent = new WindowContentSession(rulesTransport, new WindowContentSessionOptions { RootModel = rules });
using var rulesBridge = new AotRulesBridge(rulesTransport, rules, content: rulesContent);
Require(await rulesTransport.RequestAsync("""{"kind":"hello","protocol":4,"session":"aot-rules","client":{"name":"aot-proof","version":"1.0.0"}}"""), "hello");
using (var rulesSnapshot = JsonDocument.Parse(Require(await rulesTransport.RequestAsync("""{"kind":"snapshot","route":"aotRules"}"""), "snapshot")))
{
    var errors = rulesSnapshot.RootElement.GetProperty("state").GetProperty("validation").GetProperty("errors");
    if (errors.GetArrayLength() != 1 || errors[0].GetProperty("path")[0].GetString() != "name"
        || errors[0].GetProperty("message").GetString() != AotRulesViewModel.NameTooLong)
        throw new InvalidOperationException($"The ReactiveValidationObject's validation was {errors}.");
}
await context.InvokeAsync(() => rules.Approve(false));
var pushed = false;
for (var attempt = 0; attempt < 500 && !pushed; attempt++)
{
    if (attempt > 0) await Task.Delay(10);
    while (rulesTransport.Pushes.TryDequeue(out var push))
        pushed |= push.Contains("\"frame\"", StringComparison.Ordinal) && push.Contains(AotRulesViewModel.ApprovalRequired, StringComparison.Ordinal);
}
if (!pushed) throw new InvalidOperationException("A rule that names no member did not push its validation under NativeAOT.");

// A ViewModel that implements only IValidatableViewModel has validation too.
var only = new AotValidatableOnlyViewModel();
using var onlyLease = RunicModelContextRegistry.Shared.Bind(context, only);
var onlyTransport = new AotTransport();
using var onlyContent = new WindowContentSession(onlyTransport, new WindowContentSessionOptions { RootModel = only });
using var onlyBridge = new AotValidatableOnlyBridge(onlyTransport, only, content: onlyContent);
Require(await onlyTransport.RequestAsync("""{"kind":"hello","protocol":4,"session":"aot-only","client":{"name":"aot-proof","version":"1.0.0"}}"""), "hello");
using (var onlySnapshot = JsonDocument.Parse(Require(await onlyTransport.RequestAsync("""{"kind":"snapshot","route":"aotValidatableOnly"}"""), "snapshot")))
{
    var validation = onlySnapshot.RootElement.GetProperty("state").GetProperty("validation");
    if (!validation.GetProperty("hasErrors").GetBoolean()
        || validation.GetProperty("errors")[0].GetProperty("message").GetString() != AotValidatableOnlyViewModel.NameRequired)
        throw new InvalidOperationException($"The IValidatableViewModel's validation was {validation}.");
}
Console.WriteLine("ReactiveUI 25 generated codec and ReactiveUI.Validation NativeAOT proof passed.");

static string Require(string reply, string request) => reply.StartsWith("{\"ok\":true", StringComparison.Ordinal)
    ? reply : throw new InvalidOperationException($"The {request} request failed: {reply}");
