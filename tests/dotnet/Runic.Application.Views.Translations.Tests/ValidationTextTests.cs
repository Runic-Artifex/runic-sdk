using System.ComponentModel.DataAnnotations;
using Runic.Translations;
using TUnit.Core;
using static Runic.Application.Views.Translations.Tests.CatalogTests;

namespace Runic.Application.Views.Translations.Tests;

// RunicViewsTranslations.ValidationText: the built-in attributes' messages as runic-views references, in English and German.
public sealed class ValidationTextTests
{
    public static IEnumerable<Func<(ValidationAttribute Attribute, string Key, string English, string German)>> Attributes()
    {
        yield return () => (new RequiredAttribute(), "validation_required", "Name is required.", "Name ist erforderlich.");
        yield return () => (new MaxLengthAttribute(8), "validation_max_length", "Name can have at most 8 characters.", "Name darf höchstens 8 Zeichen haben.");
        yield return () => (new MaxLengthAttribute(1), "validation_max_length", "Name can have at most 1 character.", "Name darf höchstens 1 Zeichen haben.");
        yield return () => (new MinLengthAttribute(3), "validation_min_length", "Name must have at least 3 characters.", "Name muss mindestens 3 Zeichen haben.");
        yield return () => (new StringLengthAttribute(1200), "validation_max_length", "Name can have at most 1,200 characters.", "Name darf höchstens 1.200 Zeichen haben.");
        yield return () => (new StringLengthAttribute(10) { MinimumLength = 2 }, "validation_length_between", "Name must have 2 to 10 characters.", "Name muss 2 bis 10 Zeichen haben.");
        yield return () => (new LengthAttribute(2, 10), "validation_length_between", "Name must have 2 to 10 characters.", "Name muss 2 bis 10 Zeichen haben.");
        yield return () => (new RangeAttribute(18, 120), "validation_range_integer", "Name must be between 18 and 120.", "Name muss zwischen 18 und 120 liegen.");
        yield return () => (new RangeAttribute(1.0, 10.0), "validation_range_integer", "Name must be between 1 and 10.", "Name muss zwischen 1 und 10 liegen.");
        yield return () => (new RangeAttribute(0.5, 9.5), "validation_range_number", "Name must be between 0.5 and 9.5.", "Name muss zwischen 0,5 und 9,5 liegen.");
        yield return () => (new RangeAttribute(typeof(decimal), "0.25", "1000.5"), "validation_range_number", "Name must be between 0.25 and 1,000.5.", "Name muss zwischen 0,25 und 1.000,5 liegen.");
        yield return () => (Evaluated(new RangeAttribute(typeof(decimal), "0.25", "1000.5") { ParseLimitsInInvariantCulture = true }, 1m),
            "validation_range_number", "Name must be between 0.25 and 1,000.5.", "Name muss zwischen 0,25 und 1.000,5 liegen.");
        yield return () => (new RangeAttribute(typeof(DateTime), "2020-01-01", "2030-12-31"), "validation_range", "Name must be between 2020-01-01 and 2030-12-31.", "Name muss zwischen 2020-01-01 und 2030-12-31 liegen.");
        yield return () => (Evaluated(new RangeAttribute(typeof(DateTime), "2020-01-01", "2030-12-31") { ParseLimitsInInvariantCulture = true }, new DateTime(2025, 1, 1)),
            "validation_range", "Name must be between 2020-01-01 and 2030-12-31.", "Name muss zwischen 2020-01-01 und 2030-12-31 liegen.");
        yield return () => (new EmailAddressAttribute(), "validation_email", "Name is not a valid email address.", "Name ist keine gültige E-Mail-Adresse.");
        yield return () => (new PhoneAttribute(), "validation_phone", "Name is not a valid phone number.", "Name ist keine gültige Telefonnummer.");
        yield return () => (new UrlAttribute(), "validation_url", "Name is not a valid web address (http, https or ftp).", "Name ist keine gültige Webadresse (http, https oder ftp).");
        yield return () => (new CreditCardAttribute(), "validation_credit_card", "Name is not a valid credit card number.", "Name ist keine gültige Kreditkartennummer.");
        yield return () => (new RegularExpressionAttribute("^[a-z]+$"), "validation_pattern", "Name is not in the expected format.", "Name hat nicht das erwartete Format.");
        yield return () => (new CompareAttribute("Password"), "validation_compare", "Name and Password do not match.", "Name und Password stimmen nicht überein.");
        yield return () => (new FileExtensionsAttribute { Extensions = "png,jpg" }, "validation_file_extensions", "Name must have one of these file extensions: png, jpg.", "Name muss eine dieser Dateiendungen haben: png, jpg.");
        yield return () => (new Base64StringAttribute(), "validation_base64", "Name is not valid Base64 text.", "Name ist kein gültiger Base64-Text.");
        yield return () => (new AllowedValuesAttribute("a", "b"), "validation_allowed_values", "Name is not one of the allowed values.", "Name hat keinen der zulässigen Werte.");
        yield return () => (new DeniedValuesAttribute("x"), "validation_denied_values", "Name is one of the values that are not allowed.", "Name hat einen Wert, der nicht zulässig ist.");
        yield return () => (new EnumDataTypeAttribute(typeof(DayOfWeek)), "validation_enum", "Name is not one of the available values.", "Name hat keinen der verfügbaren Werte.");
    }

    [Test]
    [MethodDataSource(nameof(Attributes))]
    public void TheBuiltInAttributesTakeTheCatalogsText(ValidationAttribute attribute, string key, string english, string german)
    {
        var member = new RunicValidationMember(typeof(object), "Name", "Name", attribute.FormatErrorMessage("Name"));
        var text = RunicViewsTranslations.ValidationText(attribute, member)
            ?? throw new InvalidOperationException($"{attribute.GetType().Name} took no text.");
        Require(text is { Catalog: "runic-views", ContractFingerprint: RunicViewsTextCatalog.ContractFingerprint } && text.Key == key,
            $"{attribute.GetType().Name}: {text.ToJson()}");
        Require(text.FallbackText == english, $"{attribute.GetType().Name} fallback: {text.FallbackText}");
        var reference = TranslationReference.Parse(text.ToJson());
        Require(Context("en").Resolve(reference).Text == english, $"{attribute.GetType().Name} in English: {Context("en").Resolve(reference).Text}");
        Require(Context("de").Resolve(reference).Text == german, $"{attribute.GetType().Name} in German: {Context("de").Resolve(reference).Text}");
    }

    public static IEnumerable<Func<ValidationAttribute>> Kept()
    {
        yield return () => new RequiredAttribute { ErrorMessage = "Fill in your name." };
        yield return () => new MaxLengthAttribute(8) { ErrorMessageResourceType = typeof(ValidationTextTests), ErrorMessageResourceName = nameof(Message) };
        yield return () => new DerivedRequiredAttribute();
        yield return () => new MaxLengthAttribute();
        yield return () => new EmailAddressAttribute { ErrorMessage = "Use your work address." };
        yield return () => new RangeAttribute(0, 10) { MinimumIsExclusive = true };
        yield return () => new RangeAttribute(0, double.MaxValue);
        yield return () => new CustomValidationAttribute(typeof(ValidationTextTests), nameof(Validate));
    }

    public static string Message => "Too long.";

    public static ValidationResult? Validate(object value) => ValidationResult.Success;

    // An attribute with its own message, a derived attribute, and limits the catalog has no text for keep their message.
    [Test]
    [MethodDataSource(nameof(Kept))]
    public void OtherAttributesKeepTheirMessage(ValidationAttribute attribute)
    {
        var member = new RunicValidationMember(typeof(object), "Name", "Name", "message");
        Require(RunicViewsTranslations.ValidationText(attribute, member) is null && RunicViewsTranslations.ValidationReference(attribute, member) is null,
            $"{attribute.GetType().Name} took a text.");
    }

    [Test]
    public void NullArgumentsAreRefused()
    {
        RequireThrows<ArgumentNullException>(() => RunicViewsTranslations.ValidationText(null!, new(typeof(object), "a", "a", "m")));
        RequireThrows<ArgumentNullException>(() => RunicViewsTranslations.ValidationText(new RequiredAttribute(), null!));
    }

    private static ValidationAttribute Evaluated(ValidationAttribute attribute, object value)
    {
        attribute.IsValid(value);
        return attribute;
    }

    private sealed class DerivedRequiredAttribute : RequiredAttribute;
}
