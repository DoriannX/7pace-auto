#nullable enable
using System;
using System.IO;
using System.Text;

namespace SeptPaceAuto.Services;

/// <summary>Le lien ICS donne accès à l'agenda publié : il reste chiffré au repos.</summary>
internal static class CalendarLinkStore
{
    public static bool Configured => Read() is not null;

    public static string? Read()
    {
        try
        {
            if (!File.Exists(AppPaths.CalendarLink)) return null;
            var blob = File.ReadAllBytes(AppPaths.CalendarLink);
            var bytes = Dpapi.Unprotect(blob);
            if (bytes is null) return null;
            try { return Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string? link)
    {
        var value = (link ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            try { if (File.Exists(AppPaths.CalendarLink)) File.Delete(AppPaths.CalendarLink); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new DomainException("Impossible de supprimer le lien calendrier enregistré."); }
            return;
        }
        if (!ValidUrl(value)) throw new DomainException("Colle le lien ICS publié par Outlook.");
        var bytes = Encoding.UTF8.GetBytes(value);
        byte[]? protectedBytes;
        try { protectedBytes = Dpapi.Protect(bytes); }
        finally { Array.Clear(bytes); }
        if (protectedBytes is null) throw new DomainException("Windows a refusé de protéger le lien calendrier.");
        try
        {
            AppPaths.EnsureRoot();
            AppPaths.WriteAtomic(AppPaths.CalendarLink, protectedBytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new DomainException("Impossible d’enregistrer le lien calendrier."); }
    }

    public static bool ValidUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("outlook.office365.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("outlook.office.com", StringComparison.OrdinalIgnoreCase))
        && uri.AbsolutePath.EndsWith(".ics", StringComparison.OrdinalIgnoreCase);
}
