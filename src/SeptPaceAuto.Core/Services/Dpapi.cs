#nullable enable
using System;
using System.Runtime.InteropServices;

namespace SeptPaceAuto.Services;

/// <summary>
/// Protection DPAPI de portée utilisateur par appel direct à crypt32. Aucune dépendance
/// NuGet supplémentaire n'est permise, et System.Security.Cryptography.ProtectedData n'est
/// pas dans la bibliothèque standard de .NET 8 : ces deux appels y suppléent exactement.
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CryptUnprotectData")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CryptProtectData")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob output);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>Rend les octets en clair, ou null si le blob n'appartient pas à cet utilisateur.</summary>
    public static byte[]? Unprotect(byte[] protectedBytes)
    {
        if (protectedBytes is null || protectedBytes.Length == 0) return null;

        var input = new DataBlob();
        var output = new DataBlob();
        try
        {
            input.cbData = protectedBytes.Length;
            input.pbData = Marshal.AllocHGlobal(protectedBytes.Length);
            Marshal.Copy(protectedBytes, 0, input.pbData, protectedBytes.Length);

            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
            {
                return null;
            }

            var clear = new byte[output.cbData];
            Marshal.Copy(output.pbData, clear, 0, output.cbData);
            return clear;
        }
        catch (Exception error) when (error is OutOfMemoryException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    /// <summary>
    /// Rend le blob protégé pour cet utilisateur Windows, ou null si crypt32 refuse :
    /// dans ce cas rien n'est écrit plutôt qu'un jeton en clair.
    /// </summary>
    public static byte[]? Protect(byte[] clearBytes)
    {
        if (clearBytes is null || clearBytes.Length == 0) return null;

        var input = new DataBlob();
        var output = new DataBlob();
        try
        {
            input.cbData = clearBytes.Length;
            input.pbData = Marshal.AllocHGlobal(clearBytes.Length);
            Marshal.Copy(clearBytes, 0, input.pbData, clearBytes.Length);

            if (!CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
            {
                return null;
            }

            var blob = new byte[output.cbData];
            Marshal.Copy(output.pbData, blob, 0, output.cbData);
            return blob;
        }
        catch (Exception error) when (error is OutOfMemoryException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero)
            {
                // Le clair ne doit pas rester en mémoire non gérée après l'appel.
                for (var index = 0; index < clearBytes.Length; index++) Marshal.WriteByte(input.pbData, index, 0);
                Marshal.FreeHGlobal(input.pbData);
            }
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }
}
