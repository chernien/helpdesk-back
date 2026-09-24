namespace HelpDesk.API.Constants;

/// <summary>
/// Etats possibles d'un ticket (colonne TICKET_LIST.Etat).
/// L'ancienne application stockait des codes numeriques ("1", "2", "3") :
/// on ecrit desormais des libelles lisibles, et on traduit les anciennes
/// valeurs a la lecture (voir Normaliser) pour ne pas perdre l'historique.
/// </summary>
public static class TicketEtat
{
    public const string Ouvert = "Ouvert";
    public const string EnCours = "En cours";
    public const string EnDeveloppement = "En développement";
    /// <summary>Le developpement est fini : un collaborateur du produit doit tester en interne.</summary>
    public const string TestInterne = "Test interne";
    public const string EnAttenteValidation = "En attente de validation";
    public const string Cloture = "Clôturé";
    public const string Annule = "Annulé";

    /// <summary>Etat attribue a un ticket qui vient d'etre cree.</summary>
    public const string Initial = Ouvert;

    public static readonly string[] Tous = [Ouvert, EnCours, EnDeveloppement, TestInterne, EnAttenteValidation, Cloture, Annule];

    /// <summary>Seuls etats qu'un developpeur peut poser.</summary>
    public static readonly string[] AutorisesDev = [EnDeveloppement, TestInterne];

    /// <summary>Ordre d'avancement d'un ticket (Annulé est a part : c'est une action, pas une etape).</summary>
    private static readonly string[] Progression = [Ouvert, EnCours, EnDeveloppement, TestInterne, EnAttenteValidation, Cloture];

    /// <summary>Position dans le cycle de vie (-1 si hors progression, ex. Annulé).</summary>
    public static int IndexProgression(string? etat) => Array.IndexOf(Progression, Normaliser(etat));

    /// <summary>
    /// Un non-admin ne revient jamais a une etape precedente.
    /// Seule exception (si autorisee) : le rejet du test interne, qui renvoie le ticket
    /// en developpement — c'est la decision du testeur, pas celle du developpeur.
    /// </summary>
    public static bool EstRetourArriere(string? ancien, string cible, bool autoriserRejetTest)
    {
        if (cible == Annule) return false;
        if (autoriserRejetTest && Normaliser(ancien) == TestInterne && cible == EnDeveloppement) return false;
        var indexAncien = IndexProgression(ancien);
        return indexAncien >= 0 && IndexProgression(cible) <= indexAncien;
    }

    /// <summary>Un ticket clos ou annule n'evolue plus.</summary>
    public static bool EstTermine(string? etat) => etat == Cloture || etat == Annule;

    /// <summary>Anciens codes numeriques -> nouveaux libelles.</summary>
    private static readonly Dictionary<string, string> Legacy = new()
    {
        ["1"] = Ouvert,
        ["2"] = EnCours,
        ["3"] = EnDeveloppement
    };

    public static bool EstValide(string? etat) =>
        etat != null && Tous.Contains(etat);

    /// <summary>Convertit une valeur stockee en base (ancienne ou nouvelle) en libelle.</summary>
    public static string? Normaliser(string? etat)
    {
        if (string.IsNullOrWhiteSpace(etat)) return null;
        etat = etat.Trim();
        return Legacy.TryGetValue(etat, out var libelle) ? libelle : etat;
    }

    /// <summary>
    /// Toutes les valeurs stockees en base correspondant a un libelle,
    /// afin que le filtrage retrouve aussi les anciens tickets.
    /// </summary>
    public static string[] ValeursEnBase(string etat)
    {
        var codes = Legacy.Where(kv => kv.Value == etat).Select(kv => kv.Key);
        return codes.Append(etat).ToArray();
    }
}
