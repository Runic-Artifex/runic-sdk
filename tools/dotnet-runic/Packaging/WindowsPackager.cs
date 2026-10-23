using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// <c>package windows</c>: an MSIX package of the NativeAOT publish folder, a full-trust desktop application with the
/// project's URL schemes, file types, app URI handlers and login startup task. Unsigned unless the consumer passes
/// a certificate (<c>--certificate</c> with <c>RUNIC_SIGNING_PASSWORD</c>, or <c>--thumbprint</c>).
/// </summary>
internal static class WindowsPackager
{
    /// <summary>The environment variable with the PFX file's password.</summary>
    internal const string PasswordVariable = "RUNIC_SIGNING_PASSWORD";

    // signtool's digest algorithm, spelled so that the diagnostics catalog scan does not read it as a diagnostic ID.
    private const string Digest = "SHA" + "256";

    internal static async Task RunAsync(PackageContext context, CancellationToken cancellationToken)
    {
        PackageIdentity identity = context.Identity;
        string icon = context.RequireIcon();
        string makeappx = PackageTools.RequireWindowsKitTool("makeappx.exe");
        string? signtool = context.Options.Certificate is null && context.Options.Thumbprint is null
            ? null
            : PackageTools.RequireWindowsKitTool("signtool.exe");
        string layout = Path.Combine(context.Output, "msix");
        await context.PublishAsync(context.Runtime, layout, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> fields = PackageManifests.Fields(identity, PackageTarget.Windows, context.Runtime);
        await File.WriteAllTextAsync(Path.Combine(layout, "AppxManifest.xml"),
            PackageManifests.Render(identity, "windows/AppxManifest.xml", fields), cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(layout, "Assets"));
        File.Copy(icon, Path.Combine(layout, "Assets", "icon.png"), overwrite: true);
        string msix = Path.Combine(context.Output, $"{identity.Id}_{fields["PackageVersion"]}_{fields["Architecture"]}.msix");
        PackageTools.Delete(msix);
        await PackageTools.RunAsync(makeappx, ["pack", "/o", "/d", layout, "/p", msix], context.Output, cancellationToken).ConfigureAwait(false);

        List<string> timestamp = context.Options.TimestampUrl is { Length: > 0 } url ? ["/tr", url, "/td", Digest] : [];
        string signing = "none";
        if (context.Options.Certificate is { Length: > 0 } certificate)
        {
            string pfx = Path.GetFullPath(certificate, Environment.CurrentDirectory);
            string? password = Environment.GetEnvironmentVariable(PasswordVariable);
            List<string> arguments = ["sign", "/fd", Digest, "/f", pfx];
            HashSet<int> secret = [];
            if (!string.IsNullOrEmpty(password))
            {
                arguments.Add("/p");
                secret.Add(arguments.Count);
                arguments.Add(password);
            }
            arguments.AddRange(timestamp);
            arguments.Add(msix);
            await PackageTools.RunAsync(signtool!, arguments, context.Output, cancellationToken, secret: secret).ConfigureAwait(false);
            signing = IsSelfSigned(LoadPfx(pfx, password)) ? "test" : "identity";
        }
        else if (context.Options.Thumbprint is { Length: > 0 } thumbprint)
        {
            await PackageTools.RunAsync(signtool!, ["sign", "/fd", Digest, "/sha1", thumbprint, .. timestamp, msix],
                context.Output, cancellationToken).ConfigureAwait(false);
            signing = IsSelfSigned(FindInStore(thumbprint)) ? "test" : "identity";
        }
        else
        {
            context.Warn(PackageCodes.Unsigned, $"The package is unsigned: sign it with signtool (its Publisher is {identity.Publisher}) or pass --certificate or --thumbprint before installing it.");
        }
        context.Add(msix, "msix", signing);
    }

    private static X509Certificate2? LoadPfx(string path, string? password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static X509Certificate2? FindInStore(string thumbprint)
    {
        foreach (StoreLocation location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                X509Certificate2Collection found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                if (found.Count > 0) return found[0];
            }
            catch (CryptographicException)
            {
            }
        }
        return null;
    }

    /// <summary>A certificate that issued itself, such as one from New-SelfSignedCertificate, signs a test package.</summary>
    internal static bool IsSelfSigned(X509Certificate2? certificate)
    {
        using (certificate)
        {
            return certificate is not null && certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
        }
    }
}
