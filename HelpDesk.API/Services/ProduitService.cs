using HelpDesk.API.DTOs;
using HelpDesk.API.Repositories;

namespace HelpDesk.API.Services;

public interface IProduitService
{
    Task<List<ProduitDto>> GetAllAsync();
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
}
