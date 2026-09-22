using SeptPaceAuto.Services;
using Xunit;

namespace SeptPaceAuto.Tests;

/// <summary>
/// Le protocole du tuyau tient sur une ligne JSON par message. Il doit refuser franchement
/// ce qu'il ne comprend pas : une version inconnue ou un message abîmé ne doit jamais être
/// interprété au jugé, sans quoi une journée pourrait être écrite de travers.
/// </summary>
public sealed class AgentWireTests
{
    [Fact]
    public void Un_appel_fait_l_aller_retour_sans_rien_perdre()
    {
        var line = AgentWire.Encode(new AgentCall(42, "saveEntry", "{\"date\":\"2026-03-17\"}"));

        Assert.DoesNotContain('\n', line);
        Assert.True(AgentWire.TryReadCall(line, out var call, out _));
        Assert.Equal(42, call.Id);
        Assert.Equal("saveEntry", call.Method);
        Assert.Contains("2026-03-17", call.Parameters);
    }

    [Fact]
    public void Des_parametres_absents_valent_un_objet_vide()
    {
        Assert.True(AgentWire.TryReadCall(AgentWire.Encode(new AgentCall(1, "bootstrap", "")), out var call, out _));
        Assert.Equal("{}", call.Parameters);
    }

    [Fact]
    public void Un_resultat_indente_reste_sur_une_seule_ligne()
    {
        var line = AgentWire.Encode(new AgentReply(7, true, "{\n  \"pending\": 2\n}", string.Empty));

        Assert.DoesNotContain('\n', line);
        Assert.True(AgentWire.TryReadReply(line, out var reply, out _));
        Assert.True(reply.Ok);
        Assert.Contains("\"pending\":2", reply.Payload);
    }

    [Fact]
    public void Une_erreur_metier_garde_sa_phrase_et_sa_nature()
    {
        var line = AgentWire.Encode(new AgentReply(3, false, "La journée en cours est encore en collecte.", AgentWire.KindDomain));

        Assert.True(AgentWire.TryReadReply(line, out var reply, out _));
        Assert.False(reply.Ok);
        Assert.Equal(AgentWire.KindDomain, reply.Kind);
        Assert.Equal("La journée en cours est encore en collecte.", reply.Payload);
    }

    [Theory]
    [InlineData("{ceci n’est pas du JSON")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void Un_message_abime_est_refuse(string line)
    {
        Assert.False(AgentWire.TryReadCall(line, out _, out var problem));
        Assert.NotEmpty(problem);
    }

    [Fact]
    public void Une_autre_version_de_protocole_est_refusee_avec_sa_raison()
    {
        var line = "{\"v\":99,\"id\":1,\"method\":\"bootstrap\",\"params\":{}}";

        Assert.False(AgentWire.TryReadCall(line, out _, out var problem));
        Assert.Contains("Protocole incompatible", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Un_appel_sans_methode_est_refuse()
    {
        var line = "{\"v\":" + AgentEndpoint.Protocol + ",\"id\":1,\"params\":{}}";

        Assert.False(AgentWire.TryReadCall(line, out _, out var problem));
        Assert.Contains("méthode", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Deux_profils_de_donnees_ne_partagent_ni_tuyau_ni_verrou()
    {
        var left = AgentEndpoint.For(Path.Combine(Path.GetTempPath(), "7pace-profil-a"));
        var right = AgentEndpoint.For(Path.Combine(Path.GetTempPath(), "7pace-profil-b"));

        Assert.NotEqual(left.PipeName, right.PipeName);
        Assert.NotEqual(left.MutexName, right.MutexName);
        Assert.NotEqual(left.LegacyMutexName, right.LegacyMutexName);
        Assert.NotEqual(left.InfoPath, right.InfoPath);
    }

    [Fact]
    public void La_cle_d_un_profil_ignore_la_casse_comme_Windows()
    {
        Assert.Equal(AgentEndpoint.KeyOf(@"C:\Temp\7pace"), AgentEndpoint.KeyOf(@"c:\temp\7PACE"));
    }

    [Theory]
    // Rien à annoncer, ou déjà annoncé : le collecteur se tait.
    [InlineData(0, 600, 510, null, false)]
    [InlineData(2, 600, 510, "2026-03-17", false)]
    // Minuit vient de passer : la journée d'hier attend, mais ce n'est pas encore le matin.
    [InlineData(2, 5, 510, null, false)]
    [InlineData(1, 510, 510, null, true)]
    [InlineData(3, 1020, 510, "2026-03-16", true)]
    public void La_notification_du_matin_part_une_fois_par_journee(
        int pending, int minute, int workStart, string? announced, bool expected)
    {
        Assert.Equal(expected, MorningAnnouncer.DueNow("2026-03-17", minute, workStart, pending, announced));
    }
}
