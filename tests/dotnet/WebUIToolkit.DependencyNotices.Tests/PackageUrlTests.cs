using System;
using System.Collections.Generic;

namespace WebUIToolkit.DependencyNotices.Tests;

internal static class PackageUrlTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("purl canonicalizes scheme and NuGet type", CanonicalizesNuGetIdentity);
        tests.Add("purl preserves NuGet package name case", PreservesNuGetNameCase);
        tests.Add("purl canonicalizes an npm scope", CanonicalizesNpmScope);
        tests.Add("purl canonicalizes percent encoding", CanonicalizesPercentEncoding);
        tests.Add("purl orders and normalizes qualifiers", OrdersQualifiers);
        tests.Add("purl preserves plus in qualifier values", PreservesPlusInQualifierValues);
        tests.Add("purl parses a safe subpath", ParsesSafeSubpath);
        tests.Add("purl rejects dot and traversal subpaths", RejectsUnsafeSubpaths);
        tests.Add("purl requires an exact version", RequiresExactVersion);
        tests.Add("purl requires a type and name", RequiresTypeAndName);
        tests.Add("purl rejects duplicate qualifier keys", RejectsDuplicateQualifierKeys);
        tests.Add("purl rejects invalid qualifier keys", RejectsInvalidQualifierKeys);
        tests.Add("purl rejects malformed percent encoding", RejectsMalformedPercentEncoding);
        tests.Add("purl rejects malformed fragments", RejectsMalformedFragments);
        tests.Add("purl rejects authorities and backslashes", RejectsUrlAndPathConfusion);
        tests.Add("purl TryParse has a null-safe contract", TryParseIsNullSafe);
        tests.Add("purl Parse reports invalid input", ParseReportsInvalidInput);
    }

    private static void CanonicalizesNuGetIdentity()
    {
        PackageUrl purl = PackageUrl.Parse("PKG:NuGet/Newtonsoft.Json@13.0.3");

        Assert.Equal("nuget", purl.Type);
        Assert.Equal(null, purl.Namespace);
        Assert.Equal("Newtonsoft.Json", purl.Name);
        Assert.Equal("13.0.3", purl.Version);
        Assert.Equal("PKG:NuGet/Newtonsoft.Json@13.0.3", purl.OriginalValue);
        Assert.Equal("pkg:nuget/Newtonsoft.Json@13.0.3", purl.CanonicalValue);
        Assert.Equal(purl.CanonicalValue, purl.ToString());
    }

    private static void PreservesNuGetNameCase()
    {
        PackageUrl upper = PackageUrl.Parse("pkg:nuget/Example.Package@1.0.0");
        PackageUrl lower = PackageUrl.Parse("pkg:nuget/example.package@1.0.0");

        Assert.Equal("Example.Package", upper.Name);
        Assert.Equal("example.package", lower.Name);
        Assert.True(!StringComparer.Ordinal.Equals(upper.CanonicalValue, lower.CanonicalValue));
        Assert.True(!PackageUrl.TryParse("pkg:nuget/company/Example.Package@1.0.0", out _));
    }

    private static void CanonicalizesNpmScope()
    {
        PackageUrl encoded = PackageUrl.Parse("pkg:NPM/%40scope/package@2.1.0");
        PackageUrl raw = PackageUrl.Parse("pkg:npm/@scope/package@2.1.0");

        Assert.Equal("npm", encoded.Type);
        Assert.Equal("@scope", encoded.Namespace);
        Assert.Equal("package", encoded.Name);
        Assert.Equal("pkg:npm/%40scope/package@2.1.0", encoded.CanonicalValue);
        Assert.Equal(encoded.CanonicalValue, raw.CanonicalValue);
    }

    private static void CanonicalizesPercentEncoding()
    {
        PackageUrl purl = PackageUrl.Parse("pkg:generic/acme/caf%c3%a9@1.0%2bmeta");

        Assert.Equal("acme", purl.Namespace);
        Assert.Equal("café", purl.Name);
        Assert.Equal("1.0+meta", purl.Version);
        Assert.Equal("pkg:generic/acme/caf%C3%A9@1.0%2Bmeta", purl.CanonicalValue);
    }

    private static void OrdersQualifiers()
    {
        PackageUrl purl = PackageUrl.Parse(
            "pkg:generic/widget@4.2.1?Repository_URL=https%3a%2f%2fexample.invalid&arch=x64");

        Assert.Equal(2, purl.Qualifiers.Count);
        Assert.Equal("x64", purl.Qualifiers["arch"]);
        Assert.Equal("https://example.invalid", purl.Qualifiers["repository_url"]);
        Assert.Equal(
            "pkg:generic/widget@4.2.1?arch=x64&repository_url=https:%2F%2Fexample.invalid",
            purl.CanonicalValue);
        Assert.True(purl.Qualifiers is not IDictionary<string, string> mutable || mutable.IsReadOnly);
    }

    private static void PreservesPlusInQualifierValues()
    {
        PackageUrl purl = PackageUrl.Parse("pkg:generic/widget@1?tag=a+b");

        Assert.Equal("a+b", purl.Qualifiers["tag"]);
        Assert.Equal("pkg:generic/widget@1?tag=a%2Bb", purl.CanonicalValue);
    }

    private static void ParsesSafeSubpath()
    {
        PackageUrl purl = PackageUrl.Parse("pkg:generic/widget@1#licenses/Apache%20License.txt");

        Assert.Equal("licenses/Apache License.txt", purl.Subpath);
        Assert.Equal("pkg:generic/widget@1#licenses/Apache%20License.txt", purl.CanonicalValue);
    }

    private static void RejectsUnsafeSubpaths()
    {
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1#./LICENSE", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1#licenses/../secret", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1#licenses/%2E%2E/secret", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1#licenses//LICENSE", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1#licenses/%2FLICENSE", out _));
    }

    private static void RequiresExactVersion()
    {
        Assert.True(!PackageUrl.TryParse("pkg:nuget/example", out _));
        Assert.True(!PackageUrl.TryParse("pkg:nuget/example@", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/example@release/1", out _));
    }

    private static void RequiresTypeAndName()
    {
        Assert.True(!PackageUrl.TryParse("pkg:/example@1", out _));
        Assert.True(!PackageUrl.TryParse("pkg:nuget/@1", out _));
        Assert.True(!PackageUrl.TryParse("https:nuget/example@1", out _));
    }

    private static void RejectsDuplicateQualifierKeys()
    {
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1?arch=x64&arch=arm64", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1?Arch=x64&arch=arm64", out _));
    }

    private static void RejectsInvalidQualifierKeys()
    {
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1?1arch=x64", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1?bad%5fkey=x64", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1?arch=", out _));
    }

    private static void RejectsMalformedPercentEncoding()
    {
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget%2@1", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1?key=%FF", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@%C3", out _));
    }

    private static void RejectsMalformedFragments()
    {
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1#", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1#one#two", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic/widget@1#path?query=value", out _));
    }

    private static void RejectsUrlAndPathConfusion()
    {
        Assert.True(!PackageUrl.TryParse("pkg://nuget/example@1", out _));
        Assert.True(!PackageUrl.TryParse("pkg:generic\\example@1", out _));
        Assert.True(!PackageUrl.TryParse(" pkg:generic/example@1", out _));
    }

    private static void TryParseIsNullSafe()
    {
        Assert.True(!PackageUrl.TryParse(null, out PackageUrl? missing));
        Assert.Equal(null, missing);
        Assert.True(PackageUrl.TryParse("pkg:generic/example@1", out PackageUrl? parsed));
        Assert.Equal("pkg:generic/example@1", parsed!.CanonicalValue);
    }

    private static void ParseReportsInvalidInput()
    {
        Assert.Throws<FormatException>(() => PackageUrl.Parse("pkg:generic/example"));
        Assert.Throws<ArgumentNullException>(() => PackageUrl.Parse(null!));
    }
}
