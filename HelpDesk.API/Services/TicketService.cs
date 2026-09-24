using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Exceptions;
using HelpDesk.API.Hubs;
using HelpDesk.API.Models;
using HelpDesk.API.Repositories;
using Microsoft.AspNetCore.SignalR;

namespace HelpDesk.API.Services;

public interface ITicketService
{
    Task<TicketPageDto> GetTicketsAsync(UtilisateurContexte user, TicketFilter filter);
    Task<TicketDto> GetByIdAsync(int id, UtilisateurContexte user);
    Task<TicketDto> CreateAsync(CreateTicketRequest request, UtilisateurContexte user);
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

    public TicketService(
        ITicketRepository ticketRepository,
        IUserRepository userRepository,
        IProduitRepository produitRepository,
        INotificationRepository notificationRepository,
        IClientService clientService,
        IFichierStockage fichierStockage,
        IHubContext<NotificationHub> hubContext)
    {
        _ticketRepository = ticketRepository;
        _userRepository = userRepository;
        _produitRepository = produitRepository;
        _notificationRepository = notificationRepository;
        _clientService = clientService;
        _fichierStockage = fichierStockage;
        _hubContext = hubContext;
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

        var now = DateTime.Now;
        var ticket = new TicketList
        {
            Intitule = request.Intitule.Trim(),
            Description = request.Description,
            Produit = request.Produit,
            Version = request.Version,
            Module = request.Module,
            TypeTicket = request.TypeTicket,
            Importance = request.Importance,
            Etat = TicketEtat.Initial,
            DateDemande = DateOnly.FromDateTime(now),
            DateDemande2 = now,
            DateEstimee = request.DateEstimee.HasValue ? DateOnly.FromDateTime(request.DateEstimee.Value) : null,
            DateEstimee2 = request.DateEstimee
            // Pas de collaborateur a la creation : le ticket part dans la file du produit.
        };

        if (user.EstClient)
        {
            // Un client cree toujours pour lui-meme.
            var compte = await _userRepository.GetByIdAsync(user.Id)
                ?? throw MetierException.Introuvable("Compte introuvable.");
            ticket.ClientId = compte.Id;
            ticket.Email = compte.Username;
            ticket.Client = !string.IsNullOrWhiteSpace(compte.SocieteNom) ? compte.SocieteNom : compte.NomComplet;
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

        return dto;
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
        if (request.Module != null && !TicketReferentiel.Modules.Contains(request.Module))
            throw new MetierException("Module invalide.");
        if (request.Importance != null && !TicketReferentiel.Importances.Contains(request.Importance))
            throw new MetierException("Importance invalide.");

        Appliquer("Intitulé", request.Intitule?.Trim(), ticket.Intitule, v => ticket.Intitule = v);
        Appliquer("Description", request.Description, ticket.Description, v => ticket.Description = v);
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
        }

        // Le client est prevenu de toute evolution, qu'il soit rattache par compte ou par email.
        var compteClientId = ticket.ClientId
            ?? (ticket.Email != null ? (await _userRepository.GetByEmailAsync(ticket.Email))?.Id : null);
        if (compteClientId.HasValue)
            await _hubContext.Clients.Group($"user-{compteClientId}").SendAsync(EvtMisAJour, dto);

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

        if (!string.IsNullOrWhiteSpace(ticket.FichierJoint1))
            throw new MetierException("Un fichier est déjà joint à ce ticket.");
        if (fichier.Length == 0)
            throw new MetierException("Le fichier est vide.");
        if (fichier.Length > TailleMaxFichier)
            throw new MetierException("Le fichier ne doit pas dépasser 10 Mo.");

        var nomOrigine = Path.GetFileName(fichier.FileName);
        if (string.IsNullOrWhiteSpace(nomOrigine)) nomOrigine = "piece-jointe";
        if (nomOrigine.Length > 255) nomOrigine = nomOrigine[^255..];

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
                                (ticket.ClientId == null && string.Equals(ticket.Email?.Trim(), user.Email, StringComparison.OrdinalIgnoreCase)))));

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

    private async Task ValiderContenuAsync(CreateTicketRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Intitule)) throw new MetierException("L'intitulé est requis.");
        if (string.IsNullOrWhiteSpace(r.Produit) || await _produitRepository.GetByNomAsync(r.Produit) == null)
            throw new MetierException("Le produit est requis et doit exister.");
        if (!TicketReferentiel.Importances.Contains(r.Importance))
            throw new MetierException("Importance invalide.");
        if (r.TypeTicket != null && !TicketReferentiel.Types.Contains(r.TypeTicket))
            throw new MetierException("Type de ticket invalide.");
        if (r.Module != null && !TicketReferentiel.Modules.Contains(r.Module))
            throw new MetierException("Module invalide.");
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
