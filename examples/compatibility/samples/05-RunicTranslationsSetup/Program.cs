using System.Text.Json;
using RunicArtifex.Examples.Translations.Generated;
using Runic.Translations;

if (args.Contains("--smoke-test", StringComparer.Ordinal))
{
    return await RunSmokeTestAsync();
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.Build();

app.MapPost("/api/registration", async () =>
{
    TranslationReference reference = await CreateRequiredReferenceAsync("email");
    string json = JsonSerializer.Serialize(reference, TranslationReferenceJsonContext.Default.TranslationReference);
    return Results.Text(json, "application/json", statusCode: StatusCodes.Status400BadRequest);
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
await app.RunAsync();
return 0;

static async ValueTask<TranslationReference> CreateRequiredReferenceAsync(string field)
{
    ITranslationManager manager = await SetupTextCatalog.CreateManagerAsync("en");
    var text = new SetupText(manager);
    return new TranslationReference(
        SetupTextCatalog.CatalogId,
        SetupTextCatalog.ContractFingerprint,
        "validation_required",
        new Dictionary<string, TranslationReferenceArgument>(StringComparer.Ordinal)
        {
            ["field"] = new(TextArgumentType.String, field),
        },
        text.validation_required(field));
}

static async Task<int> RunSmokeTestAsync()
{
    Task<string> english = FormatForRequestAsync("en", "email");
    Task<string> german = FormatForRequestAsync("de", "E-Mail");
    string[] concurrent = await Task.WhenAll(english, german);

    Assert(concurrent[0] == "The field email is required.", "English request-scoped formatting failed.");
    Assert(concurrent[1] == "Das Feld E-Mail ist erforderlich.", "German request-scoped formatting failed.");

    TranslationReference reference = await CreateRequiredReferenceAsync("email");
    reference.ValidateCatalog(SetupTextCatalog.CatalogId, SetupTextCatalog.ContractFingerprint);
    string json = JsonSerializer.Serialize(reference, TranslationReferenceJsonContext.Default.TranslationReference);
    TranslationReference roundTrip = JsonSerializer.Deserialize(json, TranslationReferenceJsonContext.Default.TranslationReference)
        ?? throw new InvalidOperationException("The translation reference did not round-trip.");
    Assert(roundTrip.Key == "validation_required", "The stable translation key changed in transport.");
    Assert(roundTrip.Arguments["field"].Value == "email", "The typed argument changed in transport.");
    Assert(roundTrip.FallbackText == concurrent[0], "The version-skew fallback was not transported.");

    Console.WriteLine("PASS: C# and ESM generation, typed transport, fallback, and concurrent locale isolation");
    return 0;
}

static async Task<string> FormatForRequestAsync(string locale, string field)
{
    ITranslationManager manager = await SetupTextCatalog.CreateManagerAsync(locale);
    var text = new SetupText(manager);
    await Task.Yield();
    return text.validation_required(field);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
