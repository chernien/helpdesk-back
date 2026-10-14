using HelpDesk.API.Data;
using HelpDesk.API.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Repositories;

public interface IProduitRepository
{
    Task<List<ProduitErp>> GetAllAsync();
    Task<List<ProduitErp>> GetByIdsAsync(IEnumerable<int> ids);
    Task<ProduitErp?> GetByNomAsync(string nom);
    Task<ProduitErp> AjouterAsync(string nom);
    Task<bool> SupprimerAsync(int id);
    Task<bool> EstReferenceAsync(int id);
}

public class ProduitRepository : IProduitRepository
{
    private readonly HelpDeskContext _context;

    public ProduitRepository(HelpDeskContext context)
    {
        _context = context;
    }

    public Task<List<ProduitErp>> GetAllAsync() =>
        _context.ProduitErp.AsNoTracking().OrderBy(p => p.Nom).ToListAsync();

    public Task<List<ProduitErp>> GetByIdsAsync(IEnumerable<int> ids)
    {
        var liste = ids.ToList();
        return _context.ProduitErp.Where(p => liste.Contains(p.ProductId)).ToListAsync();
    }

    public Task<ProduitErp?> GetByNomAsync(string nom) =>
        _context.ProduitErp.AsNoTracking().FirstOrDefaultAsync(p => p.Nom == nom);

    public async Task<ProduitErp> AjouterAsync(string nom)
    {
        nom = nom.Trim();
        var existant = await _context.ProduitErp.FirstOrDefaultAsync(p => p.Nom == nom);
        if (existant != null) return existant;
        var produit = new ProduitErp { Nom = nom };   // ProductID est IDENTITY : généré par SQL Server
        _context.ProduitErp.Add(produit);
        await _context.SaveChangesAsync();
        return produit;
    }

    public async Task<bool> SupprimerAsync(int id)
    {
        var produit = await _context.ProduitErp.FirstOrDefaultAsync(p => p.ProductId == id);
        if (produit == null) return false;
        _context.ProduitErp.Remove(produit);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>Vrai si le produit est affecté à au moins un utilisateur (suppression interdite).</summary>
    public Task<bool> EstReferenceAsync(int id) =>
        _context.AffectationAuthenticationProduit.AnyAsync(a => a.ProductId == id);
}
