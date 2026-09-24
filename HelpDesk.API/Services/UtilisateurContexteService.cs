using HelpDesk.API.Constants;
using HelpDesk.API.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace HelpDesk.API.Services;

/// <summary>
/// Utilisateur connecte et son perimetre de visibilite.
/// Les produits sont relus en base a chaque requete : une affectation
/// modifiee par l'admin s'applique immediatement, sans reconnexion.
/// </summary>
public record UtilisateurContexte(int Id, string Role, string Email, string NomComplet, IReadOnlyList<string> Produits)
{
    public bool EstAdmin => Role == Roles.Admin;
    public bool EstCollaborateur => Role == Roles.User;
    public bool EstDev => Role == Roles.Dev;
    public bool EstClient => Role == Roles.Client;
}

public interface IUtilisateurContexteService
{
    Task<UtilisateurContexte> ChargerAsync(ClaimsPrincipal principal);
}

public class UtilisateurContexteService : IUtilisateurContexteService
{
    private readonly HelpDeskContext _context;

    public UtilisateurContexteService(HelpDeskContext context)
    {
        _context = context;
    }

    public async Task<UtilisateurContexte> ChargerAsync(ClaimsPrincipal principal)
    {
        var id = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var user = await _context.Authentication
            .AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new { u.Id, u.Role, u.Username, u.Prenom, u.Nom })
            .FirstAsync();

        var produits = Roles.AvecProduits.Contains(user.Role)
            ? await _context.AffectationAuthenticationProduit
                .AsNoTracking()
                .Where(a => a.UserId == id)
                .Select(a => a.Product.Nom)
                .ToListAsync()
            : new List<string>();

        var nomComplet = string.Join(' ', new[] { user.Prenom, user.Nom }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

        return new UtilisateurContexte(user.Id, user.Role ?? "", user.Username ?? "", nomComplet, produits);
    }
}
