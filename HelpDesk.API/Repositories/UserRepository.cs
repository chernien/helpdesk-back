using HelpDesk.API.Constants;
using HelpDesk.API.Data;
using HelpDesk.API.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Repositories;

public interface IUserRepository
{
    Task<Authentication?> GetByIdAsync(int id);
    /// <summary>Compte avec ses affectations produits (suivi des modifications).</summary>
    Task<Authentication?> GetAvecProduitsAsync(int id);
    Task<Authentication?> GetByUsernameAsync(string username);
    Task<bool> EmailUtiliseAsync(string email, int? saufId = null);
    /// <summary>Compte dont l'email correspond, sans tenir compte de la casse (T2 contient des majuscules).</summary>
    Task<Authentication?> GetByEmailAsync(string email);
    /// <summary>Parmi ces emails, ceux qui ont deja un compte (renvoyes en minuscules).</summary>
    Task<HashSet<string>> EmailsAvecCompteAsync(IEnumerable<string> emails);
    /// <summary>Rattache au compte les tickets et notifications ouverts avec son email avant sa creation.</summary>
    Task<int> RattacherTicketsAsync(int clientId, string email);
    Task<(List<Authentication> items, int total)> RechercherAsync(string? role, string? recherche, int page, int pageSize);
    Task<List<Authentication>> CollaborateursDuProduitAsync(string produitNom);
    Task<List<int>> IdsCollaborateursDuProduitAsync(string produitNom);
    /// <summary>Collaborateurs ROLE_USER uniquement (file du produit, testeurs internes).</summary>
    Task<List<int>> IdsUsersDuProduitAsync(string produitNom);
    Task<List<Authentication>> GetByIdsAsync(IEnumerable<int> ids);
    Task<int> CompterAdminsActifsAsync(IEnumerable<int> sauf);
    /// <summary>Supprime les comptes et detache leurs tickets, en une transaction.</summary>
    Task<ResultatSuppression> SupprimerAsync(IReadOnlyCollection<int> ids);
    Task<Authentication> AddAsync(Authentication user);
    Task SaveChangesAsync();
}

public record ResultatSuppression(int ComptesSupprimes, int TicketsRemisEnFile);

public class UserRepository : IUserRepository
{
    private readonly HelpDeskContext _context;

    public UserRepository(HelpDeskContext context)
    {
        _context = context;
    }

    public Task<Authentication?> GetByIdAsync(int id) =>
        _context.Authentication.FirstOrDefaultAsync(u => u.Id == id);

    public Task<Authentication?> GetAvecProduitsAsync(int id) =>
        _context.Authentication
            .Include(u => u.Affectations).ThenInclude(a => a.Product)
            .FirstOrDefaultAsync(u => u.Id == id);

    // L'email est l'identifiant : AMINE@MEDIASOFT.TN et amine@mediasoft.tn sont le meme compte.
    public Task<Authentication?> GetByUsernameAsync(string username) => GetByEmailAsync(username);

    public Task<Authentication?> GetByEmailAsync(string email)
    {
        var cible = email.Trim().ToLower();
        return _context.Authentication.FirstOrDefaultAsync(u =>
            u.Username != null && u.Username.Trim().ToLower() == cible);
    }

    public async Task<HashSet<string>> EmailsAvecCompteAsync(IEnumerable<string> emails)
    {
        var cibles = emails.Select(e => e.Trim().ToLower()).Distinct().ToList();
        if (cibles.Count == 0) return new HashSet<string>();
        var trouves = await _context.Authentication.AsNoTracking()
            .Where(u => u.Username != null && cibles.Contains(u.Username.Trim().ToLower()))
            .Select(u => u.Username!.Trim().ToLower())
            .ToListAsync();
        return trouves.ToHashSet();
    }

    public async Task<int> RattacherTicketsAsync(int clientId, string email)
    {
        var cible = email.Trim().ToLower();
        var tickets = await _context.TicketList
            .Where(t => t.ClientId == null && t.Email != null && t.Email.Trim().ToLower() == cible)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientId, clientId));
        await _context.NotificationHisto
            .Where(n => n.ClientId == null && n.ClientEmail != null && n.ClientEmail.Trim().ToLower() == cible)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ClientId, clientId));
        return tickets;
    }

    public Task<bool> EmailUtiliseAsync(string email, int? saufId = null)
    {
        // Collation binaire en base : comparaison explicite en minuscules.
        var cible = email.Trim().ToLower();
        return _context.Authentication.AnyAsync(u =>
            u.Username != null && u.Username.Trim().ToLower() == cible &&
            (saufId == null || u.Id != saufId));
    }

    public async Task<(List<Authentication> items, int total)> RechercherAsync(
        string? role, string? recherche, int page, int pageSize)
    {
        var query = _context.Authentication.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(role))
            query = query.Where(u => u.Role == role);

        if (!string.IsNullOrWhiteSpace(recherche))
        {
            var motif = recherche.Trim().ToLower();
            query = query.Where(u =>
                (u.Username != null && u.Username.ToLower().Contains(motif)) ||
                (u.Nom != null && u.Nom.ToLower().Contains(motif)) ||
                (u.Prenom != null && u.Prenom.ToLower().Contains(motif)) ||
                (u.SocieteNom != null && u.SocieteNom.ToLower().Contains(motif)));
        }

        var total = await query.CountAsync();
        var items = await query
            .Include(u => u.Affectations).ThenInclude(a => a.Product)
            .OrderByDescending(u => u.Enabled)
            // Comptes historiques sans nom en fin de liste
            .ThenBy(u => u.Nom == null || u.Nom.Trim() == "")
            .ThenBy(u => u.Nom)
            .ThenBy(u => u.Prenom)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, total);
    }

    // L'equipe d'un produit = collaborateurs (ROLE_USER) + developpeurs (ROLE_DEV).
    public Task<List<Authentication>> CollaborateursDuProduitAsync(string produitNom) =>
        _context.Authentication.AsNoTracking()
            .Where(u => Roles.AvecProduits.Contains(u.Role!) && u.Enabled != false &&
                        u.Affectations.Any(a => a.Product.Nom == produitNom))
            .OrderBy(u => u.Nom).ThenBy(u => u.Prenom)
            .ToListAsync();

    public Task<List<int>> IdsCollaborateursDuProduitAsync(string produitNom) =>
        _context.Authentication.AsNoTracking()
            .Where(u => Roles.AvecProduits.Contains(u.Role!) && u.Enabled != false &&
                        u.Affectations.Any(a => a.Product.Nom == produitNom))
            .Select(u => u.Id)
            .ToListAsync();

    public Task<List<int>> IdsUsersDuProduitAsync(string produitNom) =>
        _context.Authentication.AsNoTracking()
            .Where(u => u.Role == Roles.User && u.Enabled != false &&
                        u.Affectations.Any(a => a.Product.Nom == produitNom))
            .Select(u => u.Id)
            .ToListAsync();

    public Task<List<Authentication>> GetByIdsAsync(IEnumerable<int> ids)
    {
        var liste = ids.ToList();
        return _context.Authentication.AsNoTracking().Where(u => liste.Contains(u.Id)).ToListAsync();
    }

    public Task<int> CompterAdminsActifsAsync(IEnumerable<int> sauf)
    {
        var exclus = sauf.ToList();
        return _context.Authentication.CountAsync(u =>
            u.Role == Roles.Admin && u.Enabled != false && !exclus.Contains(u.Id));
    }

    public async Task<ResultatSuppression> SupprimerAsync(IReadOnlyCollection<int> ids)
    {
        var liste = ids.ToList();
        await using var transaction = await _context.Database.BeginTransactionAsync();

        // Tickets non clotures d'un collaborateur supprime : retour dans la file du produit.
        var remisEnFile = await _context.TicketList
            .Where(t => t.CollaborateurId != null && liste.Contains(t.CollaborateurId.Value) &&
                        (t.Etat == null || t.Etat != TicketEtat.Cloture))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CollaborateurId, (int?)null)
                .SetProperty(t => t.Collaborateur, (string?)null));

        // Tickets clotures : on garde le nom du collaborateur (texte) pour l'historique.
        await _context.TicketList
            .Where(t => t.CollaborateurId != null && liste.Contains(t.CollaborateurId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CollaborateurId, (int?)null));

        // Tickets d'un client supprime : ils restent rattaches a son email,
        // et reapparaitront s'il recoit un nouveau compte avec ce meme email.
        await _context.TicketList
            .Where(t => t.ClientId != null && liste.Contains(t.ClientId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientId, (int?)null));

        // Historique : les noms et emails sont stockes en clair, seuls les liens sont retires.
        await _context.NotificationHisto
            .Where(n => n.CollaborateurId != null && liste.Contains(n.CollaborateurId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.CollaborateurId, (int?)null));
        await _context.NotificationHisto
            .Where(n => n.ClientId != null && liste.Contains(n.ClientId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ClientId, (int?)null));

        await _context.AffectationAuthenticationProduit
            .Where(a => liste.Contains(a.UserId))
            .ExecuteDeleteAsync();

        var supprimes = await _context.Authentication
            .Where(u => liste.Contains(u.Id))
            .ExecuteDeleteAsync();

        await transaction.CommitAsync();
        return new ResultatSuppression(supprimes, remisEnFile);
    }

    public async Task<Authentication> AddAsync(Authentication user)
    {
        _context.Authentication.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    public Task SaveChangesAsync() => _context.SaveChangesAsync();
}
