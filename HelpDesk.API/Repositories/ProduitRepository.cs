using HelpDesk.API.Data;
using HelpDesk.API.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Repositories;

public interface IProduitRepository
{
    Task<List<ProduitErp>> GetAllAsync();
    Task<List<ProduitErp>> GetByIdsAsync(IEnumerable<int> ids);
    Task<ProduitErp?> GetByNomAsync(string nom);
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
}
