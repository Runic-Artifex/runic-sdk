using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Runic.Desktop;
using Runic.Platform;
using Runic.Translations;
using TUnit.Core;

namespace Runic.Application.Views.Translations.Tests;

// The runic-views catalog: English matches the English Runic shows without it, German covers every text.
public sealed class CatalogTests
{
    [Test]
    public void TheCatalogShipsEnglishAndGerman()
    {
        Require(RunicViewsTranslations.CatalogId == RunicViewsTextCatalog.CatalogId
            && ReferenceEquals(RunicViewsTranslations.CatalogRegistration, RunicViewsTextCatalog.Registration), "The public catalog members differ.");
        // The generated classes are internal: their shape is the Translations generator's, not Runic's public API.
        Require(!typeof(RunicViewsText).IsPublic && !typeof(RunicViewsTextCatalog).IsPublic && !typeof(RunicViewsTextMessages).IsPublic,
            "A generated catalog class is public.");
        Require(RunicViewsTextCatalog.CatalogId == "runic-views" && RunicViewsTextCatalog.DefaultLocale == "en",
            $"Catalog {RunicViewsTextCatalog.CatalogId}, default {RunicViewsTextCatalog.DefaultLocale}.");
        Require(RunicViewsTextCatalog.SupportedLocales.Order().SequenceEqual(["de", "en"]),
            $"Locales: {string.Join(", ", RunicViewsTextCatalog.SupportedLocales)}.");
    }

    // The English texts are the defaults of DesktopPlatformTexts, with the key names Runic writes in English.
    [Test]
    public void EnglishPlatformTextsAreRunicsDefaults()
    {
        var english = RunicViewsTranslations.PlatformTexts(Context("en"));
        var expected = new DesktopPlatformTexts { ControlKeyName = "Ctrl", ShiftKeyName = "Shift", AltKeyName = "Alt" };
        Require(english == expected, $"English platform texts differ from Runic's defaults: {english}");
    }

    // A text DesktopPlatformTexts gains must get a message: every German text differs from the English one, except Alt.
    [Test]
    public void GermanCoversEveryPlatformText()
    {
        var english = RunicViewsTranslations.PlatformTexts(Context("en"));
        var german = RunicViewsTranslations.PlatformTexts(Context("de"));
        foreach (var property in typeof(DesktopPlatformTexts).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string)))
        {
            var (en, de) = ((string?)property.GetValue(english), (string?)property.GetValue(german));
            Require(de is not null && (de != en || property.Name is nameof(DesktopPlatformTexts.AltKeyName)),
                $"DesktopPlatformTexts.{property.Name} has no German text: {de}");
        }
        Require(german.UntitledFileName == "Unbenannt" && german.ControlKeyName == "Strg" && german.ShiftKeyName == "Umschalt"
            && german.LoadingTitle == "Wird geladen…",
            $"German platform texts: {german}");
        // DesktopPlatformTexts accepts them.
        DesktopPlatformTexts.Current = german;
        DesktopPlatformTexts.Current = null!;
    }

    // The README's mapping to DesktopStandardMenuTitles: English gives Runic's defaults, German retitles every menu.
    [Test]
    public void TheMenuTitlesCoverEveryStandardMenu()
    {
        var english = StandardMenuTitles(RunicViewsTranslations.MenuTitles(Context("en")));
        Require(english == new DesktopStandardMenuTitles(), $"English menu titles differ from Runic's defaults: {english}");
        var german = StandardMenuTitles(RunicViewsTranslations.MenuTitles(Context("de")));
        foreach (var property in typeof(DesktopStandardMenuTitles).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Require(!Equals(property.GetValue(german), property.GetValue(english)), $"DesktopStandardMenuTitles.{property.Name} has no German title.");
        Require(german.Quit == "{0} beenden" && german.About == "Über {0}" && german.Edit == "Bearbeiten", $"German menu titles: {german}");
        // RunicViewsMenuTitles names every DesktopStandardMenuTitles member, and nothing else.
        var names = typeof(RunicViewsMenuTitles).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).Order();
        Require(names.SequenceEqual(typeof(DesktopStandardMenuTitles).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).Order()),
            $"RunicViewsMenuTitles: {string.Join(", ", names)}");
    }

    // As in the README: the members have the same names; {0} stays for the application's name, which Runic inserts.
    internal static DesktopStandardMenuTitles StandardMenuTitles(RunicViewsMenuTitles titles) => new()
    {
        About = titles.About, Hide = titles.Hide, HideOthers = titles.HideOthers, ShowAll = titles.ShowAll, Quit = titles.Quit,
        File = titles.File, Close = titles.Close, OpenRecent = titles.OpenRecent, ClearMenu = titles.ClearMenu, Edit = titles.Edit,
        Undo = titles.Undo, Redo = titles.Redo, Cut = titles.Cut, Copy = titles.Copy, Paste = titles.Paste, SelectAll = titles.SelectAll,
        View = titles.View, ActualSize = titles.ActualSize, ZoomIn = titles.ZoomIn, ZoomOut = titles.ZoomOut, Window = titles.Window,
        Minimize = titles.Minimize, Zoom = titles.Zoom, BringAllToFront = titles.BringAllToFront,
    };

    [Test]
    public async Task PlatformTextsFollowTheContextAsync()
    {
        var context = Context("en");
        try
        {
            var following = RunicViewsTranslations.FollowPlatformTexts(context);
            Require(DesktopPlatformTexts.Current.UntitledFileName == "Untitled", "Following did not apply the current texts.");
            await context.SetLocaleAsync("de");
            Require(DesktopPlatformTexts.Current.UntitledFileName == "Unbenannt" && DesktopPlatformTexts.Current.ControlKeyName == "Strg",
                $"A switch to German did not reach the platform texts: {DesktopPlatformTexts.Current}");
            await context.SetLocaleAsync("de-AT");
            Require(DesktopPlatformTexts.Current.SaveFileTitle == "Datei speichern", "de-AT did not fall back to German.");
            following.Dispose();
            await context.SetLocaleAsync("en");
            Require(DesktopPlatformTexts.Current.UntitledFileName == "Unbenannt", "A disposed follower still set the texts.");
        }
        finally { DesktopPlatformTexts.Current = null!; }
    }

    [Test]
    public void NoContextIsRefused()
    {
        RequireThrows<ArgumentNullException>(() => RunicViewsTranslations.PlatformTexts(null!));
        RequireThrows<ArgumentNullException>(() => RunicViewsTranslations.FollowPlatformTexts(null!));
        RequireThrows<ArgumentNullException>(() => RunicViewsTranslations.MenuTitles(null!));
    }

    internal static TranslationContext Context(string locale) =>
        TranslationContext.CreateBuilder().Add(RunicViewsTranslations.CatalogRegistration).Build(locale);

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void RequireThrows<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
