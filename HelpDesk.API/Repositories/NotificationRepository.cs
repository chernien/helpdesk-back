using HelpDesk.API.Data;
using HelpDesk.API.Models;
using HelpDesk.API.Services;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Repositories;

public class NotificationFilter
{
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 15;
}

public interface INotificationRepository
{
    /// <summary>Historique complet du perimetre (page Historique).</summary>
    Task<(List<NotificationHisto> items, int total)> SearchAsync(NotificationFilter filter, UtilisateurContexte user);
    /// <summary>Notifications de la cloche : perimetre, hors actions de l'utilisateur lui-meme.</summary>
    Task<List<NotificationHisto>> DernieresAsync(UtilisateurContexte user, int max);
    Task<int> CompterNonLuesAsync(UtilisateurContexte user, DateTime? luesLe);
    Task<DateTime?> LuesLeAsync(int userId);
    Task MarquerLuesAsync(int userId, DateTime date);
    /// <summary>Fil chronologique d'un ticket (acces deja verifie par l'appelant).</summary>
    Task<List<NotificationHisto>> SuiviTicketAsync(int ticketId);
    Task AddAsync(NotificationHisto notification);
    /// <summary>Dernier evenement de ce ticket ecrit par cet utilisateur (pour y joindre une capture).</summary>
    Task<NotificationHisto?> DernierEvenementDeAsync(int ticketId, int parId);
    Task<NotificationHisto?> GetEvenementAsync(int ticketId, int evenementId);
    /// <summary>Noms de stockage des captures d'un ticket (nettoyage a la suppression).</summary>
    Task<List<string>> FichiersStockageDuTicketAsync(int ticketId);
    Task SaveChangesAsync();
}

public class NotificationRepository : INotificationRepository
{
    private readonly HelpDeskContext _context;

    public NotificationRepository(HelpDeskContext context)
    {
        _context = context;
    }

    /// <summary>Meme perimetre que les tickets (voir TicketRepository.AppliquerVisibilite).</summary>
    private IQueryable<NotificationHisto> Perimetre(UtilisateurContexte user)
    {
        var query = _context.NotificationHisto.AsNoTracking();

        if (user.EstAdmin) return query;

        if (user.EstCollaborateur)
        {
            var produits = user.Produits.ToList();
            return query.Where(n => n.CollaborateurId == user.Id ||
                                    (n.Produit != null && produits.Contains(n.Produit)));
        }

        // Un developpeur suit les evenements des tickets dont il est responsable.
        if (user.EstDev)
            return query.Where(n => n.CollaborateurId == user.Id);

        if (user.EstClient)
            return query.Where(n => n.ClientId == user.Id ||
                                    (n.ClientId == null && n.ClientEmail != null && n.ClientEmail.Trim().ToLower() == user.Email.ToLower()));

        return query.Where(_ => false);
    }

    private IQueryable<NotificationHisto> PerimetreCloche(UtilisateurContexte user) =>
        Perimetre(user).Where(n => n.ParId == null || n.ParId != user.Id);

    public async Task<(List<NotificationHisto> items, int total)> SearchAsync(
        NotificationFilter filter, UtilisateurContexte user)
    {
        var query = Perimetre(user);

        if (!string.IsNullOrEmpty(filter.Search))
            query = query.Where(n =>
                (n.ClientNom != null && n.ClientNom.Contains(filter.Search)) ||
                (n.ClientEmail != null && n.ClientEmail.ToLower().Contains(filter.Search.ToLower())) ||
                (n.ParNom != null && n.ParNom.Contains(filter.Search)) ||
                (n.Etat != null && n.Etat.Contains(filter.Search)) ||
                n.TicketId.ToString() == filter.Search);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(n => n.DateEvent)
            .ThenByDescending(n => n.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        return (items, total);
    }

    public Task<List<NotificationHisto>> DernieresAsync(UtilisateurContexte user, int max) =>
        PerimetreCloche(user)
            .OrderByDescending(n => n.DateEvent)
            .ThenByDescending(n => n.Id)
            .Take(max)
            .ToListAsync();

    public Task<int> CompterNonLuesAsync(UtilisateurContexte user, DateTime? luesLe)
    {
        var query = PerimetreCloche(user);
        if (luesLe.HasValue)
            query = query.Where(n => n.DateEvent > luesLe.Value);
        return query.CountAsync();
    }

    public Task<DateTime?> LuesLeAsync(int userId) =>
        _context.Authentication.Where(u => u.Id == userId)
            .Select(u => u.NotificationsLuesLe)
            .FirstOrDefaultAsync();

    public Task MarquerLuesAsync(int userId, DateTime date) =>
        _context.Authentication.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.NotificationsLuesLe, date));

    public Task<List<NotificationHisto>> SuiviTicketAsync(int ticketId) =>
        _context.NotificationHisto.AsNoTracking()
            .Where(n => n.TicketId == ticketId)
            .OrderBy(n => n.DateEvent).ThenBy(n => n.Id)
            .ToListAsync();

    public async Task AddAsync(NotificationHisto notification)
    {
        _context.NotificationHisto.Add(notification);
        await _context.SaveChangesAsync();
    }

    public Task<NotificationHisto?> DernierEvenementDeAsync(int ticketId, int parId) =>
        _context.NotificationHisto
            .Where(n => n.TicketId == ticketId && n.ParId == parId)
            .OrderByDescending(n => n.DateEvent).ThenByDescending(n => n.Id)
            .FirstOrDefaultAsync();

    public Task<NotificationHisto?> GetEvenementAsync(int ticketId, int evenementId) =>
        _context.NotificationHisto.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == evenementId && n.TicketId == ticketId);

    public Task<List<string>> FichiersStockageDuTicketAsync(int ticketId) =>
        _context.NotificationHisto.AsNoTracking()
            .Where(n => n.TicketId == ticketId && n.FichierStockage != null)
            .Select(n => n.FichierStockage!)
            .ToListAsync();

    public Task SaveChangesAsync() => _context.SaveChangesAsync();
}
