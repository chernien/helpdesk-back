using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Exceptions;
using HelpDesk.API.Hubs;
using HelpDesk.API.Models;
using HelpDesk.API.Repositories;
using Microsoft.AspNetCore.SignalR;

namespace HelpDesk.API.Services;

/// <summary>Fichier exporté : contenu, type MIME et extension (« pdf », « xlsx »).</summary>
public record FichierExport(byte[] Contenu, string Type, string Extension);

public interface ITicketService
{
    Task<TicketPageDto> GetTicketsAsync(UtilisateurContexte user, TicketFilter filter);
    /// <summary>Tableau de bord : nombre de tickets visibles par état (membre, date, équipier du client, si précisés).</summary>
    Task<Dictionary<string, int>> CompteursAsync(UtilisateurContexte user, string? membre = null, DateOnly? depuis = null, string? equipe = null);
    /// <summary>Client administrateur de sa société : les autres membres de son équipe (contacts Divalto de ses sociétés).</summary>
    Task<List<EquipierDto>> EquipeClientAsync(UtilisateurContexte user);
    /// <summary>Membres de l'équipe (suggestions du filtre « traités par »).</summary>
    Task<List<MembreEquipeDto>> MembresEquipeAsync();
    /// <summary>
    /// Export des tickets cochés, sinon de tous ceux des filtres : PDF (logo, critères, synthèse, tableau)
    /// ou Excel (tableau de données seul).
    /// </summary>
    Task<FichierExport> ExporterAsync(UtilisateurContexte user, ExportTicketsRequest request);
    Task<TicketDto> GetByIdAsync(int id, UtilisateurContexte user);
    Task<TicketDto> CreateAsync(CreateTicketRequest request, UtilisateurContexte user);
    Task<List<DoublonTicketDto>> VerifierDoublonAsync(VerifierDoublonRequest request, UtilisateurContexte user);
    Task<int> BackfillSignaturesAsync();
    Task<TicketDto> FusionnerAsync(int sourceId, int cibleId, UtilisateurContexte user);
    /// <summary>Ticket déjà lié à cet e-mail (Message-ID, fil, ou objet+expéditeur+date), sinon null.</summary>
    Task<int?> TicketLieAsync(SignatureEmail signature);
    /// <summary>Crée un ticket depuis un e-mail analysé par l'agent IA (retourne l'id, ou null si doublon).</summary>
    Task<int?> CreerDepuisAgentAsync(AnalyseEmail analyse, string expediteurEmail, string? expediteurNom,
        string? objetEmail, SignatureEmail signature, UtilisateurContexte proprietaire,
        PieceJointeAgent? piece = null, bool autoAssign = false);
    Task<TicketDto> UpdateAsync(int id, UpdateTicketRequest request, UtilisateurContexte user);
    Task<TicketDto> ValiderParClientAsync(int id, ClientValidationRequest request, UtilisateurContexte user);
    Task<List<NotificationHisto>> SuiviAsync(int id, UtilisateurContexte user);
    Task<TicketDto> JoindreFichierAsync(int id, IFormFile fichier, UtilisateurContexte user);
    Task<(Stream Flux, string Nom, string Type)> TelechargerFichierAsync(int id, UtilisateurContexte user);
    Task JoindreCaptureSuiviAsync(int id, IFormFile fichier, UtilisateurContexte user);
    Task<(Stream Flux, string Nom, string Type)> TelechargerCaptureSuiviAsync(int id, int evenementId, UtilisateurContexte user);
    Task DeleteAsync(int id);
}

/// <summary>
/// Cycle de vie des tickets.
/// Distribution : un ticket est visible et notifie a tous les collaborateurs
/// affectes a son produit (file d'equipe). Le premier collaborateur qui fait
/// avancer le ticket en devient responsable ; il peut ensuite etre reassigne
/// a un collegue du meme produit.
/// </summary>
public class TicketService : ITicketService
{
    // Evenements SignalR
    private const string EvtCree = "ticketCree";
    private const string EvtAssigne = "ticketAssigne";
    private const string EvtMisAJour = "ticketMisAJour";      // avec toast (client concerne)
    private const string EvtRafraichir = "ticketRafraichi";   // silencieux (equipe, admins)

    private readonly ITicketRepository _ticketRepository;
    private readonly IUserRepository _userRepository;
    private readonly IProduitRepository _produitRepository;
    private readonly INotificationRepository _notificationRepository;
    private readonly IClientService _clientService;
    private readonly IFichierStockage _fichierStockage;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly GraphMailService _mail;
    private readonly ILogger<TicketService> _logger;
    private readonly IConfiguration _config;
    private readonly IModuleRepository _moduleRepository;

    /// <summary>Sérialise la conversion e-mail → ticket (contrôle du doublon + insertion), toutes requêtes confondues.</summary>
    private static readonly SemaphoreSlim _verrouConversionEmail = new(1, 1);

    public TicketService(
        ITicketRepository ticketRepository,
        IUserRepository userRepository,
        IProduitRepository produitRepository,
        INotificationRepository notificationRepository,
        IClientService clientService,
        IFichierStockage fichierStockage,
        IHubContext<NotificationHub> hubContext,
        GraphMailService mail,
        ILogger<TicketService> logger,
        IConfiguration config,
        IModuleRepository moduleRepository)
    {
        _ticketRepository = ticketRepository;
        _userRepository = userRepository;
        _produitRepository = produitRepository;
        _notificationRepository = notificationRepository;
        _clientService = clientService;
        _fichierStockage = fichierStockage;
        _hubContext = hubContext;
        _mail = mail;
        _logger = logger;
        _moduleRepository = moduleRepository;
        _config = config;
    }

    public Task<Dictionary<string, int>> CompteursAsync(UtilisateurContexte user, string? membre = null, DateOnly? depuis = null, string? equipe = null) =>
        _ticketRepository.CompterParEtatAsync(user, membre, depuis, equipe);

    public async Task<List<EquipierDto>> EquipeClientAsync(UtilisateurContexte user)
    {
        if (!user.EstClient || user.SocietesAdmin is not { Count: > 0 }) return new();
        var compte = await _userRepository.GetByIdAsync(user.Id);
        if (compte == null) return new();

        // Sociétés administrées (la société consultée seulement, si le client en a choisi une).
        var societes = UserService.SocietesDe(compte).Where(s => user.EstAdminDe(s.Nom) &&
            (user.SocieteActive == null || string.Equals(s.Nom.Trim(), user.SocieteActive, StringComparison.OrdinalIgnoreCase)));

        var moi = user.Email.Trim().ToLowerInvariant();
        var equipe = new Dictionary<string, EquipierDto>();
        foreach (var societe in societes)
            foreach (var contact in await _clientService.ContactsAsync(societe.Tiers))
            {
                var email = contact.Email.Trim().ToLowerInvariant();
                if (email == moi || equipe.ContainsKey(email)) continue;
                var nom = string.Join(' ', new[] { contact.Prenom, contact.Nom }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
                equipe[email] = new EquipierDto { Email = email, Nom = nom.Length > 0 ? nom : email };
            }
        return equipe.Values.OrderBy(e => e.Nom, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<List<MembreEquipeDto>> MembresEquipeAsync() => _ticketRepository.MembresEquipeAsync();

    /// <summary>Au-delà, le rapport ne garde que les plus récents (fichier raisonnable, génération rapide).</summary>
    private const int ExportMax = 5000;

    public async Task<FichierExport> ExporterAsync(UtilisateurContexte user, ExportTicketsRequest request)
    {
        var excel = string.Equals(request.Format?.Trim(), "excel", StringComparison.OrdinalIgnoreCase);
        var ids = request.Ids?.Distinct().ToArray();
        var filtre = new TicketFilter
        {
            Etat = request.Etat, Produit = request.Produit, Search = request.Search, Depuis = request.Depuis,
            Membre = user.EstAdmin ? request.Membre : null   // « traités par » : réservé à l'admin, comme dans la liste
        };
        var tickets = await _ticketRepository.ExporterAsync(filtre, ids, user, ExportMax);
        if (tickets.Count == 0)
            throw new MetierException("Aucun ticket à exporter.");

        var noms = (await _ticketRepository.MembresEquipeAsync()).ToDictionary(m => m.Id, m => m.Nom);
        // Historique : fin de traitement, jours en attente, message de résolution (PDF comme Excel).
        var historique = (await _ticketRepository.EvenementsAsync(tickets.Select(t => t.Id).ToArray()))
            .ToLookup(e => e.TicketId);
        if (excel)
            return new FichierExport(RapportTicketsExcel.Generer(tickets, noms, historique),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx");

        var criteres = DecrireCriteres(filtre, ids);
        if (tickets.Count == ExportMax) criteres += $" (limité aux {ExportMax} plus récents)";
        return new FichierExport(RapportTicketsPdf.Generer(tickets, noms, historique, criteres, DateTime.Now), "application/pdf", "pdf");
    }

    /// <summary>Ce que contient le rapport, en clair : « Traités par Karim Trabelsi · Produit Divalto », « Sélection de 5 tickets »…</summary>
    public static string DecrireCriteres(TicketFilter f, int[]? ids)
    {
        if (ids is { Length: > 0 })
            return ids.Length > 1 ? $"Sélection de {ids.Length} tickets" : "Sélection d'un ticket";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.Membre)) parts.Add($"Traités par {f.Membre.Trim()}");
        if (!string.IsNullOrWhiteSpace(f.Produit)) parts.Add($"Produit {f.Produit.Trim()}");
        if (!string.IsNullOrWhiteSpace(f.Etat)) parts.Add($"État {f.Etat.Trim()}");
        if (f.Depuis is { } d) parts.Add($"À partir du {d:dd/MM/yyyy}");
        if (!string.IsNullOrWhiteSpace(f.Search)) parts.Add($"Recherche « {f.Search.Trim()} »");
        return parts.Count == 0 ? "Tous les tickets" : string.Join(" · ", parts);
    }

    public async Task<TicketPageDto> GetTicketsAsync(UtilisateurContexte user, TicketFilter filter)
    {
        filter.Page = Math.Max(1, filter.Page);
        filter.PageSize = Math.Clamp(filter.PageSize, 1, 100);

        var (items, total) = await _ticketRepository.SearchAsync(filter, user);
        return new TicketPageDto { Total = total, Items = items.Select(ToDto).ToList() };
    }

    public async Task<TicketDto> GetByIdAsync(int id, UtilisateurContexte user)
    {
        var ticket = await ChargerAccessibleAsync(id, user);
        return ToDto(ticket);
    }

    public async Task<TicketDto> CreateAsync(CreateTicketRequest request, UtilisateurContexte user)
    {
        if (user.EstDev)
            throw MetierException.Interdit("Un développeur ne crée pas de tickets : il reçoit ceux qu'on lui assigne.");

        await ValiderContenuAsync(request);

        // Signature e-mail (si création depuis la boîte mail) : sert au lien partagé et à l'anti-doublon.
        var signature = LienEmailTicket.Signature(
            request.EmailMessageId, request.EmailThreadId, request.EmailExpediteur, request.EmailObjet, request.EmailDate);

        // Section critique : pour une création depuis un e-mail, on sérialise « contrôle du doublon +
        // insertion ». Ainsi deux conversions simultanées de copies différentes (même fil, ou même
        // objet+expéditeur — hors index unique Message-ID) ne peuvent pas créer deux tickets.
        var verrou = signature.EstExploitable;
        if (verrou) await _verrouConversionEmail.WaitAsync();
        TicketList ticket = null!;
        try
        {
            if (verrou)
            {
                var dejaTicketId = await TrouverTicketLieAsync(signature);
                if (dejaTicketId != null)
                    throw MetierException.Conflit($"Cet e-mail est déjà transformé en ticket #{dejaTicketId}.");
            }

            var now = DateTime.Now;
            ticket = new TicketList
            {
                Intitule = request.Intitule.Trim(),
                Description = NettoyageTexte.Nettoyer(request.Description),   // texte d'e-mail : invisibles, [Image], séparateurs
                Produit = request.Produit,
                Version = request.Version,
                Module = request.Module,
                TypeTicket = request.TypeTicket,
                Importance = request.Importance,
                Etat = TicketEtat.Initial,
                DateDemande = DateOnly.FromDateTime(now),
                DateDemande2 = now,
                DateEstimee = request.DateEstimee.HasValue ? DateOnly.FromDateTime(request.DateEstimee.Value) : null,
                DateEstimee2 = request.DateEstimee,
                // Lien partagé avec l'e-mail d'origine (anti-doublon pour toute l'équipe).
                EmailMessageId = signature.MessageId,
                EmailThreadId = signature.ThreadId,
                EmailExpediteur = signature.Expediteur,
                EmailObjet = signature.Objet,
                EmailDate = signature.Date
                // Pas de collaborateur a la creation : le ticket part dans la file du produit.
            };

            if (user.EstClient)
            {
                // Un client cree toujours pour lui-meme.
                var compte = await _userRepository.GetByIdAsync(user.Id)
                    ?? throw MetierException.Introuvable("Compte introuvable.");
                ticket.ClientId = compte.Id;
                ticket.Email = compte.Username;
                // Multi-sociétés : la société choisie si elle est bien dans son périmètre, sinon la principale.
                var societeChoisie = request.Societe?.Trim();
                ticket.Client = !string.IsNullOrWhiteSpace(societeChoisie) && user.SocietesClient.Contains(societeChoisie)
                    ? societeChoisie
                    : (!string.IsNullOrWhiteSpace(compte.SocieteNom) ? compte.SocieteNom : compte.NomComplet);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.ClientTiers) || string.IsNullOrWhiteSpace(request.ContactEmail))
                    throw new MetierException("Le client et le contact sont requis.");

                var contact = await _clientService.TrouverContactAsync(request.ClientTiers, request.ContactEmail)
                    ?? throw new MetierException("Ce contact n'appartient pas à la société sélectionnée.");

                ticket.Client = contact.SocieteNom;
                ticket.Email = contact.Email.Trim().ToLowerInvariant();
                // Si le contact a un compte helpdesk, le ticket apparait dans son espace.
                ticket.ClientId = (await _userRepository.GetByEmailAsync(contact.Email))?.Id;
            }

            await _ticketRepository.AddAsync(ticket);
        }
        finally
        {
            if (verrou) _verrouConversionEmail.Release();
        }

        await TracerAsync(ticket, "CREATION", user);

        // A la creation, seuls les collaborateurs ROLE_USER du produit sont prevenus :
        // les developpeurs n'entrent en jeu que lorsqu'on leur assigne le ticket.
        // La requete DbContext reste seule (pas thread-safe), puis les envois
        // SignalR, independants entre eux, partent en parallele.
        var dto = ToDto(ticket);
        var equipe = await _userRepository.IdsUsersDuProduitAsync(ticket.Produit!);
        await Task.WhenAll(
            EnvoyerAuxUtilisateursAsync(equipe, user.Id, EvtCree, dto),
            _hubContext.Clients.Group("admins").SendAsync(EvtCree, dto));

        // Temps réel : prévenir toutes les boîtes mail que cet e-mail est désormais un ticket,
        // pour que le badge « Ticket #N » apparaisse en direct chez les autres destinataires.
        if (signature.EstExploitable)
            await DiffuserLienEmailAsync(ticket.Id, signature);

        return dto;
    }

    public Task<int?> TicketLieAsync(SignatureEmail signature) => TrouverTicketLieAsync(signature);

    /// <summary>Cherche un ticket déjà lié à cet e-mail (Message-ID, fil, ou objet+expéditeur+date).</summary>
    private async Task<int?> TrouverTicketLieAsync(SignatureEmail signature)
    {
        var candidats = await _ticketRepository.CandidatsLienEmailAsync(
            signature.MessageId != null ? new[] { signature.MessageId } : Array.Empty<string>(),
            signature.ThreadId != null ? new[] { signature.ThreadId } : Array.Empty<string>(),
            signature.Objet != null ? new[] { signature.Objet } : Array.Empty<string>(),
            signature.Expediteur != null ? new[] { signature.Expediteur } : Array.Empty<string>());

        return candidats
            .Where(c => LienEmailTicket.Correspond(signature, c.MessageId, c.ThreadId, c.Expediteur, c.Objet, c.Date))
            .Select(c => (int?)c.Id)
            .DefaultIfEmpty(null)
            .Min();
    }

    /// <summary>
    /// Pousse à TOUTES les boîtes mail connectées le lien e-mail ↔ ticket nouvellement créé.
    /// Chaque boîte ne met à jour son badge que si elle a l'e-mail concerné (filtrage côté client),
    /// donc une diffusion large est sans effet de bord et couvre admins, collaborateurs et devs.
    /// </summary>
    private Task DiffuserLienEmailAsync(int ticketId, SignatureEmail s) =>
        _hubContext.Clients.All.SendAsync("emailLieTicket", new
        {
            ticketId,
            messageId = s.MessageId,
            threadId = s.ThreadId,
            expediteur = s.Expediteur,
            objet = s.Objet,
            date = s.Date
        });

    public async Task<int?> CreerDepuisAgentAsync(AnalyseEmail a, string expediteurEmail, string? expediteurNom,
        string? objetEmail, SignatureEmail signature, UtilisateurContexte proprietaire,
        PieceJointeAgent? piece = null, bool autoAssign = false)
    {
        // Anti-doublon : si cet e-mail (ou une copie) est déjà un ticket, on ne recrée rien.
        if (signature.EstExploitable && await TrouverTicketLieAsync(signature) != null)
            return null;

        var importance = (a.Priorite ?? "").ToUpperInvariant() switch
        {
            "HAUTE" => "Grave",
            "URGENTE" => "Bloquante",
            _ => "Mineur"
        };
        var typeTicket = (a.Categorie ?? "").ToUpperInvariant() switch
        {
            "TECHNIQUE" or "ACCES_COMPTE" => "Erreur",
            "FACTURATION" => "Assistance",
            "DEMANDE_INFO" => "Question",
            _ => "Assistance"
        };
        // ── Client : OBLIGATOIREMENT un contact Divalto (T2) rattaché à une société (CLI) ──
        var email = (expediteurEmail ?? "").Trim().ToLowerInvariant();
        var societes = string.IsNullOrWhiteSpace(email)
            ? new List<SocieteDivaltoDto>()
            : await _clientService.SocietesParEmailAsync(email);
        if (societes.Count == 0)
        {
            _logger.LogInformation("Agent : {Email} n'est pas un contact Divalto — pas de ticket.", email);
            return null;
        }
        // Compte HelpDesk de l'expéditeur (s'il en a un) : le ticket apparaît dans son espace.
        var authClient = await _userRepository.GetByEmailAsync(email);

        // Contact de plusieurs sociétés : 1. citée dans l'e-mail (objet ou selon l'IA) > 2. société de ses derniers
        // tickets > 3. société principale de son compte > 4. la première (ordre alphabétique), « à confirmer ».
        var societeAConfirmer = false;
        var societe = societes.Count == 1 ? societes[0]
            : societes.FirstOrDefault(s => MemeSociete(s.Nom, a.Societe) || NomCiteDans(s.Nom, objetEmail));
        if (societe == null)
            foreach (var recente in await _ticketRepository.SocietesRecentesAsync(email))
                if ((societe = societes.FirstOrDefault(s => string.Equals(s.Nom.Trim(), recente, StringComparison.OrdinalIgnoreCase))) != null)
                    break;
        societe ??= societes.FirstOrDefault(s => authClient?.ClientTiers != null
            && string.Equals(s.Tiers.Trim(), authClient.ClientTiers.Trim(), StringComparison.OrdinalIgnoreCase));
        if (societe == null)
        {
            societe = societes[0];
            societeAConfirmer = true;
        }
        var client = societe.Nom;

        // ── Produit : forcément l'un des produits de la base ──
        // 1. cité dans l'e-mail (IA) > 2. fiche client Divalto (CLI.U_PRODUIT) > 3. produit affecté au compte
        // > 4. produit habituel des tickets de la société > 5. défaut.
        var catalogue = (await _produitRepository.GetAllAsync()).Select(p => p.Nom).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var produitDivalto = ProduitDuCatalogue(societe.Produit, catalogue);
        var produit = ProduitDuCatalogue(a.Produit, catalogue) ?? produitDivalto;
        if (produit == null && authClient != null)
            produit = ProduitDuCatalogue(await _userRepository.PremierProduitAffecteAsync(authClient.Id), catalogue);
        if (produit == null)
            foreach (var habituel in await _ticketRepository.ProduitsHabituelsAsync(client))
                if ((produit = ProduitDuCatalogue(habituel, catalogue)) != null) break;
        produit ??= ProduitDuCatalogue(_config["OutlookAgent:ProduitDefaut"], catalogue) ?? catalogue.FirstOrDefault() ?? "Helpdesk";

        // ── Module : forcément l'un des modules de la base, et parmi ceux autorisés pour ce client s'il en a ──
        var tousModules = await _moduleRepository.NomsAsync();
        if (tousModules.Count == 0) tousModules = TicketReferentiel.Modules.ToList();
        var modulesClient = (authClient?.Modules ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => ProduitDuCatalogue(m, tousModules)).OfType<string>().Distinct().ToList();
        var modulesPermis = modulesClient.Count > 0 ? modulesClient : tousModules;

        // Version + module : IA > fiche client Divalto (CLI.U_VERSION, si c'est son produit) > dernier ticket (société + produit).
        var version = string.IsNullOrWhiteSpace(a.Version) ? null : a.Version!.Trim();
        if (version == null && produit == produitDivalto && !string.IsNullOrWhiteSpace(societe.Version))
            version = societe.Version!.Trim();
        var module = ProduitDuCatalogue(a.Module, modulesPermis);   // déduit du sujet par l'IA
        {
            var (vPrec, mPrec) = await _ticketRepository.DerniereVersionModuleClientAsync(client, produit);
            if (string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(vPrec)) version = vPrec;
            module ??= ProduitDuCatalogue(mPrec, modulesPermis);
        }
        // Client limité à un seul module : c'est forcément lui. Sinon, vide plutôt qu'un module inventé.
        module ??= modulesClient.Count == 1 ? modulesClient[0] : null;

        var now = DateTime.Now;
        var intitule = ("[AI] " + (a.Resume ?? objetEmail ?? "Ticket")).Trim();
        if (intitule.Length > 200) intitule = intitule[..200];

        var ticket = new TicketList
        {
            Intitule = intitule,
            // Pas de « ⚠ » : la colonne Description (non Unicode) l'enregistrerait comme « ? ».
            Description = NettoyageTexte.Nettoyer(societeAConfirmer
                ? $"Société à confirmer — ce contact travaille pour : {string.Join(", ", societes.Select(s => s.Nom))}.\n\n{a.Description}"
                : a.Description) ?? "",
            Produit = produit,
            Module = module,
            Version = version,
            TypeTicket = typeTicket,
            Importance = importance,
            Etat = TicketEtat.Initial,
            Client = client,
            Email = email,
            ClientId = authClient?.Id,   // si l'expéditeur a un compte, le ticket apparaît dans son espace
            DateDemande = DateOnly.FromDateTime(now),
            DateDemande2 = now,
            // Signature e-mail (lien partagé + anti-doublon pour les prochaines conversions).
            EmailMessageId = signature.MessageId,
            EmailThreadId = signature.ThreadId,
            EmailExpediteur = signature.Expediteur,
            EmailObjet = signature.Objet,
            EmailDate = signature.Date
        };

        // Option : le propriétaire de l'agent s'affecte les tickets créés par son agent.
        if (autoAssign)
        {
            ticket.CollaborateurId = proprietaire.Id;
            ticket.Collaborateur = proprietaire.Email;
        }

        // Pièce jointe de l'e-mail attachée au ticket (nom d'origine / stockage disque / type|taille).
        if (piece is { Contenu.Length: > 0 })
        {
            var nom = string.IsNullOrWhiteSpace(piece.Nom) ? "piece-jointe" : piece.Nom;
            if (nom.Length > 255) nom = nom[^255..];
            ticket.FichierJoint = nom;
            ticket.FichierJoint1 = await _fichierStockage.EnregistrerAsync(piece.Contenu, nom);
            ticket.FichierJoint2 = $"{piece.Type}|{piece.Contenu.Length}";
        }

        await _ticketRepository.AddAsync(ticket);
        await TracerAsync(ticket, "CREATION", proprietaire);

        // Temps réel : équipe du produit + admins.
        var dto = ToDto(ticket);
        var equipe = await _userRepository.IdsUsersDuProduitAsync(ticket.Produit!);
        await Task.WhenAll(
            EnvoyerAuxUtilisateursAsync(equipe, proprietaire.Id, EvtCree, dto),
            _hubContext.Clients.Group("admins").SendAsync(EvtCree, dto));
        if (signature.EstExploitable) await DiffuserLienEmailAsync(ticket.Id, signature);

        return ticket.Id;
    }

    /// <summary>
    /// Élément du catalogue (produit ou module) correspondant au texte (« divalto infinity » → « Divalto »,
    /// « reglement » → « Règlement »), sinon null. Sans tenir compte de la casse ni des accents :
    /// égalité d'abord, puis nom du catalogue présent comme mot dans le texte.
    /// </summary>
    public static string? ProduitDuCatalogue(string? texte, IReadOnlyList<string> catalogue)
    {
        if (string.IsNullOrWhiteSpace(texte)) return null;
        var t = SansAccents(texte.Trim());
        var exact = catalogue.FirstOrDefault(p => string.Equals(SansAccents(p.Trim()), t, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        return catalogue
            .OrderByDescending(p => p.Length)   // le plus précis d'abord
            .FirstOrDefault(p => System.Text.RegularExpressions.Regex.IsMatch(t,
                $@"(^|[^\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(SansAccents(p.Trim()))}($|[^\p{{L}}\p{{N}}])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private static string SansAccents(string s) =>
        new string(s.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray()).Normalize(System.Text.NormalizationForm.FormC);

    /// <summary>
    /// Même société, sans tenir compte de la casse, des accents ni des espaces (« Espace formation SARL » ≈
    /// « ESPACE FORMATION »). Un nom d'un seul mot (« Société ») ne suffit pas à désigner une société plus longue.
    /// </summary>
    private static bool MemeSociete(string nomDivalto, string? nomIa)
    {
        if (string.IsNullOrWhiteSpace(nomIa)) return false;
        string N(string s) => string.Join(' ', SansAccents(s).ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var d = N(nomDivalto); var i = N(nomIa);
        return d == i || i.Contains(d) || (d.Contains(i) && i.Split(' ').Length >= 2);
    }

    /// <summary>Le nom de la société apparaît (mot à mot) dans le texte, par exemple l'objet de l'e-mail.</summary>
    private static bool NomCiteDans(string nomSociete, string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte) || string.IsNullOrWhiteSpace(nomSociete)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(SansAccents(texte),
            $@"(^|[^\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(SansAccents(nomSociete.Trim()))}($|[^\p{{L}}\p{{N}}])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Fusionne le ticket source dans la cible : son suivi et sa discussion basculent sur la cible,
    /// puis la source est clôturée avec une note « Fusionné dans #cible ». Réservé à l'admin.
    /// </summary>
    public async Task<TicketDto> FusionnerAsync(int sourceId, int cibleId, UtilisateurContexte user)
    {
        if (!user.EstAdmin)
            throw MetierException.Interdit("Seul un administrateur peut fusionner des tickets.");
        if (sourceId == cibleId)
            throw new MetierException("Impossible de fusionner un ticket avec lui-même.");

        var source = await _ticketRepository.GetByIdAsync(sourceId)
            ?? throw MetierException.Introuvable($"Ticket source #{sourceId} introuvable.");
        var cible = await _ticketRepository.GetByIdAsync(cibleId)
            ?? throw MetierException.Introuvable($"Ticket cible #{cibleId} introuvable.");

        // La fusion n'est autorisée qu'au sein d'une même société.
        var societeSource = source.Client?.Trim() ?? "";
        var societeCible = cible.Client?.Trim() ?? "";
        if (!string.Equals(societeSource, societeCible, StringComparison.OrdinalIgnoreCase))
            throw new MetierException(
                $"Fusion impossible : les deux tickets n'appartiennent pas à la même société " +
                $"(« {(societeSource.Length > 0 ? societeSource : "—")} » ≠ « {(societeCible.Length > 0 ? societeCible : "—")} »).");

        if (string.Equals(source.Etat?.Trim(), TicketEtat.Cloture, StringComparison.OrdinalIgnoreCase) &&
            source.Resolution?.Contains("Fusionné dans le ticket #", StringComparison.OrdinalIgnoreCase) == true)
            throw new MetierException("Ce ticket a déjà été fusionné.");

        await _ticketRepository.FusionnerAsync(sourceId, cibleId, TicketEtat.Cloture, $"Fusionné dans le ticket #{cibleId}.");

        var dto = ToDto((await _ticketRepository.GetByIdAsync(cibleId)) ?? cible);
        var envois = new List<Task> { _hubContext.Clients.Group("admins").SendAsync(EvtRafraichir, dto) };
        if (cible.CollaborateurId.HasValue)
            envois.Add(_hubContext.Clients.Group($"user-{cible.CollaborateurId}").SendAsync(EvtRafraichir, dto));
        if (cible.ClientId.HasValue)
            envois.Add(_hubContext.Clients.Group($"user-{cible.ClientId}").SendAsync(EvtMisAJour, dto));
        await Task.WhenAll(envois);
        return dto;
    }

    /// <summary>Rétro-remplit la signature e-mail des anciens tickets (opération unique, réservée à l'admin).</summary>
    public Task<int> BackfillSignaturesAsync() =>
        _ticketRepository.BackfillEmailSignaturesAsync(LienEmailTicket.NormaliserObjet);

    /// <summary>
    /// Tickets récents du même client « proches » du nouveau (intitulé + description + produit) :
    /// avertissement de doublon non bloquant, pour admin, collaborateur ET client.
    /// </summary>
    public async Task<List<DoublonTicketDto>> VerifierDoublonAsync(VerifierDoublonRequest request, UtilisateurContexte user)
    {
        if (string.IsNullOrWhiteSpace(request.Intitule)) return new List<DoublonTicketDto>();

        // Périmètre client : admin/collaborateur via société + contact choisis ; client via son propre compte.
        string? societe = null;
        int? clientId = null;
        if (user.EstClient)
        {
            clientId = user.Id;
            societe = !string.IsNullOrWhiteSpace(request.Societe) && user.SocietesClient.Contains(request.Societe.Trim())
                ? request.Societe.Trim()
                : user.SocietesClient.FirstOrDefault();
            // Simple membre : on ne lui montre pas les tickets de ses collègues, seulement les siens.
            if (!user.EstAdminDe(societe)) societe = null;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.ClientTiers))
                societe = (await _clientService.TrouverSocieteAsync(request.ClientTiers.Trim()))?.Nom;
            if (!string.IsNullOrWhiteSpace(request.ContactEmail))
                clientId = (await _userRepository.GetByEmailAsync(request.ContactEmail.Trim()))?.Id;
        }
        if (societe == null && clientId == null) return new List<DoublonTicketDto>();

        var recents = await _ticketRepository.TicketsRecentsDuClientAsync(clientId, societe, DateTime.Now.AddDays(-90));

        return recents
            .Select(t => new
            {
                Ticket = t,
                Score = SimilariteTicket.Score(
                    request.Intitule, request.Description, request.Produit,
                    t.Intitule, t.Description, t.Produit)
            })
            .Where(x => x.Score >= SimilariteTicket.Seuil)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Ticket.Id)
            .Take(5)
            .Select(x => new DoublonTicketDto
            {
                Id = x.Ticket.Id,
                Intitule = x.Ticket.Intitule,
                Produit = x.Ticket.Produit,
                Etat = x.Ticket.Etat,
                Date = x.Ticket.DateDemande2
            })
            .ToList();
    }

    public async Task<TicketDto> UpdateAsync(int id, UpdateTicketRequest request, UtilisateurContexte user)
    {
        if (user.EstClient)
            throw MetierException.Interdit("Un client ne peut pas modifier le traitement d'un ticket.");

        var ticket = await ChargerAccessibleAsync(id, user);

        var ancienCollaborateurId = ticket.CollaborateurId;
        var ancienEtat = TicketEtat.Normaliser(ticket.Etat);

        if (TicketEtat.EstTermine(ancienEtat) && !user.EstAdmin)
            throw new MetierException($"Ce ticket est {ancienEtat?.ToLower()} : il ne peut plus être modifié.");

        var commentaire = string.IsNullOrWhiteSpace(request.Commentaire) ? null : request.Commentaire.Trim();
        if (commentaire?.Length > 1000)
            throw new MetierException("Le commentaire ne doit pas dépasser 1000 caractères.");

        // Un developpeur fait avancer SON developpement, rien d'autre :
        // pas de reassignation, pas d'edition du ticket, et seulement ses etats.
        if (user.EstDev)
        {
            var editeAutreChose = request.CollaborateurId.HasValue || request.Intitule != null ||
                request.Description != null || request.Produit != null || request.Version != null ||
                request.Module != null || request.TypeTicket != null || request.Importance != null ||
                request.Reponse != null || request.DateEstimee.HasValue;
            if (editeAutreChose)
                throw MetierException.Interdit("Un développeur ne peut que faire avancer l'état, commenter et noter sa résolution.");
            if (request.Etat != null && !TicketEtat.AutorisesDev.Contains(request.Etat))
                throw MetierException.Interdit($"Un développeur ne peut passer un ticket qu'à : {string.Join(", ", TicketEtat.AutorisesDev)}.");
        }

        // Un ticket avance, il ne recule pas : revenir en arriere est reserve a l'admin
        // (seule exception : le rejet du test interne, qui renvoie le ticket au developpeur).
        if (!user.EstAdmin && request.Etat != null && request.Etat != ancienEtat &&
            TicketEtat.EstRetourArriere(ancienEtat, request.Etat, autoriserRejetTest: !user.EstDev))
            throw new MetierException($"Impossible de revenir de « {ancienEtat} » à « {request.Etat} » : seul un administrateur peut revenir à une étape précédente.");

        // Cloturer exige une justification transmise au client.
        if (request.Etat == TicketEtat.Cloture && ancienEtat != TicketEtat.Cloture && commentaire == null)
            throw new MetierException("Une justification est requise pour clôturer un ticket.");

        // Rejet du test interne : le developpeur doit savoir ce qui ne va pas.
        if (ancienEtat == TicketEtat.TestInterne && request.Etat == TicketEtat.EnDeveloppement && commentaire == null)
            throw new MetierException("Expliquez au développeur pourquoi le test n'est pas concluant.");

        // ---- Champs du ticket : correction reservee a l'administrateur ----
        // (un ticket mal saisi a la creation, quel que soit son auteur, se corrige ici)
        var corrigeContenu = request.Intitule != null || request.Description != null || request.Produit != null ||
            request.Version != null || request.Module != null || request.TypeTicket != null ||
            request.Importance != null || request.DateEstimee.HasValue ||
            request.ClientTiers != null || request.ContactEmail != null;
        if (corrigeContenu && !user.EstAdmin)
            throw MetierException.Interdit("Seul un administrateur peut corriger les informations d'un ticket.");

        var champsModifies = new List<string>();
        void Appliquer(string libelle, string? valeur, string? actuelle, Action<string> affecter)
        {
            if (valeur == null || valeur == actuelle) return;
            affecter(valeur);
            champsModifies.Add(libelle);
        }

        if (request.Intitule != null && string.IsNullOrWhiteSpace(request.Intitule))
            throw new MetierException("L'intitulé ne peut pas être vide.");
        if (request.Intitule?.Trim().Length > 200)
            throw new MetierException("L'intitulé ne doit pas dépasser 200 caractères.");
        if (request.TypeTicket != null && !TicketReferentiel.Types.Contains(request.TypeTicket))
            throw new MetierException("Type de ticket invalide.");
        if (!await ModuleValideAsync(request.Module))
            throw new MetierException("Module invalide.");
        if (request.Importance != null && !TicketReferentiel.Importances.Contains(request.Importance))
            throw new MetierException("Importance invalide.");

        Appliquer("Intitulé", request.Intitule?.Trim(), ticket.Intitule, v => ticket.Intitule = v);
        Appliquer("Description", NettoyageTexte.Nettoyer(request.Description), ticket.Description, v => ticket.Description = v);
        Appliquer("Version", request.Version?.Trim(), ticket.Version, v => ticket.Version = v);
        Appliquer("Module", request.Module, ticket.Module, v => ticket.Module = v);
        Appliquer("Type", request.TypeTicket, ticket.TypeTicket, v => ticket.TypeTicket = v);
        Appliquer("Importance", request.Importance, ticket.Importance, v => ticket.Importance = v);

        if (request.Produit != null && request.Produit != ticket.Produit)
        {
            if (await _produitRepository.GetByNomAsync(request.Produit) == null)
                throw new MetierException("Ce produit n'existe pas.");
            ticket.Produit = request.Produit;
            champsModifies.Add("Produit");
        }

        if (request.DateEstimee.HasValue && request.DateEstimee != ticket.DateEstimee2)
        {
            ticket.DateEstimee = DateOnly.FromDateTime(request.DateEstimee.Value);
            ticket.DateEstimee2 = request.DateEstimee;
            champsModifies.Add("Date estimée");
        }

        // Ticket ouvert pour le mauvais client / contact : on le reaffecte (contact verifie dans Divalto).
        if (request.ClientTiers != null || request.ContactEmail != null)
        {
            if (string.IsNullOrWhiteSpace(request.ClientTiers) || string.IsNullOrWhiteSpace(request.ContactEmail))
                throw new MetierException("Le client et le contact sont requis.");
            var contact = await _clientService.TrouverContactAsync(request.ClientTiers.Trim(), request.ContactEmail.Trim())
                ?? throw new MetierException("Ce contact n'appartient pas à la société sélectionnée.");
            var email = contact.Email.Trim().ToLowerInvariant();
            if (!string.Equals(email, ticket.Email?.Trim(), StringComparison.OrdinalIgnoreCase) || contact.SocieteNom != ticket.Client?.Trim())
            {
                ticket.Client = contact.SocieteNom;
                ticket.Email = email;
                ticket.ClientId = (await _userRepository.GetByEmailAsync(email))?.Id;
                champsModifies.Add("Client");
            }
        }

        // Notes de suivi (pas des corrections de contenu : ouvertes a l'equipe).
        var noteModifiee = false;
        if (request.Reponse != null && request.Reponse != ticket.Reponse) { ticket.Reponse = request.Reponse; noteModifiee = true; }
        if (request.Resolution != null && request.Resolution != ticket.Resolution) { ticket.Resolution = request.Resolution; noteModifiee = true; }
        var modifie = champsModifies.Count > 0 || noteModifiee;

        // Le commentaire adresse au client devient la derniere reponse du ticket.
        if (commentaire != null) ticket.Reponse = commentaire;

        // ---- Etat ----
        if (request.Etat != null && request.Etat != ancienEtat)
        {
            if (!TicketEtat.EstValide(request.Etat))
                throw new MetierException($"État invalide. Valeurs autorisées : {string.Join(", ", TicketEtat.Tous)}.");

            ticket.Etat = request.Etat;
            if (request.Etat == TicketEtat.EnAttenteValidation && ticket.DateEnattente == null)
                ticket.DateEnattente = DateTime.Now;

            // Prise en charge : le collaborateur qui fait avancer un ticket libre en devient responsable.
            if (user.EstCollaborateur && ticket.CollaborateurId == null && request.CollaborateurId == null)
                await AssignerAsync(ticket, user.Id);
        }

        // ---- Assignation (a soi-meme ou a un collegue du meme produit) ----
        // L'etat n'est pas modifie par une assignation : il reste celui choisi (Ouvert, En cours...).
        if (request.CollaborateurId.HasValue && request.CollaborateurId != ticket.CollaborateurId)
            await AssignerAsync(ticket, request.CollaborateurId.Value);

        var etatChange = TicketEtat.Normaliser(ticket.Etat) != ancienEtat;
        var reassigne = ticket.CollaborateurId.HasValue && ticket.CollaborateurId != ancienCollaborateurId;

        if (!etatChange && !reassigne && !modifie && commentaire == null)
            return ToDto(ticket); // rien a enregistrer, rien a notifier

        await _ticketRepository.SaveChangesAsync();

        // ---- Historique : UN SEUL evenement par action, qui regroupe tout ----
        // (assignation + etat + message) => le client ne recoit qu'une notification.
        // Sans message de l'admin, le suivi indique ce qui a ete corrige
        // (sans ecraser la derniere reponse adressee au client).
        var messageSuivi = commentaire ?? (champsModifies.Count > 0
            ? $"Informations corrigées : {string.Join(", ", champsModifies)}."
            : null);
        var typeEvent =
            etatChange && reassigne ? "ASSIGNATION_ETAT" :
            etatChange              ? "ETAT" :
            reassigne               ? "ASSIGNATION" :
            commentaire != null     ? "COMMENTAIRE" : "MISE_A_JOUR";
        await TracerAsync(ticket, typeEvent, user, messageSuivi);

        // ---- Temps reel ----
        var dto = ToDto(ticket);
        var toastes = new HashSet<int> { user.Id };

        if (reassigne && ticket.CollaborateurId != user.Id)
        {
            await _hubContext.Clients.Group($"user-{ticket.CollaborateurId}").SendAsync(EvtAssigne, dto);
            toastes.Add(ticket.CollaborateurId!.Value);

            // E-mail automatique au développeur affecté (en plus de la notification temps réel).
            await NotifierAffectationParEmailAsync(ticket, user);
        }

        // Le client est prevenu de toute evolution, qu'il soit rattache par compte ou par email.
        var compteClientId = ticket.ClientId
            ?? (ticket.Email != null ? (await _userRepository.GetByEmailAsync(ticket.Email))?.Id : null);
        if (compteClientId.HasValue)
            await _hubContext.Clients.Group($"user-{compteClientId}").SendAsync(EvtMisAJour, dto);

        // Passage « En attente de validation » : le client reçoit un vrai e-mail (pas qu'une notification).
        if (etatChange && TicketEtat.Normaliser(ticket.Etat) == TicketEtat.EnAttenteValidation)
            await NotifierValidationClientParEmailAsync(ticket, user);

        // Developpement termine -> tous les collaborateurs du produit sont invites a tester.
        if (etatChange && TicketEtat.Normaliser(ticket.Etat) == TicketEtat.TestInterne)
        {
            var testeurs = await _userRepository.IdsUsersDuProduitAsync(ticket.Produit ?? "");
            await EnvoyerAuxUtilisateursAsync(testeurs.Where(i => !toastes.Contains(i)), user.Id, EvtMisAJour, dto);
            toastes.UnionWith(testeurs);
        }

        // Le responsable (ex. le developpeur dont le test est rejete) recoit un toast, pas un simple rafraichissement.
        if (ticket.CollaborateurId.HasValue && !toastes.Contains(ticket.CollaborateurId.Value) && (etatChange || commentaire != null || champsModifies.Count > 0))
        {
            await _hubContext.Clients.Group($"user-{ticket.CollaborateurId}").SendAsync(EvtMisAJour, dto);
            toastes.Add(ticket.CollaborateurId.Value);
        }

        var equipe = await _userRepository.IdsCollaborateursDuProduitAsync(ticket.Produit ?? "");
        await EnvoyerAuxUtilisateursAsync(equipe.Where(i => !toastes.Contains(i)), user.Id, EvtRafraichir, dto);
        await _hubContext.Clients.Group("admins").SendAsync(EvtRafraichir, dto);

        return dto;
    }

    /// <summary>
    /// Decision du client sur un ticket "En attente de validation" :
    /// il valide la solution (le ticket est cloture) ou annule le ticket.
    /// </summary>
    public async Task<TicketDto> ValiderParClientAsync(int id, ClientValidationRequest request, UtilisateurContexte user)
    {
        if (!user.EstClient)
            throw MetierException.Interdit("Cette action est réservée au client du ticket.");

        var ticket = await ChargerAccessibleAsync(id, user);
        if (TicketEtat.Normaliser(ticket.Etat) != TicketEtat.EnAttenteValidation)
            throw new MetierException("Ce ticket n'est pas en attente de votre validation.");

        var commentaire = string.IsNullOrWhiteSpace(request.Commentaire) ? null : request.Commentaire.Trim();
        if (commentaire?.Length > 1000)
            throw new MetierException("Le commentaire ne doit pas dépasser 1000 caractères.");

        ticket.Etat = request.Valider ? TicketEtat.Cloture : TicketEtat.Annule;
        await _ticketRepository.SaveChangesAsync();
        await TracerAsync(ticket, request.Valider ? "VALIDATION_CLIENT" : "ANNULATION_CLIENT", user, commentaire);

        // Le responsable est prevenu (toast) ; le reste de l'equipe et les admins rafraichissent.
        // Requete DbContext d'abord (jamais en parallele), puis les envois SignalR ensemble.
        var dto = ToDto(ticket);
        var equipe = await _userRepository.IdsCollaborateursDuProduitAsync(ticket.Produit ?? "");

        var envois = new List<Task>
        {
            EnvoyerAuxUtilisateursAsync(equipe.Where(i => i != ticket.CollaborateurId), user.Id, EvtRafraichir, dto),
            _hubContext.Clients.Group("admins").SendAsync(EvtRafraichir, dto)
        };
        if (ticket.CollaborateurId.HasValue)
            envois.Add(_hubContext.Clients.Group($"user-{ticket.CollaborateurId}").SendAsync(EvtMisAJour, dto));
        await Task.WhenAll(envois);

        return dto;
    }

    /// <summary>Fil de suivi du ticket, dans le perimetre de l'utilisateur.</summary>
    public async Task<List<NotificationHisto>> SuiviAsync(int id, UtilisateurContexte user)
    {
        await ChargerAccessibleAsync(id, user);
        return await _notificationRepository.SuiviTicketAsync(id);
    }

    private const long TailleMaxFichier = 10 * 1024 * 1024; // 10 Mo

    /// <summary>
    /// Joint un fichier au ticket (une seule piece jointe, ajoutee a la creation).
    /// Colonnes reprises de l'ancienne application : Fichier_joint = nom d'origine,
    /// Fichier_joint1 = nom de stockage disque, Fichier_joint2 = "type|taille".
    /// </summary>
    public async Task<TicketDto> JoindreFichierAsync(int id, IFormFile fichier, UtilisateurContexte user)
    {
        var ticket = await ChargerAccessibleAsync(id, user);

        if (fichier.Length == 0)
            throw new MetierException("Le fichier est vide.");
        if (fichier.Length > TailleMaxFichier)
            throw new MetierException("Le fichier ne doit pas dépasser 10 Mo.");

        var nomOrigine = Path.GetFileName(fichier.FileName);
        if (string.IsNullOrWhiteSpace(nomOrigine)) nomOrigine = "piece-jointe";
        if (nomOrigine.Length > 255) nomOrigine = nomOrigine[^255..];

        // Remplacement : on supprime l'ancienne pièce jointe du disque le cas échéant.
        if (!string.IsNullOrWhiteSpace(ticket.FichierJoint1))
            _fichierStockage.Supprimer(ticket.FichierJoint1);

        ticket.FichierJoint = nomOrigine;
        ticket.FichierJoint1 = await _fichierStockage.EnregistrerAsync(fichier);
        ticket.FichierJoint2 = $"{fichier.ContentType}|{fichier.Length}";
        await _ticketRepository.SaveChangesAsync();

        return ToDto(ticket);
    }

    /// <summary>Flux de la piece jointe, pour l'afficher ou la telecharger.</summary>
    public async Task<(Stream Flux, string Nom, string Type)> TelechargerFichierAsync(int id, UtilisateurContexte user)
    {
        var ticket = await ChargerAccessibleAsync(id, user);
        if (string.IsNullOrWhiteSpace(ticket.FichierJoint1))
            throw MetierException.Introuvable("Aucun fichier n'est joint à ce ticket.");

        var flux = _fichierStockage.Ouvrir(ticket.FichierJoint1)
            ?? throw MetierException.Introuvable("Le fichier n'existe plus sur le serveur.");

        return (flux, ticket.FichierJoint ?? "piece-jointe", TypeFichier(ticket.FichierJoint2));
    }

    /// <summary>
    /// Joint une capture au DERNIER evenement du suivi ecrit par cet utilisateur
    /// (rejet d'un test interne illustre, ou preuve du test envoyee au client).
    /// </summary>
    public async Task JoindreCaptureSuiviAsync(int id, IFormFile fichier, UtilisateurContexte user)
    {
        if (user.EstClient)
            throw MetierException.Interdit("Cette action est réservée à l'équipe Mediasoft.");

        await ChargerAccessibleAsync(id, user);

        if (fichier.Length == 0)
            throw new MetierException("Le fichier est vide.");
        if (fichier.Length > TailleMaxFichier)
            throw new MetierException("Le fichier ne doit pas dépasser 10 Mo.");

        var evenement = await _notificationRepository.DernierEvenementDeAsync(id, user.Id)
            ?? throw MetierException.Introuvable("Aucune action de votre part à illustrer sur ce ticket.");
        if (evenement.FichierStockage != null)
            throw new MetierException("Une capture est déjà jointe à cette action.");

        var nomOrigine = Path.GetFileName(fichier.FileName);
        if (string.IsNullOrWhiteSpace(nomOrigine)) nomOrigine = "capture";
        if (nomOrigine.Length > 255) nomOrigine = nomOrigine[^255..];

        evenement.FichierNom = nomOrigine;
        evenement.FichierStockage = await _fichierStockage.EnregistrerAsync(fichier);
        evenement.FichierType = fichier.ContentType;
        await _notificationRepository.SaveChangesAsync();
    }

    /// <summary>Capture d'un evenement du suivi (visible par tous ceux qui ont acces au ticket).</summary>
    public async Task<(Stream Flux, string Nom, string Type)> TelechargerCaptureSuiviAsync(int id, int evenementId, UtilisateurContexte user)
    {
        await ChargerAccessibleAsync(id, user);

        var evenement = await _notificationRepository.GetEvenementAsync(id, evenementId);
        if (evenement?.FichierStockage == null)
            throw MetierException.Introuvable("Aucune capture sur cet événement.");

        var flux = _fichierStockage.Ouvrir(evenement.FichierStockage)
            ?? throw MetierException.Introuvable("Le fichier n'existe plus sur le serveur.");

        return (flux, evenement.FichierNom ?? "capture",
                string.IsNullOrWhiteSpace(evenement.FichierType) ? "application/octet-stream" : evenement.FichierType);
    }

    public async Task DeleteAsync(int id)
    {
        var ticket = await _ticketRepository.GetByIdAsync(id)
            ?? throw MetierException.Introuvable("Ticket introuvable.");

        // Les fichiers disque partent avec le ticket (piece jointe + captures du suivi).
        var captures = await _notificationRepository.FichiersStockageDuTicketAsync(id);
        await _ticketRepository.DeleteAsync(ticket);
        _fichierStockage.Supprimer(ticket.FichierJoint1);
        foreach (var capture in captures)
            _fichierStockage.Supprimer(capture);
    }

    // ---------- Regles ----------

    /// <summary>Charge un ticket en appliquant la regle de visibilite (404 si hors perimetre).</summary>
    private async Task<TicketList> ChargerAccessibleAsync(int id, UtilisateurContexte user)
    {
        var ticket = await _ticketRepository.GetByIdAsync(id);

        var visible = ticket != null && (
            user.EstAdmin ||
            (user.EstCollaborateur && (ticket.CollaborateurId == user.Id ||
                                       (ticket.Produit != null && user.Produits.Contains(ticket.Produit)))) ||
            (user.EstDev && ticket.CollaborateurId == user.Id) ||
            (user.EstClient && (ticket.ClientId == user.Id ||
                                (ticket.ClientId == null && string.Equals(ticket.Email?.Trim(), user.Email, StringComparison.OrdinalIgnoreCase)) ||
                                user.EstAdminDe(ticket.Client))));   // admin de la société : tous ses tickets

        // 404 plutot que 403 : on ne revele pas l'existence d'un ticket hors perimetre.
        return visible ? ticket! : throw MetierException.Introuvable("Ticket introuvable.");
    }

    /// <summary>Seul un collaborateur actif affecte au produit du ticket peut en etre responsable.</summary>
    private async Task AssignerAsync(TicketList ticket, int collaborateurId)
    {
        var equipe = await _userRepository.IdsCollaborateursDuProduitAsync(ticket.Produit ?? "");
        if (!equipe.Contains(collaborateurId))
            throw new MetierException($"Ce collaborateur n'est pas affecté au produit {ticket.Produit}.");

        var collaborateur = await _userRepository.GetByIdAsync(collaborateurId);
        ticket.CollaborateurId = collaborateur!.Id;
        ticket.Collaborateur = collaborateur.Username;
    }

    /// <summary>
    /// Envoie un e-mail au développeur quand un ticket lui est affecté (par un admin ou un autre utilisateur).
    /// L'échec de l'envoi ne bloque pas l'affectation : il est seulement journalisé.
    /// </summary>
    private async Task NotifierAffectationParEmailAsync(TicketList ticket, UtilisateurContexte par)
    {
        if (!ticket.CollaborateurId.HasValue) return;

        var dev = await _userRepository.GetByIdAsync(ticket.CollaborateurId.Value);
        var email = dev?.Username?.Trim();
        // Username = e-mail de connexion ; on n'envoie que si c'est une adresse plausible.
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            _logger.LogWarning("Ticket #{Id} : pas d'e-mail valide pour le collaborateur #{Dev}, notification non envoyée.",
                ticket.Id, ticket.CollaborateurId);
            return;
        }

        var objet = $"[HelpDesk] Ticket #{ticket.Id} vous a été affecté — {ticket.Intitule}";
        var appUrl = (_config["App:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');
        var html = ConstruireHtmlAffectation(ticket, dev!, par, appUrl);

        // Destinataires : le responsable affecté + tous les admins (pour visibilité), sauf s'ils sont le responsable.
        var destinataires = await AvecAdminsAsync(email!);

        // Expéditeur dédié aux notifications, sinon la boîte partagée par défaut.
        var expediteur = _config["Outlook:ExpediteurNotifications"];
        var mail = string.IsNullOrWhiteSpace(expediteur) ? _mail : _mail.PourBoite(expediteur);

        try
        {
            await mail.EnvoyerHtmlAsync(destinataires, objet, html);
            _logger.LogWarning("MAIL AFFECTATION: ticket #{Id} -> {N} destinataire(s), responsable {Email} (de {Exp}).",
                ticket.Id, destinataires.Count, email, expediteur ?? "boîte partagée");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MAIL AFFECTATION: échec de l'envoi pour le ticket #{Id} à {Email} (de {Exp}).",
                ticket.Id, email, expediteur ?? "boîte partagée");
        }
    }

    /// <summary>Destinataire principal + tous les admins actifs (pour visibilité), sans doublon ni l'adresse principale.</summary>
    private async Task<List<string>> AvecAdminsAsync(string destinatairePrincipal)
    {
        var liste = new List<string> { destinatairePrincipal.Trim() };
        foreach (var a in await _userRepository.EmailsAdminsAsync())
            if (!string.IsNullOrWhiteSpace(a) && !string.Equals(a.Trim(), destinatairePrincipal.Trim(), StringComparison.OrdinalIgnoreCase))
                liste.Add(a.Trim());
        return liste.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// E-mail au client quand le ticket passe « En attente de validation » (une vraie demande d'action,
    /// pas une simple notification). Les admins sont en copie pour la visibilité.
    /// </summary>
    private async Task NotifierValidationClientParEmailAsync(TicketList ticket, UtilisateurContexte par)
    {
        var email = ticket.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            _logger.LogWarning("Ticket #{Id} : pas d'e-mail client valide, e-mail de validation non envoyé.", ticket.Id);
            return;
        }

        var objet = $"[HelpDesk] Ticket #{ticket.Id} — votre validation est attendue — {ticket.Intitule}";
        var appUrl = (_config["App:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');
        var html = ConstruireHtmlValidationClient(ticket, appUrl);
        var destinataires = await AvecAdminsAsync(email);

        var expediteur = _config["Outlook:ExpediteurNotifications"];
        var mail = string.IsNullOrWhiteSpace(expediteur) ? _mail : _mail.PourBoite(expediteur);
        try
        {
            await mail.EnvoyerHtmlAsync(destinataires, objet, html);
            _logger.LogWarning("MAIL VALIDATION: ticket #{Id} -> client {Email} (+ admins).", ticket.Id, email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MAIL VALIDATION: échec de l'envoi pour le ticket #{Id} à {Email}.", ticket.Id, email);
        }
    }

    /// <summary>Gabarit HTML (styles inline) de la demande de validation au client.</summary>
    private static string ConstruireHtmlValidationClient(TicketList t, string appUrl)
    {
        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var produit = t.Produit + (string.IsNullOrWhiteSpace(t.Module) ? "" : " / " + t.Module);
        var lien = $"{appUrl}/tickets/{t.Id}";
        var descBloc = string.IsNullOrWhiteSpace(t.Resolution) ? "" :
            "<div style=\"margin-top:16px;\"><div style=\"font-size:11.5px;font-weight:700;color:#8a94a4;text-transform:uppercase;letter-spacing:.5px;margin-bottom:6px;\">Solution proposée</div>" +
            $"<div style=\"background:#f7f9fc;border:1px solid #eef1f6;border-radius:8px;padding:12px 14px;font-size:13.5px;color:#3a4450;white-space:pre-wrap;line-height:1.5;\">{Enc(t.Resolution)}</div></div>";

        return
$@"<div style=""background:#eef2f7;padding:24px 12px;font-family:'Segoe UI',Arial,sans-serif;"">
  <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:600px;margin:0 auto;background:#ffffff;border-radius:14px;overflow:hidden;box-shadow:0 4px 18px rgba(12,40,80,.10);"">
    <tr><td style=""background:linear-gradient(135deg,#0b3a66,#124a7e);padding:22px 28px;"">
      <div style=""color:#bcd4ef;font-size:12px;letter-spacing:1.5px;font-weight:600;"">HELPDESK &middot; SUPPORT MEDIASOFT</div>
      <div style=""color:#ffffff;font-size:20px;font-weight:700;margin-top:6px;"">Votre validation est attendue</div>
    </td></tr>
    <tr><td style=""padding:26px 28px;"">
      <p style=""margin:0 0 18px;font-size:14px;color:#5a6472;line-height:1.5;"">Une solution a été proposée pour votre ticket. Merci de vous connecter pour la <b>valider</b> ou la <b>refuser</b>.</p>
      <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""border:1px solid #e3e8ef;border-radius:10px;"">
        <tr><td style=""padding:16px 18px;"">
          <span style=""display:inline-block;background:#eaf2fb;color:#1769c0;font-weight:700;font-size:12.5px;padding:4px 10px;border-radius:6px;"">Ticket #{t.Id}</span>
          <div style=""font-size:16px;font-weight:600;color:#1a2331;margin-top:12px;line-height:1.4;"">{Enc(t.Intitule)}</div>
          <div style=""font-size:13px;color:#5a6472;margin-top:8px;"">{Enc(produit)}{(string.IsNullOrWhiteSpace(t.Version) ? "" : " &middot; " + Enc(t.Version))}</div>
          {descBloc}
        </td></tr>
      </table>
      <p style=""margin:22px 0 0;text-align:center;"">
        <a href=""{lien}"" style=""background:#1769c0;color:#fff;text-decoration:none;padding:12px 24px;border-radius:8px;display:inline-block;font-weight:600;"">Valider ou refuser la solution</a>
      </p>
    </td></tr>
  </table>
</div>";
    }

    /// <summary>Gabarit HTML (styles inline, compatible e-mail) de la notification d'affectation.</summary>
    private static string ConstruireHtmlAffectation(TicketList t, Models.Authentication dev, UtilisateurContexte par, string appUrl)
    {
        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var etat = TicketEtat.Normaliser(t.Etat);

        var (impBg, impFg) = t.Importance switch
        {
            "Bloquant" => ("#fdecec", "#e5484d"),
            "Majeur" => ("#fff4e5", "#d98a00"),
            _ => ("#e9f9f1", "#10b981")
        };

        static string Ligne(string label, string? valeur) => string.IsNullOrWhiteSpace(valeur) ? "" :
            $"<tr><td style=\"padding:5px 0;color:#8a94a4;width:110px;font-size:13px;vertical-align:top;\">{Enc(label)}</td>" +
            $"<td style=\"padding:5px 0;color:#2a333f;font-size:13.5px;font-weight:600;\">{Enc(valeur)}</td></tr>";

        var produit = t.Produit + (string.IsNullOrWhiteSpace(t.Module) ? "" : " / " + t.Module);
        var descBloc = string.IsNullOrWhiteSpace(t.Description) ? "" :
            "<div style=\"margin-top:18px;\"><div style=\"font-size:11.5px;font-weight:700;color:#8a94a4;text-transform:uppercase;letter-spacing:.5px;margin-bottom:6px;\">Description</div>" +
            $"<div style=\"background:#f7f9fc;border:1px solid #eef1f6;border-radius:8px;padding:12px 14px;font-size:13.5px;color:#3a4450;white-space:pre-wrap;line-height:1.5;\">{Enc(t.Description)}</div></div>";

        return
$@"<div style=""background:#eef2f7;padding:24px 12px;font-family:'Segoe UI',Arial,sans-serif;"">
  <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:600px;margin:0 auto;background:#ffffff;border-radius:14px;overflow:hidden;box-shadow:0 4px 18px rgba(12,40,80,.10);"">
    <tr><td style=""background:#0b3a66;background:linear-gradient(135deg,#0b3a66,#124a7e);padding:22px 28px;"">
      <div style=""color:#bcd4ef;font-size:12px;letter-spacing:1.5px;font-weight:600;"">HELPDESK &middot; SUPPORT MEDIASOFT</div>
      <div style=""color:#ffffff;font-size:20px;font-weight:700;margin-top:6px;"">Nouveau ticket affecté</div>
    </td></tr>
    <tr><td style=""padding:26px 28px;"">
      <p style=""margin:0 0 6px;font-size:15px;color:#1a2331;"">Bonjour <b>{Enc(dev.NomComplet)}</b>,</p>
      <p style=""margin:0 0 20px;font-size:14px;color:#5a6472;line-height:1.5;"">Le ticket ci-dessous vous a été affecté par <b>{Enc(par.NomComplet)}</b>.</p>
      <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""border:1px solid #e3e8ef;border-radius:10px;"">
        <tr><td style=""padding:16px 18px;border-bottom:1px solid #eef1f6;"">
          <span style=""display:inline-block;background:#eaf2fb;color:#1769c0;font-weight:700;font-size:12.5px;padding:4px 10px;border-radius:6px;"">Ticket #{t.Id}</span>
          <span style=""display:inline-block;background:{impBg};color:{impFg};font-weight:700;font-size:12px;padding:4px 10px;border-radius:6px;margin-left:6px;"">{Enc(t.Importance)}</span>
          <div style=""font-size:16px;font-weight:600;color:#1a2331;margin-top:12px;line-height:1.4;"">{Enc(t.Intitule)}</div>
        </td></tr>
        <tr><td style=""padding:12px 18px;"">
          <table width=""100%"" cellpadding=""0"" cellspacing=""0"">
            {Ligne("Produit", produit)}
            {Ligne("Version", t.Version)}
            {Ligne("État", etat)}
            {Ligne("Client", t.Client?.Trim())}
          </table>
        </td></tr>
      </table>
      {descBloc}
      <div style=""text-align:center;margin-top:26px;"">
        <a href=""{appUrl}/tickets/{t.Id}"" style=""display:inline-block;background:#1769c0;color:#ffffff;text-decoration:none;font-weight:600;font-size:14px;padding:12px 28px;border-radius:8px;"">Ouvrir le ticket &rarr;</a>
      </div>
    </td></tr>
    <tr><td style=""padding:16px 28px;background:#f7f9fc;border-top:1px solid #eef1f6;text-align:center;"">
      <div style=""font-size:11.5px;color:#9aa4b2;line-height:1.5;"">E-mail automatique du HelpDesk Mediasoft — merci de ne pas répondre directement.</div>
    </td></tr>
  </table>
</div>";
    }

    private async Task ValiderContenuAsync(CreateTicketRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Intitule)) throw new MetierException("L'intitulé est requis.");
        if (string.IsNullOrWhiteSpace(r.Produit) || await _produitRepository.GetByNomAsync(r.Produit) == null)
            throw new MetierException("Le produit est requis et doit exister.");
        if (!TicketReferentiel.Importances.Contains(r.Importance))
            throw new MetierException("Importance invalide.");
        if (r.TypeTicket != null && !TicketReferentiel.Types.Contains(r.TypeTicket))
            throw new MetierException("Type de ticket invalide.");
        if (!await ModuleValideAsync(r.Module))
            throw new MetierException("Module invalide.");
    }

    /// <summary>Le module doit exister dans la liste gérée par l'admin (repli sur la liste par défaut si table vide).</summary>
    private async Task<bool> ModuleValideAsync(string? module)
    {
        if (string.IsNullOrWhiteSpace(module)) return true;
        var noms = await _moduleRepository.NomsAsync();
        return noms.Count == 0 ? TicketReferentiel.Modules.Contains(module) : noms.Contains(module);
    }

    private async Task EnvoyerAuxUtilisateursAsync(IEnumerable<int> ids, int saufId, string evenement, TicketDto dto)
    {
        var groupes = ids.Where(id => id != saufId).Select(id => $"user-{id}").ToList();
        if (groupes.Count > 0)
            await _hubContext.Clients.Groups(groupes).SendAsync(evenement, dto);
    }

    private async Task TracerAsync(TicketList ticket, string typeEvent, UtilisateurContexte par, string? commentaire = null)
    {
        var responsable = ticket.CollaborateurId.HasValue
            ? await _userRepository.GetByIdAsync(ticket.CollaborateurId.Value)
            : null;

        await _notificationRepository.AddAsync(new NotificationHisto
        {
            ParId = par.Id,
            Commentaire = commentaire,
            CollaborateurNom = responsable?.NomComplet is { Length: > 0 } nom ? nom : responsable?.Username,
            TicketId = ticket.Id,
            TypeEvent = typeEvent,
            Etat = TicketEtat.Normaliser(ticket.Etat),
            ClientId = ticket.ClientId,
            ClientNom = ticket.Client?.Trim(),
            ClientEmail = ticket.Email,
            CollaborateurId = ticket.CollaborateurId,
            Produit = ticket.Produit,
            ParNom = par.NomComplet,
            DateEvent = DateTime.Now
        });
    }

    private static TicketDto ToDto(TicketList t) => new()
    {
        Id = t.Id,
        Intitule = t.Intitule,
        Description = t.Description,
        Produit = t.Produit,
        Version = t.Version,
        Module = t.Module,
        TypeTicket = t.TypeTicket,
        Importance = t.Importance,
        Etat = TicketEtat.Normaliser(t.Etat),
        Email = t.Email,
        Client = t.Client?.Trim(),
        ClientId = t.ClientId,
        Collaborateur = t.Collaborateur,
        CollaborateurId = t.CollaborateurId,
        DateDemande = t.DateDemande2 ?? t.DateDemande?.ToDateTime(TimeOnly.MinValue),
        DateEstimee = t.DateEstimee2 ?? t.DateEstimee?.ToDateTime(TimeOnly.MinValue),
        DateEnattente = t.DateEnattente,
        Reponse = t.Reponse,
        Resolution = t.Resolution,
        FichierNom = string.IsNullOrWhiteSpace(t.FichierJoint1) ? null : t.FichierJoint,
        FichierType = string.IsNullOrWhiteSpace(t.FichierJoint1) ? null : TypeFichier(t.FichierJoint2),
        FichierTaille = TailleFichier(t.FichierJoint2)
    };

    // Fichier_joint2 = "type mime|taille en octets"
    private static string TypeFichier(string? meta)
    {
        var type = meta?.Split('|')[0];
        return string.IsNullOrWhiteSpace(type) ? "application/octet-stream" : type;
    }

    private static long? TailleFichier(string? meta)
    {
        var parts = meta?.Split('|');
        return parts?.Length > 1 && long.TryParse(parts[1], out var taille) ? taille : null;
    }
}
