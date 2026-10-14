using System.Text.RegularExpressions;

namespace HelpDesk.API.Services;

/// <summary>Signature normalisée d'un e-mail, servant à le rapprocher d'un ticket (anti-doublon).</summary>
public record SignatureEmail(string? MessageId, string? ThreadId, string? Expediteur, string? Objet, DateTime? Date)
{
    /// <summary>Au moins une clé exploitable pour tenter un rapprochement.</summary>
    public bool EstExploitable =>
        MessageId != null || ThreadId != null ||
        (Expediteur != null && Objet != null && Date.HasValue);
}

/// <summary>
/// Rapprochement e-mail ↔ ticket pour éviter les doublons quand plusieurs membres de l'équipe
/// reçoivent le même e-mail. Trois niveaux : Message-ID exact, même fil de discussion,
/// puis heuristique (expéditeur + objet + date proche) pour les e-mails envoyés séparément.
/// </summary>
public static class LienEmailTicket
{
    /// <summary>Tolérance de date pour l'heuristique (copies d'un même envoi, tolérance relais/traitement).</summary>
    public static readonly TimeSpan FenetreDate = TimeSpan.FromMinutes(15);

    /// <summary>Longueur minimale de l'objet normalisé pour tenter l'heuristique (évite « re », « info »…).</summary>
    public const int LongueurObjetMin = 4;

    private static readonly Regex PrefixeObjet =
        new(@"^\s*(re|ré|tr|fw|fwd|ref)\s*(\[\d+\])?\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Message-ID RFC normalisé (sans chevrons, minuscules) pour une comparaison fiable.</summary>
    public static string? NettoyerId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : id.Trim().Trim('<', '>').Trim().ToLowerInvariant();

    /// <summary>Objet normalisé : sans préfixes RE:/TR:/FW:…, espaces compactés, minuscules.</summary>
    public static string? NormaliserObjet(string? objet)
    {
        if (string.IsNullOrWhiteSpace(objet)) return null;
        var s = objet.Trim();
        Match m;
        while ((m = PrefixeObjet.Match(s)).Success) s = s[m.Length..];
        s = Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
        return s.Length == 0 ? null : s;
    }

    /// <summary>Construit la signature normalisée d'un e-mail (le fil retombe sur le message si absent).</summary>
    public static SignatureEmail Signature(string? messageId, string? threadId, string? expediteur, string? objet, DateTime? date) =>
        new(NettoyerId(messageId),
            NettoyerId(threadId) ?? NettoyerId(messageId),
            string.IsNullOrWhiteSpace(expediteur) ? null : expediteur.Trim().ToLowerInvariant(),
            NormaliserObjet(objet),
            date);

    /// <summary>Un e-mail (signature) correspond-il à un ticket (ses champs e-mail déjà stockés) ?</summary>
    public static bool Correspond(SignatureEmail e,
        string? tMessageId, string? tThreadId, string? tExpediteur, string? tObjet, DateTime? tDate)
    {
        // Message-ID exact : même e-mail reçu par plusieurs destinataires.
        if (e.MessageId != null && e.MessageId == tMessageId) return true;

        // Même fil de discussion : réponse ou transfert (identifiant RFC, commun à IMAP et Graph).
        if (e.ThreadId != null && tThreadId != null && e.ThreadId == tThreadId) return true;

        // Heuristique : même expéditeur + même objet (non trivial).
        if (e.Expediteur != null && e.Objet != null && e.Objet.Length >= LongueurObjetMin &&
            e.Expediteur == tExpediteur && e.Objet == tObjet)
        {
            // Date du ticket inconnue (ancien ticket rétro-rempli) → l'objet + l'expéditeur suffisent.
            if (!tDate.HasValue || !e.Date.HasValue) return true;
            // Sinon on exige des dates proches (copies d'un même envoi).
            if ((e.Date.Value - tDate.Value).Duration() <= FenetreDate) return true;
        }
        return false;
    }
}
