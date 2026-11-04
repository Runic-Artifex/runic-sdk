using System.Globalization;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Translations;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// translation-text/1 (specs/application/README.md, "Translated text"; values.md, "text-reference"): TranslationReference
// and RunicTextReference values, the text of validation errors, and the window's locale.
public sealed class TranslatedTextTests
{
    private const string Feature = "translation-text/1";

    private static string Json(TranslationReference reference) =>
        JsonSerializer.Serialize(reference, TranslationReferenceJsonContext.Default.TranslationReference);

    [Test]
    public void TextReferencesCrossAsTheTranslationsTransportWritesThem()
    {
        var model = new TranslatedTextViewModel
        {
            Note = TranslatedText.Text(TranslatedText.Reference("note", "A note.")),
            Entries = [new("first", TranslatedText.TitleTooLong, null)],
        };
        using var host = new RunicWindowTestHost<TranslatedTextViewModel>(model,
            (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "translatedText" });
        using var snapshot = host.RawSnapshot();
        var state = snapshot.RootElement.GetProperty("state");
        Require(state.GetProperty("status").GetRawText() == Json(model.Status),
            $"A TranslationReference member is not the reference's transport JSON: {state.GetProperty("status")}");
        Require(state.GetProperty("previous").ValueKind == JsonValueKind.Null, "A null reference is not null.");
        Require(state.GetProperty("note").GetRawText() == model.Note.ToJson(), "A RunicTextReference member is not its JSON.");
        var entry = state.GetProperty("entries")[0];
        Require(entry.GetProperty("label").GetRawText() == Json(TranslatedText.TitleTooLong)
            && entry.GetProperty("note").ValueKind == JsonValueKind.Null, $"A DTO's references are not their JSON: {entry}");
        // RunicViewState reads a reference as either type.
        var read = host.Root.Snapshot();
        Require(read.Read(vm => vm.Status).Key == "ready" && read.Read(vm => vm.Note)!.Key == "note"
            && read.Read(vm => vm.Previous) is null, "RunicViewState.Read did not read the references.");
    }

    [Test]
    public async Task ACommandTakesATextReferenceAndRefusesAMalformedOneAsync()
    {
        var model = new TranslatedTextViewModel();
        using var host = new RunicWindowTestHost<TranslatedTextViewModel>(model,
            (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "translatedText" });
        var saved = TranslatedText.Reference("saved", "Saved.");
        var reply = await host.Root.ExecuteAsync(vm => vm.SaveCommand, saved);
        Require(reply.Succeeded && model.Status.Key == "saved" && model.Status.FallbackText == "Saved.",
            $"The command did not receive the reference: {reply.ErrorKind} {model.Status.Key}");
        foreach (var malformed in new[] { """{"version":9}""", "\"saved\"", """{"version":2,"catalog":"notes","key":"k"}""" })
        {
            var refused = await host.Root.ExecuteAsync(vm => vm.SaveCommand, malformed);
            Require(!refused.Succeeded && refused.ErrorKind == "rejected" && refused.ErrorReason == "invalid-input",
                $"{malformed} was not refused as invalid input: {refused.ErrorKind} {refused.ErrorReason}");
        }
    }

    [Test]
    public void GeneratedClientsTypeAndDecodeTextReferences()
    {
        var directory = GeneratedTypeScriptDirectory();
        var module = File.ReadAllText(Path.Combine(directory, "translatedText.ts")).ReplaceLineEndings("\n");
        var types = File.ReadAllText(Path.Combine(directory, "types.ts")).ReplaceLineEndings("\n");
        var mock = File.ReadAllText(Path.Combine(directory, "translatedText.mock.ts")).ReplaceLineEndings("\n");
        foreach (var (file, text, expected) in new[]
        {
            ("translatedText.ts", module, "readonly status: bridgeWire.TextReference;"),
            ("translatedText.ts", module, "readonly previous: bridgeWire.TextReference | null;"),
            ("translatedText.ts", module, "bridgeWire.textReference("),
            ("types.ts", types, "import type * as bridgeWire from \"@runic-artifex/views/generated/wire\";\n"),
            ("types.ts", types, "readonly label: bridgeWire.TextReference;"),
            ("translatedText.mock.ts", mock, "import * as bridgeWire from \"@runic-artifex/views/generated/wire\";"),
        })
            Require(text.Contains(expected, StringComparison.Ordinal), $"Generated {file} is missing:\n{expected}\n--- {file} ---\n{text}");
    }

    [Test]
    public void ValidationErrorsCarryTheTextOfTheirMessage()
    {
        var model = new TranslatedTextViewModel { Title = "much too long" };
        using var host = new RunicWindowTestHost<TranslatedTextViewModel>(model,
            (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "translatedText" });
        using var snapshot = host.RawSnapshot();
        var errors = snapshot.RootElement.GetProperty("state").GetProperty("validation").GetProperty("errors");
        var tooLong = Error(errors, "Title must be at most 8 characters.");
        Require(tooLong.GetProperty("code").GetString() == "title-too-long"
            && tooLong.GetProperty("text").GetRawText() == Json(TranslatedText.TitleTooLong),
            $"A RunicValidationMessage's text is not on its error: {tooLong}");
        Require(Error(errors, "Title is not unique.").GetProperty("text").GetProperty("key").GetString() == "title_taken",
            $"A RunicValidationResult's text is not on its error: {errors}");
        // {name}Errors stays the messages.
        var titleErrors = snapshot.RootElement.GetProperty("state").GetProperty("titleErrors");
        Require(titleErrors.GetArrayLength() == 2 && titleErrors[0].ValueKind == JsonValueKind.String,
            $"titleErrors is not the messages: {titleErrors}");
    }

    [Test]
    public void AToolkitValidatorsResultCarriesItsText()
    {
        var model = new TranslatedToolkitViewModel { Title = "much too long" };
        model.Validate();
        BridgeDataSubscriptionMember[] metadata =
        [
            new("title", static owner => ((TranslatedToolkitViewModel)owner).Title, propertyName: nameof(TranslatedToolkitViewModel.Title)),
        ];
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) BridgeValidation.Write(writer, model, metadata);
        using var document = JsonDocument.Parse(buffer.ToArray());
        var error = Error(document.RootElement.GetProperty("errors"), "Title must be at most 8 characters.");
        Require(error.GetProperty("text").GetProperty("key").GetString() == "title_too_long",
            $"An ObservableValidator's RunicValidationResult lost its text: {error}");
    }

    [Test]
    public void AReactiveValidationTextCarriesItsText()
    {
        var model = new TranslatedReactiveViewModel { Title = "much too long" };
        using var host = new RunicWindowTestHost<TranslatedReactiveViewModel>(model,
            (transport, content, vm) => new TranslatedReactiveBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "translatedReactive", ViewLocator = new TestViewLocator() });
        using var snapshot = host.RawSnapshot();
        var errors = snapshot.RootElement.GetProperty("state").GetProperty("validation").GetProperty("errors");
        var error = Error(errors, "Title must be at most 8 characters.");
        Require(error.GetProperty("text").GetRawText() == Json(TranslatedText.TitleTooLong),
            $"A ReactiveUI.Validation text that carries a reference did not give its error the text: {errors}");
    }

    [Test]
    public async Task TheWindowsLocaleReachesTheConnectionsThatUseTheFeatureAsync()
    {
        var services = new ServiceCollection().AddRunicWindowService()
            .AddScoped<TranslatedTextViewModel>(provider => new(provider.GetRequiredService<IRunicWindowLocale>()));
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, TranslatedTextViewModel, IDisposable>>(
            _ => (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        using var host = RunicWindowTestHost.Create<TranslatedTextViewModel>(scope.ServiceProvider);
        var locale = scope.ServiceProvider.GetRequiredService<IRunicWindowLocale>();
        // The ViewModel's constructor set the locale before the window had a session.
        Require(host.Content.Locale == "de" && locale.Locale == "de", $"The window's locale is {host.Content.Locale}.");

        host.Transport.HelloWith("new", [Feature]);
        host.Transport.HelloWith("old", []);
        using (var first = await host.Transport.TakePushAsync("new", "locale", TimeSpan.FromSeconds(5)))
            Require(first.RootElement.GetProperty("locale").GetString() == "de", $"The hello was not followed by the locale: {first.RootElement}");

        var changes = new List<string?>();
        locale.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        host.ViewModel.SwitchLanguage("en");
        using (var second = await host.Transport.TakePushAsync("new", "locale", TimeSpan.FromSeconds(5)))
            Require(second.RootElement.GetRawText() == """{"kind":"locale","locale":"en"}""", $"The change was not pushed: {second.RootElement}");
        Require(changes.SequenceEqual([nameof(IRunicWindowLocale.Locale)]) && host.Content.Locale == "en",
            "Setting the locale did not raise PropertyChanged once.");
        host.Content.Locale = null;
        using (var third = await host.Transport.TakePushAsync("new", "locale", TimeSpan.FromSeconds(5)))
            Require(third.RootElement.GetProperty("locale").ValueKind == JsonValueKind.Null, $"null was not pushed: {third.RootElement}");
        Require(locale.Locale is null && changes.Count == 2, "WindowContentSession.Locale did not reach IRunicWindowLocale.");
        host.Content.Locale = null;
        Require(changes.Count == 2, "Setting the same locale raised PropertyChanged.");
        await BridgeTargetedPushesIdleAsync(host);
        Require(!host.Transport.Messages.Any(message => message.Kind == "locale" && message.ConnectionKey == "old"),
            "A connection without the feature received a locale push.");
        foreach (var invalid in new[] { "", "de_DE", "-de", "de-", "de--x", "1de", new string('a', 65), "de DE" })
        {
            var thrown = false;
            try { host.Content.Locale = invalid; } catch (ArgumentException) { thrown = true; }
            Require(thrown, $"'{invalid}' was accepted as a locale.");
        }
        host.Content.Locale = "pt-BR";
        Require(host.Content.Locale == "pt-BR", "A region tag was refused.");
    }

    [Test]
    public async Task AWindowThatOpenWindowAsyncOpenedTakesTheLocaleItsViewModelSetAsync()
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddRunicWindowService();
        services.AddScoped<TranslatedTextViewModel>(provider => new(provider.GetRequiredService<IRunicWindowLocale>()));
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, TranslatedTextViewModel, IDisposable>>(
            _ => (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<TranslatedTextWindow>();
        var host = ((TestWindow<TranslatedTextViewModel>)window.Host).Host;
        // The ViewModel's constructor set "de" before the window had a session.
        Require(host.Content.Locale == "de", $"The Window's locale is {host.Content.Locale}, not the one its ViewModel set.");
        window.ViewModel.SwitchLanguage("en");
        Require(host.Content.Locale == "en", "IRunicWindowLocale did not set the open Window's locale.");
    }

    // The state the page test resolves with the real Translations ESM (packages/web/views/test/translated-text.esm.test.ts):
    // the producer's wire output, kept as a fixture. RUNIC_UPDATE_GOLDEN=1 writes it again.
    [Test]
    public void TheProducersReferencesAreThePageFixture()
    {
        using var host = new RunicWindowTestHost<TranslatedCatalogViewModel>(new TranslatedCatalogViewModel(),
            (transport, content, vm) => new TranslatedCatalogBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "translatedCatalog" });
        using var snapshot = host.RawSnapshot();
        var state = snapshot.RootElement.GetProperty("state").GetRawText();
        var path = Path.Combine(WorkspaceRoot(), "packages", "web", "views", "test", "fixtures", "translations", "producer-state.json");
        if (Environment.GetEnvironmentVariable("RUNIC_UPDATE_GOLDEN") == "1") File.WriteAllText(path, state + "\n");
        var expected = File.ReadAllText(path).TrimEnd();
        Require(state == expected, $"The producer's state differs from {path}. Review it and run with RUNIC_UPDATE_GOLDEN=1.\n{state}");
    }

    [Test]
    public void TheShapeFollowsTheTranslationsReaders()
    {
        // Version 1 takes a null fallback as none; a later version reads; arguments are required.
        var v1 = RunicTextReference.Parse("""{"version":1,"catalog":"c","contractFingerprint":"f","key":"k","arguments":{},"fallbackText":null}""");
        Require(v1.Version == 1 && v1.FallbackText is null && v1.ToString() == "k", "A version 1 null fallback was not read as none.");
        var v3 = RunicTextReference.Parse("""{"version":3,"catalog":"c","contractFingerprint":"f","key":"k","arguments":{},"fallbackText":"F"}""");
        Require(v3.Version == 3 && v3.FallbackText == "F", "A later transport version was refused.");
        foreach (var invalid in new[]
        {
            """{"version":2,"catalog":"c","contractFingerprint":"f","key":"k"}""",
            """{"version":2,"catalog":"c","contractFingerprint":"f","key":"k","arguments":{},"fallbackText":null}""",
            """{"version":0,"catalog":"c","contractFingerprint":"f","key":"k","arguments":{}}""",
            """{"version":2.0,"catalog":"c","contractFingerprint":"f","key":"k","arguments":{}}""",
        })
        {
            var thrown = false;
            try { _ = RunicTextReference.Parse(invalid); } catch (JsonException) { thrown = true; }
            Require(thrown, $"{invalid} was read as a text reference.");
        }
    }

    [Test]
    public void AnOversizedTextLeavesItsErrorWithTheMessageAndIsLogged()
    {
        var huge = RunicTextReference.Parse("{\"version\":2,\"catalog\":\"c\",\"contractFingerprint\":\"f\",\"key\":\"big\",\"arguments\":{},\"fallbackText\":\""
            + new string('x', 20_000) + "\"}");
        var model = new ErrorsOnlyModel(new RunicValidationMessage("Too big.") { Text = huge });
        using var buffer = new MemoryStream();
        var logger = new CapturingLogger();
        using (BridgeSnapshotPublication.Enter(static () => false, logger))
        using (var writer = new Utf8JsonWriter(buffer)) BridgeValidation.Write(writer, model, []);
        using var document = JsonDocument.Parse(buffer.ToArray());
        var error = document.RootElement.GetProperty("errors")[0];
        Require(!error.TryGetProperty("text", out _) && error.GetProperty("message").GetString() == "Too big.",
            "An oversized text was written into its error.");
        Require(!document.RootElement.GetProperty("truncated").GetBoolean(), "An oversized text set truncated.");
        Require(logger.Events.SequenceEqual([1171]), $"The oversized text was not logged as 1171: {string.Join(", ", logger.Events)}");
    }

    // S5b: RunicValidationOptions.TextFor gives the messages of validation attributes a text reference.
    private static RunicValidationOptions AttributeTexts(List<(ValidationAttribute Attribute, RunicValidationMember Member)>? calls = null) => new()
    {
        TextFor = (attribute, member) =>
        {
            calls?.Add((attribute, member));
            return attribute switch
            {
                RequiredAttribute => TranslatedText.Text(TranslatedText.Reference("required", member.Message)),
                MaxLengthAttribute maximum => TranslatedText.Text(TranslatedText.Reference("too_long", member.Message, maximum.Length)),
                MinLengthAttribute => TranslatedText.Text(TranslatedText.Reference("too_short", member.Message)),
                RangeAttribute => TranslatedText.Text(TranslatedText.Reference("out_of_range", member.Message)),
                StringLengthAttribute => TranslatedText.Text(TranslatedText.Reference("length", member.Message)),
                CompareAttribute => TranslatedText.Text(TranslatedText.Reference("mismatch", member.Message)),
                _ => null,
            };
        },
    };

    private static JsonElement ValidationErrors(RunicWindowTestHost<ValidationTextViewModel> host)
    {
        using var snapshot = host.RawSnapshot();
        return snapshot.RootElement.GetProperty("state").GetProperty("validation").GetProperty("errors").Clone();
    }

    private static JsonElement ErrorAt(JsonElement errors, params string[] path) =>
        errors.EnumerateArray().FirstOrDefault(error => error.GetProperty("path").EnumerateArray().Select(segment => segment.GetString())
            .SequenceEqual(path)) is { ValueKind: JsonValueKind.Object } found ? found : throw new InvalidOperationException($"No error at {string.Join(".", path)}: {errors}");

    [Test]
    public void AttributeMessagesTakeTheTextOfTextFor()
    {
        var model = new ValidationTextViewModel { Tag = "much too long", Count = 0, Note = "x" };
        model.Details.Code = "x";
        model.Validate();
        var calls = new List<(ValidationAttribute Attribute, RunicValidationMember Member)>();
        using var host = new RunicWindowTestHost<ValidationTextViewModel>(model,
            (transport, content, vm) => new ValidationTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "validationText", Validation = AttributeTexts(calls) });
        var errors = ValidationErrors(host);
        var title = ErrorAt(errors, "title");
        Require(title.GetProperty("message").GetString() == "The Heading field is required."
            && title.GetProperty("text").GetProperty("key").GetString() == "required"
            && title.GetProperty("text").GetProperty("fallbackText").GetString() == "The Heading field is required.",
            $"[Required] with [Display(Name)] did not take its text: {title}");
        var tag = ErrorAt(errors, "tag");
        Require(tag.GetProperty("text").GetProperty("key").GetString() == "too_long"
            && tag.GetProperty("text").GetProperty("arguments").GetProperty("max").GetProperty("value").GetString() == "8",
            $"A string error of [MaxLength] did not take its text: {tag}");
        Require(ErrorAt(errors, "count").GetProperty("text").GetProperty("key").GetString() == "out_of_range", $"[Range]: {errors}");
        Require(ErrorAt(errors, "details", "code").GetProperty("text").GetProperty("key").GetString() == "length",
            $"A DTO member's [StringLength] with [Display(Name)] did not take its text: {errors}");
        Require(!ErrorAt(errors, "note").TryGetProperty("text", out _),
            "A message the ViewModel wrote while the member's attributes pass took an attribute's text.");
        var member = calls.First(call => call.Attribute is RequiredAttribute && call.Member.Name == nameof(ValidationTextViewModel.Title)).Member;
        Require(member == new RunicValidationMember(typeof(ValidationTextViewModel), "Title", "Heading", "The Heading field is required."),
            $"TextFor received {member}.");
        Require(calls.Any(call => call.Member is { ModelType: var type, DisplayName: "Postcode" } && type == typeof(ValidationTextDetails)),
            "A DTO member's TextFor call did not name its type and display name.");
    }

    private static T InCulture<T>(string name, Func<T> action)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        try { return action(); }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture); }
    }

    private static RunicWindowTestHost<ValidationTextViewModel> ValidationTextHost(ValidationTextViewModel model,
        List<(ValidationAttribute Attribute, RunicValidationMember Member)>? calls = null) =>
        new(model, (transport, content, vm) => new ValidationTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "validationText", Validation = AttributeTexts(calls) });

    private static string? TextKey(JsonElement errors, params string[] path) =>
        ErrorAt(errors, path).TryGetProperty("text", out var text) ? text.GetProperty("key").GetString() : null;

    // The attribute is found by evaluating the member's attributes, which does not depend on the culture: errors
    // formatted in one culture take their text in a publication in another, with limits that format differently.
    [Test]
    [Arguments("de-DE", "en-US")]
    [Arguments("en-US", "de-DE")]
    public void AttributeTextsDoNotDependOnTheCultureOfThePublication(string validated, string published)
    {
        var model = new ValidationTextViewModel { Ratio = 20, Price = 20 };
        InCulture(validated, () => { model.Validate(); return 0; });
        using var host = ValidationTextHost(model);
        var errors = InCulture(published, () => ValidationErrors(host));
        var separator = validated == "de-DE" ? "0,5" : "0.5";
        Require(ErrorAt(errors, "ratio").GetProperty("message").GetString()!.Contains(separator, StringComparison.Ordinal)
            && TextKey(errors, "ratio") == "out_of_range", $"[Range(0.5, 9.5)] formatted in {validated} lost its text in {published}: {errors}");
        // In de-DE, RangeAttribute would not parse "0.5" in the culture; the Bridge parses string limits invariantly.
        Require(TextKey(errors, "price") == "out_of_range",
            $"[Range(typeof(decimal), \"0.5\", \"9.5\")] formatted in {validated} lost its text in {published}: {errors}");
        // The same text whichever culture the publication runs in, as with FlowCultureFromLocale's wrapped turns.
        var again = InCulture(validated, () => ValidationErrors(host));
        Require(again.GetRawText() == errors.GetRawText(), $"The errors differ between {published} and {validated}: {errors} / {again}");
    }

    // After a window language switch the ViewModel keeps errors formatted in the old culture.
    [Test]
    public void ErrorsOfTheOldLanguageKeepTheirTextAfterALanguageSwitch()
    {
        var model = new ValidationTextViewModel { Ratio = 0, Count = 0 };
        using var host = ValidationTextHost(model);
        host.Content.Locale = "de";
        var before = InCulture("de-DE", () => { model.Validate(); return ValidationErrors(host); });
        host.Content.Locale = "en";
        var after = InCulture("en-US", () => ValidationErrors(host));
        Require(ErrorAt(after, "ratio").GetProperty("message").GetString()!.Contains("0,5", StringComparison.Ordinal),
            $"The ViewModel's error is not the old language's: {after}");
        Require(TextKey(before, "ratio") == "out_of_range" && TextKey(after, "ratio") == "out_of_range"
            && TextKey(after, "count") == "out_of_range" && TextKey(after, "title") == "required",
            $"An error of the old language lost its text after the switch: {before} / {after}");
    }

    // [Compare] formats the other property's [Display(Name)] only after it has evaluated: with an error the ViewModel
    // adds, the errors and attributes do not line up, and the message alone finds the attribute.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ACompareWithTheOtherPropertysDisplayNameTakesItsText(bool extraError)
    {
        var model = new ValidationTextViewModel { Password = "secret", Confirm = "secrets", ExtraConfirmError = extraError };
        model.Validate();
        var calls = new List<(ValidationAttribute Attribute, RunicValidationMember Member)>();
        using var host = ValidationTextHost(model, calls);
        var errors = ValidationErrors(host);
        var compare = Error(errors, "'Confirm' and 'Passwort' do not match.");
        Require(compare.GetProperty("text").GetProperty("key").GetString() == "mismatch", $"[Compare] did not take its text: {errors}");
        Require(calls.Any(call => call.Attribute is CompareAttribute { OtherPropertyDisplayName: "Passwort" }),
            "TextFor did not receive the evaluated [Compare].");
        if (extraError) Require(!Error(errors, "Choose another password.").TryGetProperty("text", out _), "The ViewModel's error took a text.");
    }

    // [MinLength] and [MaxLength] with the same message: the one that fails is the error's.
    [Test]
    [Arguments("ab", "too_short")]
    [Arguments("much too long", "too_long")]
    public void OfTwoAttributesWithTheSameMessageTheFailingOneIsTheErrors(string handle, string key)
    {
        var model = new ValidationTextViewModel { Handle = handle };
        model.Validate();
        using var host = ValidationTextHost(model);
        var errors = ValidationErrors(host);
        Require(ErrorAt(errors, "handle").GetProperty("message").GetString() == "Invalid" && TextKey(errors, "handle") == key,
            $"'{handle}' did not take the text {key}: {errors}");
    }

    // An error the ViewModel has not revalidated since the value changed keeps its attribute's text.
    [Test]
    public void AnErrorOfAnEarlierValueKeepsItsAttributesText()
    {
        var model = new ValidationTextViewModel { Handle = "ab" };
        model.Validate();
        model.Title = "A title";
        model.Handle = "much too long";
        using var host = ValidationTextHost(model);
        var errors = ValidationErrors(host);
        Require(TextKey(errors, "title") == "required", $"A [Required] error whose value is now valid lost its text: {errors}");
        // Both attributes have the message, and the error's attribute no longer fails: the failing one is the error's.
        Require(TextKey(errors, "handle") == "too_long", $"The same-message error did not follow the failing attribute: {errors}");
    }

    // A property that hides an inherited one with new, and a member of a union case, have generated metadata.
    [Test]
    public void AHidingPropertyAndAUnionCaseMemberTakeTheirTexts()
    {
        var model = new ValidationTextViewModel { Alias = "too long", Shape = new ValidationTextCircle { Radius = 0 } };
        model.Validate();
        using var host = ValidationTextHost(model);
        var errors = ValidationErrors(host);
        Require(TextKey(errors, "alias") == "too_long", $"A property that hides an inherited one did not take its text: {errors}");
        Require(TextKey(errors, "shape", "radius") == "out_of_range", $"A union case's member did not take its text: {errors}");
    }

    // An attribute that throws while it is evaluated, or metadata that throws, leaves the member's errors without a text,
    // logged once (1177), and the publication and the other members' texts unharmed.
    [Test]
    public void AnAttributeThatThrowsLeavesItsMemberWithoutText()
    {
        var model = new ValidationTextFailures();
        BridgeDataSubscriptionMember[] metadata =
        [
            new("flaky", static owner => ((ValidationTextFailures)owner).Flaky, propertyName: nameof(ValidationTextFailures.Flaky))
            {
                ValidationProperty = static () => typeof(ValidationTextFailures).GetProperty(nameof(ValidationTextFailures.Flaky)),
            },
            new("name", static owner => ((ValidationTextFailures)owner).Name, propertyName: nameof(ValidationTextFailures.Name))
            {
                ValidationProperty = static () => throw new System.Reflection.AmbiguousMatchException("Ambiguous."),
            },
            new("other", static owner => ((ValidationTextFailures)owner).Other, propertyName: nameof(ValidationTextFailures.Other))
            {
                ValidationProperty = static () => typeof(ValidationTextFailures).GetProperty(nameof(ValidationTextFailures.Other)),
            },
        ];
        var logger = new CapturingLogger();
        for (var publication = 0; publication < 2; publication++)
        {
            using var buffer = new MemoryStream();
            using (BridgeSnapshotPublication.Enter(static () => false, logger, AttributeTexts()))
            using (var writer = new Utf8JsonWriter(buffer)) BridgeValidation.Write(writer, model, metadata);
            using var document = JsonDocument.Parse(buffer.ToArray());
            var errors = document.RootElement.GetProperty("errors");
            Require(!ErrorAt(errors, "flaky").TryGetProperty("text", out _) && !ErrorAt(errors, "name").TryGetProperty("text", out _)
                && TextKey(errors, "other") == "required", $"A failing attribute or metadata changed the texts: {errors}");
        }
        Require(logger.Events.SequenceEqual([1177, 1177]), $"The failures were not logged once each as 1177: {string.Join(", ", logger.Events)}");
    }

    // The generated metadata of the fixture: ValidationProperty from the declaring type, DeclaredOnly, on ViewModel,
    // DTO and union case members.
    [Test]
    public void GeneratedValidationMetadata()
    {
        var root = DomainFailureCodegenTests.FindWorkspaceRoot();
        var project = Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests");
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(TranslatedTextTests).Assembly)?.Configuration ?? "Debug";
        var bridges = Path.Combine(project, "obj", configuration, "net10.0", "runic-bridge");
        DomainFailureCodegenTests.CompareGolden(project, "validation-texts",
            [("ValidationTextViewModel.Bridge.g.cs", Path.Combine(bridges, "Runic.Application.Testing.Tests.ValidationTextViewModel.Bridge.g.cs"))]);
    }

    [Test]
    public void WithoutTextForAttributeMessagesStayAsTheyAre()
    {
        var model = new ValidationTextViewModel { Tag = "much too long" };
        model.Validate();
        using var host = new RunicWindowTestHost<ValidationTextViewModel>(model,
            (transport, content, vm) => new ValidationTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "validationText" });
        var errors = ValidationErrors(host);
        Require(errors.GetArrayLength() > 0 && errors.EnumerateArray().All(error => !error.TryGetProperty("text", out _)),
            $"An error has a text without RunicValidationOptions: {errors}");
    }

    [Test]
    public void AToolkitValidatorsAttributeMessagesTakeTheTextOfTextFor()
    {
        var model = new ToolkitValidationViewModel();
        model.Validate();
        BridgeDataSubscriptionMember[] metadata =
        [
            new("name", static owner => ((ToolkitValidationViewModel)owner).Name, propertyName: nameof(ToolkitValidationViewModel.Name))
            {
                ValidationProperty = static () => typeof(ToolkitValidationViewModel).GetProperty(nameof(ToolkitValidationViewModel.Name)),
            },
            new("count", static owner => ((ToolkitValidationViewModel)owner).Count, propertyName: nameof(ToolkitValidationViewModel.Count)),
        ];
        using var buffer = new MemoryStream();
        using (BridgeSnapshotPublication.Enter(static () => false, null, AttributeTexts()))
        using (var writer = new Utf8JsonWriter(buffer)) BridgeValidation.Write(writer, model, metadata);
        using var document = JsonDocument.Parse(buffer.ToArray());
        var errors = document.RootElement.GetProperty("errors");
        // An ErrorMessage the attribute sets is its message too.
        Require(Error(errors, "Name is required.").GetProperty("text").GetProperty("key").GetString() == "required",
            $"An ObservableValidator's [Required] did not take its text: {errors}");
        Require(!Error(errors, "Count must be between 1 and 10.").TryGetProperty("text", out _),
            "A member without ValidationProperty metadata took a text.");
    }

    [Test]
    public void ATextForThatThrowsIsLoggedAndTheMessageShown()
    {
        var model = new ValidationTextViewModel();
        model.Validate();
        BridgeDataSubscriptionMember[] metadata =
        [
            new("title", static owner => ((ValidationTextViewModel)owner).Title, propertyName: nameof(ValidationTextViewModel.Title))
            {
                ValidationProperty = static () => typeof(ValidationTextViewModel).GetProperty(nameof(ValidationTextViewModel.Title)),
            },
        ];
        var logger = new CapturingLogger();
        using var buffer = new MemoryStream();
        using (BridgeSnapshotPublication.Enter(static () => false, logger,
            new RunicValidationOptions { TextFor = static (_, _) => throw new InvalidOperationException("broken") }))
        using (var writer = new Utf8JsonWriter(buffer)) BridgeValidation.Write(writer, model, metadata);
        using var document = JsonDocument.Parse(buffer.ToArray());
        var error = Error(document.RootElement.GetProperty("errors"), "The Heading field is required.");
        Require(!error.TryGetProperty("text", out _), "A failed TextFor gave its error a text.");
        Require(logger.Events.SequenceEqual([1176]), $"The failure was not logged as 1176: {string.Join(", ", logger.Events)}");
    }

    [Test]
    public void CreateTakesTheServicesValidationOptions()
    {
        var model = new ValidationTextViewModel();
        model.Validate();
        var services = new ServiceCollection()
            .AddSingleton(AttributeTexts())
            .AddSingleton<Func<IBridgeTransport, WindowContentSession, ValidationTextViewModel, IDisposable>>(
                (transport, content, vm) => new ValidationTextBridge(transport, vm, content: content))
            .BuildServiceProvider();
        using var host = RunicWindowTestHost.Create(services, model, new RunicWindowTestHostOptions { RootRoute = "validationText" });
        Require(ErrorAt(ValidationErrors(host), "title").GetProperty("text").GetProperty("key").GetString() == "required",
            "RunicWindowTestHost.Create did not take the services' RunicValidationOptions.");
    }

    private sealed class ErrorsOnlyModel(object error) : System.ComponentModel.INotifyDataErrorInfo
    {
        public bool HasErrors => true;
        public event EventHandler<System.ComponentModel.DataErrorsChangedEventArgs>? ErrorsChanged { add { } remove { } }
        public System.Collections.IEnumerable GetErrors(string? propertyName) => propertyName is null ? new[] { error } : Array.Empty<object>();
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<int> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Events.Add(eventId.Id);
    }

    private static string WorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    [Test]
    public void TheHelloListsTheFeature()
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport);
        using var reply = JsonDocument.Parse(transport.Request(BridgeRequests.Json(writer =>
        {
            writer.WriteString("kind", "hello");
            writer.WriteNumber("protocol", 4);
            writer.WriteString("session", "s-1");
            writer.WriteStartObject("client");
            writer.WriteString("name", "test");
            writer.WriteString("version", "1.0.0");
            writer.WriteEndObject();
        }), "c"));
        Require(reply.RootElement.GetProperty("features").EnumerateArray().Any(feature => feature.GetString() == Feature),
            $"The hello reply does not list {Feature}: {reply.RootElement}");
    }

    [Test]
    public void ARunicTextReferenceChecksItsShape()
    {
        var reference = RunicTextReference.Parse(Json(TranslatedText.TitleTooLong));
        Require(reference.Version == 2 && reference.Catalog == "notes" && reference.Key == "title_too_long"
            && reference.FallbackText == "Title must be at most 8 characters." && reference.ToString() == reference.FallbackText,
            "The reference's members were not read.");
        Require(reference.Equals(TranslatedText.Text(TranslatedText.TitleTooLong))
            && reference.GetHashCode() == TranslatedText.Text(TranslatedText.TitleTooLong).GetHashCode(),
            "Two references with the same JSON are not equal.");
        Require(RunicTextReference.Parse("""{"version":1,"catalog":"c","contractFingerprint":"f","key":"k","arguments":{"n":"1"}}""").Version == 1,
            "A version 1 reference was refused.");
        foreach (var invalid in new[]
        {
            "[]", "\"k\"", """{"version":0,"catalog":"c","contractFingerprint":"f","key":"k","arguments":{}}""",
            """{"version":2,"catalog":"c","contractFingerprint":"f","key":"","arguments":{}}""",
            """{"version":2,"contractFingerprint":"f","key":"k","arguments":{}}""",
            """{"version":2,"catalog":"c","contractFingerprint":"f","key":"k","arguments":[]}""",
            """{"version":2,"catalog":"c","contractFingerprint":"f","key":"k","arguments":{},"fallbackText":1}""",
            """{"version":2,"catalog":"c","contractFingerprint":"f","key":"k","arguments":{}} {}""",
            "{\"version\":2,\"catalog\":\"c\",\"contractFingerprint\":\"f\",\"key\":\"k\",\"arguments\":{},\"fallbackText\":\"" + new string('x', 300_000) + "\"}",
        })
        {
            var thrown = false;
            try { _ = RunicTextReference.Parse(invalid); } catch (JsonException) { thrown = true; }
            Require(thrown, $"{(invalid.Length > 80 ? invalid[..80] : invalid)} was read as a text reference.");
        }
    }

    private static async Task BridgeTargetedPushesIdleAsync(RunicWindowTestHost<TranslatedTextViewModel> host)
    {
        // A push to "new" queued after any push to "old" shows that the queue reached it.
        host.Content.Locale = "fr";
        using (await host.Transport.TakePushAsync("new", "locale", TimeSpan.FromSeconds(5))) { }
    }

    private static JsonElement Error(JsonElement errors, string message) =>
        errors.EnumerateArray().FirstOrDefault(error => error.GetProperty("message").GetString() == message) is { ValueKind: JsonValueKind.Object } found
            ? found : throw new InvalidOperationException($"No error '{message}': {errors}");

    private static string GeneratedTypeScriptDirectory()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx")))
                return Path.Combine(directory.FullName, "tests", "dotnet", "Runic.Application.Testing.Tests", "obj", "bridge-frontend", "generated");
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

// An ObservableValidator whose rule returns a RunicValidationResult. It has no View (the generator's Native AOT check
// refuses an ObservableValidator, see ToolkitValidationFixture.cs).
public sealed partial class TranslatedToolkitViewModel : ObservableValidator
{
    [CustomValidation(typeof(TranslatedToolkitViewModel), nameof(ValidateTitle))]
    [ObservableProperty]
    public partial string Title { get; set; } = "";

    public void Validate() => ValidateAllProperties();

    public static ValidationResult? ValidateTitle(string title, ValidationContext context) => title.Length <= 8
        ? ValidationResult.Success
        : new RunicValidationResult("Title must be at most 8 characters.", TranslatedText.Text(TranslatedText.TitleTooLong),
            [context.MemberName!]);
}
