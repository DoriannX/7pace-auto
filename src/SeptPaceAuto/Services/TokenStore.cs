#nullable enable
using System;
using System.IO;
using System.Text;

namespace SeptPaceAuto.Services;

/// <summary>
/// Jeton 7pace au repos. Il est protégé par DPAPI (portée utilisateur) : illisible pour un
/// autre compte Windows, et jamais écrit en clair. L'ancien emplacement, alimenté par un
/// script PowerShell externe, reste lu tant qu'il existe.
/// </summary>
internal static class TokenStore
{
    /// <summary>Le jeton en clair, ou null si aucun n'est lisible par cet utilisateur.</summary>
    public static string? Read() => ReadCurrent() ?? ReadLegacy();

    /// <summary>
    /// Remplace le jeton. Une chaîne vide supprime le fichier : l'application se retrouve
    /// alors sans jeton, et le dit, plutôt que de garder une valeur périmée.
    /// </summary>
    public static void Save(string? token)
    {
        var clear = (token ?? string.Empty).Trim();
        if (clear.Length == 0)
        {
            Delete();
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(clear);
        byte[]? blob;
        try
        {
            blob = Dpapi.Protect(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
        if (blob is null)
        {
            throw new DomainException("Windows a refusé de protéger le jeton : il n’a pas été enregistré.");
        }

        try
        {
            AppPaths.EnsureRoot();
            AppPaths.WriteAtomic(AppPaths.SevenPaceToken, blob);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DomainException("Impossible d’écrire le jeton : vérifie l’accès à %LOCALAPPDATA%\\7pace-auto.");
        }

        // L'ancien fichier ferait autorité au prochain démarrage s'il restait là.
        DeleteLegacy();
    }

    public static void Delete()
    {
        DeleteFile(AppPaths.SevenPaceToken);
        DeleteLegacy();
    }

    private static void DeleteLegacy() => DeleteFile(AppPaths.LegacySevenPaceToken);

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DomainException("Impossible de supprimer le jeton enregistré : un autre programme le garde ouvert.");
        }
    }

    /// <summary>Emplacement actuel : blob DPAPI brut dont le clair est de l'UTF-8.</summary>
    private static string? ReadCurrent()
    {
        try
        {
            if (!File.Exists(AppPaths.SevenPaceToken)) return null;
            var blob = File.ReadAllBytes(AppPaths.SevenPaceToken);
            return blob.Length == 0 ? null : Clear(Dpapi.Unprotect(blob), Encoding.UTF8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ancien emplacement, écrit par ConvertFrom-SecureString : hexadécimal d'un blob DPAPI
    /// utilisateur dont le clair est de l'UTF-16LE.
    /// </summary>
    private static string? ReadLegacy()
    {
        try
        {
            if (!File.Exists(AppPaths.LegacySevenPaceToken)) return null;
            var hex = File.ReadAllText(AppPaths.LegacySevenPaceToken, Encoding.ASCII).Trim();
            if (hex.Length == 0 || hex.Length % 2 != 0) return null;

            byte[] blob;
            try
            {
                blob = Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                return null;
            }
            return Clear(Dpapi.Unprotect(blob), Encoding.Unicode);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Clear(byte[]? bytes, Encoding encoding)
    {
        if (bytes is null || bytes.Length == 0) return null;
        var token = encoding.GetString(bytes).Trim('\0', ' ', '\r', '\n', '\t');
        Array.Clear(bytes);
        return token.Length == 0 ? null : token;
    }
}
