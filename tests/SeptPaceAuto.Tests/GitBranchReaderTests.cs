#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SeptPaceAuto.Services;
using Xunit;

namespace SeptPaceAuto.Tests;

/// <summary>
/// Qualification d'un relevé de branche. Tout l'enjeu tient ici : un git lent ou fâché n'est
/// pas un dossier sans dépôt, et une HEAD détachée est une lecture réussie.
/// </summary>
public sealed class GitBranchReaderTests
{
    private const string Repository = @"C:\depot";

    [Fact]
    public async Task BrancheAttachee_EstLueSansSecondAppel()
    {
        var calls = new List<string>();
        var reader = Reader(calls, Answered("feature/34131-collecte\n"));

        var read = await reader.ReadAsync(Repository, CancellationToken.None);

        Assert.Equal(BranchStatus.Ok, read.Status);
        Assert.Equal("feature/34131-collecte", read.Name);
        Assert.Equal(new[] { "rev-parse" }, calls);
    }

    [Fact]
    public async Task DelaiDepasse_EstUneLectureRateeEtNonUnDepotAbsent()
    {
        var reader = Reader(new List<string>(), TimedOut(), TimedOut());

        var read = await reader.ReadAsync(Repository, CancellationToken.None);

        Assert.Equal(BranchStatus.Unreadable, read.Status);
        Assert.Equal("git a dépassé le délai", read.Detail);
    }

    [Fact]
    public async Task GitQuiNeSeLancePas_EstUneLectureRatee()
    {
        var reader = Reader(new List<string>(), NotStarted(), NotStarted());

        var read = await reader.ReadAsync(Repository, CancellationToken.None);

        Assert.Equal(BranchStatus.Unreadable, read.Status);
    }

    [Fact]
    public async Task DossierIndisponible_EstUneLectureRatee()
    {
        var reader = new GitBranchReader((arguments, ct) => throw new InvalidOperationException("git ne doit pas être appelé"), _ => false);

        var read = await reader.ReadAsync(Repository, CancellationToken.None);

        Assert.Equal(BranchStatus.Unreadable, read.Status);
    }

    [Theory]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git")]
    [InlineData("fatal : ce n’est pas un dépôt git (ni aucun des répertoires parents) : .git")]
    public async Task DossierSansDepot_EstUnDepotAbsent(string message)
    {
        var calls = new List<string>();
        var reader = Reader(calls, Refused(128, message));

        var read = await reader.ReadAsync(Repository, CancellationToken.None);

        Assert.Equal(BranchStatus.Missing, read.Status);
        Assert.Equal(new[] { "rev-parse" }, calls);
    }

    [Fact]
    public async Task HeadDetachee_EstUneLectureReussie()
    {
        var reader = Reader(new List<string>(), Answered("HEAD\n"), Refused(1, string.Empty));

        var read = await reader.ReadAsync(Repository, CancellationToken.None);

        Assert.Equal(BranchStatus.Ok, read.Status);
        Assert.Equal(GitBranchReader.Detached, read.Name);
    }

    [Fact]
    public async Task DepotSansCommit_EstLuParSymbolicRef()
    {
        var reader = Reader(
            new List<string>(),
            Refused(128, "fatal: ambiguous argument 'HEAD': unknown revision or path not in the working tree."),
            Answered("main\n"));

        var read = await reader.ReadAsync(Repository, CancellationToken.None);

        Assert.Equal(BranchStatus.Ok, read.Status);
        Assert.Equal("main", read.Name);
    }

    [Fact]
    public async Task HeadIllisibleApresLesDeuxCommandes_EstUneLectureRatee()
    {
        var reader = Reader(new List<string>(), Refused(128, "fatal: unable to read the index"), Refused(128, "fatal: unable to read the index"));

        var read = await reader.ReadAsync(Repository, CancellationToken.None);

        Assert.Equal(BranchStatus.Unreadable, read.Status);
    }

    [Fact]
    public async Task CheminVideOuGitAbsent_EstUnDepotAbsent()
    {
        var configured = Reader(new List<string>(), Answered("main"));
        var withoutGit = new GitBranchReader(null, _ => true);

        Assert.Equal(BranchStatus.Missing, (await configured.ReadAsync(string.Empty, CancellationToken.None)).Status);
        Assert.Equal(BranchStatus.Missing, (await withoutGit.ReadAsync(Repository, CancellationToken.None)).Status);
    }

    private static GitBranchReader Reader(List<string> calls, params ProcessResult[] answers)
    {
        var index = 0;
        return new GitBranchReader(
            (arguments, ct) =>
            {
                calls.Add(arguments.First(argument => argument is "rev-parse" or "symbolic-ref"));
                var answer = answers[Math.Min(index, answers.Length - 1)];
                index++;
                return Task.FromResult(answer);
            },
            _ => true);
    }

    private static ProcessResult Answered(string output) => new(0, output, string.Empty, false, true);

    private static ProcessResult Refused(int code, string error) => new(code, string.Empty, error, false, true);

    private static ProcessResult TimedOut() => new(-1, string.Empty, string.Empty, true, true);

    private static ProcessResult NotStarted() => new(-1, string.Empty, string.Empty, false, false);
}
