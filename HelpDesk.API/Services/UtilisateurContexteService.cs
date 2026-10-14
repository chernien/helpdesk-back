using HelpDesk.API.Constants;
using HelpDesk.API.Data;
using HelpDesk.API.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace HelpDesk.API.Services;

/// <summary>
/// Utilisateur connecte et son perimetre de visibilite.
/// Les produits sont relus en base a chaque requete : une affectation
/// modifiee par l'admin s'applique immediatement, sans reconnexion.
/// </summary>
/// <param name="SocietesAdmin">
/// Client : sociétés (noms) dont il est administrateur (compte coché « administrateur de la société ») —
/// il y voit tous les tickets ; simple membre : seulement les siens.
/// </param>
public record UtilisateurContexte(int Id, string Role, string Email, string NomComplet,
    IReadOnlyList<string> Produits, IReadOnlyList<PerimetreClientContexte> PerimetreClient,
    IReadOnlyList<string> ModulesClient, IReadOnlyList<string> SocietesClient,
    string? SocieteActive, IReadOnlyList<string>? SocietesAdmin = null)
{
    public bool EstAdmin => Role == Roles.Admin;
    public bool EstCollaborateur => Role == Roles.User;
    public bool EstDev => Role == Roles.Dev;
    public bool EstClient => Role == Roles.Client;

    /// <summary>Client administrateur de cette société : il voit les tickets de tous ses membres.</summary>
    public bool EstAdminDe(string? societe) =>
        societe != null && (SocietesAdmin?.Contains(societe.Trim(), StringComparer.OrdinalIgnoreCase) ?? false);
}

/// <summary>Une ligne du périmètre d'un client : un produit (+ version). Les modules sont au niveau du client.</summary>
public record PerimetreClientContexte(string Produit, string? Version);

public interface IUtilisateurContexteService
{
    Task<UtilisateurContexte> ChargerAsync(ClaimsPrincipal principal);
    /// <summary>Charge le contexte d'un utilisateur par id (tâches de fond, hors requête HTTP).</summary>
    Task<UtilisateurContexte> ChargerParIdAsync(int id);
}

public class UtilisateurContexteService : IUtilisateurContexteService
{
    private readonly HelpDeskContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public UtilisateurContexteService(HelpDeskContext context, IHttpContextAccessor httpContextAccessor)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<UtilisateurContexte> ChargerAsync(ClaimsPrincipal principal)
    {
        var id = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var entete = _httpContextAccessor.HttpContext?.Request.Headers["X-Societe"].ToString();
        return await ConstruireAsync(id, entete);
    }

    /// <summary>Contexte d'un utilisateur par son id (hors requête HTTP : tâches de fond). Pas de société active.</summary>
    public Task<UtilisateurContexte> ChargerParIdAsync(int id) => ConstruireAsync(id, null);

    private async Task<UtilisateurContexte> ConstruireAsync(int id, string? societeEntete)
    {
        var user = await _context.Authentication
            .AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new { u.Id, u.Role, u.Username, u.Prenom, u.Nom, u.Modules, u.Societes, u.ClientTiers, u.SocieteNom, u.AdminSociete })
            .FirstAsync();

        var produits = Roles.AvecProduits.Contains(user.Role)
            ? await _context.AffectationAuthenticationProduit
                .AsNoTracking()
                .Where(a => a.UserId == id)
                .Select(a => a.Product.Nom)
                .ToListAsync()
            : new List<string>();

        // Un client ne voit que les tickets de son périmètre : produits (+ version) issus de la table
        // d'affectation, et modules au niveau du client (colonne Authentication.Modules).
        var perimetre = user.Role == Roles.Client
            ? await _context.AffectationAuthenticationProduit
                .AsNoTracking()
                .Where(a => a.UserId == id)
                .Select(a => new PerimetreClientContexte(a.Product.Nom, a.Version))
                .ToListAsync()
            : new List<PerimetreClientContexte>();

        var modulesClient = user.Role == Roles.Client && !string.IsNullOrWhiteSpace(user.Modules)
            ? user.Modules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : new List<string>();

        // Sociétés rattachées au client (noms, pour filtrer les tickets par société).
        var rattachees = new List<SocieteClientDto>();
        if (user.Role == Roles.Client)
        {
            if (!string.IsNullOrWhiteSpace(user.Societes))
            {
                try
                {
                    rattachees = JsonSerializer.Deserialize<List<SocieteClientDto>>(user.Societes) ?? new();
                }
                catch (JsonException) { /* données anciennes : repli ci-dessous */ }
            }
            rattachees = rattachees.Where(s => !string.IsNullOrWhiteSpace(s.Nom)).ToList();
            if (rattachees.Count == 0 && !string.IsNullOrWhiteSpace(user.SocieteNom))
                rattachees.Add(new SocieteClientDto { Tiers = user.ClientTiers ?? "", Nom = user.SocieteNom });
        }
        var societesClient = rattachees.Select(s => s.Nom.Trim()).ToList();

        // Administrateur de sa société (case du compte) : il voit tous les tickets de ses sociétés.
        var societesAdmin = user.Role == Roles.Client && user.AdminSociete ? societesClient : new List<string>();

        // Société consultée (en-tête X-Societe), uniquement si c'est bien une société du client.
        string? societeActive = null;
        if (user.Role == Roles.Client && societesClient.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(societeEntete))
            {
                var demandee = System.Net.WebUtility.UrlDecode(societeEntete).Trim();
                societeActive = societesClient.FirstOrDefault(s =>
                    string.Equals(s, demandee, StringComparison.OrdinalIgnoreCase));
            }
        }

        var nomComplet = string.Join(' ', new[] { user.Prenom, user.Nom }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

        return new UtilisateurContexte(user.Id, user.Role ?? "", user.Username ?? "", nomComplet,
            produits, perimetre, modulesClient, societesClient, societeActive, societesAdmin);
    }
}
