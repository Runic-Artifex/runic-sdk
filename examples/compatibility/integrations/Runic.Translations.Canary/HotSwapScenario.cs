using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RunicArtifex.Examples.Translations.Generated;
using Runic.Translations;

namespace RunicArtifex.Examples.Translations;

/// <summary>
/// Deterministic hot-swap proof for the pinned Runic.Translations external pack pipeline.
/// Every missed assertion throws, so the process exits non-zero; the final marker line is
/// printed only after all three passes succeeded.
/// Packs/*.json embed the contract fingerprint of the current catalog contract; regenerate
/// them whenever translations/runic.json or its MF2 messages change, or passes 1-2 fail with RTR0023.
/// </summary>
internal static class HotSwapScenario
{
    private const string LocaleEn = "en";
    private const string LocaleDe = "de";
    private const string ArgumentName = "Runic Artifex";
    private const string ExpectedPackAText = "Hello, Runic Artifex!";
    private const string ExpectedPackBText = "Hi again, Runic Artifex!";
    private const string ExpectedCompiledGermanText = "Hallo, Runic Artifex!";

    public static async Task<int> RunAsync()
    {
        string packsDirectory = Path.Combine(AppContext.BaseDirectory, "Packs");
        string packAPath = Path.Combine(packsDirectory, "pack-a.en.json");
        string packBPath = Path.Combine(packsDirectory, "pack-b.en.json");
        string packCPath = Path.Combine(packsDirectory, "pack-c.en.json");
        string activePath = Path.Combine(packsDirectory, "active.en.json");

        File.Copy(packAPath, activePath, overwrite: true);

        var transitions = new List<string>();
        var provider = new RotatingExternalProvider(new FilePackSource(LocaleEn, activePath));
        ITranslationSnapshot initialSnapshot = await provider.GetSnapshotAsync(LocaleEn);
        var manager = new TranslationManager(provider, initialSnapshot);
        manager.LocaleChanged += (_, args) => transitions.Add($"{args.OldLocale}->{args.NewLocale}");
        var text = new CanaryText(manager);

        // PASS 1: compose an external manager from the staged pack A file and serve en.
        AssertTrue(
            string.Equals(text.Greeting(ArgumentName), ExpectedPackAText, StringComparison.Ordinal),
            $"pass 1 expected pack A greeting '{ExpectedPackAText}'.");
        Console.WriteLine($"PASS 1: composed external manager from staged pack A and served {ExpectedPackAText}");

        // PASS 2: atomically stage compatible pack B, cycle de then en so the recomposition
        // picks up the replacement bytes, and assert the new message is served exactly once.
        StageFile(packBPath, activePath);
        provider.Rotate(new FilePackSource(LocaleEn, activePath));
        await manager.SetLocaleAsync(LocaleDe);
        AssertTrue(
            string.Equals(text.Greeting(ArgumentName), ExpectedCompiledGermanText, StringComparison.Ordinal),
            $"pass 2 expected compiled German fallback '{ExpectedCompiledGermanText}' after switching to de.");
        await manager.SetLocaleAsync(LocaleEn);
        AssertTrue(
            string.Equals(text.Greeting(ArgumentName), ExpectedPackBText, StringComparison.Ordinal),
            $"pass 2 expected replacement pack B greeting '{ExpectedPackBText}' after returning to en.");
        AssertTrue(
            transitions.Count == 2 && transitions[0] == "en->de" && transitions[1] == "de->en",
            $"pass 2 expected exactly one LocaleChanged per transition but observed [{string.Join(", ", transitions)}].");
        Console.WriteLine($"PASS 2: atomically staged pack B across en->de->en and served {ExpectedPackBText}");

        // PASS 3: stage tampered pack C (valid shape, wrong contract fingerprint). The attempt
        // toward en must be rejected as RTR0023/contract-fingerprint-mismatch while the current
        // snapshot stays untouched, and recovery with pack B must serve its text again.
        StageFile(packCPath, activePath);
        provider.Rotate(new FilePackSource(LocaleEn, activePath));
        await manager.SetLocaleAsync(LocaleDe);
        ITranslationSnapshot snapshotBeforeAttempt = manager.Current;
        bool rejectedAsFingerprintMismatch = false;
        try
        {
            await manager.SetLocaleAsync(LocaleEn);
        }
        catch (TranslationPackException exception)
            when (string.Equals(TranslationPackFailure.GetDiagnosticId(exception), TranslationPackFailure.DiagnosticId, StringComparison.Ordinal)
                && TranslationPackFailure.GetReason(exception) == TranslationPackFailureReason.ContractFingerprintMismatch)
        {
            rejectedAsFingerprintMismatch = true;
        }

        AssertTrue(rejectedAsFingerprintMismatch, "pass 3 tampered pack C was not rejected as RTR0023/contract-fingerprint-mismatch.");
        AssertTrue(
            ReferenceEquals(snapshotBeforeAttempt, manager.Current),
            "pass 3 the rejected attempt mutated the current snapshot.");
        AssertTrue(
            string.Equals(manager.CurrentLocale, LocaleDe, StringComparison.Ordinal),
            $"pass 3 expected the locale to remain '{LocaleDe}' after the rejected attempt.");
        AssertTrue(
            transitions.Count == 3 && transitions[2] == "en->de",
            $"pass 3 expected no LocaleChanged from the rejected attempt but observed [{string.Join(", ", transitions)}].");

        StageFile(packBPath, activePath);
        provider.Rotate(new FilePackSource(LocaleEn, activePath));
        await manager.SetLocaleAsync(LocaleEn);
        AssertTrue(
            string.Equals(text.Greeting(ArgumentName), ExpectedPackBText, StringComparison.Ordinal),
            $"pass 3 expected recovered pack B greeting '{ExpectedPackBText}' after returning to en.");
        AssertTrue(
            transitions.Count == 4 && transitions[3] == "de->en",
            $"pass 3 expected a single recovery transition but observed [{string.Join(", ", transitions)}].");
        Console.WriteLine("PASS 3: tampered pack C rejected as RTR0023/contract-fingerprint-mismatch, snapshot preserved, recovery serves pack B again");

        Console.WriteLine("HOT-SWAP CANARY PASS");
        return 0;
    }

    private static void StageFile(string payloadPath, string activePath)
    {
        string stagingPath = activePath + ".staging";
        File.Copy(payloadPath, stagingPath, overwrite: true);
        File.Replace(stagingPath, activePath, destinationBackupFileName: null);
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Canary assertion failed: {message}");
        }
    }
}
