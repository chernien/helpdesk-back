using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text.Json;
using HelpDesk.API.DTOs;
using HelpDesk.API.Exceptions;
using HelpDesk.API.Models;
using HelpDesk.API.Repositories;
using Microsoft.AspNetCore.DataProtection;

namespace HelpDesk.API.Services;

public interface IMailboxService
{
    Task<MailboxDto> StatutAsync(UtilisateurContexte user);
    Task<MailboxDto> ConnecterAsync(MailboxConnectRequest request, UtilisateurContexte user);
    Task DeconnecterAsync(UtilisateurContexte user);
    /// <summary>URL Microsoft de connexion (OAuth délégué) vers laquelle rediriger l'utilisateur.</summary>
    string UrlAutorisation(string state);
    /// <summary>Échange le code d'autorisation Microsoft contre les jetons et enregistre la boîte.</summary>
    Task<MailboxDto> ConnecterOAuthAsync(string code, UtilisateurContexte user);
}

/// <summary>
/// Connexion / déconnexion de la boîte personnelle d'un utilisateur.
/// Le mot de passe est chiffré (Data Protection) et la connexion est testée avant d'être enregistrée.
/// </summary>
public class MailboxService : IMailboxService
{
    private readonly IMailboxRepository _repository;
    private readonly IDataProtector _protector;
    private readonly GraphMailService _graph;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;

    // Scopes demandés à Microsoft pour l'accès délégué à la boîte.
    private const string ScopesOAuth =
        "openid profile email offline_access " +
        "https://graph.microsoft.com/Mail.ReadWrite https://graph.microsoft.com/Mail.Send https://graph.microsoft.com/User.Read";

    public MailboxService(IMailboxRepository repository, IDataProtectionProvider dataProtection,
        GraphMailService graph, IHttpClientFactory http, IConfiguration config)
    {
        _repository = repository;
        _protector = dataProtection.CreateProtector(MailboxProtection.Purpose);
        _graph = graph;
        _http = http;
        _config = config;
    }

    public string UrlAutorisation(string state)
    {
        var clientId = _config["Outlook:ClientId"];
        var redirect = _config["Outlook:RedirectUri"] ?? "http://localhost:4200/oauth/callback";
        return "https://login.microsoftonline.com/common/oauth2/v2.0/authorize" +
               $"?client_id={Uri.EscapeDataString(clientId ?? "")}" +
               "&response_type=code" +
               $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
               "&response_mode=query" +
               $"&scope={Uri.EscapeDataString(ScopesOAuth)}" +
               $"&state={Uri.EscapeDataString(state)}" +
               "&prompt=select_account";
    }

    public async Task<MailboxDto> ConnecterOAuthAsync(string code, UtilisateurContexte user)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new MetierException("Code d'autorisation manquant.");

        var redirect = _config["Outlook:RedirectUri"] ?? "http://localhost:4200/oauth/callback";

        // 1) Échange du code contre access_token + refresh_token.
        var client = _http.CreateClient();
        var reponse = await client.PostAsync("https://login.microsoftonline.com/common/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _config["Outlook:ClientId"] ?? "",
                ["client_secret"] = _config["Outlook:ClientSecret"] ?? "",
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirect,
                ["scope"] = ScopesOAuth
            }));

        var jeton = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync()).RootElement;
        if (!reponse.IsSuccessStatusCode)
        {
            var err = jeton.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            throw new MetierException("Connexion Microsoft refusée." + (err != null ? $" ({err.Split('\n')[0]})" : ""));
        }

        if (!jeton.TryGetProperty("refresh_token", out var rt) || rt.GetString() is not { Length: > 0 } refreshToken)
            throw new MetierException("Microsoft n'a pas renvoyé de jeton de rafraîchissement (scope offline_access manquant ?).");
        var accessToken = jeton.GetProperty("access_token").GetString()!;

        // 2) Récupère l'adresse de la boîte connectée (/me).
        var me = _http.CreateClient();
        me.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var meResp = await me.GetAsync("https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName");
        var meJson = JsonDocument.Parse(await meResp.Content.ReadAsStringAsync()).RootElement;
        var email =
            (meJson.TryGetProperty("mail", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null)
            ?? (meJson.TryGetProperty("userPrincipalName", out var u) ? u.GetString() : null)
            ?? throw new MetierException("Impossible de lire l'adresse de la boîte connectée.");

        // 3) Enregistre la boîte : le refresh token (chiffré) sert d'accès durable.
        await _repository.EnregistrerAsync(new Mailbox
        {
            UserId = user.Id,
            Provider = "outlook-oauth",
            Email = email,
            Login = email,
            SecretChiffre = _protector.Protect(refreshToken),
            ImapHost = "", ImapPort = 0, ImapSsl = false,
            SmtpHost = "", SmtpPort = 0, SmtpStartTls = false,
            DateCreation = DateTime.Now
        });

        return await StatutAsync(user);
    }

    public async Task<MailboxDto> StatutAsync(UtilisateurContexte user)
    {
        var mb = await _repository.GetByUserAsync(user.Id);
        return mb == null
            ? new MailboxDto { Connectee = false }
            : new MailboxDto
            {
                Connectee = true,
                Provider = mb.Provider,
                Email = mb.Email,
                ImapHost = mb.ImapHost,
                SmtpHost = mb.SmtpHost,
                DepuisLe = mb.DateCreation
            };
    }

    public async Task<MailboxDto> ConnecterAsync(MailboxConnectRequest request, UtilisateurContexte user)
    {
        var email = (request.Email ?? "").Trim();
        if (!MailAddress.TryCreate(email, out _))
            throw new MetierException("Adresse e-mail invalide.");

        var provider = (request.Provider ?? "gmail").Trim().ToLowerInvariant();

        // Outlook / Microsoft 365 : pas de mot de passe. L'application accède à la boîte du membre
        // via Microsoft Graph (app-only), avec ses propres identifiants d'application.
        if (provider == "outlook")
        {
            await _graph.PourBoite(email).TesterAsync();

            await _repository.EnregistrerAsync(new Mailbox
            {
                UserId = user.Id,
                Provider = provider,
                Email = email,
                Login = email,
                SecretChiffre = "",          // aucun secret côté HelpDesk (l'app Graph gère l'accès)
                ImapHost = "", ImapPort = 0, ImapSsl = false,
                SmtpHost = "", SmtpPort = 0, SmtpStartTls = false,
                DateCreation = DateTime.Now
            });

            return await StatutAsync(user);
        }

        // Gmail / IMAP personnalisé : mot de passe requis (mot de passe d'application pour Gmail).
        if (string.IsNullOrWhiteSpace(request.Password))
            throw new MetierException("Le mot de passe est requis (pour Gmail, un mot de passe d'application).");

        var (imapHost, imapPort, imapSsl, smtpHost, smtpPort, smtpStartTls) = Serveurs(provider, request);
        var login = string.IsNullOrWhiteSpace(request.Login) ? email : request.Login!.Trim();

        var connexion = new MailboxConnexion(email, login, request.Password,
            imapHost, imapPort, imapSsl, smtpHost, smtpPort, smtpStartTls);

        // On vérifie que les identifiants fonctionnent AVANT d'enregistrer.
        await new ImapMailService(connexion).TesterAsync();

        await _repository.EnregistrerAsync(new Mailbox
        {
            UserId = user.Id,
            Provider = provider,
            Email = email,
            Login = login,
            SecretChiffre = _protector.Protect(request.Password),
            ImapHost = imapHost,
            ImapPort = imapPort,
            ImapSsl = imapSsl,
            SmtpHost = smtpHost,
            SmtpPort = smtpPort,
            SmtpStartTls = smtpStartTls,
            DateCreation = DateTime.Now
        });

        return await StatutAsync(user);
    }

    public Task DeconnecterAsync(UtilisateurContexte user) => _repository.SupprimerAsync(user.Id);

    /// <summary>Serveurs IMAP/SMTP préréglés par fournisseur, ou personnalisés pour « imap ». (Outlook passe par Graph, pas ici.)</summary>
    private static (string, int, bool, string, int, bool) Serveurs(string provider, MailboxConnectRequest r) => provider switch
    {
        "gmail" => ("imap.gmail.com", 993, true, "smtp.gmail.com", 587, true),
        "yahoo" => ("imap.mail.yahoo.com", 993, true, "smtp.mail.yahoo.com", 587, true),
        "imap" => (
            r.ImapHost?.Trim() ?? throw new MetierException("Le serveur IMAP est requis."),
            r.ImapPort ?? 993, r.ImapSsl ?? true,
            r.SmtpHost?.Trim() ?? throw new MetierException("Le serveur SMTP est requis."),
            r.SmtpPort ?? 587, r.SmtpStartTls ?? true),
        _ => throw new MetierException("Fournisseur non pris en charge.")
    };
}
