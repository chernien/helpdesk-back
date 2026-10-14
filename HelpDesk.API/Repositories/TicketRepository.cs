using HelpDesk.API.Constants;
using HelpDesk.API.Data;
using HelpDesk.API.DTOs;
using HelpDesk.API.Models;
using HelpDesk.API.Services;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Repositories;

/// <summary>Champs e-mail d'un ticket, pour le rapprochement anti-doublon.</summary>
public record TicketLienEmail(int Id, string? MessageId, string? ThreadId, string? Expediteur, string? Objet, DateTime? Date);

/// <summary>Événement de l'historique d'un ticket, réduit à ce qu'il faut pour l'export (clôture, créateur, message de résolution).</summary>
public record EvenementTicket(int TicketId, string TypeEvent, string? Etat, int? ParId, DateTime DateEvent, string? Commentaire = null);

public class TicketFilter
{
    public string? Etat { get; set; }
    public string? Produit { get; set; }
    public string? Search { get; set; }
    /// <summary>Nom et/ou prénom d'un membre de l'équipe : tickets traités par lui (responsable ou auteur d'une action).</summary>
    public string? Membre { get; set; }
    /// <summary>Tickets demandés à partir de ce jour (inclus) : 1er janvier d'une année, 1er d'un mois, ou un jour précis.</summary>
    public DateOnly? Depuis { get; set; }
    /// <summary>
    /// Client : vide = ses propres tickets ; administrateur de sa société : l'e-mail d'un membre de son équipe
    /// (ses tickets) ou « * » (toute la société).
    /// </summary>
    public string? Equipe { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public interface ITicketRepository
{
    Task<(List<TicketList> items, int total)> SearchAsync(TicketFilter filter, UtilisateurContexte user);
    /// <summary>Export : les tickets visibles cochés (ids) ou, à défaut, tous ceux des filtres ; du plus récent au plus ancien.</summary>
    Task<List<TicketList>> ExporterAsync(TicketFilter filter, int[]? ids, UtilisateurContexte user, int max);
    /// <summary>Historique des tickets exportés (une seule requête).</summary>
    Task<List<EvenementTicket>> EvenementsAsync(int[] ticketIds);
    /// <summary>
    /// Nombre de tickets visibles par état (tableau de bord), traités par un membre, depuis une date et
    /// (client) de l'équipier choisi, si précisés.
    /// </summary>
    Task<Dictionary<string, int>> CompterParEtatAsync(UtilisateurContexte user, string? membre = null, DateOnly? depuis = null, string? equipe = null);
    /// <summary>Membres de l'équipe (admin, collaborateur, développeur actifs) : suggestions du filtre.</summary>
    Task<List<MembreEquipeDto>> MembresEquipeAsync();
    Task<TicketList?> GetByIdAsync(int id);
    /// <summary>Fusionne le ticket source dans la cible : bascule historique + messages, puis clôture la source.</summary>
    Task FusionnerAsync(int sourceId, int cibleId, string etatCloture, string note);
    /// <summary>Tickets liés à un e-mail candidats pour le rapprochement (par Message-ID, fil, ou objet+expéditeur).</summary>
    Task<List<TicketLienEmail>> CandidatsLienEmailAsync(
        IReadOnlyCollection<string> messageIds, IReadOnlyCollection<string> threadIds,
        IReadOnlyCollection<string> objets, IReadOnlyCollection<string> expediteurs);
    /// <summary>Tickets récents d'un client (pour détecter un doublon à la création manuelle).</summary>
    Task<List<TicketList>> TicketsRecentsDuClientAsync(int? clientId, string? societe, DateTime depuis);
    /// <summary>Version + module du dernier ticket de ce client (+produit) : repli pour l'agent IA.</summary>
    Task<(string? Version, string? Module)> DerniereVersionModuleClientAsync(string client, string? produit);
    /// <summary>Produits des tickets de cette société, du plus fréquent au moins fréquent (100 derniers tickets).</summary>
    Task<List<string>> ProduitsHabituelsAsync(string societe);
    /// <summary>Sociétés (colonne Client) des derniers tickets de cet e-mail, du plus récent au plus ancien.</summary>
    Task<List<string>> SocietesRecentesAsync(string email);
    /// <summary>Rétro-remplit objet+expéditeur de la signature e-mail sur les tickets qui n'en ont pas (cas des anciens tickets).</summary>
    Task<int> BackfillEmailSignaturesAsync(Func<string?, string?> normaliserObjet);
    Task<TicketList> AddAsync(TicketList ticket);
    Task DeleteAsync(TicketList ticket);
    Task SaveChangesAsync();
}

public class TicketRepository : ITicketRepository
{
    private readonly HelpDeskContext _context;

    public TicketRepository(HelpDeskContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Perimetre de visibilite, applique en SQL (jamais en memoire) :
    /// - admin : tout ;
    /// - collaborateur : les tickets de ses produits + ceux qui lui sont assignes ;
    /// - client : ses tickets (par compte, ou par email pour les tickets crees avant son
    ///   compte), restreints aux modules qui lui sont affectes (aucun module = tous).
    /// </summary>
    public static IQueryable<TicketList> AppliquerVisibilite(IQueryable<TicketList> query, UtilisateurContexte user)
    {
        if (user.EstAdmin) return query;

        if (user.EstCollaborateur)
        {
            var produits = user.Produits.ToList();
            return query.Where(t => t.CollaborateurId == user.Id ||
                                    (t.Produit != null && produits.Contains(t.Produit)));
        }

        // Un developpeur ne voit que les tickets qui lui sont assignes.
        if (user.EstDev)
            return query.Where(t => t.CollaborateurId == user.Id);

        if (user.EstClient)
            return FiltrerVisibiliteClient(query, user);

        return query.Where(_ => false);
    }

    /// <summary>
    /// Visibilité d'un client : ses propres tickets (par compte/email), plus TOUS ceux des sociétés dont il
    /// est administrateur (Divalto T2.U_FLAG_ADMIN) ; un simple membre ne voit pas les tickets de ses collègues.
    /// Le tout restreint aux produits ET aux modules qui lui sont affectés (aucun produit = tous ; aucun module = tous).
    /// Société consultée (en-tête X-Societe) : les tickets de cette société seulement.
    /// </summary>
    private static IQueryable<TicketList> FiltrerVisibiliteClient(IQueryable<TicketList> query, UtilisateurContexte user)
    {
        var email = user.Email?.ToLower();
        var userId = user.Id;
        if (!string.IsNullOrEmpty(user.SocieteActive))
        {
            var societe = user.SocieteActive;
            query = query.Where(t => t.Client != null && t.Client.Trim() == societe);
            // Simple membre de cette société : seulement ses propres tickets.
            if (!user.EstAdminDe(societe))
                query = query.Where(t => t.ClientId == userId ||
                                         (email != null && t.Email != null && t.Email.ToLower() == email));
        }
        else
        {
            var administrees = user.SocietesAdmin?.ToList() ?? new List<string>();
            query = administrees.Count > 0
                ? query.Where(t => (t.Client != null && administrees.Contains(t.Client.Trim())) ||
                                   t.ClientId == userId ||
                                   (email != null && t.Email != null && t.Email.ToLower() == email))
                : query.Where(t => t.ClientId == userId ||
                                   (email != null && t.Email != null && t.Email.ToLower() == email));
        }

        var produits = user.PerimetreClient?.Select(p => p.Produit).ToList();
        if (produits != null && produits.Count > 0)
            query = query.Where(t => t.Produit != null && produits.Contains(t.Produit));

        var modules = user.ModulesClient;
        if (modules != null && modules.Count > 0)
            query = query.Where(t => t.Module != null && modules.Contains(t.Module));

        return query;
    }

    public async Task<(List<TicketList> items, int total)> SearchAsync(TicketFilter filter, UtilisateurContexte user)
    {
        var query = await RequeteFiltreeAsync(filter, user);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(t => t.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        return (items, total);
    }

    public async Task<List<TicketList>> ExporterAsync(TicketFilter filter, int[]? ids, UtilisateurContexte user, int max)
    {
        // Tickets cochés : exactement ceux-là (s'ils sont visibles), sans les autres filtres.
        var query = ids is { Length: > 0 }
            ? AppliquerVisibilite(_context.TicketList.AsNoTracking(), user).Where(t => ids.Contains(t.Id))
            : await RequeteFiltreeAsync(filter, user);
        return await query.OrderByDescending(t => t.Id).Take(max).ToListAsync();
    }

    public Task<List<EvenementTicket>> EvenementsAsync(int[] ticketIds) =>
        _context.NotificationHisto.AsNoTracking()
            .Where(n => ticketIds.Contains(n.TicketId))
            .Select(n => new EvenementTicket(n.TicketId, n.TypeEvent, n.Etat, n.ParId, n.DateEvent, n.Commentaire))
            .ToListAsync();

    /// <summary>Tickets visibles par l'utilisateur, restreints par les filtres de la liste (état, produit, membre, date, recherche).</summary>
    private async Task<IQueryable<TicketList>> RequeteFiltreeAsync(TicketFilter filter, UtilisateurContexte user)
    {
        var query = AppliquerVisibilite(_context.TicketList.AsNoTracking(), user);

        if (!string.IsNullOrEmpty(filter.Etat))
        {
            // On accepte le libelle ET les anciens codes numeriques equivalents.
            var valeurs = TicketEtat.ValeursEnBase(filter.Etat);
            query = query.Where(t => t.Etat != null && valeurs.Contains(t.Etat));
        }
        if (!string.IsNullOrEmpty(filter.Produit))
            query = query.Where(t => t.Produit == filter.Produit);
        query = await FiltrerMembreAsync(query, filter.Membre);
        query = FiltrerDepuis(query, filter.Depuis);
        query = await FiltrerEquipeClientAsync(query, user, filter.Equipe);
        if (!string.IsNullOrEmpty(filter.Search))
            query = query.Where(t =>
                (t.Intitule != null && t.Intitule.Contains(filter.Search)) ||
                (t.Email != null && t.Email.ToLower().Contains(filter.Search.ToLower())) ||
                (t.Client != null && t.Client.Contains(filter.Search)));
        return query;
    }

    /// <summary>
    /// Tickets « traités » par un membre de l'équipe, désigné par son nom et/ou prénom (« Ammar », « guitouni ammar ») :
    /// il en est le responsable, ou il y a fait une action (état, affectation, commentaire). La création seule ne
    /// compte pas : sinon tous les tickets créés par son agent IA seraient « traités » par lui.
    /// </summary>
    private async Task<IQueryable<TicketList>> FiltrerMembreAsync(IQueryable<TicketList> query, string? membre)
    {
        if (string.IsNullOrWhiteSpace(membre)) return query;
        var ids = (await MembresCorrespondantsAsync(membre)).ToList();
        if (ids.Count == 0) return query.Where(_ => false);
        return query.Where(t =>
            (t.CollaborateurId != null && ids.Contains(t.CollaborateurId.Value)) ||
            _context.NotificationHisto.Any(n => n.TicketId == t.Id && n.ParId != null
                                                && ids.Contains(n.ParId.Value) && n.TypeEvent != "CREATION"));
    }

    /// <summary>
    /// Liste d'un client : par défaut SES tickets. Un administrateur de société (dont la visibilité couvre toute
    /// la société) peut choisir un membre de son équipe (son e-mail) ou « * » pour toute la société.
    /// </summary>
    private async Task<IQueryable<TicketList>> FiltrerEquipeClientAsync(IQueryable<TicketList> query, UtilisateurContexte user, string? equipe)
    {
        if (!user.EstClient) return query;
        var admin = user.SocietesAdmin is { Count: > 0 };
        if (admin && equipe?.Trim() == "*") return query;

        // Le membre choisi (administrateur seulement), sinon le client lui-même.
        var email = (admin && !string.IsNullOrWhiteSpace(equipe) ? equipe : user.Email).Trim().ToLower();
        var compteId = await _context.Authentication.AsNoTracking()
            .Where(a => a.Username != null && a.Username.ToLower() == email)
            .Select(a => (int?)a.Id).FirstOrDefaultAsync();
        return query.Where(t => (t.Email != null && t.Email.ToLower() == email) || (compteId != null && t.ClientId == compteId));
    }

    /// <summary>Tickets demandés à partir de ce jour (inclus) : date de la demande, à défaut son horodatage.</summary>
    private static IQueryable<TicketList> FiltrerDepuis(IQueryable<TicketList> query, DateOnly? depuis)
    {
        if (depuis is not { } jour) return query;
        var debut = jour.ToDateTime(TimeOnly.MinValue);
        return query.Where(t => t.DateDemande != null ? t.DateDemande >= jour : t.DateDemande2 >= debut);
    }

    /// <summary>Membres de l'équipe dont le prénom / nom / identifiant contient TOUS les mots saisis (sans accents ni casse).</summary>
    private async Task<IEnumerable<int>> MembresCorrespondantsAsync(string texte)
    {
        var mots = Normaliser(texte).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (await MembresEquipeAsync())
            .Where(m => mots.All(mot => Normaliser($"{m.Nom} {m.Username}").Contains(mot)))
            .Select(m => m.Id);
    }

    public async Task<List<MembreEquipeDto>> MembresEquipeAsync()
    {
        var roles = new[] { Roles.Admin, Roles.User, Roles.Dev };
        var membres = await _context.Authentication.AsNoTracking()
            .Where(a => a.Role != null && roles.Contains(a.Role))
            .Select(a => new { a.Id, a.Prenom, a.Nom, a.Username, a.Role })
            .ToListAsync();
        return membres.Select(m =>
        {
            var nom = string.Join(' ', new[] { m.Prenom, m.Nom }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            return new MembreEquipeDto
            {
                Id = m.Id,
                Nom = string.IsNullOrWhiteSpace(nom) ? (m.Username ?? $"Utilisateur #{m.Id}") : nom,
                Username = m.Username,
                Role = m.Role
            };
        }).OrderBy(m => m.Nom, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Normaliser(string? s) =>
        new string((s ?? "").Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());

    public async Task<Dictionary<string, int>> CompterParEtatAsync(UtilisateurContexte user, string? membre = null, DateOnly? depuis = null, string? equipe = null)
    {
        // Une seule requête GROUP BY, avec la même visibilité que la liste (rôle, produits, société consultée).
        var query = await FiltrerMembreAsync(AppliquerVisibilite(_context.TicketList.AsNoTracking(), user), membre);
        query = await FiltrerEquipeClientAsync(query, user, equipe);
        var brut = await FiltrerDepuis(query, depuis)
            .GroupBy(t => t.Etat)
            .Select(g => new { Etat = g.Key, Nombre = g.Count() })
            .ToListAsync();

        var resultat = TicketEtat.Tous.ToDictionary(e => e, _ => 0);
        foreach (var ligne in brut)
        {
            var etat = TicketEtat.Normaliser(ligne.Etat);
            if (etat != null && resultat.ContainsKey(etat)) resultat[etat] += ligne.Nombre;
        }
        return resultat;
    }

    public Task<TicketList?> GetByIdAsync(int id) =>
        _context.TicketList.FirstOrDefaultAsync(t => t.Id == id);

    public async Task FusionnerAsync(int sourceId, int cibleId, string etatCloture, string note)
    {
        await using var tx = await _context.Database.BeginTransactionAsync();

        // Historique (suivi) et messages (discussion) de la source basculent sur la cible.
        await _context.NotificationHisto.Where(n => n.TicketId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.TicketId, cibleId));
        await _context.Message.Where(m => m.TicketId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.TicketId, cibleId));
        // Repères de lecture (UserId, TicketId) : on supprime ceux de la source (PK composite, non fusionnables).
        await _context.MessageLu.Where(l => l.TicketId == sourceId).ExecuteDeleteAsync();

        // La source est clôturée avec une note pointant vers la cible (on la garde pour la traçabilité).
        await _context.TicketList.Where(t => t.Id == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Etat, etatCloture).SetProperty(t => t.Resolution, note));

        await tx.CommitAsync();
    }

    public async Task<List<TicketLienEmail>> CandidatsLienEmailAsync(
        IReadOnlyCollection<string> messageIds, IReadOnlyCollection<string> threadIds,
        IReadOnlyCollection<string> objets, IReadOnlyCollection<string> expediteurs)
    {
        var parMessage = messageIds.Count > 0;
        var parFil = threadIds.Count > 0;
        var parHeuristique = objets.Count > 0 && expediteurs.Count > 0;
        if (!parMessage && !parFil && !parHeuristique) return new List<TicketLienEmail>();

        var query = _context.TicketList.AsNoTracking().Where(t =>
            (parMessage && t.EmailMessageId != null && messageIds.Contains(t.EmailMessageId)) ||
            (parFil && t.EmailThreadId != null && threadIds.Contains(t.EmailThreadId)) ||
            (parHeuristique && t.EmailObjet != null && t.EmailExpediteur != null &&
                objets.Contains(t.EmailObjet) && expediteurs.Contains(t.EmailExpediteur)));

        return await query
            .Select(t => new TicketLienEmail(t.Id, t.EmailMessageId, t.EmailThreadId, t.EmailExpediteur, t.EmailObjet, t.EmailDate))
            .ToListAsync();
    }

    public async Task<int> BackfillEmailSignaturesAsync(Func<string?, string?> normaliserObjet)
    {
        // Anciens tickets sans signature e-mail : on rétro-remplit ce qu'on peut à partir de l'intitulé
        // et de l'email client. La date d'envoi d'origine étant inconnue, on laisse EmailDate à null
        // (le rapprochement se fera alors sur objet + expéditeur).
        var tickets = await _context.TicketList
            .Where(t => t.EmailObjet == null && t.Intitule != null && t.Intitule != "")
            .ToListAsync();

        var n = 0;
        foreach (var t in tickets)
        {
            var objet = normaliserObjet(t.Intitule);
            if (objet == null) continue;
            t.EmailObjet = objet;
            t.EmailExpediteur = string.IsNullOrWhiteSpace(t.Email) ? null : t.Email.Trim().ToLowerInvariant();
            t.EmailDate = null;
            n++;
        }
        await _context.SaveChangesAsync();
        return n;
    }

    public async Task<List<string>> SocietesRecentesAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return new List<string>();
        var cible = email.Trim().ToLower();
        return await _context.TicketList.AsNoTracking()
            .Where(t => t.Client != null && t.Client != "" &&
                        ((t.Email != null && t.Email.Trim().ToLower() == cible) ||
                         (t.EmailExpediteur != null && t.EmailExpediteur == cible)))
            .OrderByDescending(t => t.Id)
            .Take(20)
            .Select(t => t.Client!.Trim())
            .ToListAsync();
    }

    public async Task<List<string>> ProduitsHabituelsAsync(string societe)
    {
        if (string.IsNullOrWhiteSpace(societe)) return new List<string>();
        var cible = societe.Trim();
        var produits = await _context.TicketList.AsNoTracking()
            .Where(t => t.Client != null && t.Client.Trim() == cible && t.Produit != null && t.Produit != "")
            .OrderByDescending(t => t.Id)
            .Take(100)
            .Select(t => t.Produit!)
            .ToListAsync();
        return produits.GroupBy(p => p.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();
    }

    public async Task<(string? Version, string? Module)> DerniereVersionModuleClientAsync(string client, string? produit)
    {
        if (string.IsNullOrWhiteSpace(client)) return (null, null);
        var cible = client.Trim();
        var query = _context.TicketList.AsNoTracking()
            .Where(t => t.Client != null && t.Client.Trim() == cible && t.Version != null && t.Version != "");
        if (!string.IsNullOrWhiteSpace(produit))
            query = query.Where(t => t.Produit == produit);

        var r = await query.OrderByDescending(t => t.DateDemande2)
            .Select(t => new { t.Version, t.Module })
            .FirstOrDefaultAsync();
        return (r?.Version, r?.Module);
    }

    public Task<List<TicketList>> TicketsRecentsDuClientAsync(int? clientId, string? societe, DateTime depuis) =>
        _context.TicketList.AsNoTracking()
            .Where(t => t.DateDemande2 >= depuis &&
                        ((clientId != null && t.ClientId == clientId) ||
                         (societe != null && t.Client != null && t.Client == societe)))
            .OrderByDescending(t => t.Id)
            .Take(200)
            .ToListAsync();

    public async Task<TicketList> AddAsync(TicketList ticket)
    {
        _context.TicketList.Add(ticket);
        await _context.SaveChangesAsync();
        return ticket;
    }

    public async Task DeleteAsync(TicketList ticket)
    {
        // Le ticket part avec sa discussion, ses repères de lecture et son historique (aucune ligne orpheline).
        await using var tx = await _context.Database.BeginTransactionAsync();
        await _context.MessageLu.Where(l => l.TicketId == ticket.Id).ExecuteDeleteAsync();
        await _context.Message.Where(m => m.TicketId == ticket.Id).ExecuteDeleteAsync();
        await _context.NotificationHisto.Where(n => n.TicketId == ticket.Id).ExecuteDeleteAsync();
        _context.TicketList.Remove(ticket);
        await _context.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public Task SaveChangesAsync() => _context.SaveChangesAsync();
}
