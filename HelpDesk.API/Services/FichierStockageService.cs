namespace HelpDesk.API.Services;

public interface IFichierStockage
{
    /// <summary>Enregistre le fichier sur disque et retourne son nom de stockage (anonyme).</summary>
    Task<string> EnregistrerAsync(IFormFile fichier);
    /// <summary>Ouvre un fichier stocké, ou null s'il n'existe plus.</summary>
    Stream? Ouvrir(string nomStockage);
    void Supprimer(string? nomStockage);
}

/// <summary>
/// Stockage des pièces jointes sur le disque du serveur.
/// Le fichier est renommé en GUID (jamais le nom fourni par l'utilisateur) :
/// le nom d'origine n'est conservé qu'en base, pour l'affichage et le téléchargement.
/// </summary>
public class FichierStockageDisque : IFichierStockage
{
    private readonly string _dossier;

    public FichierStockageDisque(IConfiguration configuration, IHostEnvironment env)
    {
        _dossier = configuration["Stockage:FichiersJoints"]
            ?? Path.Combine(env.ContentRootPath, "App_Data", "fichiers-joints");
        Directory.CreateDirectory(_dossier);
    }

    public async Task<string> EnregistrerAsync(IFormFile fichier)
    {
        // Extension d'origine conservee (assainie), le reste du nom est un GUID.
        var extension = Path.GetExtension(fichier.FileName);
        if (extension.Length > 10 || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            extension = "";

        var nomStockage = Guid.NewGuid().ToString("N") + extension.ToLowerInvariant();
        await using var flux = File.Create(Path.Combine(_dossier, nomStockage));
        await fichier.CopyToAsync(flux);
        return nomStockage;
    }

    public Stream? Ouvrir(string nomStockage)
    {
        // GetFileName neutralise toute tentative de remontee de chemin.
        var chemin = Path.Combine(_dossier, Path.GetFileName(nomStockage));
        return File.Exists(chemin) ? File.OpenRead(chemin) : null;
    }

    public void Supprimer(string? nomStockage)
    {
        if (string.IsNullOrWhiteSpace(nomStockage)) return;
        var chemin = Path.Combine(_dossier, Path.GetFileName(nomStockage));
        try { if (File.Exists(chemin)) File.Delete(chemin); }
        catch (IOException) { /* le ticket est deja supprime : on ne bloque pas pour un fichier orphelin */ }
    }
}
