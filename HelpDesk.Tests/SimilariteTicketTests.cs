using HelpDesk.API.Services;
using Xunit;

namespace HelpDesk.Tests;

/// <summary>Tests de la similarité « floue » entre tickets (avertissement de doublon à la saisie manuelle).</summary>
public class SimilariteTicketTests
{
    [Theory]
    [InlineData("Problème de facturation", "probleme de facturation")]   // accents retirés, casse
    [InlineData("  RÈGLEMENT   Client  ", "reglement client")]           // espaces compactés
    [InlineData("Écart de montant !!!", "ecart de montant")]             // ponctuation → espaces
    public void Normaliser_minuscules_sans_accents_ni_ponctuation(string entree, string attendu)
        => Assert.Equal(attendu, SimilariteTicket.Normaliser(entree));

    [Fact]
    public void Mots_retire_les_mots_courts_et_vides()
    {
        var mots = SimilariteTicket.Mots("Le problème de la facture du client");
        Assert.Contains("probleme", mots);
        Assert.Contains("facture", mots);
        Assert.Contains("client", mots);
        Assert.DoesNotContain("le", mots);   // mot vide
        Assert.DoesNotContain("de", mots);    // mot vide + court
    }

    [Fact]
    public void Intitules_identiques_sont_similaires()
        => Assert.True(SimilariteTicket.EstSimilaire(
            "Problème de facturation Tunisie Telecom", null, "Divalto",
            "Problème de facturation Tunisie Telecom", null, "Divalto"));

    [Fact]
    public void Intitules_proches_reformules_sont_similaires() // même demande, mots réordonnés/variés
        => Assert.True(SimilariteTicket.EstSimilaire(
            "Erreur montant facture Tunisie Telecom", "Merci de corriger le montant", "Divalto",
            "Facture Tunisie Telecom : montant erroné", "Le montant est faux", "Divalto"));

    [Fact]
    public void Sujets_differents_ne_sont_pas_similaires()
        => Assert.False(SimilariteTicket.EstSimilaire(
            "Problème de connexion au serveur", "impossible de se connecter", "Divalto",
            "Demande d'évolution du module paie", "ajouter une rubrique", "Divalto"));

    [Fact]
    public void Meme_produit_ajoute_un_bonus_mais_ne_suffit_pas_seul()
    {
        // Intitulés sans mots communs : le seul bonus « même produit » (0.1) reste sous le seuil (0.5).
        Assert.False(SimilariteTicket.EstSimilaire(
            "Connexion impossible", null, "Divalto",
            "Rubrique paie manquante", null, "Divalto"));
    }

    [Fact]
    public void Texte_vide_non_similaire()
        => Assert.False(SimilariteTicket.EstSimilaire("", "", "Divalto", "", "", "Divalto"));
}
