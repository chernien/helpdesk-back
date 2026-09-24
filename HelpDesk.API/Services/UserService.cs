using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Exceptions;
using HelpDesk.API.Models;
using HelpDesk.API.Repositories;
using System.Net.Mail;

namespace HelpDesk.API.Services;

public interface IUserService
{
    Task<UserPageDto> RechercherAsync(string? role, string? recherche, int page, int pageSize);
    Task<UserDto> GetByIdAsync(int id);
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

    public UserService(IUserRepository userRepository, IProduitRepository produitRepository, IClientService clientService)
    {
        _userRepository = userRepository;
        _produitRepository = produitRepository;
        _clientService = clientService;
    }

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
        return ToDto(user);
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
            await AppliquerSocieteAsync(user, request.ClientTiers);

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

        return await CreateAsync(new CreateUserRequest
        {
            Role = Roles.Client,
            Email = contact.Email,
            Nom = request.Nom,
            Prenom = request.Prenom,
            Password = request.Password,
            ClientTiers = request.ClientTiers.Trim()
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

        user.Username = email;
        user.Nom = request.Nom.Trim();
        user.Prenom = request.Prenom.Trim();
        user.Enabled = request.Enabled;

        if (!string.IsNullOrEmpty(request.Password))
        {
            ValiderMotDePasse(request.Password);
            user.Password = BCrypt.Net.BCrypt.HashPassword(request.Password);
        }

        if (user.Role == Roles.Client)
            await AppliquerSocieteAsync(user, request.ClientTiers);

        if (Roles.AvecProduits.Contains(user.Role ?? ""))
            await AppliquerProduitsAsync(user, request.ProduitIds);

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

    private async Task AppliquerSocieteAsync(Authentication user, string? tiers)
    {
        if (string.IsNullOrWhiteSpace(tiers))
            throw new MetierException("La société du client est requise.");

        // On ne fait pas confiance au nom envoye : il est relu dans Divalto.
        var societe = await _clientService.TrouverSocieteAsync(tiers.Trim())
            ?? throw new MetierException("Cette société n'existe pas dans Divalto.");

        user.ClientTiers = societe.Tiers;
        user.SocieteNom = societe.Nom;
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
        NombreConnexion = u.NombreConnexion,
        DateCreation = u.DateCreation,
        Produits = u.Affectations
            .Where(a => a.Product != null)
            .Select(a => new ProduitDto { ProductId = a.ProductId, Nom = a.Product.Nom })
            .OrderBy(p => p.Nom)
            .ToList()
    };
}
