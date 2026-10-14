using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Exceptions;
using HelpDesk.API.Hubs;
using HelpDesk.API.Models;
using HelpDesk.API.Repositories;
using Microsoft.AspNetCore.SignalR;

namespace HelpDesk.API.Services;

public interface IMessageService
{
    Task<List<MessageDto>> FilAsync(int ticketId, UtilisateurContexte user);
    Task<MessageDto> EnvoyerAsync(int ticketId, string contenu, UtilisateurContexte user);
    Task<MessageDto> EnvoyerAvecFichierAsync(int ticketId, string? contenu, IFormFile fichier, UtilisateurContexte user);
    Task<(Stream Flux, string Nom, string Type)> TelechargerFichierAsync(int messageId, UtilisateurContexte user);
    Task SupprimerAsync(int messageId, UtilisateurContexte user);
    Task MarquerLuAsync(int ticketId, UtilisateurContexte user);
    Task<MessagesApercuDto> ApercuAsync(UtilisateurContexte user);
}

/// <summary>Discussion (chat) temps réel d'un ticket, entre le client et l'équipe.</summary>
public class MessageService : IMessageService
{
    /// <summary>Événement SignalR d'un nouveau message (même nom côté Angular).</summary>
    private const string EvtMessage = "messageRecu";
    /// <summary>Événement SignalR de suppression d'un message (même nom côté Angular).</summary>
    private const string EvtMessageSupprime = "messageSupprime";
    private const int LongueurMax = 4000;
    private const long TailleMaxFichier = 10 * 1024 * 1024;

    private readonly IMessageRepository _messageRepository;
    private readonly IUserRepository _userRepository;
    private readonly IFichierStockage _fichierStockage;
    private readonly IHubContext<NotificationHub> _hubContext;

    public MessageService(
        IMessageRepository messageRepository,
        IUserRepository userRepository,
        IFichierStockage fichierStockage,
        IHubContext<NotificationHub> hubContext)
    {
        _messageRepository = messageRepository;
        _userRepository = userRepository;
        _fichierStockage = fichierStockage;
        _hubContext = hubContext;
    }

    public async Task<List<MessageDto>> FilAsync(int ticketId, UtilisateurContexte user)
    {
        // Visibilité + fil en une seule requête (la base est distante : un aller-retour de moins).
        var messages = await _messageRepository.FilTicketVisibleAsync(ticketId, user);
        return messages.Select(m => EnDto(m, user)).ToList();
    }

    public async Task<MessageDto> EnvoyerAsync(int ticketId, string contenu, UtilisateurContexte user)
    {
        var ticket = await TicketVisibleOuErreurAsync(ticketId, user);

        var texte = (contenu ?? "").Trim();
        if (texte.Length == 0) throw new MetierException("Le message est vide.");
        if (texte.Length > LongueurMax) throw new MetierException($"Le message ne doit pas dépasser {LongueurMax} caractères.");

        var maintenant = DateTime.Now;
        var message = await _messageRepository.AjouterAsync(new Message
        {
            TicketId = ticketId,
            AuteurId = user.Id,
            AuteurNom = user.NomComplet is { Length: > 0 } nom ? nom : user.Email,
            AuteurRole = user.Role,
            Contenu = texte,
            DateEnvoi = maintenant
        });

        // L'auteur a « lu » sa propre conversation.
        await _messageRepository.MarquerLuAsync(ticketId, user.Id, maintenant);

        // Temps réel : on prévient les participants (le front ignore ses propres messages).
        var charge = new
        {
            ticketId,
            auteurId = user.Id,
            auteurNom = message.AuteurNom,
            duClient = user.EstClient,
            societe = ticket.Client?.Trim(),
            apercu = texte.Length > 140 ? texte[..140] + "…" : texte,
            date = maintenant
        };
        await NotifierParticipantsAsync(ticket, user.Id, charge);

        return EnDto(message, user);
    }

    public async Task<MessageDto> EnvoyerAvecFichierAsync(int ticketId, string? contenu, IFormFile fichier, UtilisateurContexte user)
    {
        var ticket = await TicketVisibleOuErreurAsync(ticketId, user);

        var texte = (contenu ?? "").Trim();
        if (texte.Length > LongueurMax) throw new MetierException($"Le message ne doit pas dépasser {LongueurMax} caractères.");
        if (fichier == null || fichier.Length == 0) throw new MetierException("Le fichier est vide.");
        if (fichier.Length > TailleMaxFichier) throw new MetierException("Le fichier ne doit pas dépasser 10 Mo.");

        var nomOrigine = Path.GetFileName(fichier.FileName);
        if (string.IsNullOrWhiteSpace(nomOrigine)) nomOrigine = "piece-jointe";
        var stockage = await _fichierStockage.EnregistrerAsync(fichier);

        var maintenant = DateTime.Now;
        var message = await _messageRepository.AjouterAsync(new Message
        {
            TicketId = ticketId,
            AuteurId = user.Id,
            AuteurNom = user.NomComplet is { Length: > 0 } nom ? nom : user.Email,
            AuteurRole = user.Role,
            Contenu = texte,
            DateEnvoi = maintenant,
            FichierNom = nomOrigine,
            FichierStockage = stockage,
            FichierType = fichier.ContentType,
            FichierTaille = fichier.Length
        });

        await _messageRepository.MarquerLuAsync(ticketId, user.Id, maintenant);
        await NotifierParticipantsAsync(ticket, user.Id, new
        {
            ticketId,
            auteurId = user.Id,
            auteurNom = message.AuteurNom,
            duClient = user.EstClient,
            societe = ticket.Client?.Trim(),
            apercu = texte.Length > 0 ? texte : $"📎 {nomOrigine}",
            date = maintenant
        });

        return EnDto(message, user);
    }

    public async Task<(Stream Flux, string Nom, string Type)> TelechargerFichierAsync(int messageId, UtilisateurContexte user)
    {
        var message = await _messageRepository.MessageVisiblePourFichierAsync(messageId, user)
            ?? throw MetierException.Introuvable("Message introuvable.");
        if (string.IsNullOrWhiteSpace(message.FichierStockage))
            throw MetierException.Introuvable("Ce message n'a pas de pièce jointe.");

        var flux = _fichierStockage.Ouvrir(message.FichierStockage)
            ?? throw MetierException.Introuvable("Le fichier n'est plus disponible.");
        return (flux, message.FichierNom ?? "piece-jointe", message.FichierType ?? "application/octet-stream");
    }

    public async Task SupprimerAsync(int messageId, UtilisateurContexte user)
    {
        var message = await _messageRepository.GetByIdAsync(messageId)
            ?? throw MetierException.Introuvable("Message introuvable.");

        // Le ticket doit être dans le périmètre de l'utilisateur.
        var ticket = await _messageRepository.TicketVisibleAsync(message.TicketId, user)
            ?? throw MetierException.Introuvable("Message introuvable.");

        // Seul l'auteur du message, ou un administrateur, peut le supprimer.
        if (!user.EstAdmin && message.AuteurId != user.Id)
            throw MetierException.Interdit("Vous ne pouvez supprimer que vos propres messages.");

        var fichier = message.FichierStockage;
        await _messageRepository.SupprimerAsync(message);
        _fichierStockage.Supprimer(fichier);

        // Prévenir les participants pour retirer la bulle en temps réel.
        await NotifierParticipantsAsync(ticket, user.Id,
            new { ticketId = message.TicketId, messageId }, EvtMessageSupprime);
    }

    public async Task MarquerLuAsync(int ticketId, UtilisateurContexte user)
    {
        await TicketVisibleOuErreurAsync(ticketId, user);
        await _messageRepository.MarquerLuAsync(ticketId, user.Id, DateTime.Now);
    }

    public Task<MessagesApercuDto> ApercuAsync(UtilisateurContexte user) =>
        _messageRepository.ApercuNonLusAsync(user);

    // ─── Interne ──────────────────────────────────────────────────────────────

    private async Task<TicketList> TicketVisibleOuErreurAsync(int ticketId, UtilisateurContexte user)
    {
        // 404 (et non 403) : on ne révèle pas l'existence d'un ticket hors périmètre.
        return await _messageRepository.TicketVisibleAsync(ticketId, user)
            ?? throw MetierException.Introuvable("Ticket introuvable.");
    }

    /// <summary>Pousse l'événement aux groupes SignalR des participants (hors auteur) + admins.</summary>
    private async Task NotifierParticipantsAsync(TicketList ticket, int auteurId, object charge, string evenement = EvtMessage)
    {
        var destinataires = new HashSet<int>();
        if (ticket.ClientId.HasValue) destinataires.Add(ticket.ClientId.Value);
        if (ticket.CollaborateurId.HasValue) destinataires.Add(ticket.CollaborateurId.Value);
        if (!string.IsNullOrWhiteSpace(ticket.Produit))
            foreach (var id in await _userRepository.IdsUsersDuProduitAsync(ticket.Produit))
                destinataires.Add(id);

        destinataires.Remove(auteurId);

        var envois = new List<Task>();
        var groupes = destinataires.Select(id => $"user-{id}").ToList();
        if (groupes.Count > 0)
            envois.Add(_hubContext.Clients.Groups(groupes).SendAsync(evenement, charge));
        // Les admins suivent tout (le front ignore le message si l'admin en est l'auteur).
        envois.Add(_hubContext.Clients.Group("admins").SendAsync(evenement, charge));
        await Task.WhenAll(envois);
    }

    private static MessageDto EnDto(Message m, UtilisateurContexte user) => new()
    {
        Id = m.Id,
        TicketId = m.TicketId,
        AuteurId = m.AuteurId,
        AuteurNom = m.AuteurNom,
        AuteurRole = m.AuteurRole,
        DeMoi = m.AuteurId == user.Id,
        DuClient = m.AuteurRole == Roles.Client,
        Contenu = m.Contenu,
        DateEnvoi = m.DateEnvoi,
        FichierNom = m.FichierNom,
        FichierType = m.FichierType,
        FichierTaille = m.FichierTaille
    };
}
