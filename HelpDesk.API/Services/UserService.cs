using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Exceptions;
using HelpDesk.API.Models;
using HelpDesk.API.Repositories;
using System.Net.Mail;
using System.Text.Json;

namespace HelpDesk.API.Services;

public interface IUserService
{
    Task<UserPageDto> RechercherAsync(string? role, string? recherche, int page, int pageSize);
    Task<UserDto> GetByIdAsync(int id);
    Task<PerimetreClientDto> PerimetreParEmailAsync(string email);
    Task<List<CollaborateurDto>> CollaborateursDuProduitAsync(string produit);
    Task<UserDto> CreateAsync(CreateUserRequest request);
    Task<UserDto> CreerCompteContactAsync(CreerCompteContactRequest request);
    Task<UserDto> UpdateAsync(int id, UpdateUserRequest request, int adminId);
    Task<SuppressionUsersResultDto> SupprimerAsync(List<int> ids, int adminId);
}

public class UserService : IUserService
{
    private const int LongueurMinMotDePasse = 8;

    private readonly IUserRepository _userRepository;
    private readonly IProduitRepository _produitRepository;
    private readonly IClientService _clientService;
    private readonly IModuleRepository _moduleRepository;

    public UserService(IUserRepository userRepository, IProduitRepository produitRepository,
        IClientService clientService, IModuleRepository moduleRepository)
    {
        _userRepository = userRepository;
        _produitRepository = produitRepository;
        _clientService = clientService;
        _moduleRepository = moduleRepository;
    }

    /// <summary>
    /// Périmètre d'un client : enregistré dans la table d'affectation existante
    /// (produit + version + liste de modules séparés par des virgules).
    /// </summary>
    private async Task AppliquerPerimetreClientAsync(Authentication user, List<PerimetreLigneDto> lignes, List<string> modules)
    {
        // Produits (+ version) → table d'affectation.
        var nouvelles = new List<AffectationAuthenticationProduit>();
        foreach (var l in lignes.Where(x => !string.IsNullOrWhiteSpace(x.Produit)))
        {
            var produit = await _produitRepository.GetByNomAsync(l.Produit.Trim());
            if (produit == null) continue;                                   // produit inconnu : ignoré
            if (nouvelles.Any(a => a.ProductId == produit.ProductId)) continue; // clé (UserId, ProductId) unique
            nouvelles.Add(new AffectationAuthenticationProduit
            {
                ProductId = produit.ProductId,
                User = user,
                Version = string.IsNullOrWhiteSpace(l.Version) ? null : l.Version.Trim()
            });
        }
        foreach (var a in user.Affectations.ToList()) user.Affectations.Remove(a);
        foreach (var a in nouvelles) user.Affectations.Add(a);

        // Modules → au niveau du client (colonne Authentication.Modules) : uniquement des modules de la base,
        // enregistrés sous leur nom exact (« reglement » → « Règlement »).
        var catalogue = await _moduleRepository.NomsAsync();
        var mods = new List<string>();
        var inconnus = new List<string>();
        foreach (var m in (modules ?? new List<string>()).Select(m => m.Trim()).Where(m => m.Length > 0))
        {
            var nom = catalogue.FirstOrDefault(b => string.Equals(SansAccents(b), SansAccents(m), StringComparison.OrdinalIgnoreCase));
            if (nom == null) inconnus.Add(m);
            else if (!mods.Contains(nom)) mods.Add(nom);
        }
        if (inconnus.Count > 0)
            throw new MetierException($"Module(s) inconnu(s) : {string.Join(", ", inconnus)}. Ajoutez-les d'abord à la liste des modules (bouton « + »).");
        user.Modules = mods.Count > 0 ? string.Join(",", mods) : null;
    }

    private static string SansAccents(string s) =>
        new string(s.Trim().Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());

    public async Task<UserPageDto> RechercherAsync(string? role, string? recherche, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (items, total) = await _userRepository.RechercherAsync(role, recherche, page, pageSize);
        return new UserPageDto { Total = total, Items = items.Select(ToDto).ToList() };
    }

    public async Task<UserDto> GetByIdAsync(int id)
    {
        var user = await _userRepository.GetAvecProduitsAsync(id)
            ?? throw MetierException.Introuvable("Utilisateur introuvable.");
        var dto = ToDto(user);
        if (user.Role == Roles.Client)
        {
            dto.Perimetre = user.Affectations.Select(a => new PerimetreLigneDto
            {
                Produit = a.Product.Nom,
                Version = a.Version
            }).ToList();
            dto.Modules = string.IsNullOrWhiteSpace(user.Modules)
                ? new List<string>()
                : user.Modules.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
        }
        return dto;
    }

    /// <summary>
    /// Périmètre (produits + modules) du compte client portant cet email.
    /// Listes vides si aucun compte, pas un client, ou aucun périmètre affecté (= tout autorisé).
    /// </summary>
    public async Task<PerimetreClientDto> PerimetreParEmailAsync(string email)
    {
        var dto = new PerimetreClientDto();
        if (string.IsNullOrWhiteSpace(email)) return dto;

        var user = await _userRepository.GetAvecProduitsParEmailAsync(email);
        if (user == null || user.Role != Roles.Client) return dto;

        dto.Produits = user.Affectations.Select(a => a.Product.Nom).ToList();
        dto.Modules = string.IsNullOrWhiteSpace(user.Modules)
            ? new List<string>()
            : user.Modules.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
        return dto;
    }

    public async Task<List<CollaborateurDto>> CollaborateursDuProduitAsync(string produit)
    {
        var users = await _userRepository.CollaborateursDuProduitAsync(produit);
        return users.Select(u => new CollaborateurDto { Id = u.Id, NomComplet = u.NomComplet, Email = u.Username, Role = u.Role }).ToList();
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request)
    {
        if (!Roles.Tous.Contains(request.Role))
            throw new MetierException("Rôle invalide.");

        var email = NormaliserEmail(request.Email);
        ValiderIdentite(request.Nom, request.Prenom);
        ValiderMotDePasse(request.Password);

        if (await _userRepository.EmailUtiliseAsync(email))
            throw MetierException.Conflit($"Un compte existe déjà avec l'email {email}.");

        var user = new Authentication
        {
            Username = email,
            Password = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Nom = request.Nom.Trim(),
            Prenom = request.Prenom.Trim(),
            Role = request.Role,
            Enabled = true,
            Connected = false,
            NombreConnexion = 0,
            DateCreation = DateTime.Now,
            // Un nouveau compte part "a jour" : pas d'historique ancien de son produit en non lu.
            NotificationsLuesLe = DateTime.Now
        };

        if (request.Role == Roles.Client)
        {
            await AppliquerSocietesAsync(user, request.SocietesTiers, request.ClientTiers);
            await AppliquerPerimetreClientAsync(user, request.Perimetre, request.Modules);
            user.AdminSociete = request.AdminSociete;
        }

        if (Roles.AvecProduits.Contains(request.Role))
            await AppliquerProduitsAsync(user, request.ProduitIds);

        await _userRepository.AddAsync(user);

        // Les tickets deja ouverts pour cet email appartiennent desormais a ce compte.
        if (request.Role == Roles.Client)
            await _userRepository.RattacherTicketsAsync(user.Id, email);

        return await GetByIdAsync(user.Id);
    }

    /// <summary>
    /// Compte Helpdesk d'un contact Divalto, cree depuis le formulaire de ticket
    /// (admin ou collaborateur). Le contact doit appartenir a la societe choisie :
    /// on ne cree jamais de compte client pour un email libre.
    /// </summary>
    public async Task<UserDto> CreerCompteContactAsync(CreerCompteContactRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientTiers) || string.IsNullOrWhiteSpace(request.Email))
            throw new MetierException("La société et l'email du contact sont requis.");

        var contact = await _clientService.TrouverContactAsync(request.ClientTiers.Trim(), request.Email.Trim())
            ?? throw new MetierException("Ce contact n'appartient pas à la société sélectionnée.");

        // Administrateur de sa société si « Profil Admin helpdesk » est coché sur son contact dans Divalto.
        var tiersAdmin = await _clientService.TiersAdminAsync(contact.Email);

        return await CreateAsync(new CreateUserRequest
        {
            Role = Roles.Client,
            Email = contact.Email,
            Nom = request.Nom,
            Prenom = request.Prenom,
            Password = request.Password,
            ClientTiers = request.ClientTiers.Trim(),
            AdminSociete = tiersAdmin.Contains(request.ClientTiers.Trim(), StringComparer.OrdinalIgnoreCase)
        });
    }

    public async Task<UserDto> UpdateAsync(int id, UpdateUserRequest request, int adminId)
    {
        var user = await _userRepository.GetAvecProduitsAsync(id)
            ?? throw MetierException.Introuvable("Utilisateur introuvable.");

        var email = NormaliserEmail(request.Email);
        ValiderIdentite(request.Nom, request.Prenom);

        if (await _userRepository.EmailUtiliseAsync(email, saufId: id))
            throw MetierException.Conflit($"Un autre compte utilise déjà l'email {email}.");

        if (id == adminId && !request.Enabled)
            throw new MetierException("Vous ne pouvez pas désactiver votre propre compte.");

        // Rôle : modifiable. Vide = inchangé. On empêche un admin de changer SON PROPRE rôle (risque de blocage).
        var nouveauRole = string.IsNullOrWhiteSpace(request.Role) ? (user.Role ?? "") : request.Role.Trim();
        if (!Roles.Tous.Contains(nouveauRole))
            throw new MetierException("Rôle invalide.");
        if (id == adminId && nouveauRole != user.Role)
            throw new MetierException("Vous ne pouvez pas changer votre propre rôle.");

        user.Username = email;
        user.Nom = request.Nom.Trim();
        user.Prenom = request.Prenom.Trim();
        user.Enabled = request.Enabled;
        user.Role = nouveauRole;

        if (!string.IsNullOrEmpty(request.Password))
        {
            ValiderMotDePasse(request.Password);
            user.Password = BCrypt.Net.BCrypt.HashPassword(request.Password);
        }

        // Données propres au rôle, reconstruites selon le NOUVEAU rôle (changement de rôle géré proprement).
        if (nouveauRole == Roles.Client)
        {
            await AppliquerSocietesAsync(user, request.SocietesTiers, request.ClientTiers);
            await AppliquerPerimetreClientAsync(user, request.Perimetre, request.Modules);
            user.AdminSociete = request.AdminSociete;
        }
        else
        {
            // Plus un client : on efface société, modules et statut d'administrateur de société.
            user.ClientTiers = null;
            user.SocieteNom = null;
            user.Societes = null;
            user.Modules = null;
            user.AdminSociete = false;

            if (Roles.AvecProduits.Contains(nouveauRole))
                await AppliquerProduitsAsync(user, request.ProduitIds);
            else
                // ADMIN : aucune affectation produit.
                foreach (var a in user.Affectations.ToList()) user.Affectations.Remove(a);
        }

        await _userRepository.SaveChangesAsync();
        return await GetByIdAsync(id);
    }

    private const int MaxSuppressionsParLot = 100;

    public async Task<SuppressionUsersResultDto> SupprimerAsync(List<int> ids, int adminId)
    {
        var cibles = ids.Distinct().ToList();

        if (cibles.Count == 0)
            throw new MetierException("Aucun compte sélectionné.");
        if (cibles.Count > MaxSuppressionsParLot)
            throw new MetierException($"{MaxSuppressionsParLot} comptes maximum par suppression.");
        if (cibles.Contains(adminId))
            throw new MetierException("Vous ne pouvez pas supprimer votre propre compte.");

        var comptes = await _userRepository.GetByIdsAsync(cibles);
        if (comptes.Count != cibles.Count)
            throw MetierException.Introuvable("Un ou plusieurs comptes n'existent plus. Rechargez la liste.");

        if (comptes.Any(c => c.Role == Roles.Admin) &&
            await _userRepository.CompterAdminsActifsAsync(sauf: cibles) == 0)
            throw new MetierException("Impossible : il doit rester au moins un administrateur actif.");

        var resultat = await _userRepository.SupprimerAsync(cibles);
        return new SuppressionUsersResultDto
        {
            ComptesSupprimes = resultat.ComptesSupprimes,
            TicketsRemisEnFile = resultat.TicketsRemisEnFile
        };
    }

    // ---------- Regles ----------

    /// <summary>
    /// Rattache une ou plusieurs sociétés au client. Chaque code tiers est relu dans Divalto
    /// (on ne fait pas confiance au nom envoyé). La 1re société devient la société principale
    /// (ClientTiers/SocieteNom) ; la liste complète est stockée en JSON dans Societes.
    /// </summary>
    private async Task AppliquerSocietesAsync(Authentication user, List<string> tiersListe, string? tiersPrincipal)
    {
        // Compat : si aucune liste n'est fournie, on retombe sur le code principal unique.
        var codes = (tiersListe ?? new List<string>())
            .Select(t => t?.Trim() ?? "").Where(t => t.Length > 0).ToList();
        if (codes.Count == 0 && !string.IsNullOrWhiteSpace(tiersPrincipal))
            codes.Add(tiersPrincipal.Trim());

        if (codes.Count == 0)
            throw new MetierException("Au moins une société est requise pour un client.");

        var societes = new List<SocieteClientDto>();
        foreach (var code in codes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var societe = await _clientService.TrouverSocieteAsync(code)
                ?? throw new MetierException($"La société « {code} » n'existe pas dans Divalto.");
            if (societes.All(s => !string.Equals(s.Tiers, societe.Tiers, StringComparison.OrdinalIgnoreCase)))
                societes.Add(societe);
        }

        user.Societes = JsonSerializer.Serialize(societes);
        user.ClientTiers = societes[0].Tiers;   // société principale = la première
        user.SocieteNom = societes[0].Nom;
    }

    /// <summary>Sociétés rattachées à un client (JSON Societes ; repli sur la société principale).</summary>
    public static List<SocieteClientDto> SocietesDe(Authentication u)
    {
        if (!string.IsNullOrWhiteSpace(u.Societes))
        {
            try
            {
                var liste = JsonSerializer.Deserialize<List<SocieteClientDto>>(u.Societes);
                if (liste is { Count: > 0 }) return liste;
            }
            catch (JsonException) { /* données anciennes/corrompues : repli ci-dessous */ }
        }
        return string.IsNullOrWhiteSpace(u.ClientTiers)
            ? new List<SocieteClientDto>()
            : new List<SocieteClientDto> { new() { Tiers = u.ClientTiers, Nom = u.SocieteNom ?? u.ClientTiers } };
    }

    private async Task AppliquerProduitsAsync(Authentication user, List<int> produitIds)
    {
        var ids = produitIds.Distinct().ToList();
        if (ids.Count == 0)
            throw new MetierException("Un collaborateur doit être affecté à au moins un produit.");

        var existants = await _produitRepository.GetByIdsAsync(ids);
        if (existants.Count != ids.Count)
            throw new MetierException("Un des produits sélectionnés n'existe pas.");

        // Remplacement de l'ensemble : on retire les affectations en trop, on ajoute les nouvelles.
        foreach (var affectation in user.Affectations.Where(a => !ids.Contains(a.ProductId)).ToList())
            user.Affectations.Remove(affectation);

        foreach (var produit in existants.Where(p => user.Affectations.All(a => a.ProductId != p.ProductId)))
            user.Affectations.Add(new AffectationAuthenticationProduit { ProductId = produit.ProductId, User = user });
    }

    private static string NormaliserEmail(string? email)
    {
        var valeur = (email ?? "").Trim().ToLowerInvariant();
        if (!MailAddress.TryCreate(valeur, out var adresse) || adresse.Address != valeur)
            throw new MetierException("L'adresse email n'est pas valide.");
        return valeur;
    }

    private static void ValiderIdentite(string? nom, string? prenom)
    {
        if (string.IsNullOrWhiteSpace(nom)) throw new MetierException("Le nom est requis.");
        if (string.IsNullOrWhiteSpace(prenom)) throw new MetierException("Le prénom est requis.");
    }

    private static void ValiderMotDePasse(string? motDePasse)
    {
        if (string.IsNullOrEmpty(motDePasse) || motDePasse.Length < LongueurMinMotDePasse)
            throw new MetierException($"Le mot de passe doit contenir au moins {LongueurMinMotDePasse} caractères.");
    }

    public static UserDto ToDto(Authentication u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        Nom = u.Nom,
        Prenom = u.Prenom,
        NomComplet = u.NomComplet,
        Role = u.Role,
        Enabled = u.Enabled,
        ClientTiers = u.ClientTiers,
        SocieteNom = u.SocieteNom,
        Societes = SocietesDe(u),
        AdminSociete = u.Role == Roles.Client && u.AdminSociete,
        NombreConnexion = u.NombreConnexion,
        DateCreation = u.DateCreation,
        Produits = u.Affectations
            .Where(a => a.Product != null)
            .Select(a => new ProduitDto { ProductId = a.ProductId, Nom = a.Product.Nom })
            .OrderBy(p => p.Nom)
            .ToList()
    };
}
