using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HelpDesk.API.Services;

/// <summary>
/// Similarité « floue » entre deux tickets (intitulé + description + produit), pour avertir
/// d'un doublon possible à la création manuelle. Comparaison par recouvrement de mots (Jaccard),
/// insensible à la casse, aux accents et à la ponctuation.
/// </summary>
public static class SimilariteTicket
{
    /// <summary>Seuil au-delà duquel on considère deux tickets « proches » (0..1).</summary>
    public const double Seuil = 0.5;

    private static readonly HashSet<string> MotsVides = new(StringComparer.Ordinal)
    {
        "le","la","les","de","des","du","un","une","et","en","au","aux","pour","par","sur","dans",
        "ce","cette","ces","que","qui","est","sont","avec","sans","pas","ne","son","sa","ses","the","of","and","for","to"
    };

    /// <summary>Texte normalisé : minuscules, sans accents, ponctuation → espaces, espaces compactés.</summary>
    public static string Normaliser(string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return "";
        var s = RetirerAccents(texte.Trim().ToLowerInvariant());
        s = Regex.Replace(s, "[^a-z0-9]+", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Ensemble de mots significatifs (≥ 3 lettres, hors mots vides).</summary>
    public static HashSet<string> Mots(string? texte) =>
        Normaliser(texte).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(m => m.Length >= 3 && !MotsVides.Contains(m))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Indice de Jaccard : |A ∩ B| / |A ∪ B| (0..1).</summary>
    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        var union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }

    /// <summary>
    /// Score de similarité 0..1 : l'intitulé pèse le plus, la description en appoint,
    /// et le même produit ajoute un petit bonus.
    /// </summary>
    public static double Score(
        string? intituleA, string? descriptionA, string? produitA,
        string? intituleB, string? descriptionB, string? produitB)
    {
        var simIntitule = Jaccard(Mots(intituleA), Mots(intituleB));
        var simDescription = Jaccard(Mots(descriptionA), Mots(descriptionB));
        var memeProduit = !string.IsNullOrWhiteSpace(produitA) &&
                          string.Equals(produitA?.Trim(), produitB?.Trim(), StringComparison.OrdinalIgnoreCase);
        return 0.7 * simIntitule + 0.2 * simDescription + (memeProduit ? 0.1 : 0);
    }

    public static bool EstSimilaire(
        string? intituleA, string? descriptionA, string? produitA,
        string? intituleB, string? descriptionB, string? produitB) =>
        Score(intituleA, descriptionA, produitA, intituleB, descriptionB, produitB) >= Seuil;

    private static string RetirerAccents(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
