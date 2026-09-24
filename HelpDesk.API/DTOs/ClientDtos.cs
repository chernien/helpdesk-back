namespace HelpDesk.API.DTOs;

/// <summary>Societe cliente (table Divalto CLI).</summary>
public class SocieteClientDto
{
    public string Tiers { get; set; } = null!;
    public string Nom { get; set; } = null!;
}

/// <summary>Contact d'une societe (table Divalto T2).</summary>
public class ContactClientDto
{
    public string Email { get; set; } = null!;
    public string? Nom { get; set; }
    public string? Prenom { get; set; }
    /// <summary>Le contact a deja un compte Helpdesk (il recevra ses tickets).</summary>
    public bool ACompte { get; set; }
}

/// <summary>Creation du compte Helpdesk d'un contact Divalto, depuis le formulaire de ticket.</summary>
public class CreerCompteContactRequest
{
    public string ClientTiers { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string Nom { get; set; } = null!;
    public string Prenom { get; set; } = null!;
    public string Password { get; set; } = null!;
}

/// <summary>Contact verifie avec le nom de sa societe (utilise a la creation d'un ticket).</summary>
public class ContactSocieteDto
{
    public string SocieteNom { get; set; } = null!;
    public string Email { get; set; } = null!;
}
