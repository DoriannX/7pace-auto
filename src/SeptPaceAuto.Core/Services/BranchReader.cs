#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>
/// Issue d'un relevé de branche. « Illisible » n'est jamais « absent » : la première
/// situation est passagère et le dernier état sûr reste valable, la seconde demande un
/// réglage et interrompt réellement la collecte.
/// </summary>
internal enum BranchStatus
{
    /// <summary>Branche relevée avec certitude.</summary>
    Ok,

    /// <summary>Aucun dépôt exploitable : rien n'est réglé, git manque, ou le dossier n'est pas un dépôt.</summary>
    Missing,

    /// <summary>Lecture momentanément impossible : délai dépassé, dossier indisponible, git en échec.</summary>
    Unreadable,
}

/// <param name="Detail">Cause courte de l'échec, affichée telle quelle dans l'état du suivi.</param>
internal readonly record struct BranchRead(BranchStatus Status, string? Name, string Detail)
{
    public static BranchRead Found(string name) => new(BranchStatus.Ok, name, string.Empty);

    public static BranchRead Absent(string detail) => new(BranchStatus.Missing, null, detail);

    public static BranchRead Failed(string detail) => new(BranchStatus.Unreadable, null, detail);
}

internal interface IBranchReader
{
    /// <summary>
    /// Faux quand aucun relevé n'est possible sur ce poste, git étant introuvable. Une
    /// panne passagère ne se lit pas ici : elle se voit au statut du relevé.
    /// </summary>
    bool Available { get; }

    Task<BranchRead> ReadAsync(string repository, CancellationToken ct);
}

/// <summary>
/// Relevé de la branche active par git. Chaque échec est qualifié : un dépôt introuvable
/// se règle dans les réglages, alors qu'un délai dépassé ou un dossier momentanément
/// indisponible se retente au relevé suivant sans rien changer à la journée.
/// </summary>
internal sealed class GitBranchReader : IBranchReader
{
    /// <summary>Nom porté par un créneau relevé sur une HEAD détachée : le temps est réel, l'attribution reste à faire.</summary>
    internal const string Detached = "HEAD détachée";

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Messages de git, français comme anglais, qui désignent un dossier sans dépôt.</summary>
    private static readonly string[] NotARepositoryMarkers =
    {
        "not a git repository",
        "dépôt git",
        "depot git",
    };

    internal delegate Task<ProcessResult> Runner(IReadOnlyList<string> arguments, CancellationToken ct);

    private readonly Runner? _run;
    private readonly Func<string, bool> _exists;

    public GitBranchReader() : this(Through(ProcessRunner.Git), SafeExists) { }

    internal GitBranchReader(Runner? run, Func<string, bool> exists)
    {
        _run = run;
        _exists = exists;
    }

    public bool Available => _run is not null;

    public async Task<BranchRead> ReadAsync(string repository, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repository)) return BranchRead.Absent("aucun dépôt réglé");
        if (_run is null) return BranchRead.Absent("git est introuvable sur ce poste");

        // Un dossier momentanément absent — lecteur réseau, VPN, sauvegarde en cours — n'est
        // pas un dépôt supprimé : le relevé suivant tranchera.
        if (!_exists(repository)) return BranchRead.Failed("dossier du dépôt indisponible");

        var head = await _run(new[] { "-C", repository, "rev-parse", "--abbrev-ref", "HEAD" }, ct).ConfigureAwait(false);

        // Cas courant : une branche attachée répond du premier coup, sans second appel.
        if (Name(head) is string attached && !string.Equals(attached, "HEAD", StringComparison.Ordinal))
        {
            return BranchRead.Found(attached);
        }
        if (NotARepository(head)) return BranchRead.Absent("le dossier n’est pas un dépôt Git");

        var symbolic = await _run(new[] { "-C", repository, "symbolic-ref", "--quiet", "--short", "HEAD" }, ct).ConfigureAwait(false);
        return Decide(head, symbolic);
    }

    /// <summary>
    /// Conclusion une fois les deux commandes jouées. Sans état : c'est ici que se décide
    /// la différence entre une HEAD détachée, un dossier sans dépôt et une lecture ratée.
    /// </summary>
    internal static BranchRead Decide(ProcessResult head, ProcessResult symbolic)
    {
        // Dépôt sans premier commit : rev-parse échoue, symbolic-ref donne la branche à venir.
        if (Name(symbolic) is string branch) return BranchRead.Found(branch);
        if (NotARepository(symbolic) || NotARepository(head)) return BranchRead.Absent("le dossier n’est pas un dépôt Git");

        // rev-parse a répondu « HEAD » et symbolic-ref a répondu proprement qu'aucune branche
        // n'est attachée : la HEAD est détachée, et c'est une lecture réussie.
        if (string.Equals(Name(head), "HEAD", StringComparison.Ordinal) && Answered(symbolic))
        {
            return BranchRead.Found(Detached);
        }

        return BranchRead.Failed(Detail(head, symbolic));
    }

    private static string? Name(ProcessResult result)
    {
        if (!result.Ok) return null;
        var text = result.StdOut.Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>La commande est allée au bout, même en refusant : sa réponse fait foi.</summary>
    private static bool Answered(ProcessResult result) => result.Started && !result.TimedOut;

    private static bool NotARepository(ProcessResult result)
    {
        if (!Answered(result) || result.ExitCode == 0) return false;
        foreach (var marker in NotARepositoryMarkers)
        {
            if (result.StdErr.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Cause courte de l'échec, de quoi comprendre sans relancer la commande.</summary>
    private static string Detail(ProcessResult head, ProcessResult symbolic)
    {
        if (head.TimedOut || symbolic.TimedOut) return "git a dépassé le délai";
        if (!head.Started || !symbolic.Started) return "git n’a pas pu être lancé";
        var error = symbolic.StdErr.Trim().Length > 0 ? symbolic.StdErr : head.StdErr;
        error = error.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (error.Length > 120) error = error[..120];
        return error.Length > 0 ? error : $"git a répondu {head.ExitCode}";
    }

    private static Runner? Through(string? git) =>
        git is null ? null : (arguments, ct) => ProcessRunner.RunAsync(git, arguments, GitTimeout, ct);

    private static bool SafeExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
