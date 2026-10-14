using HelpDesk.API.Models;
using HelpDesk.API.Repositories;
using HelpDesk.API.Services;
using Xunit;

namespace HelpDesk.Tests;

/// <summary>Colonnes de l'ancienne application : Heure début (ouverture), Heure fin et Client/Mediasoft (historique).</summary>
public class SuiviTicketTests
{
    private const int Client = 50, Equipe = 7;
    private static readonly DateTime J = new(2026, 10, 1, 9, 0, 0);

    private static TicketList Ticket(string etat, string? expediteur = null) =>
        new() { Id = 1, Etat = etat, ClientId = Client, Importance = "Mineur", EmailExpediteur = expediteur, DateDemande2 = J };

    private static EvenementTicket Evt(string type, string? etat, int? par, int heures, string? message = null) =>
        new(1, type, etat, par, J.AddHours(heures), message);

    [Fact]
    public void Jours_en_attente_entre_ouverture_et_fin()
    {
        var suivi = RapportTicketsExcel.Suivi(Ticket("Clôturé"),
            [Evt("CREATION", "Ouvert", Client, 0), Evt("VALIDATION_CLIENT", "Clôturé", Client, 3 * 24 + 2)]);
        Assert.Equal(3, suivi.JoursEnAttente);
    }

    [Fact]
    public void Jours_en_attente_jusqu_a_aujourd_hui_si_le_ticket_n_est_pas_termine()
    {
        var suivi = RapportTicketsExcel.Suivi(Ticket("En cours"), [Evt("ETAT", "En cours", Equipe, 1)], aujourdhui: J.AddDays(5));
        Assert.Equal(5, suivi.JoursEnAttente);
    }

    [Fact]
    public void Resolution_note_interne_prioritaire()
    {
        var t = Ticket("Clôturé");
        t.Resolution = "Index de la table recréé";
        Assert.Equal("Index de la table recréé",
            RapportTicketsExcel.Suivi(t, [Evt("VALIDATION_CLIENT", "Clôturé", Client, 5, "Merci")]).Resolution);
    }

    [Fact]
    public void Resolution_dernier_message_ecrit_a_la_resolution()
    {
        var suivi = RapportTicketsExcel.Suivi(Ticket("Clôturé"),
        [
            Evt("ETAT", "En cours", Equipe, 1, "Je regarde"),
            Evt("ETAT", "En attente de validation", Equipe, 4, "c fait"),
            Evt("VALIDATION_CLIENT", "Clôturé", Client, 6, "Parfait, merci.")
        ]);
        Assert.Equal("Parfait, merci.", suivi.Resolution);
    }

    [Fact]
    public void Pas_de_resolution_pour_un_ticket_reparti_en_cours()
        => Assert.Null(RapportTicketsExcel.Suivi(Ticket("En cours"),
            [Evt("ETAT", "En attente de validation", Equipe, 4, "c fait"), Evt("ETAT", "En cours", Equipe, 6)]).Resolution);

    [Fact]
    public void Debut_est_l_ouverture_du_ticket_meme_s_il_n_est_pas_pris_en_charge()
    {
        var suivi = RapportTicketsExcel.Suivi(Ticket("Ouvert"), [Evt("CREATION", "Ouvert", Client, 0)]);
        Assert.Equal(J, suivi.Debut);
        Assert.Null(suivi.Fin);                              // ticket pas terminé : pas d'heure de fin
    }

    [Fact]
    public void Debut_a_defaut_d_heure_la_date_de_la_demande()
    {
        var t = Ticket("Ouvert");
        t.DateDemande2 = null;
        t.DateDemande = DateOnly.FromDateTime(J);
        Assert.Equal(J.Date, RapportTicketsExcel.Suivi(t, []).Debut);
    }

    [Theory]
    [InlineData("Clôturé")]
    [InlineData("Annulé")]
    public void Fin_est_le_passage_a_un_etat_final(string etatFinal)
    {
        var suivi = RapportTicketsExcel.Suivi(Ticket(etatFinal),
        [
            Evt("CREATION", "Ouvert", Equipe, 0),
            Evt("ETAT", "En cours", Equipe, 1),
            Evt("ETAT", "En attente de validation", Equipe, 5),
            Evt(etatFinal == "Clôturé" ? "VALIDATION_CLIENT" : "ANNULATION_CLIENT", etatFinal, Client, 8)
        ]);
        Assert.Equal(J, suivi.Debut);
        Assert.Equal(J.AddHours(8), suivi.Fin);
    }

    [Fact]
    public void Declarant_agent_ia_pour_un_ticket_cree_par_l_agent()
    {
        var t = Ticket("Ouvert", "client@societe.tn");
        t.Intitule = "[AI] Problème d'import / export";
        Assert.Equal("Agent IA", RapportTicketsExcel.Suivi(t, [Evt("CREATION", "Ouvert", Equipe, 0)]).Declarant);
    }

    [Fact]
    public void Declarant_mediasoft_pour_un_e_mail_converti_a_la_main()
        => Assert.Equal("Mediasoft", RapportTicketsExcel.Suivi(Ticket("Ouvert", "client@societe.tn"),
            [Evt("CREATION", "Ouvert", Equipe, 0)]).Declarant);

    [Fact]
    public void Declarant_client_quand_il_cree_le_ticket_dans_son_espace()
        => Assert.Equal("Client", RapportTicketsExcel.Suivi(Ticket("Ouvert"), [Evt("CREATION", "Ouvert", Client, 0)]).Declarant);

    [Fact]
    public void Declarant_mediasoft_quand_l_equipe_cree_le_ticket()
        => Assert.Equal("Mediasoft", RapportTicketsExcel.Suivi(Ticket("Ouvert"), [Evt("CREATION", "Ouvert", Equipe, 0)]).Declarant);

    [Fact]
    public void Valeurs_de_l_ancienne_application_conservees()
    {
        var t = Ticket("Clôturé");
        t.Ce1 = "Mediasoft";
        t.Heured2 = J.AddDays(-3);
        t.Heuref2 = J.AddDays(-1);
        var suivi = RapportTicketsExcel.Suivi(t, [Evt("ETAT", "Clôturé", Equipe, 0)]);
        Assert.Equal(("Mediasoft", J.AddDays(-3), J.AddDays(-1)), (suivi.Declarant, suivi.Debut, suivi.Fin));
    }

    [Fact]
    public void Sans_historique_seule_l_ouverture_est_connue()
    {
        var suivi = RapportTicketsExcel.Suivi(Ticket("Ouvert"), []);
        Assert.Equal((null, J, null), (suivi.Declarant, suivi.Debut, suivi.Fin));
    }
}
