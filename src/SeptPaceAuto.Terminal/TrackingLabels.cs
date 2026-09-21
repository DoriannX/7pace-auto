namespace SeptPaceAuto.Terminal;

/// <summary>
/// Traduction des états du suivi rendus par le cœur. Un seul endroit : le menu et la
/// consultation de la journée en cours doivent nommer une panne de la même façon.
/// </summary>
internal static class TrackingLabels
{
    public static string For(string state) => state switch
    {
        "running" => "en cours",
        "outside-hours" => "hors horaires",
        "git-unreadable" => "lecture Git en échec",
        "no-repo" => "dépôt introuvable",
        _ => state,
    };
}
