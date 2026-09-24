namespace HelpDesk.API.Constants;

/// <summary>Valeurs autorisees des listes du formulaire de ticket (alignees sur la production).</summary>
public static class TicketReferentiel
{
    public static readonly string[] Types = ["Erreur", "Evolution", "Question", "Assistance"];
    public static readonly string[] Importances = ["Mineur", "Grave", "Bloquante"];
    public static readonly string[] Modules =
        ["Affaire", "Administration", "Règlement", "Comptabilité", "ERP", "CRM", "Paie", "Gestion"];
}
