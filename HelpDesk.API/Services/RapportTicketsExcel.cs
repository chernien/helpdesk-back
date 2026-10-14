using ClosedXML.Excel;
using HelpDesk.API.Constants;
using HelpDesk.API.Models;
using HelpDesk.API.Repositories;

namespace HelpDesk.API.Services;

/// <summary>
/// Colonnes de l'ancienne application déduites du ticket et de son historique : ouverture, fin de traitement,
/// déclarant, nombre de jours en attente et résolution.
/// </summary>
public record SuiviTicket(DateTime? Debut, DateTime? Fin, string? Declarant, int? JoursEnAttente = null, string? Resolution = null);

/// <summary>
/// Export Excel des tickets : uniquement le tableau de données (pas de logo ni de titre),
/// avec les colonnes de l'export historique (ID, Email, Client… Résolution) et un filtre sur chaque colonne.
/// </summary>
public static class RapportTicketsExcel
{
    /// <summary>Limite d'une cellule Excel (32 767 caractères).</summary>
    private const int TexteMax = 32_000;

    public static readonly (string Titre, double Largeur)[] Colonnes =
    [
        ("ID", 8), ("Email", 32), ("Client", 22), ("Produit", 11), ("Version", 9), ("Module", 15),
        ("Intitulé", 38), ("Date de création", 15), ("Date estimée", 13), ("Type", 11), ("Importance", 12),
        ("État", 22), ("Description", 50), ("Collaborateur", 20), ("Client/Mediasoft", 16), ("Collaborateur 2", 16),
        ("Heure début", 16), ("Heure fin", 16), ("Jours en attente", 15), ("Résolution", 32)
    ];

    /// <summary>
    /// Heure début = ouverture du ticket (date et heure de la demande) ; Heure fin = passage à Clôturé ou Annulé
    /// (ticket terminé) ; Client/Mediasoft = qui a créé le ticket : « Client » dans son espace, « Mediasoft » pour un
    /// membre de l'équipe (y compris un e-mail converti à la main), « Agent IA » pour l'agent ; Jours en attente = Heure fin − Heure début
    /// (jusqu'à aujourd'hui si le ticket n'est pas terminé) ; Résolution = la note interne, sinon le dernier message
    /// écrit à la résolution (passage en attente de validation, clôture, annulation, réponse du client).
    /// Les valeurs saisies par l'ancienne application priment.
    /// </summary>
    public static SuiviTicket Suivi(TicketList t, IEnumerable<EvenementTicket> historique, DateTime? aujourdhui = null)
    {
        var evenements = historique.OrderBy(e => e.DateEvent).ThenBy(e => e.TypeEvent == "CREATION" ? 0 : 1).ToList();

        var debut = t.Heured2 ?? t.DateDemande2 ?? t.DateDemande?.ToDateTime(TimeOnly.MinValue);

        var fin = t.Heuref2;
        if (fin == null && TicketEtat.Normaliser(t.Etat) is TicketEtat.Cloture or TicketEtat.Annule)
            fin = evenements.LastOrDefault(e => e.TypeEvent != "CREATION" &&
                                               TicketEtat.Normaliser(e.Etat) is TicketEtat.Cloture or TicketEtat.Annule)?.DateEvent;

        string? declarant = RapportTicketsPdf.Propre(t.Ce1);
        if (declarant == null)
        {
            var creation = evenements.FirstOrDefault(e => e.TypeEvent == "CREATION");
            if (t.Intitule?.TrimStart().StartsWith("[AI]", StringComparison.OrdinalIgnoreCase) == true)
                declarant = "Agent IA";                                   // l'agent préfixe toujours ses tickets « [AI] »
            else if (creation?.ParId is int par)
                declarant = par == t.ClientId ? "Client" : "Mediasoft";   // e-mail converti à la main = l'équipe
        }

        int? jours = debut is { } d ? Math.Max(0, ((fin ?? aujourdhui ?? DateTime.Today).Date - d.Date).Days) : null;

        // Le message de résolution ne vaut que si le ticket en est à ce stade (pas s'il est reparti en cours).
        var resolution = RapportTicketsPdf.Propre(t.Resolution);
        if (resolution == null && EstResolu(t.Etat))
            resolution = evenements.LastOrDefault(e => e.TypeEvent != "CREATION" && EstResolu(e.Etat)
                                                       && !string.IsNullOrWhiteSpace(e.Commentaire))?.Commentaire;

        return new SuiviTicket(debut, fin, declarant, jours, resolution);
    }

    private static bool EstResolu(string? etat) =>
        TicketEtat.Normaliser(etat) is TicketEtat.EnAttenteValidation or TicketEtat.Cloture or TicketEtat.Annule;

    /// <param name="nomsMembres">Id → prénom + nom des membres de l'équipe (colonne Collaborateur).</param>
    /// <param name="historique">Historique des tickets (Heure fin, Client/Mediasoft, Résolution).</param>
    public static byte[] Generer(IReadOnlyList<TicketList> tickets, IReadOnlyDictionary<int, string> nomsMembres,
        ILookup<int, EvenementTicket> historique)
    {
        using var classeur = new XLWorkbook();
        classeur.Properties.Title = "Tickets";
        classeur.Properties.Company = "Mediasoft";
        var ws = classeur.Worksheets.Add("Tickets");

        for (var c = 0; c < Colonnes.Length; c++)
        {
            ws.Column(c + 1).Width = Colonnes[c].Largeur;
            ws.Cell(1, c + 1).Value = Colonnes[c].Titre;
        }

        var ligne = 1;
        foreach (var t in tickets)
        {
            ligne++;
            var collaborateur = t.CollaborateurId is int id && nomsMembres.TryGetValue(id, out var nom) ? nom : t.Collaborateur;
            var suivi = Suivi(t, historique[t.Id]);
            ws.Cell(ligne, 1).Value = t.Id;
            Texte(ws.Cell(ligne, 2), t.Email);
            Texte(ws.Cell(ligne, 3), t.Client);
            Texte(ws.Cell(ligne, 4), t.Produit);
            Texte(ws.Cell(ligne, 5), t.Version);
            Texte(ws.Cell(ligne, 6), t.Module);
            Texte(ws.Cell(ligne, 7), t.Intitule);
            Date(ws.Cell(ligne, 8), t.DateDemande2 ?? t.DateDemande?.ToDateTime(TimeOnly.MinValue));
            Date(ws.Cell(ligne, 9), t.DateEstimee2 ?? t.DateEstimee?.ToDateTime(TimeOnly.MinValue));
            Texte(ws.Cell(ligne, 10), t.TypeTicket);
            Texte(ws.Cell(ligne, 11), t.Importance);
            Texte(ws.Cell(ligne, 12), TicketEtat.Normaliser(t.Etat) ?? t.Etat);
            Texte(ws.Cell(ligne, 13), t.Description);
            Texte(ws.Cell(ligne, 14), collaborateur);
            Texte(ws.Cell(ligne, 15), suivi.Declarant);
            Texte(ws.Cell(ligne, 16), t.Ce2);
            if (suivi.Debut.HasValue) Date(ws.Cell(ligne, 17), suivi.Debut); else Texte(ws.Cell(ligne, 17), t.Heured);
            if (suivi.Fin.HasValue) Date(ws.Cell(ligne, 18), suivi.Fin); else Texte(ws.Cell(ligne, 18), t.Heuref);
            if (suivi.JoursEnAttente is int jours) ws.Cell(ligne, 19).Value = jours;
            Texte(ws.Cell(ligne, 20), suivi.Resolution);
        }

        var table = ws.Range(1, 1, ligne, Colonnes.Length).CreateTable("Tickets");
        table.Theme = XLTableTheme.TableStyleMedium2;
        table.ShowAutoFilter = true;

        // Une ligne par ticket : un long texte reste dans sa cellule (entier dans la barre de formule)
        // au lieu de déborder sur les colonnes voisines.
        if (ligne > 1)
        {
            var donnees = ws.Range(2, 1, ligne, Colonnes.Length);
            donnees.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Top).Alignment.SetWrapText(true);
            ws.Rows(2, ligne).Height = 15;
            foreach (var c in new[] { 1, 19 }) ws.Range(2, c, ligne, c).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            foreach (var c in new[] { 8, 9 }) ws.Range(2, c, ligne, c).Style.DateFormat.Format = "dd/MM/yyyy";
            foreach (var c in new[] { 17, 18 }) ws.Range(2, c, ligne, c).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
        }
        ws.SheetView.Freeze(1, 1);   // en-têtes et colonne ID toujours visibles

        using var flux = new MemoryStream();
        classeur.SaveAs(flux);
        return flux.ToArray();
    }

    /// <summary>Valeur absente : cellule laissée vide.</summary>
    private static void Texte(IXLCell cellule, string? texte)
    {
        var s = RapportTicketsPdf.Propre(texte);
        if (s != null) cellule.Value = s.Length > TexteMax ? s[..TexteMax] + "…" : s;
    }

    private static void Date(IXLCell cellule, DateTime? date)
    {
        if (date.HasValue) cellule.Value = date.Value;
    }
}
