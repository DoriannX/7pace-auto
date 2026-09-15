#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

internal readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Started)
{
    public bool Ok => Started && !TimedOut && ExitCode == 0;
}

/// <summary>
/// Appels d'outils externes (git, az). Un outil absent, lent ou en échec ne doit jamais
/// faire tomber l'application : tout revient sous forme de <see cref="ProcessResult"/>.
/// </summary>
internal static class ProcessRunner
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Lazy<string?> GitPath = new(static () => Locate(
        "git",
        @"C:\Program Files\Git\cmd\git.exe",
        @"C:\Program Files (x86)\Git\cmd\git.exe"));

    private static readonly Lazy<string?> AzPath = new(static () => Locate(
        "az",
        @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd",
        @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"));

    private static readonly Lazy<string?> CurlPath = new(static () => Locate(
        "curl",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe")));

    /// <summary>git de ce poste, cherché une seule fois ; null quand il n'est pas installé.</summary>
    public static string? Git => GitPath.Value;

    /// <summary>Azure CLI de ce poste, cherchée une seule fois ; null quand elle est absente.</summary>
    public static string? Az => AzPath.Value;

    /// <summary>curl.exe livré avec Windows, cherché une seule fois ; null sur un poste qui n'en a pas.</summary>
    public static string? Curl => CurlPath.Value;

    public static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) return new ProcessResult(-1, string.Empty, string.Empty, false, false);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new ProcessResult(-1, string.Empty, error.Message, false, false);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        // Les deux flux sont lus en octets pendant l'exécution : en tampon on bloquerait le
        // processus fils, et git écrit en UTF-8 là où az écrit dans la page de codes du poste.
        var stdout = Drain(process.StandardOutput.BaseStream, deadline.Token);
        var stderr = Drain(process.StandardError.BaseStream, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, Decode(await stdout.ConfigureAwait(false)), Decode(await stderr.ConfigureAwait(false)), false, true);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true, Started: true);
        }
    }

    private static async Task<byte[]> Drain(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        try
        {
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            // Processus tué ou tuyau fermé : on garde ce qui a été lu.
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// UTF-8 quand c'est possible, sinon page de codes Windows 1252 : az écrit ses accents
    /// en ANSI, et un titre de Fix abîmé serait recopié tel quel dans la journée.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            const string high = "\u20AC\u0081\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\u008D\u017D\u008F"
                + "\u0090\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\u009D\u017E\u0178";
            var chars = new char[bytes.Length];
            for (var index = 0; index < bytes.Length; index++)
            {
                var value = bytes[index];
                chars[index] = value is >= 0x80 and <= 0x9F ? high[value - 0x80] : (char)value;
            }
            return new string(chars);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Le processus est déjà parti : rien à faire.
        }
    }

    /// <summary>
    /// Trouve un exécutable sans passer par un shell : PATH + PATHEXT, puis emplacements connus.
    /// </summary>
    public static string? Locate(string name, params string[] wellKnown)
    {
        foreach (var candidate in wellKnown)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var folder in folders)
        {
            string trimmed;
            try
            {
                trimmed = folder.Trim('"');
                if (trimmed.Length == 0) continue;
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                string full;
                try
                {
                    full = Path.Combine(trimmed, name + extension);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }
}
