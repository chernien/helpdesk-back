using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HelpDesk.API.Services;

/// <summary>Pièce jointe d'un e-mail, à attacher au ticket créé par l'agent.</summary>
public record PieceJointeAgent(string Nom, string? Type, byte[] Contenu);

/// <summary>Résultat structuré de l'analyse IA d'un e-mail (pour créer un ticket).</summary>
public record AnalyseEmail(
    bool EstUnProbleme, string? Resume, string? Description, string? Societe,
    string? NomClient, string? Produit, string? Version, string? Priorite, string? Categorie,
    string? Module = null);

/// <summary>
/// L'IA ne peut plus répondre : quota atteint (Quota) ou modèle indisponible. L'utilisateur est prévenu.
/// TousLesModeles : en mode automatique, aucun modèle n'a pu répondre (Modele est alors null).
/// </summary>
public record BlocageIa(string? Modele, bool Quota, bool TousLesModeles = false);

/// <summary>Résultat d'un appel IA : l'analyse, ou le blocage qui l'a empêchée.</summary>
public record ResultatIa(AnalyseEmail? Analyse, BlocageIa? Blocage);

/// <summary>Un modèle IA proposé dans l'interface (pour basculer).</summary>
public record ModeleIa(string Fournisseur, string Id);

/// <summary>Modèle écarté (quota atteint, saturé ou indisponible) jusqu'à l'heure de reprise (UTC).</summary>
public record ModeleEnPause(string Id, bool Quota, DateTime Reprise);

/// <summary>
/// Modèles écartés, partagés par tous les utilisateurs (les clés IA sont communes) :
/// un modèle en quota est sauté jusqu'à son heure de reprise, puis retenté.
/// </summary>
public class QuotaModelesIa
{
    private readonly ConcurrentDictionary<string, ModeleEnPause> _pauses = new(StringComparer.OrdinalIgnoreCase);

    public bool EstEnPause(string modele, out bool quota)
    {
        if (_pauses.TryGetValue(modele, out var p) && p.Reprise > DateTime.UtcNow) { quota = p.Quota; return true; }
        quota = false;
        return false;
    }

    public void Mettre(string modele, bool quota, TimeSpan duree) =>
        _pauses[modele] = new ModeleEnPause(modele, quota, DateTime.UtcNow + duree);

    public void Liberer(string modele) => _pauses.TryRemove(modele, out _);

    /// <summary>Modèles encore écartés, du plus proche de la reprise au plus lointain.</summary>
    public IReadOnlyList<ModeleEnPause> EnCours() =>
        _pauses.Values.Where(p => p.Reprise > DateTime.UtcNow).OrderBy(p => p.Reprise).ToList();
}

/// <summary>
/// Transforme le texte d'un e-mail en ticket structuré via IA.
/// - Mode automatique (aucun modèle choisi) : de modèle en modèle — Gemini, Groq, les gratuits
///   d'OpenRouter, puis NVIDIA en dernier recours. Un modèle en quota ou saturé est sauté jusqu'à
///   sa reprise ; si TOUS sont épuisés, l'utilisateur est prévenu.
/// - Modèle choisi par l'utilisateur : ce modèle seul. En cas de quota (ou s'il n'existe plus),
///   l'utilisateur est prévenu pour choisir un autre modèle, passer en automatique ou couper l'IA.
/// Les listes se règlent dans appsettings.json : OutlookAgent:GeminiModeles, GroqModeles,
/// OpenRouterModeles (mode automatique) et NvidiaModeles.
/// </summary>
public class AgentIaService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<AgentIaService> _logger;
    private readonly QuotaModelesIa _quotas;
    private readonly CatalogueOpenRouter _openRouter;

    /// <summary>Pause d'un modèle en quota quand le fournisseur ne donne pas de délai.</summary>
    private static readonly TimeSpan PauseQuotaParDefaut = TimeSpan.FromMinutes(2);
    /// <summary>Pause d'un modèle saturé (pas de réponse à temps, ou « surchargé »).</summary>
    private static readonly TimeSpan PauseSature = TimeSpan.FromMinutes(5);
    /// <summary>Pause d'un modèle introuvable / retiré (404) avant de le retenter.</summary>
    private static readonly TimeSpan PauseIndisponible = TimeSpan.FromHours(6);

    public AgentIaService(IHttpClientFactory http, IConfiguration config, ILogger<AgentIaService> logger,
        QuotaModelesIa quotas, CatalogueOpenRouter openRouter)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _quotas = quotas;
        _openRouter = openRouter;
    }

    private string? CleGemini => _config["OutlookAgent:GeminiApiKey"];
    private string? CleGroq => _config["OutlookAgent:GroqApiKey"];
    private string? CleOpenRouter => _config["OutlookAgent:OpenRouterApiKey"];
    private string? CleNvidia => _config["OutlookAgent:NvidiaApiKey"];

    private static bool Configuree(string? cle) => !string.IsNullOrWhiteSpace(cle);

    public bool EstConfigure =>
        Configuree(CleGemini) || Configuree(CleGroq) || Configuree(CleOpenRouter) || Configuree(CleNvidia);

    /// <summary>Délai max d'une réponse : au-delà, le modèle est considéré saturé et on passe au suivant.</summary>
    private TimeSpan DelaiReponse =>
        TimeSpan.FromSeconds(int.TryParse(_config["OutlookAgent:DelaiReponseSecondes"], out var s) && s >= 1 ? s : 60);

    /// <summary>Liste de modèles lue dans la configuration (ordre conservé), sinon la liste par défaut.</summary>
    private string[] Liste(string cle, params string[] parDefaut)
    {
        var config = _config.GetSection(cle).GetChildren()
            .Select(c => c.Value?.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToArray();
        return (config.Length > 0 ? config : parDefaut).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Listes par défaut : modèles vérifiés (réponse JSON correcte au prompt de l'agent).
    private string[] ModelesGemini => Liste("OutlookAgent:GeminiModeles",
        "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-3.1-flash-lite");

    private string[] ModelesGroq => Liste("OutlookAgent:GroqModeles", "qwen/qwen3.8-27b", "openai/gpt-oss-20b");

    /// <summary>NVIDIA, sans les identifiants déjà servis par Groq ou Gemini (un même nom → un seul fournisseur).</summary>
    private string[] ModelesNvidia => Liste("OutlookAgent:NvidiaModeles",
            "nvidia/nemotron-3-ultra-550b-a55b", "nvidia/nemotron-3.5-lightning-30b-a3b",
            "nvidia/nemotron-3-super-120b-a12b", "z-ai/glm-5.3")
        .Where(m => !m.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)
                 && !ModelesGroq.Contains(m, StringComparer.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// Modèles OpenRouter du mode automatique, dans l'ordre. Les autres modèles gratuits restent
    /// proposés au choix ; « openrouter/free » laisse OpenRouter choisir un modèle gratuit disponible.
    /// </summary>
    private string[] OpenRouterAutomatique => Liste("OutlookAgent:OpenRouterModeles", "openrouter/free");

    /// <summary>Modèles proposés, par fournisseur (seulement ceux dont la clé est configurée).</summary>
    public async Task<IReadOnlyList<ModeleIa>> ModelesAsync(CancellationToken ct = default)
    {
        var liste = new List<ModeleIa>();
        if (Configuree(CleGemini)) liste.AddRange(ModelesGemini.Select(m => new ModeleIa("Gemini", m)));
        if (Configuree(CleGroq)) liste.AddRange(ModelesGroq.Select(m => new ModeleIa("Groq", m)));
        if (Configuree(CleOpenRouter))
            liste.AddRange((await ModelesOpenRouterAsync(ct)).Select(m => new ModeleIa("OpenRouter", m.Id)));
        if (Configuree(CleNvidia)) liste.AddRange(ModelesNvidia.Select(m => new ModeleIa("NVIDIA", m)));
        return liste;
    }

    public async Task<bool> EstModeleConnuAsync(string modele, CancellationToken ct = default) =>
        (await ModelesAsync(ct)).Any(m => string.Equals(m.Id, modele, StringComparison.OrdinalIgnoreCase));

    /// <summary>Modèles actuellement écartés (quota / saturés / indisponibles), pour l'affichage.</summary>
    public IReadOnlyList<ModeleEnPause> ModelesEnPause() => _quotas.EnCours();

    /// <summary>Modèles gratuits d'OpenRouter : ceux du mode automatique d'abord, puis les autres.</summary>
    private async Task<IReadOnlyList<ModeleOpenRouter>> ModelesOpenRouterAsync(CancellationToken ct)
    {
        var catalogue = await _openRouter.ModelesAsync(ct);
        var auto = OpenRouterAutomatique;
        if (catalogue.Count == 0)   // catalogue injoignable : on s'en tient à la configuration
            return auto.Select(id => new ModeleOpenRouter(id, Json: true, Images: false)).ToList();
        int Rang(string id) { var i = Array.FindIndex(auto, a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }
        return catalogue.OrderBy(m => Rang(m.Id)).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Ordre du mode automatique : Gemini, Groq, OpenRouter (retenus et encore gratuits), puis NVIDIA.</summary>
    private async Task<List<string>> OrdreAutomatiqueAsync(CancellationToken ct)
    {
        var ordre = new List<string>();
        if (Configuree(CleGemini)) ordre.AddRange(ModelesGemini);
        if (Configuree(CleGroq)) ordre.AddRange(ModelesGroq);
        if (Configuree(CleOpenRouter))
        {
            var gratuits = (await ModelesOpenRouterAsync(ct)).Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ordre.AddRange(OpenRouterAutomatique.Where(gratuits.Contains));
        }
        if (Configuree(CleNvidia)) ordre.AddRange(ModelesNvidia);
        return ordre;
    }

    private string Fournisseur(string modele) =>
        modele.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) ? "Gemini"
        : ModelesGroq.Contains(modele, StringComparer.OrdinalIgnoreCase) ? "Groq"
        : ModelesNvidia.Contains(modele, StringComparer.OrdinalIgnoreCase) ? "NVIDIA"
        : "OpenRouter";

    private async Task<IEnumerable<string>> ModelesDuFournisseurAsync(string fournisseur, CancellationToken ct) => fournisseur switch
    {
        "Gemini" => ModelesGemini,
        "Groq" => ModelesGroq,
        "NVIDIA" => ModelesNvidia,
        _ => (await ModelesOpenRouterAsync(ct)).Select(m => m.Id)
    };

    private const int TailleMaxMediaOctets = 8 * 1024 * 1024;

    private static bool EstMediaSupporte(string? type) =>
        !string.IsNullOrWhiteSpace(type) && type.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <param name="modele">Modèle choisi par l'utilisateur ; null = automatique (de modèle en modèle).</param>
    /// <param name="produits">Produits de la base : l'IA doit choisir l'un d'eux (ou null).</param>
    /// <param name="modules">Modules de la base : l'IA choisit celui qui correspond au sujet (ou null).</param>
    public async Task<ResultatIa> AnalyserAsync(string message, string expediteurNom, string email,
        IReadOnlyList<(string MimeType, byte[] Data)>? medias = null, string? modele = null, CancellationToken ct = default,
        IReadOnlyList<string>? produits = null, IReadOnlyList<string>? modules = null)
    {
        var prompt = PromptUtilisateur(message, expediteurNom, email, produits, modules);
        var images = (medias ?? Array.Empty<(string, byte[])>())
            .Where(m => EstMediaSupporte(m.MimeType) && m.Data.Length is > 0 and <= TailleMaxMediaOctets).ToList();

        if (!string.IsNullOrWhiteSpace(modele))
        {
            // Modèle choisi : lui seul, pas de bascule — c'est l'utilisateur qui décide.
            if (_quotas.EstEnPause(modele, out var enQuota)) return new ResultatIa(null, new BlocageIa(modele, enQuota));
            var r = await AppelerAsync(modele, prompt, images, ct);
            return r.Issue switch
            {
                Issue.Ok => new ResultatIa(r.Analyse, null),
                Issue.Quota => new ResultatIa(null, new BlocageIa(modele, true)),
                Issue.Indisponible => new ResultatIa(null, new BlocageIa(modele, false)),
                _ => new ResultatIa(null, null)
            };
        }

        // Automatique : de modèle en modèle, en sautant ceux qui sont en pause.
        var ordre = await OrdreAutomatiqueAsync(ct);
        bool quota = false, panne = false;
        foreach (var m in ordre)
        {
            if (_quotas.EstEnPause(m, out var q)) { quota |= q; continue; }
            var r = await AppelerAsync(m, prompt, images, ct);
            switch (r.Issue)
            {
                case Issue.Ok: return new ResultatIa(r.Analyse, null);
                case Issue.Quota: quota = true; break;
                case Issue.Erreur: panne = true; break;
            }
        }

        // Aucun modèle n'a répondu. Panne passagère → pas d'alerte ; sinon tous sont épuisés → on prévient.
        if (panne || ordre.Count == 0) return new ResultatIa(null, null);
        _logger.LogWarning("IA : aucun modèle disponible ({Raison}).", quota ? "quotas atteints" : "modèles indisponibles");
        return new ResultatIa(null, new BlocageIa(null, quota, TousLesModeles: true));
    }

    private enum Issue { Ok, Quota, Indisponible, Erreur }

    /// <summary>
    /// Issue d'un appel à UN modèle : délai de reprise annoncé en cas de quota, et si la limite vaut pour
    /// tout le compte du fournisseur (OpenRouter : gratuits par minute / par jour ; NVIDIA : crédits).
    /// </summary>
    private record Appel(Issue Issue, AnalyseEmail? Analyse = null, TimeSpan? Reprise = null,
        bool ToutLeFournisseur = false, bool Journalier = false);

    /// <summary>Appelle un modèle et tient à jour sa mise en pause (quota / saturé / indisponible).</summary>
    private async Task<Appel> AppelerAsync(string modele, string prompt,
        IReadOnlyList<(string MimeType, byte[] Data)> images, CancellationToken ct)
    {
        var fournisseur = Fournisseur(modele);
        var r = fournisseur switch
        {
            "Gemini" => await GeminiAsync(modele, prompt, images, ct),
            "Groq" => await GroqAsync(modele, prompt, ct),
            "NVIDIA" => await NvidiaAsync(modele, prompt, ct),
            _ => await OpenRouterAsync(modele, prompt, images, ct)
        };
        switch (r.Issue)
        {
            case Issue.Ok:
                _quotas.Liberer(modele);
                break;
            case Issue.Quota when r.ToutLeFournisseur:
                // Limite du compte : tous les modèles de ce fournisseur sont épuisés jusqu'à la reprise.
                var duree = Borner(r.Reprise, r.Journalier ? TimeSpan.FromHours(24) : TimeSpan.FromHours(1));
                foreach (var m in await ModelesDuFournisseurAsync(fournisseur, ct)) _quotas.Mettre(m, true, duree);
                _quotas.Mettre(modele, true, duree);
                break;
            case Issue.Quota:
                _quotas.Mettre(modele, true, Borner(r.Reprise, TimeSpan.FromHours(1)));
                break;
            case Issue.Indisponible:
                _quotas.Mettre(modele, false, PauseIndisponible);
                break;
        }
        return r;
    }

    /// <summary>Délai de reprise annoncé par le fournisseur, borné entre 30 s et <paramref name="max"/>.</summary>
    private static TimeSpan Borner(TimeSpan? delai, TimeSpan max) =>
        delai is { } d ? TimeSpan.FromSeconds(Math.Clamp(d.TotalSeconds, 30, max.TotalSeconds)) : PauseQuotaParDefaut;

    /// <summary>Modèle saturé (pas de réponse à temps, surchargé) : écarté quelques minutes, on passe au suivant.</summary>
    private Appel Sature(string fournisseur, string modele, string raison)
    {
        _logger.LogWarning("{F} {M} : {Raison} → modèle suivant.", fournisseur, modele, raison);
        return new Appel(Issue.Quota, Reprise: PauseSature);
    }

    private static bool EstIndisponible(int statut, string texte) =>
        statut == 404 || texte.Contains("decommissioned", StringComparison.OrdinalIgnoreCase)
            || texte.Contains("model_not_found", StringComparison.OrdinalIgnoreCase)
            || texte.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? DelaiRetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        return ra?.Delta ?? (ra?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
    }

    private static string PromptSysteme() =>
        "Tu es l'agent IA du help desk de la société Mediasoft. Analyse les e-mails de support et " +
        "produis des tickets structurés en JSON. Réponds UNIQUEMENT avec un objet JSON valide, rien d'autre.";

    private static string PromptUtilisateur(string message, string nom, string email,
        IReadOnlyList<string>? produits = null, IReadOnlyList<string>? modules = null) => $@"
INFORMATIONS SUR L'EXPEDITEUR :
- Nom : {nom}
- Email : {email}

MESSAGE DU CLIENT :
""""""
{message}
""""""

RÈGLES :
- Réponds UNIQUEMENT avec un objet JSON valide.
- ""estUnProbleme"" : true UNIQUEMENT si le message décrit un problème, une erreur, une panne, ou pose une
  question / fait une demande PRÉCISE à l'équipe de support (ex. « la validation des factures renvoie une erreur »,
  « comment exporter la balance ? »). Un objet seul qui signale un problème suffit, même sans détail ni
  texte dans le corps (ex. « Problem divalto », « Erreur facture », « Bug import Olympia ») : true.
  false dans tous les autres cas : simple test (« test », « essai », « Test HELPDESK »), message vide ou sans
  demande précise, remerciements, confirmation, salutations, invitation à une réunion, compte rendu, newsletter,
  publicité, notification automatique, réponse d'absence. En cas de doute : false.
- Si une information est absente, mets null (ne l'invente jamais).
- ""priorite"" : BASSE, MOYENNE, HAUTE ou URGENTE (URGENTE seulement si blocage total).
- ""resume"" : titre court du problème (max 10 mots), en français.
- ""description"" : reformulation claire et professionnelle, en français.
{(produits is { Count: > 0 }
    ? $"- \"produit\" : EXACTEMENT l'un de ces produits, UNIQUEMENT s'il est cité explicitement dans le message (objet, texte ou image) : {string.Join(", ", produits)} ; sinon null. Ne devine jamais le produit.\n"
    : "")}{(modules is { Count: > 0 }
    ? $"- \"module\" : EXACTEMENT l'un de ces modules, celui qui correspond au sujet du message (ex. un règlement → Règlement, une facture ou une écriture → Comptabilité, une commande client → Vent, une commande fournisseur → Achat) : {string.Join(", ", modules)} ; null si aucun ne correspond clairement.\n"
    : "")}
FORMAT (JSON strict) :
{{""estUnProbleme"":<true|false>,""nomClient"":""{nom}"",""societe"":null,""resume"":""titre court"",""description"":""description claire"",""categorie"":""FACTURATION|TECHNIQUE|ACCES_COMPTE|DEMANDE_INFO|AUTRE"",""priorite"":""MOYENNE"",""produit"":null,""module"":null,""version"":null}}";

    // ─── Gemini (comme ai.js : 503 → 1 nouvel essai après 5 s ; 429 → quota) ─────────────────────
    private async Task<Appel> GeminiAsync(string modele, string prompt,
        IReadOnlyList<(string MimeType, byte[] Data)> images, CancellationToken ct)
    {
        if (!Configuree(CleGemini)) return new Appel(Issue.Indisponible);

        var parts = new List<object> { new { text = prompt } };
        foreach (var (type, data) in images)
            parts.Add(new { inline_data = new { mime_type = type, data = Convert.ToBase64String(data) } });
        var corps = JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts } },
            generationConfig = new { temperature = 0.2, responseMimeType = "application/json" }
        });
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modele}:generateContent?key={CleGemini}";
        var client = _http.CreateClient();
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(DelaiReponse);

        for (var essai = 1; essai <= 2; essai++)
        {
            try
            {
                using var resp = await client.PostAsync(url, new StringContent(corps, Encoding.UTF8, "application/json"), delai.Token);
                var statut = (int)resp.StatusCode;
                if (statut == 503 && essai < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), delai.Token);
                    continue;
                }
                var texte = await resp.Content.ReadAsStringAsync(delai.Token);
                if (statut == 429 || (!resp.IsSuccessStatusCode &&
                    (texte.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                     texte.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase))))
                {
                    _logger.LogWarning("Gemini {M} : quota atteint.", modele);
                    return new Appel(Issue.Quota, Reprise: DelaiGemini(texte));
                }
                if (statut == 503) return Sature("Gemini", modele, "surchargé (503)");
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Gemini {M} : HTTP {S}.", modele, statut);
                    return new Appel(EstIndisponible(statut, texte) ? Issue.Indisponible : Issue.Erreur);
                }
                using var doc = JsonDocument.Parse(texte);
                var json = doc.RootElement.GetProperty("candidates")[0].GetProperty("content")
                    .GetProperty("parts")[0].GetProperty("text").GetString();
                var a = Parser(json);
                return a != null ? new Appel(Issue.Ok, a) : new Appel(Issue.Erreur);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Sature("Gemini", modele, $"pas de réponse en {DelaiReponse.TotalSeconds:0} s");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Gemini {M} : erreur.", modele);
                return new Appel(Issue.Erreur);
            }
        }
        return new Appel(Issue.Erreur);
    }

    /// <summary>Délai de reprise fourni par Gemini dans un 429 (« retryDelay »: « 41s »).</summary>
    private static TimeSpan? DelaiGemini(string texte)
    {
        var m = Regex.Match(texte, @"""retryDelay""\s*:\s*""(\d+(?:\.\d+)?)s""");
        return m.Success ? TimeSpan.FromSeconds(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) : null;
    }

    // ─── Groq (comme ai.js : 429 → 1 nouvel essai si l'attente est courte, sinon quota) ───────────
    private async Task<Appel> GroqAsync(string modele, string prompt, CancellationToken ct)
    {
        if (!Configuree(CleGroq)) return new Appel(Issue.Indisponible);

        var corps = JsonSerializer.Serialize(new
        {
            model = modele,
            messages = new object[]
            {
                new { role = "system", content = PromptSysteme() },
                new { role = "user", content = prompt }
            },
            temperature = 0.2,
            response_format = new { type = "json_object" },
            max_tokens = 1024
        });
        var client = _http.CreateClient();
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(DelaiReponse);

        for (var essai = 1; essai <= 2; essai++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions")
                { Content = new StringContent(corps, Encoding.UTF8, "application/json") };
                req.Headers.Add("Authorization", $"Bearer {CleGroq}");

                using var resp = await client.SendAsync(req, delai.Token);
                var statut = (int)resp.StatusCode;
                if (statut == 429)
                {
                    var attente = DelaiRetryAfter(resp);
                    if (essai < 2 && (attente == null || attente <= TimeSpan.FromSeconds(5)))
                    {
                        await Task.Delay(attente is { } a0 && a0 > TimeSpan.Zero ? a0 : TimeSpan.FromSeconds(3), delai.Token);
                        continue;
                    }
                    _logger.LogWarning("Groq {M} : quota / limite atteint.", modele);
                    return new Appel(Issue.Quota, Reprise: attente);
                }
                if (statut == 503) return Sature("Groq", modele, "surchargé (503)");
                var texte = await resp.Content.ReadAsStringAsync(delai.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Groq {M} : HTTP {S}.", modele, statut);
                    return new Appel(EstIndisponible(statut, texte) ? Issue.Indisponible : Issue.Erreur);
                }
                var a = Parser(ContenuChat(texte));
                return a != null ? new Appel(Issue.Ok, a) : new Appel(Issue.Erreur);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Sature("Groq", modele, $"pas de réponse en {DelaiReponse.TotalSeconds:0} s");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Groq {M} : erreur.", modele);
                return new Appel(Issue.Erreur);
            }
        }
        return new Appel(Issue.Erreur);
    }

    // ─── OpenRouter (modèles gratuits) ─────────────────────────────────────────────────────────────
    // 429 « rate-limited upstream » : CE modèle est saturé → pause courte, modèle suivant.
    // 429 « free-models-per-day / per-min » : limite du COMPTE → tous les modèles gratuits en pause.
    private async Task<Appel> OpenRouterAsync(string modele, string prompt,
        IReadOnlyList<(string MimeType, byte[] Data)> images, CancellationToken ct)
    {
        if (!Configuree(CleOpenRouter)) return new Appel(Issue.Indisponible);

        var info = (await ModelesOpenRouterAsync(ct))
            .FirstOrDefault(m => string.Equals(m.Id, modele, StringComparison.OrdinalIgnoreCase));
        object contenu = images.Count > 0 && info?.Images == true
            ? new object[] { new { type = "text", text = prompt } }
                .Concat(images.Select(i => (object)new
                {
                    type = "image_url",
                    image_url = new { url = $"data:{i.MimeType};base64,{Convert.ToBase64String(i.Data)}" }
                })).ToArray()
            : prompt;
        var messages = new object[]
        {
            new { role = "system", content = PromptSysteme() },
            new { role = "user", content = contenu }
        };
        var corps = info?.Json == false
            ? JsonSerializer.Serialize(new { model = modele, messages, temperature = 0.2, max_tokens = 1024 })
            : JsonSerializer.Serialize(new { model = modele, messages, temperature = 0.2, max_tokens = 1024,
                response_format = new { type = "json_object" } });

        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(DelaiReponse);   // modèles gratuits parfois lents : on ne bloque pas le cycle
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
            { Content = new StringContent(corps, Encoding.UTF8, "application/json") };
            req.Headers.Add("Authorization", $"Bearer {CleOpenRouter}");
            req.Headers.TryAddWithoutValidation("X-Title", "HelpDesk Mediasoft");
            if (!string.IsNullOrWhiteSpace(_config["App:BaseUrl"]))
                req.Headers.TryAddWithoutValidation("HTTP-Referer", _config["App:BaseUrl"]);

            using var resp = await _http.CreateClient().SendAsync(req, delai.Token);
            var statut = (int)resp.StatusCode;
            var texte = await resp.Content.ReadAsStringAsync(delai.Token);
            if (statut == 429)
            {
                var journalier = texte.Contains("per-day", StringComparison.OrdinalIgnoreCase);
                var compte = journalier || texte.Contains("per-min", StringComparison.OrdinalIgnoreCase);
                _logger.LogWarning("OpenRouter {M} : {Raison}.", modele,
                    journalier ? "quota journalier gratuit du compte atteint" : compte ? "limite par minute du compte" : "modèle saturé");
                var reprise = ResetOpenRouter(resp, texte) ?? DelaiRetryAfter(resp)
                    ?? (compte && !journalier ? TimeSpan.FromSeconds(60) : null);
                return new Appel(Issue.Quota, Reprise: reprise, ToutLeFournisseur: compte, Journalier: journalier);
            }
            if (statut is 502 or 503) return Sature("OpenRouter", modele, $"surchargé ({statut})");
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenRouter {M} : HTTP {S}.", modele, statut);
                return new Appel(statut == 402 || EstIndisponible(statut, texte) ? Issue.Indisponible : Issue.Erreur);
            }
            // Un fournisseur en échec peut renvoyer 200 sans « choices » : on passe au modèle suivant.
            var a = Parser(ContenuChat(texte));
            return a != null ? new Appel(Issue.Ok, a) : new Appel(Issue.Erreur);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Sature("OpenRouter", modele, $"pas de réponse en {DelaiReponse.TotalSeconds:0} s");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "OpenRouter {M} : erreur.", modele);
            return new Appel(Issue.Erreur);
        }
    }

    /// <summary>Heure de remise à zéro annoncée par OpenRouter (X-RateLimit-Reset, en millisecondes).</summary>
    private static TimeSpan? ResetOpenRouter(HttpResponseMessage resp, string texte)
    {
        string? brut = resp.Headers.TryGetValues("X-RateLimit-Reset", out var v) ? v.FirstOrDefault() : null;
        brut ??= Regex.Match(texte, @"""X-RateLimit-Reset""\s*:\s*""?(\d{10,13})").Groups[1].Value;
        if (!long.TryParse(brut, out var n)) return null;
        var reset = n > 9_999_999_999 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
        var delai = reset - DateTimeOffset.UtcNow;
        return delai > TimeSpan.Zero ? delai : null;
    }

    // ─── NVIDIA (endpoints gratuits de build.nvidia.com) ──────────────────────────────────────────
    // Gratuits mais parfois en file d'attente : délai max, puis modèle suivant. La « réflexion » est
    // désactivée : sinon certains modèles écrivent leur raisonnement avant le JSON.
    private async Task<Appel> NvidiaAsync(string modele, string prompt, CancellationToken ct)
    {
        if (!Configuree(CleNvidia)) return new Appel(Issue.Indisponible);

        var corps = JsonSerializer.Serialize(new
        {
            model = modele,
            messages = new object[]
            {
                new { role = "system", content = PromptSysteme() },
                new { role = "user", content = prompt }
            },
            temperature = 0.2,
            max_tokens = 1024,
            chat_template_kwargs = new { enable_thinking = false, thinking = false }
        });

        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(DelaiReponse);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://integrate.api.nvidia.com/v1/chat/completions")
            { Content = new StringContent(corps, Encoding.UTF8, "application/json") };
            req.Headers.Add("Authorization", $"Bearer {CleNvidia}");

            using var resp = await _http.CreateClient().SendAsync(req, delai.Token);
            var statut = (int)resp.StatusCode;
            var texte = await resp.Content.ReadAsStringAsync(delai.Token);
            if (statut == 429)
            {
                _logger.LogWarning("NVIDIA {M} : limite de débit atteinte.", modele);
                return new Appel(Issue.Quota, Reprise: DelaiRetryAfter(resp) ?? TimeSpan.FromSeconds(60));
            }
            if (statut == 402)
            {
                _logger.LogWarning("NVIDIA : crédits du compte épuisés.");
                return new Appel(Issue.Quota, Reprise: TimeSpan.FromHours(1), ToutLeFournisseur: true);
            }
            if (statut is 502 or 503 or 504) return Sature("NVIDIA", modele, $"surchargé ({statut})");
            if (!resp.IsSuccessStatusCode)
            {
                // 404 « Function … not found for account » : modèle listé mais non ouvert à ce compte.
                _logger.LogWarning("NVIDIA {M} : HTTP {S}.", modele, statut);
                return new Appel(statut is 403 or 404 or 410 || EstIndisponible(statut, texte) ? Issue.Indisponible : Issue.Erreur);
            }
            var a = Parser(ContenuChat(texte));
            return a != null ? new Appel(Issue.Ok, a) : new Appel(Issue.Erreur);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Sature("NVIDIA", modele, $"pas de réponse en {DelaiReponse.TotalSeconds:0} s");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "NVIDIA {M} : erreur.", modele);
            return new Appel(Issue.Erreur);
        }
    }

    // ─── Lecture des réponses ─────────────────────────────────────────────────────────────────────

    /// <summary>Texte d'une réponse au format OpenAI (choices[0].message.content) — Groq, OpenRouter, NVIDIA.</summary>
    private static string? ContenuChat(string texte)
    {
        using var doc = JsonDocument.Parse(texte);
        if (!doc.RootElement.TryGetProperty("choices", out var choix)
            || choix.ValueKind != JsonValueKind.Array || choix.GetArrayLength() == 0) return null;
        return choix[0].TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
            && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
    }

    /// <summary>
    /// Lit le ticket dans la réponse. Tolérant : la réponse peut être entourée de ```json … ```, d'un
    /// court texte ou d'une réflexion (&lt;think&gt;…) qui cite le format — on garde le DERNIER objet JSON
    /// complet qui contient « resume » ou « estUnProbleme ».
    /// </summary>
    private static AnalyseEmail? Parser(string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return null;
        texte = Regex.Replace(texte, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        AnalyseEmail? trouve = null;
        for (var debut = texte.IndexOf('{'); debut >= 0; debut = texte.IndexOf('{', debut + 1))
        {
            var fin = FinObjet(texte, debut);
            if (fin < 0) continue;
            if (Lire(texte[debut..(fin + 1)]) is { } a) { trouve = a; debut = fin; }
        }
        return trouve;
    }

    /// <summary>Position de l'accolade qui ferme l'objet commençant à <paramref name="debut"/> (chaînes JSON comprises).</summary>
    private static int FinObjet(string t, int debut)
    {
        int profondeur = 0;
        bool chaine = false, echappe = false;
        for (var j = debut; j < t.Length; j++)
        {
            var c = t[j];
            if (chaine)
            {
                if (echappe) echappe = false;
                else if (c == '\\') echappe = true;
                else if (c == '"') chaine = false;
                continue;
            }
            if (c == '"') chaine = true;
            else if (c == '{') profondeur++;
            else if (c == '}' && --profondeur == 0) return j;
        }
        return -1;
    }

    private static AnalyseEmail? Lire(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object
                || !(r.TryGetProperty("resume", out _) || r.TryGetProperty("estUnProbleme", out _))) return null;
            string? S(string nom) => r.TryGetProperty(nom, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            // Un ticket seulement si l'IA dit explicitement « true » (champ absent ou ambigu → pas de ticket).
            var estPb = r.TryGetProperty("estUnProbleme", out var e)
                && (e.ValueKind == JsonValueKind.True
                    || (e.ValueKind == JsonValueKind.String && string.Equals(e.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
            return new AnalyseEmail(estPb, S("resume"), S("description"), S("societe"),
                S("nomClient"), S("produit"), S("version"), S("priorite"), S("categorie"), S("module"));
        }
        catch (JsonException) { return null; }
    }
}
