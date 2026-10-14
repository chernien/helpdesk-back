using System.Text.Json;

namespace HelpDesk.API.Services;

/// <summary>Modèle gratuit d'OpenRouter : sortie JSON garantie (Json) et lecture d'images (Images).</summary>
public record ModeleOpenRouter(string Id, bool Json, bool Images);

/// <summary>
/// Catalogue des modèles GRATUITS d'OpenRouter, lu en ligne (la liste change souvent) et gardé 6 h.
/// Ne garde que les modèles utiles à l'agent : réponse texte uniquement, hors classifieurs de modération.
/// </summary>
public class CatalogueOpenRouter
{
    private static readonly TimeSpan DureeCache = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<CatalogueOpenRouter> _logger;
    private readonly SemaphoreSlim _verrou = new(1, 1);
    private IReadOnlyList<ModeleOpenRouter> _modeles = Array.Empty<ModeleOpenRouter>();
    private DateTime _valableJusqua = DateTime.MinValue;

    public CatalogueOpenRouter(IHttpClientFactory http, ILogger<CatalogueOpenRouter> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ModeleOpenRouter>> ModelesAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow < _valableJusqua) return _modeles;
        await _verrou.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _valableJusqua) return _modeles;
            try
            {
                using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delai.CancelAfter(TimeSpan.FromSeconds(20));
                using var resp = await _http.CreateClient().GetAsync("https://openrouter.ai/api/v1/models", delai.Token);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(delai.Token));
                _modeles = Filtrer(doc.RootElement.GetProperty("data"));
                _valableJusqua = DateTime.UtcNow + DureeCache;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Catalogue injoignable : on garde l'ancien et on réessaie dans 5 minutes.
                _logger.LogWarning(ex, "OpenRouter : catalogue des modèles indisponible.");
                _valableJusqua = DateTime.UtcNow + TimeSpan.FromMinutes(5);
            }
            return _modeles;
        }
        finally { _verrou.Release(); }
    }

    /// <summary>Modèles gratuits (« :free » ou prix nul), réponse texte seule, hors modération.</summary>
    public static List<ModeleOpenRouter> Filtrer(JsonElement data)
    {
        var liste = new List<ModeleOpenRouter>();
        foreach (var m in data.EnumerateArray())
        {
            var id = m.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var gratuit = id.EndsWith(":free", StringComparison.OrdinalIgnoreCase)
                || (m.TryGetProperty("pricing", out var prix) && Zero(prix, "prompt") && Zero(prix, "completion"));
            if (!gratuit) continue;

            var architecture = m.TryGetProperty("architecture", out var a) ? a : default;
            var sorties = Modalites(architecture, "output_modalities");
            if (sorties.Count > 0 && !(sorties.Count == 1 && sorties.Contains("text"))) continue;   // musique, images…
            if (id.Contains("safety", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("guard", StringComparison.OrdinalIgnoreCase)) continue;                     // classifieurs de modération

            var json = m.TryGetProperty("supported_parameters", out var p) && p.ValueKind == JsonValueKind.Array
                && p.EnumerateArray().Any(x => x.GetString() == "response_format");
            liste.Add(new ModeleOpenRouter(id, json, Modalites(architecture, "input_modalities").Contains("image")));
        }
        return liste;
    }

    private static bool Zero(JsonElement prix, string nom) =>
        prix.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String
        && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && d == 0;

    private static HashSet<string> Modalites(JsonElement architecture, string nom) =>
        architecture.ValueKind == JsonValueKind.Object && architecture.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
