using HelpDesk.API.Data;
using HelpDesk.API.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Repositories;

public interface IModuleRepository
{
    Task<List<Module>> ListAsync();
    Task<List<string>> NomsAsync();
    Task<Module> AjouterAsync(string nom);
    Task<bool> SupprimerAsync(int id);
    /// <summary>Nombre de clients à qui ce module est affecté (colonne Authentication.Modules).</summary>
    Task<int> NombreClientsAsync(int id);
}

public class ModuleRepository : IModuleRepository
{
    private readonly HelpDeskContext _context;
    public ModuleRepository(HelpDeskContext context) => _context = context;

    public Task<List<Module>> ListAsync() =>
        _context.Module.AsNoTracking().OrderBy(m => m.Nom).ToListAsync();

    public Task<List<string>> NomsAsync() =>
        _context.Module.AsNoTracking().OrderBy(m => m.Nom).Select(m => m.Nom).ToListAsync();

    public async Task<Module> AjouterAsync(string nom)
    {
        nom = nom.Trim();
        var existant = await _context.Module.FirstOrDefaultAsync(m => m.Nom == nom);
        if (existant != null) return existant;
        var module = new Module { Nom = nom };
        _context.Module.Add(module);
        await _context.SaveChangesAsync();
        return module;
    }

    public async Task<int> NombreClientsAsync(int id)
    {
        var nom = await _context.Module.AsNoTracking().Where(m => m.Id == id).Select(m => m.Nom).FirstOrDefaultAsync();
        if (nom == null) return 0;
        // Liste séparée par des virgules : comparaison exacte de chaque élément (en mémoire, peu de clients).
        var listes = await _context.Authentication.AsNoTracking()
            .Where(a => a.Modules != null && a.Modules.Contains(nom))
            .Select(a => a.Modules!)
            .ToListAsync();
        return listes.Count(l => l.Split(',', StringSplitOptions.TrimEntries).Contains(nom));
    }

    public async Task<bool> SupprimerAsync(int id)
    {
        var module = await _context.Module.FirstOrDefaultAsync(m => m.Id == id);
        if (module == null) return false;
        _context.Module.Remove(module);
        await _context.SaveChangesAsync();
        return true;
    }
}
