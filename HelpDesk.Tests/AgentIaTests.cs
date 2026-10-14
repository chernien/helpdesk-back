using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelpDesk.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HelpDesk.Tests;

/// <summary>
/// Choix du modèle IA et quotas :
/// - automatique : bascule de modèle en modèle (Gemini, Groq, OpenRouter, NVIDIA), saute ceux en pause,
///   prévient quand TOUS sont épuisés ;
/// - modèle choisi : lui seul, l'utilisateur est prévenu dès qu'il atteint son quota.
/// Aucun appel réseau réel : les réponses des fournisseurs sont simulées.
/// </summary>
public class AgentIaTests
{
    private const string ReponseGemini =
        """{"candidates":[{"content":{"parts":[{"text":"{\"estUnProbleme\":true,\"resume\":\"Erreur facture\"}"}]}}]}""";
    private const string ReponseChat =
        """{"choices":[{"message":{"content":"{\"estUnProbleme\":true,\"resume\":\"Erreur facture\"}"}}]}""";

    private const string Gemini1 = "gemini-3.5-flash";
    private const string Gemini2 = "gemini-3.5-flash-lite";
    private const string Gemini3 = "gemini-3.1-flash-lite";

    private const string Nemotron = "nvidia/nemotron-3-super-120b-a12b:free";
    private const string Gemma = "google/gemma-4-31b-it:free";
    private const string RouteurGratuit = "openrouter/free";
    private const string Laguna = "poolside/laguna-s-2.1:free";

    private const string Ultra = "nvidia/nemotron-3-ultra-550b-a55b";
    private const string Lightning = "nvidia/nemotron-3.5-lightning-30b-a3b";

    /// <summary>Extrait du catalogue OpenRouter : gratuits utiles, plus des modèles à écarter.</summary>
    private const string CatalogueJson = """
        {"data":[
          {"id":"nvidia/nemotron-3-super-120b-a12b:free","pricing":{"prompt":"0","completion":"0"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["response_format","temperature"]},
          {"id":"google/gemma-4-31b-it:free","pricing":{"prompt":"0","completion":"0"},"architecture":{"input_modalities":["image","text"],"output_modalities":["text"]},"supported_parameters":["response_format"]},
          {"id":"openrouter/free","pricing":{"prompt":"0","completion":"0"},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["response_format"]},
          {"id":"poolside/laguna-s-2.1:free","pricing":{"prompt":"0","completion":"0"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["temperature"]},
          {"id":"google/lyria-3-pro-preview","pricing":{"prompt":"0","completion":"0"},"architecture":{"input_modalities":["text"],"output_modalities":["text","audio"]},"supported_parameters":[]},
          {"id":"nvidia/nemotron-3.5-content-safety:free","pricing":{"prompt":"0","completion":"0"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":[]},
          {"id":"openai/gpt-5","pricing":{"prompt":"0.00000125","completion":"0.00001"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["response_format"]}
        ]}
        """;

    private static readonly string[] TousLesModeles =
        { Gemini1, Gemini2, Gemini3, "qwen/qwen3.8-27b", "openai/gpt-oss-20b" };

    /// <summary>Faux réseau : une réponse null simule un modèle qui ne répond jamais.</summary>
    private sealed class FauxHttp : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage?> _repondre;
        public List<(string Url, string Corps)> Appels { get; } = new();
        public FauxHttp(Func<HttpRequestMessage, string, HttpResponseMessage?> repondre) => _repondre = repondre;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var corps = req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);
            Appels.Add((req.RequestUri!.ToString(), corps));
            // Le catalogue OpenRouter est toujours servi (lecture des modèles gratuits).
            if (req.RequestUri!.AbsoluteUri == "https://openrouter.ai/api/v1/models")
                return Repondre(HttpStatusCode.OK, CatalogueJson);
            var reponse = _repondre(req, corps);
            if (reponse != null) return reponse;
            await Task.Delay(Timeout.Infinite, ct);   // ne répond jamais : coupé par le délai de l'agent
            throw new OperationCanceledException();
        }

        public int VersGemini => Appels.Count(a => a.Url.Contains("generativelanguage.googleapis.com"));
        public int VersGroq => Appels.Count(a => a.Url.Contains("api.groq.com"));
        public int VersOpenRouter => Appels.Count(a => a.Url.Contains("openrouter.ai/api/v1/chat"));
        public int VersNvidia => Appels.Count(a => a.Url.Contains("integrate.api.nvidia.com"));
        public int Vers(string modele) => Appels.Count(a => a.Url.Contains($"/models/{modele}:") || a.Corps.Contains($"\"model\":\"{modele}\""));
    }

    private sealed class Fabrique(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static HttpResponseMessage Repondre(HttpStatusCode code, string corps = "{}") =>
        new(code) { Content = new StringContent(corps, Encoding.UTF8, "application/json") };

    /// <summary>429 Groq avec un délai de reprise long (pas de nouvel essai immédiat).</summary>
    private static HttpResponseMessage QuotaGroq()
    {
        var r = Repondre((HttpStatusCode)429);
        r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        return r;
    }

    private static HttpResponseMessage SatureOpenRouter(string modele) => Repondre((HttpStatusCode)429,
        """{"error":{"message":"Provider returned error","code":429,"metadata":{"raw":"MODELE is temporarily rate-limited upstream.","limit_source":"upstream_provider_shared_pool"}}}"""
            .Replace("MODELE", modele));

    private static bool EstGemini(HttpRequestMessage r) => r.RequestUri!.Host.Contains("googleapis");
    private static bool EstOpenRouter(HttpRequestMessage r) => r.RequestUri!.Host.Contains("openrouter.ai");
    private static bool EstNvidia(HttpRequestMessage r) => r.RequestUri!.Host.Contains("nvidia.com");
    private static string ModeleGemini(HttpRequestMessage r) => Regex.Match(r.RequestUri!.AbsolutePath, @"models/([^:]+):").Groups[1].Value;
    private static string ModeleChat(string corps) => Regex.Match(corps, @"""model"":""([^""]+)""").Groups[1].Value;

    private static AgentIaService Service(FauxHttp http, QuotaModelesIa? quotas = null,
        bool gemini = true, bool groq = true, bool openRouter = false, bool nvidia = false,
        Dictionary<string, string?>? reglages = null)
    {
        var valeurs = new Dictionary<string, string?>
        {
            ["OutlookAgent:GeminiApiKey"] = gemini ? "cle-gemini" : null,
            ["OutlookAgent:GroqApiKey"] = groq ? "cle-groq" : null,
            ["OutlookAgent:OpenRouterApiKey"] = openRouter ? "cle-openrouter" : null,
            ["OutlookAgent:NvidiaApiKey"] = nvidia ? "cle-nvidia" : null,
            ["OutlookAgent:OpenRouterModeles:0"] = Nemotron,
            ["OutlookAgent:OpenRouterModeles:1"] = Gemma,
            ["OutlookAgent:OpenRouterModeles:2"] = RouteurGratuit,
            ["OutlookAgent:NvidiaModeles:0"] = Ultra,
            ["OutlookAgent:NvidiaModeles:1"] = Lightning,
        };
        foreach (var (cle, valeur) in reglages ?? new()) valeurs[cle] = valeur;
        var config = new ConfigurationBuilder().AddInMemoryCollection(valeurs).Build();
        var fabrique = new Fabrique(http);
        return new AgentIaService(fabrique, config, NullLogger<AgentIaService>.Instance, quotas ?? new QuotaModelesIa(),
            new CatalogueOpenRouter(fabrique, NullLogger<CatalogueOpenRouter>.Instance));
    }

    private static Task<ResultatIa> Analyser(AgentIaService ia, string? modele = null,
        IReadOnlyList<(string, byte[])>? images = null) =>
        ia.AnalyserAsync("Objet: facture", "Client", "c@x.tn", images, modele);

    // ─── Modèle choisi par l'utilisateur ────────────────────────────────────────

    [Fact]
    public async Task Modele_choisi_en_quota_met_en_pause_sans_basculer()
    {
        var quotas = new QuotaModelesIa();
        var http = new FauxHttp((r, _) => Repondre((HttpStatusCode)429));
        var res = await Analyser(Service(http, quotas), Gemini1);

        Assert.Null(res.Analyse);
        Assert.Equal(new BlocageIa(Gemini1, Quota: true), res.Blocage);
        Assert.Equal(1, http.VersGemini);
        Assert.Equal(0, http.VersGroq);   // pas de bascule : c'est l'utilisateur qui choisit
        Assert.Contains(quotas.EnCours(), p => p.Id == Gemini1 && p.Quota);
    }

    [Fact]
    public async Task Modele_choisi_deja_en_quota_ne_rappelle_pas_l_IA()
    {
        var quotas = new QuotaModelesIa();
        quotas.Mettre(Gemini1, quota: true, TimeSpan.FromMinutes(10));
        var http = new FauxHttp((r, _) => Repondre(HttpStatusCode.OK, ReponseGemini));
        var res = await Analyser(Service(http, quotas), Gemini1);

        Assert.Equal(new BlocageIa(Gemini1, Quota: true), res.Blocage);
        Assert.Empty(http.Appels);
    }

    [Fact]
    public async Task Modele_choisi_Groq_n_appelle_que_ce_modele()
    {
        var http = new FauxHttp((r, _) => EstGemini(r) ? Repondre(HttpStatusCode.OK, ReponseGemini) : Repondre(HttpStatusCode.OK, ReponseChat));
        var res = await Analyser(Service(http), "openai/gpt-oss-20b");

        Assert.NotNull(res.Analyse);
        Assert.Null(res.Blocage);
        Assert.Equal(0, http.VersGemini);
        Assert.Contains("\"model\":\"openai/gpt-oss-20b\"", Assert.Single(http.Appels).Corps);
    }

    [Fact]
    public async Task Modele_choisi_retire_est_signale_indisponible()
    {
        var http = new FauxHttp((r, _) => Repondre(HttpStatusCode.NotFound, """{"error":{"code":"model_not_found"}}"""));
        var res = await Analyser(Service(http), "openai/gpt-oss-20b");

        Assert.Null(res.Analyse);
        Assert.Equal(new BlocageIa("openai/gpt-oss-20b", Quota: false), res.Blocage);
    }

    // ─── Automatique : de modèle en modèle ──────────────────────────────────────

    [Fact]
    public async Task Automatique_bascule_de_modele_en_modele_sans_alerte()
    {
        var http = new FauxHttp((r, _) => EstGemini(r) ? Repondre((HttpStatusCode)429) : Repondre(HttpStatusCode.OK, ReponseChat));
        var res = await Analyser(Service(http));

        Assert.NotNull(res.Analyse);
        Assert.Null(res.Blocage);          // un modèle a répondu : pas de popup
        Assert.Equal(3, http.VersGemini);  // les 3 modèles Gemini sont essayés l'un après l'autre
        Assert.Equal(1, http.VersGroq);
    }

    [Fact]
    public async Task Automatique_tous_les_modeles_en_quota_previent_l_utilisateur()
    {
        var http = new FauxHttp((r, _) => EstGemini(r) ? Repondre((HttpStatusCode)429) : QuotaGroq());
        var res = await Analyser(Service(http));

        Assert.Null(res.Analyse);
        Assert.Equal(new BlocageIa(null, Quota: true, TousLesModeles: true), res.Blocage);
        Assert.Equal(3, http.VersGemini);
        Assert.Equal(2, http.VersGroq);
    }

    [Fact]
    public async Task Automatique_saute_un_modele_en_quota_jusqu_a_sa_reprise()
    {
        var quotas = new QuotaModelesIa();
        var http = new FauxHttp((r, _) => EstGemini(r) && ModeleGemini(r) == Gemini1
            ? Repondre((HttpStatusCode)429, """{"error":{"details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"600s"}]}}""")
            : Repondre(HttpStatusCode.OK, ReponseGemini));
        var ia = Service(http, quotas);

        Assert.NotNull((await Analyser(ia)).Analyse);   // le 1er en quota → le 2e répond
        Assert.NotNull((await Analyser(ia)).Analyse);   // le 1er est sauté directement

        Assert.Equal(1, http.Vers(Gemini1));
        Assert.Equal(2, http.Vers(Gemini2));
        var pause = Assert.Single(quotas.EnCours());
        Assert.InRange((pause.Reprise - DateTime.UtcNow).TotalSeconds, 590, 601);   // délai annoncé par Gemini
    }

    [Fact]
    public async Task Automatique_tous_en_pause_ne_rappelle_pas_l_IA()
    {
        var quotas = new QuotaModelesIa();
        foreach (var m in TousLesModeles) quotas.Mettre(m, quota: true, TimeSpan.FromMinutes(5));
        var http = new FauxHttp((r, _) => Repondre(HttpStatusCode.OK, ReponseGemini));
        var res = await Analyser(Service(http, quotas));

        Assert.Equal(new BlocageIa(null, Quota: true, TousLesModeles: true), res.Blocage);
        Assert.Empty(http.Appels);
    }

    [Fact]
    public async Task Automatique_panne_passagere_ne_previent_pas()
    {
        var http = new FauxHttp((r, _) => EstGemini(r) ? Repondre((HttpStatusCode)429) : Repondre(HttpStatusCode.InternalServerError));
        var res = await Analyser(Service(http));

        Assert.Null(res.Analyse);
        Assert.Null(res.Blocage);   // Groq en panne passagère : pas un problème de quota
    }

    [Fact]
    public async Task Automatique_panne_sans_quota_ne_bloque_pas()
    {
        var http = new FauxHttp((r, _) => Repondre(HttpStatusCode.InternalServerError));
        var res = await Analyser(Service(http));

        Assert.Null(res.Analyse);
        Assert.Null(res.Blocage);
    }

    [Fact]
    public async Task Liste_des_modeles_selon_les_cles_configurees()
    {
        var ia = Service(new FauxHttp((_, _) => Repondre(HttpStatusCode.OK)), gemini: true, groq: false);
        var modeles = await ia.ModelesAsync();

        Assert.All(modeles, m => Assert.Equal("Gemini", m.Fournisseur));
        Assert.Equal(new[] { Gemini1, Gemini2, Gemini3 }, modeles.Select(m => m.Id));
        Assert.True(await ia.EstModeleConnuAsync(Gemini2));
        Assert.False(await ia.EstModeleConnuAsync("openai/gpt-oss-20b"));   // clé Groq absente
    }

    [Fact]
    public async Task Listes_de_modeles_modifiables_dans_la_configuration()
    {
        var ia = Service(new FauxHttp((_, _) => Repondre(HttpStatusCode.OK)), reglages: new()
        {
            ["OutlookAgent:GeminiModeles:0"] = "gemini-3.8-flash",
            ["OutlookAgent:GroqModeles:0"] = "llama-4-scout",
        });

        Assert.Equal(new[] { "gemini-3.8-flash", "llama-4-scout" }, (await ia.ModelesAsync()).Select(m => m.Id));
    }

    // ─── OpenRouter (modèles gratuits) ──────────────────────────────────────────

    [Fact]
    public void Catalogue_OpenRouter_ne_garde_que_les_modeles_gratuits_utiles()
    {
        using var doc = JsonDocument.Parse(CatalogueJson);
        var modeles = CatalogueOpenRouter.Filtrer(doc.RootElement.GetProperty("data"));

        Assert.Equal(new[] { Nemotron, Gemma, RouteurGratuit, Laguna }, modeles.Select(m => m.Id));   // ni payant, ni musique, ni modération
        Assert.True(modeles.Single(m => m.Id == Gemma).Images);
        Assert.False(modeles.Single(m => m.Id == Laguna).Json);
    }

    [Fact]
    public async Task OpenRouter_propose_tous_les_modeles_gratuits_ceux_du_mode_auto_d_abord()
    {
        var ia = Service(new FauxHttp((_, _) => Repondre(HttpStatusCode.OK)), gemini: false, groq: false, openRouter: true);
        var modeles = await ia.ModelesAsync();

        Assert.All(modeles, m => Assert.Equal("OpenRouter", m.Fournisseur));
        Assert.Equal(new[] { Nemotron, Gemma, RouteurGratuit, Laguna }, modeles.Select(m => m.Id));
    }

    [Fact]
    public async Task Automatique_passe_a_OpenRouter_quand_Gemini_et_Groq_sont_epuises()
    {
        var quotas = new QuotaModelesIa();
        var http = new FauxHttp((r, corps) =>
            EstGemini(r) ? Repondre((HttpStatusCode)429)
            : !EstOpenRouter(r) ? QuotaGroq()
            : ModeleChat(corps) == Nemotron ? SatureOpenRouter(Nemotron)
            : Repondre(HttpStatusCode.OK, ReponseChat));
        var res = await Analyser(Service(http, quotas, openRouter: true));

        Assert.NotNull(res.Analyse);
        Assert.Null(res.Blocage);
        Assert.Equal(1, http.Vers(Nemotron));   // saturé → modèle suivant
        Assert.Equal(1, http.Vers(Gemma));      // a répondu
        Assert.Contains(quotas.EnCours(), p => p.Id == Nemotron);
        Assert.DoesNotContain(quotas.EnCours(), p => p.Id == Gemma);   // seul le modèle saturé est écarté
    }

    [Fact]
    public async Task OpenRouter_quota_journalier_met_tous_les_modeles_gratuits_en_pause()
    {
        var quotas = new QuotaModelesIa();
        var reset = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeMilliseconds();
        var http = new FauxHttp((r, _) => Repondre((HttpStatusCode)429,
            """{"error":{"message":"Rate limit exceeded: free-models-per-day. Add 10 credits to unlock 1000 free model requests per day","code":429,"metadata":{"headers":{"X-RateLimit-Limit":"50","X-RateLimit-Remaining":"0","X-RateLimit-Reset":"RESET"}}}}"""
                .Replace("RESET", reset.ToString())));
        var res = await Analyser(Service(http, quotas, gemini: false, groq: false, openRouter: true));

        Assert.Equal(new BlocageIa(null, Quota: true, TousLesModeles: true), res.Blocage);
        Assert.Equal(1, http.VersOpenRouter);   // limite du compte : inutile d'essayer les autres modèles gratuits
        Assert.Equal(4, quotas.EnCours().Count);
        Assert.All(quotas.EnCours(), p => Assert.InRange((p.Reprise - DateTime.UtcNow).TotalHours, 2.9, 3.01));
    }

    [Fact]
    public async Task Reponse_entouree_de_balises_markdown_est_lue()
    {
        var http = new FauxHttp((r, _) => Repondre(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Voici le ticket :\n```json\n{\"estUnProbleme\":true,\"resume\":\"Erreur facture\"}\n```"}}]}"""));
        var res = await Analyser(Service(http, gemini: false, groq: false, openRouter: true), Laguna);

        Assert.Equal("Erreur facture", res.Analyse?.Resume);
        Assert.DoesNotContain("response_format", http.Appels.Single(a => a.Url.Contains("/chat/")).Corps);   // non géré par ce modèle
    }

    [Fact]
    public async Task Reflexion_qui_cite_le_format_puis_reponse_finale()
    {
        var http = new FauxHttp((r, _) => Repondre(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Réflexion : le format attendu est {\"estUnProbleme\":true,\"resume\":\"titre court\"}.\nRéponse : {\"estUnProbleme\":true,\"resume\":\"Erreur facture\",\"priorite\":\"URGENTE\"}"}}]}"""));
        var res = await Analyser(Service(http), "qwen/qwen3.8-27b");

        Assert.Equal("Erreur facture", res.Analyse?.Resume);   // le dernier objet, pas l'exemple du format
        Assert.Equal("URGENTE", res.Analyse?.Priorite);
    }

    [Fact]
    public async Task OpenRouter_envoie_les_images_aux_seuls_modeles_qui_les_lisent()
    {
        var http = new FauxHttp((r, _) => Repondre(HttpStatusCode.OK, ReponseChat));
        var ia = Service(http, gemini: false, groq: false, openRouter: true);
        var images = new List<(string, byte[])> { ("image/png", new byte[] { 1, 2, 3 }) };

        await Analyser(ia, Gemma, images);
        await Analyser(ia, Nemotron, images);

        var appels = http.Appels.Where(a => a.Url.Contains("/chat/")).ToList();
        Assert.Contains("image_url", appels[0].Corps);        // Gemma lit les images
        Assert.DoesNotContain("image_url", appels[1].Corps);  // Nemotron : texte seul
    }

    // ─── NVIDIA (endpoints gratuits) ────────────────────────────────────────────

    [Fact]
    public async Task Nvidia_desactive_la_reflexion_et_lit_le_json()
    {
        var http = new FauxHttp((r, _) => Repondre(HttpStatusCode.OK, ReponseChat));
        var res = await Analyser(Service(http, nvidia: true), Ultra);

        Assert.Equal("Erreur facture", res.Analyse?.Resume);
        var appel = Assert.Single(http.Appels);
        Assert.StartsWith("https://integrate.api.nvidia.com/v1/chat/completions", appel.Url);
        Assert.Contains("\"enable_thinking\":false", appel.Corps);
    }

    [Fact]
    public async Task Nvidia_modele_non_ouvert_au_compte_est_indisponible()
    {
        var http = new FauxHttp((r, _) => Repondre(HttpStatusCode.NotFound,
            """{"status":404,"title":"Not Found","detail":"Function '84bf12ff': Not found for account 'abc'"}"""));
        var res = await Analyser(Service(http, nvidia: true), Lightning);

        Assert.Equal(new BlocageIa(Lightning, Quota: false), res.Blocage);
    }

    [Fact]
    public async Task Modele_sans_reponse_a_temps_est_ecarte_cinq_minutes()
    {
        var quotas = new QuotaModelesIa();
        var http = new FauxHttp((r, _) => null);   // file d'attente : jamais de réponse
        var ia = Service(http, quotas, nvidia: true, reglages: new() { ["OutlookAgent:DelaiReponseSecondes"] = "1" });
        var res = await Analyser(ia, Ultra);

        Assert.Equal(new BlocageIa(Ultra, Quota: true), res.Blocage);
        var pause = Assert.Single(quotas.EnCours());
        Assert.InRange((pause.Reprise - DateTime.UtcNow).TotalMinutes, 4.9, 5.01);
    }

    [Fact]
    public async Task Nvidia_surcharge_est_ecarte_cinq_minutes()
    {
        var quotas = new QuotaModelesIa();
        var http = new FauxHttp((r, _) => Repondre(HttpStatusCode.ServiceUnavailable));
        var res = await Analyser(Service(http, quotas, nvidia: true), Ultra);

        Assert.Equal(new BlocageIa(Ultra, Quota: true), res.Blocage);
        Assert.InRange((Assert.Single(quotas.EnCours()).Reprise - DateTime.UtcNow).TotalMinutes, 4.9, 5.01);
    }

    [Fact]
    public async Task Automatique_NVIDIA_en_dernier_recours()
    {
        var http = new FauxHttp((r, _) =>
            EstGemini(r) ? Repondre((HttpStatusCode)429)
            : EstNvidia(r) ? Repondre(HttpStatusCode.OK, ReponseChat)
            : QuotaGroq());
        var res = await Analyser(Service(http, nvidia: true));

        Assert.NotNull(res.Analyse);
        Assert.Equal(3, http.VersGemini);
        Assert.Equal(2, http.VersGroq);
        Assert.Equal(1, http.VersNvidia);
        Assert.Contains("integrate.api.nvidia.com", http.Appels.Last().Url);   // essayé après tous les autres
    }

    [Fact]
    public async Task Liste_NVIDIA_ignore_un_modele_deja_servi_par_Groq()
    {
        var ia = Service(new FauxHttp((_, _) => Repondre(HttpStatusCode.OK)), gemini: false, nvidia: true, reglages: new()
        {
            ["OutlookAgent:NvidiaModeles:0"] = Ultra,
            ["OutlookAgent:NvidiaModeles:1"] = "openai/gpt-oss-20b",   // même identifiant que chez Groq
        });
        var modeles = await ia.ModelesAsync();

        Assert.Equal("Groq", Assert.Single(modeles, m => m.Id == "openai/gpt-oss-20b").Fournisseur);
        Assert.Equal(new[] { Ultra }, modeles.Where(m => m.Fournisseur == "NVIDIA").Select(m => m.Id));
    }

    // ─── Alerte : une seule fois par blocage ────────────────────────────────────

    [Fact]
    public void Blocage_signale_une_seule_fois_puis_de_nouveau_apres_reprise()
    {
        var stats = new AgentStatistiques();
        var quota = new BlocageIa(Gemini1, true);

        Assert.True(stats.SignalerBlocage(7, quota));    // 1re fois → popup
        Assert.False(stats.SignalerBlocage(7, quota));   // cycles suivants → pas de nouveau popup
        Assert.Equal(quota, stats.Pour(7).Blocage);

        stats.LeverBlocage(7);                           // modèle changé / répond de nouveau
        Assert.Null(stats.Pour(7).Blocage);
        Assert.True(stats.SignalerBlocage(7, quota));    // nouveau blocage → nouveau popup
        Assert.True(stats.SignalerBlocage(7, new BlocageIa(null, true, TousLesModeles: true)));   // tous épuisés → popup
        Assert.False(stats.SignalerBlocage(7, new BlocageIa(null, true, TousLesModeles: true)));
    }

    [Fact]
    public void Statistiques_conservent_le_blocage_entre_les_cycles()
    {
        var stats = new AgentStatistiques();
        stats.SignalerBlocage(3, new BlocageIa(Gemini1, true));
        stats.EnregistrerCycle(3, emails: 0, tickets: 0, erreur: null);

        Assert.NotNull(stats.Pour(3).Blocage);
        Assert.NotNull(stats.Pour(3).DerniereExecution);
    }
}
