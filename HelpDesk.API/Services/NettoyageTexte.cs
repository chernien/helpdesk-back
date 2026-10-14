using System.Text.RegularExpressions;

namespace HelpDesk.API.Services;

/// <summary>
/// Nettoie le texte d'un e-mail avant d'en faire la description d'un ticket : caractères invisibles,
/// emplacements d'images et lignes de séparation. La colonne Description (type text, non Unicode) changerait
/// sinon chaque caractère invisible en « ? ».
/// </summary>
public static class NettoyageTexte
{
    /// <summary>Caractères invisibles : remplissage des aperçus d'e-mails publicitaires, césures, marques de direction…</summary>
    private static readonly Regex Invisibles = new(
        @"[­͏؜ᅟᅠ឴឵᠎​-‏‪-‮⁠-⁤⁪-⁯ㅤ﻿ﾠ]",
        RegexOptions.Compiled);

    /// <summary>Emplacements d'images de la version texte d'un e-mail : « [Image] », « [cid:image001.png@…] ».</summary>
    private static readonly Regex Images = new(@"\[(?:image|cid:[^\]]*)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Lignes de séparation (« ______ », « ----- », « ===== »).</summary>
    private static readonly Regex Separateurs = new(@"^[ \t]*[_\-=*─—]{5,}[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>« ? ? ? ? » : caractères invisibles déjà perdus en base (tickets enregistrés avant ce nettoyage).</summary>
    private static readonly Regex Remplissage = new(@"(?:\?[  ]*){4,}", RegexOptions.Compiled);

    private static readonly Regex EspacesDeLigne = new(@"[ \t ]+", RegexOptions.Compiled);
    private static readonly Regex LignesVides = new(@"\n{3,}", RegexOptions.Compiled);

    public static string? Nettoyer(string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return texte;
        var s = texte.Replace("\r\n", "\n");
        s = Invisibles.Replace(s, "");
        s = Images.Replace(s, "");
        s = Separateurs.Replace(s, "");
        s = Remplissage.Replace(s, " ");
        s = string.Join('\n', s.Split('\n').Select(l => EspacesDeLigne.Replace(l, " ").Trim()));
        s = LignesVides.Replace(s, "\n\n").Trim();
        return s;
    }
}
