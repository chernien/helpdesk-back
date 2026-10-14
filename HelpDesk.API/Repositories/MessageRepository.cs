using HelpDesk.API.Constants;
using HelpDesk.API.Data;
using HelpDesk.API.DTOs;
using HelpDesk.API.Models;
using HelpDesk.API.Services;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Repositories;

public interface IMessageRepository
{
    /// <summary>Le ticket s'il est visible par l'utilisateur (sinon null), pour vérifier l'accès.</summary>
    Task<TicketList?> TicketVisibleAsync(int ticketId, UtilisateurContexte user);
    Task<List<Message>> FilTicketAsync(int ticketId);
    /// <summary>Message par son id (suivi/tracked, pour suppression).</summary>
    Task<Message?> GetByIdAsync(int messageId);
    Task SupprimerAsync(Message message);
    /// <summary>Fil d'un ticket filtré par la visibilité, en une seule requête (vide si non visible).</summary>
    Task<List<Message>> FilTicketVisibleAsync(int ticketId, UtilisateurContexte user);
    Task<Message> AjouterAsync(Message message);
    /// <summary>Upsert du repère de lecture individuel (UserId, TicketId).</summary>
    Task MarquerLuAsync(int ticketId, int userId, DateTime date);
    /// <summary>Conversations avec messages non lus pour cet utilisateur (badge Messenger).</summary>
    Task<MessagesApercuDto> ApercuNonLusAsync(UtilisateurContexte user);
    /// <summary>Message (avec sa pièce jointe) si son ticket est visible — pour le téléchargement.</summary>
    Task<Message?> MessageVisiblePourFichierAsync(int messageId, UtilisateurContexte user);
}

public class MessageRepository : IMessageRepository
{
    private readonly HelpDeskContext _context;

    public MessageRepository(HelpDeskContext context)
    {
        _context = context;
    }

    public Task<TicketList?> TicketVisibleAsync(int ticketId, UtilisateurContexte user) =>
        TicketRepository.AppliquerVisibilite(_context.TicketList.AsNoTracking(), user)
            .FirstOrDefaultAsync(t => t.Id == ticketId);

    public Task<List<Message>> FilTicketAsync(int ticketId) =>
        _context.Message.AsNoTracking()
            .Where(m => m.TicketId == ticketId)
            .OrderBy(m => m.DateEnvoi).ThenBy(m => m.Id)
            .ToListAsync();

    public Task<List<Message>> FilTicketVisibleAsync(int ticketId, UtilisateurContexte user)
    {
        // Une seule requête : messages du ticket ET ticket visible (EXISTS). Vide si hors périmètre.
        var visibles = TicketRepository.AppliquerVisibilite(_context.TicketList.AsNoTracking(), user);
        return _context.Message.AsNoTracking()
            .Where(m => m.TicketId == ticketId && visibles.Any(t => t.Id == ticketId))
            .OrderBy(m => m.DateEnvoi).ThenBy(m => m.Id)
            .ToListAsync();
    }

    public async Task<Message> AjouterAsync(Message message)
    {
        _context.Message.Add(message);
        await _context.SaveChangesAsync();
        return message;
    }

    public Task<Message?> GetByIdAsync(int messageId) =>
        _context.Message.FirstOrDefaultAsync(m => m.Id == messageId);

    public async Task SupprimerAsync(Message message)
    {
        _context.Message.Remove(message);
        await _context.SaveChangesAsync();
    }

    public Task<Message?> MessageVisiblePourFichierAsync(int messageId, UtilisateurContexte user)
    {
        var visibles = TicketRepository.AppliquerVisibilite(_context.TicketList.AsNoTracking(), user);
        return _context.Message.AsNoTracking()
            .Where(m => m.Id == messageId && visibles.Any(t => t.Id == m.TicketId))
            .FirstOrDefaultAsync();
    }

    public async Task MarquerLuAsync(int ticketId, int userId, DateTime date)
    {
        var repere = await _context.MessageLu.FirstOrDefaultAsync(l => l.UserId == userId && l.TicketId == ticketId);
        if (repere == null)
            _context.MessageLu.Add(new MessageLu { UserId = userId, TicketId = ticketId, DerniereLectureLe = date });
        else if (date > repere.DerniereLectureLe)
            repere.DerniereLectureLe = date;
        else
            return;
        await _context.SaveChangesAsync();
    }

    public async Task<MessagesApercuDto> ApercuNonLusAsync(UtilisateurContexte user)
    {
        var visibles = TicketRepository.AppliquerVisibilite(_context.TicketList.AsNoTracking(), user);

        // Messages des tickets visibles (champs utiles seulement).
        var stubs = await (from m in _context.Message.AsNoTracking()
                           join t in visibles on m.TicketId equals t.Id
                           select new
                           {
                               m.TicketId,
                               t.Intitule,
                               m.AuteurRole,
                               m.AuteurNom,
                               m.Contenu,
                               m.DateEnvoi
                           })
                          .ToListAsync();

        var lus = await _context.MessageLu.AsNoTracking()
            .Where(l => l.UserId == user.Id)
            .ToDictionaryAsync(l => l.TicketId, l => l.DerniereLectureLe);

        var estClient = user.EstClient;
        var conversations = new List<ConversationApercuDto>();

        foreach (var groupe in stubs.GroupBy(s => s.TicketId))
        {
            var maLecture = lus.TryGetValue(groupe.Key, out var d) ? d : DateTime.MinValue;

            // Camp adverse : le client voit les messages de l'equipe, l'equipe voit ceux du client.
            var adverses = groupe.Where(m => EstDuClient(m.AuteurRole) != estClient);

            DateTime seuil = maLecture;
            if (!estClient)
            {
                // Cote equipe : repondre (message d'equipe plus recent) vaut lu pour tous.
                var derniereReponseEquipe = groupe
                    .Where(m => !EstDuClient(m.AuteurRole))
                    .Select(m => (DateTime?)m.DateEnvoi)
                    .DefaultIfEmpty(null)
                    .Max();
                if (derniereReponseEquipe.HasValue && derniereReponseEquipe.Value > seuil)
                    seuil = derniereReponseEquipe.Value;
            }

            // Toutes les conversations sont listées ; NonLus = 0 est autorisé (juste non mis en évidence).
            var nonLus = adverses.Count(m => m.DateEnvoi > seuil);

            var dernier = groupe.OrderByDescending(m => m.DateEnvoi).First();
            conversations.Add(new ConversationApercuDto
            {
                TicketId = groupe.Key,
                Intitule = groupe.First().Intitule,
                DernierMessage = dernier.Contenu,
                DernierAuteur = dernier.AuteurNom,
                DerniereDate = dernier.DateEnvoi,
                NonLus = nonLus
            });
        }

        // Les plus récentes d'abord ; on borne la liste du menu déroulant.
        conversations = conversations.OrderByDescending(c => c.DerniereDate).Take(30).ToList();
        return new MessagesApercuDto
        {
            // Le badge Messenger ne compte que les messages non lus.
            Total = conversations.Sum(c => c.NonLus),
            Conversations = conversations
        };
    }

    private static bool EstDuClient(string? role) => role == Roles.Client;
}
