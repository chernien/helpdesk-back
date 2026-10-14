using HelpDesk.API.DTOs;
using HelpDesk.API.Exceptions;
using HelpDesk.API.Repositories;

namespace HelpDesk.API.Services;

public interface IProduitService
{
    Task<List<ProduitDto>> GetAllAsync();
    Task<ProduitDto> AjouterAsync(string nom);
    Task SupprimerAsync(int id);
}

/// <summary>
/// Catalogue des produits ERP. Les affectations produit ↔ collaborateur
/// sont gerees depuis la gestion des utilisateurs (UserService).
/// </summary>
public class ProduitService : IProduitService
{
    private readonly IProduitRepository _produitRepository;

    public ProduitService(IProduitRepository produitRepository)
    {
        _produitRepository = produitRepository;
    }

    public async Task<List<ProduitDto>> GetAllAsync()
    {
        var produits = await _produitRepository.GetAllAsync();
        return produits.Select(p => new ProduitDto { ProductId = p.ProductId, Nom = p.Nom }).ToList();
    }

    public async Task<ProduitDto> AjouterAsync(string nom)
    {
        if (string.IsNullOrWhiteSpace(nom))
            throw new MetierException("Le nom du produit est requis.");
        var produit = await _produitRepository.AjouterAsync(nom);
        return new ProduitDto { ProductId = produit.ProductId, Nom = produit.Nom };
    }

    public async Task SupprimerAsync(int id)
    {
        if (await _produitRepository.EstReferenceAsync(id))
            throw new MetierException("Ce produit est affecté à des utilisateurs : retirez-le d'abord de leurs comptes.");
        var ok = await _produitRepository.SupprimerAsync(id);
        if (!ok) throw MetierException.Introuvable("Produit introuvable.");
    }
}
