using System.Text;
using Runic.Platform.Linux.Portal;
using TUnit.Core;

// libsecret's file backend format, checked against a keyring libsecret itself wrote.
internal sealed class LibsecretKeyringTests
{
    // Written by libsecret 0.21.7's file backend (secret-tool from nixpkgs), isolated from any keyring:
    //   DBUS_SESSION_BUS_ADDRESS=unix:path=/nonexistent/bus SECRET_BACKEND=file SECRET_FILE_TEST_PATH=$PWD/default.keyring \
    //   SECRET_FILE_TEST_PASSWORD=<FixturePassword> sh -c '
    //     printf "text-secret-é" | secret-tool store --label=com.example.Text service com.example.Text account user@example.com
    //     printf "\000\001\002\377\376" | secret-tool store --label=bin service com.example.Bin account bin'
    internal const string FixturePassword = "runic-test-password-0123456789abcdef0123456789abcdef0123456789ab";
    internal const string Fixture =
        "R25vbWVLZXlyaW5nCg0ACgEAIAAAAGaDHSgglKaDYsxK2mlbzprHVcpSS3Cc5sok9/3bNPceoIYBAOoN22oAAAAAAgAAAGFjY291bnQAuUvJ316guOaQtNiI8aNb8Wnj5uHLzaxs0iNHzXJ86DEIc2VydmljZQCcdjSDOVTzO1xoS65D9JBl7xvmODdDMdzxjQ8cfLslkAgpUklXNzHPAEUpjD2SeKoZT/USbNqr7FqSSR3vSvcgP0v8BIDmlaEw9DMVXDTmpf/ZEbzJ44P57+nNNb8SRBVephzUlfK9t7YDQIrCILdZD3d0uVG6n84zJZQTkemUBVl8nlb25jbx5naF+IGq5BmOkbleRE9CBBnnK/YPhW98RDVQSI2OxCv4Jl17sFZN12ivsWkVmFAleCDUDE/i87VZ5UBUYWNjb3VudABv1n0RLGRz7ffhzvj3qJQi3Jsu0a5DmIfv4kiwu6dxRwhzZXJ2aWNlAP8eUTIis32lkTjCVXnkbXBd2TzeNJY6ApQENEug7dq2CClS6ET1Iz4g5fcIegl2dfeKbv0//ZfLKp8YxNhsn9uqeJo3PGlZbW/ZNAIohEEQAXs2vS7g81tsfEmeGnMJDcwENMeTP7v607xkGhG4xhxicS6zFofmgryvvA/HwY0yTuebxXKnW9ePTMOPeEER1EIKTj6E2XmXvn78y2h5jgw27M9U9QDKASQA";

    [Test, DisplayName("libsecret file: a keyring libsecret wrote parses, serialises back byte for byte and decrypts")]
    public void ReadsLibsecretOutput()
    {
        byte[] file = Convert.FromBase64String(Fixture);
        var keyring = LibsecretKeyring.Parse(file);
        Check(keyring.Salt.Length == 32 && keyring.Iterations == 100_000 && keyring.UsageCount == 2 && keyring.Items.Count == 2, "header fields");
        Check(keyring.Serialize().AsSpan().SequenceEqual(file), "the GVariant writer reproduces libsecret's normal form exactly");

        byte[] key = keyring.DeriveKey(Encoding.UTF8.GetBytes(FixturePassword));
        var text = new Dictionary<string, string> { ["service"] = "com.example.Text", ["account"] = "user@example.com" };
        var binary = new Dictionary<string, string> { ["service"] = "com.example.Bin", ["account"] = "bin" };
        var first = keyring.Items.Single(item => LibsecretKeyring.Matches(item.Attributes, key, text));
        var second = keyring.Items.Single(item => LibsecretKeyring.Matches(item.Attributes, key, binary));
        Check(first.Attributes.Select(a => a.Name).SequenceEqual(["account", "service"]), "hashed attributes are sorted by name");
        Check(LibsecretKeyring.Hash(key, text).Zip(first.Attributes).All(pair => pair.First.Name == pair.Second.Name && pair.First.Mac.SequenceEqual(pair.Second.Mac)),
            "our attribute hashes equal libsecret's");
        var item = LibsecretKeyring.Decrypt(key, first.Blob)!;
        Check(Encoding.UTF8.GetString(item.Secret) == "text-secret-é" && item.Label == "com.example.Text" && item.Attributes.Count == 2
            && item.Attributes["service"] == "com.example.Text" && item.Created == item.Modified && item.Created > 0, "the text item");
        Check(LibsecretKeyring.Decrypt(key, second.Blob)!.Secret.SequenceEqual(new byte[] { 0, 1, 2, 0xFF, 0xFE }), "the binary item");

        byte[] wrong = keyring.DeriveKey(Encoding.UTF8.GetBytes(FixturePassword.ToUpperInvariant()));
        Check(!keyring.Items.Any(entry => LibsecretKeyring.Matches(entry.Attributes, wrong, text)) && LibsecretKeyring.Decrypt(wrong, first.Blob) is null,
            "another password finds and decrypts nothing");
        byte[] damaged = first.Blob.ToArray();
        damaged[3] ^= 1;
        Check(LibsecretKeyring.Decrypt(key, damaged) is null, "a damaged blob fails its MAC");
    }

    [Test, DisplayName("libsecret file: a keyring Runic writes reads back, and damaged files are rejected")]
    public void WritesAndRejects()
    {
        var keyring = LibsecretKeyring.Create();
        byte[] key = keyring.DeriveKey(new byte[64]);
        var attributes = new Dictionary<string, string> { ["xdg:schema"] = "org.freedesktop.Secret.Generic", ["service"] = "s", ["account"] = "a" };
        byte[] secret = new byte[65_536];
        Random.Shared.NextBytes(secret);
        keyring.Items.Add((LibsecretKeyring.Hash(key, attributes), LibsecretKeyring.Encrypt(key, new(attributes, "s (a)", 1, 2, secret))));
        keyring.Items.Add((LibsecretKeyring.Hash(key, attributes), LibsecretKeyring.Encrypt(key, new(attributes, "", 3, 4, []))));
        byte[] file = keyring.Serialize();
        var read = LibsecretKeyring.Parse(file);
        Check(read.Serialize().AsSpan().SequenceEqual(file) && read.Items.Count == 2, "round trip");
        var item = LibsecretKeyring.Decrypt(key, read.Items[0].Blob)!;
        Check(item.Secret.SequenceEqual(secret) && item.Label == "s (a)" && item.Created == 1 && item.Modified == 2 && item.Attributes.Count == 3, "64 KiB item");
        Check(LibsecretKeyring.Decrypt(key, read.Items[1].Blob)!.Secret.Length == 0, "an empty secret");
        Check(LibsecretKeyring.Parse(LibsecretKeyring.Create().Serialize()).Items.Count == 0, "an empty keyring");

        foreach (var broken in new[] { file[..10], [.. "GnomeKeyring\n\r\0\n"u8, 2, 0, .. file[18..]], file[..^3], [.. file, 0] })
        {
            try { LibsecretKeyring.Parse(broken); throw new InvalidOperationException("A damaged keyring parsed."); }
            catch (FormatException) { }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("libsecret keyring check failed: " + message);
    }
}
