using HelpDesk.API.Constants;
using HelpDesk.API.Data;
using HelpDesk.API.Models;
using HelpDesk.API.Services;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Repositories;

public class TicketFilter
{
    public string? Etat { get; set; }
    public string? Produit { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public interface ITicketRepository
{
    Task<(List<TicketList> items, int total)> SearchAsync(TicketFilter filter, UtilisateurContexte user);
    Task<TicketList?> GetByIdAsync(int id);
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
    /// - client : ses tickets (par compte, ou par email pour les tickets crees
    ///   avant son compte : WhatsApp, saisie par un collaborateur...).
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
            return query.Where(t => t.ClientId == user.Id ||
                                    (t.ClientId == null && t.Email != null && t.Email.Trim().ToLower() == user.Email.ToLower()));

        return query.Where(_ => false);
    }

    public async Task<(List<TicketList> items, int total)> SearchAsync(TicketFilter filter, UtilisateurContexte user)
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
        if (!string.IsNullOrEmpty(filter.Search))
            query = query.Where(t =>
                (t.Intitule != null && t.Intitule.Contains(filter.Search)) ||
                (t.Email != null && t.Email.ToLower().Contains(filter.Search.ToLower())) ||
                (t.Client != null && t.Client.Contains(filter.Search)));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(t => t.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        return (items, total);
    }

    public Task<TicketList?> GetByIdAsync(int id) =>
        _context.TicketList.FirstOrDefaultAsync(t => t.Id == id);

    public async Task<TicketList> AddAsync(TicketList ticket)
    {
        _context.TicketList.Add(ticket);
        await _context.SaveChangesAsync();
        return ticket;
    }

    public async Task DeleteAsync(TicketList ticket)
    {
        _context.TicketList.Remove(ticket);
        await _context.SaveChangesAsync();
    }

    public Task SaveChangesAsync() => _context.SaveChangesAsync();
}
