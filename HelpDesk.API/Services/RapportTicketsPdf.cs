using System.Net;
using System.Text.RegularExpressions;
using HelpDesk.API.Constants;
using HelpDesk.API.Models;
using HelpDesk.API.Repositories;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace HelpDesk.API.Services;

/// <summary>
/// Rapport PDF des tickets (A4 paysage) : logo Mediasoft, titre, critères, synthèse par état,
/// puis un tableau (en-tête répété sur chaque page) et la pagination en pied de page.
/// </summary>
public static class RapportTicketsPdf
{
    private const string Police = "Segoe UI";
    /// <summary>Extrait de description affiché sous l'intitulé (le rapport reste lisible).</summary>
    private const int ExtraitMax = 260;
    private const int ResolutionMax = 160;

    private static readonly Color Bleu = Rgb(0x1D, 0x5F, 0xA8);
    private static readonly Color Encre = Rgb(0x1F, 0x29, 0x37);
    private static readonly Color Gris = Rgb(0x6B, 0x72, 0x80);
    private static readonly Color Zebre = Rgb(0xF4, 0xF7, 0xFB);
    private static readonly Color Trait = Rgb(0xDD, 0xE3, 0xEA);

    /// <summary>Couleurs des états, comme dans l'application (texte, fond de la pastille).</summary>
    private static readonly Dictionary<string, (Color Texte, Color Fond)> CouleursEtat = new()
    {
        [TicketEtat.Ouvert] = (Rgb(0x1D, 0x4E, 0xD8), Rgb(0xDB, 0xEA, 0xFE)),
        [TicketEtat.EnCours] = (Rgb(0x0E, 0x74, 0x90), Rgb(0xCF, 0xFA, 0xFE)),
        [TicketEtat.EnDeveloppement] = (Rgb(0x6D, 0x28, 0xD9), Rgb(0xED, 0xE9, 0xFE)),
        [TicketEtat.TestInterne] = (Rgb(0x93, 0x33, 0xEA), Rgb(0xF3, 0xE8, 0xFF)),
        [TicketEtat.EnAttenteValidation] = (Rgb(0xB4, 0x53, 0x09), Rgb(0xFE, 0xF3, 0xC7)),
        [TicketEtat.Cloture] = (Rgb(0x15, 0x80, 0x3D), Rgb(0xDC, 0xFC, 0xE7)),
        [TicketEtat.Annule] = (Rgb(0x47, 0x55, 0x69), Rgb(0xF1, 0xF5, 0xF9)),
    };

    /// <summary>Colonnes (titre, largeur en cm) : 27,3 cm utiles en A4 paysage.</summary>
    private static readonly (string Titre, double Largeur)[] Colonnes =
    [
        ("N°", 1.2), ("Créé le", 1.8), ("Jours en attente", 1.5), ("Client", 3.8), ("Produit", 2.3), ("Intitulé", 6.3),
        ("Type", 1.6), ("Importance", 1.6), ("État", 2.7), ("Responsable", 2.3), ("Résolution", 2.2)
    ];

    /// <summary>Colonne centrée (nombre de jours).</summary>
    private const int ColonneJours = 2;

    private static readonly Regex Balise = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Html = new(@"<(p|br|div|span|table|td|b|i|strong|a)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Espaces = new(@"\s+", RegexOptions.Compiled);

    private static readonly Lazy<string?> Logo = new(() =>
    {
        using var flux = typeof(RapportTicketsPdf).Assembly.GetManifestResourceStream("HelpDesk.API.logo-mediasoft.png");
        if (flux == null) return null;
        using var copie = new MemoryStream();
        flux.CopyTo(copie);
        return "base64:" + Convert.ToBase64String(copie.ToArray());
    });

    static RapportTicketsPdf()
    {
        GlobalFontSettings.FontResolver ??= new PolicesWindows();
    }

    /// <summary>Polices installées sur le serveur Windows (dossier Fonts) : Segoe UI, sinon Arial.</summary>
    private sealed class PolicesWindows : IFontResolver
    {
        private static readonly string Dossier = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _cache = new();

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            var segoe = (bold, italic) switch
            {
                (true, true) => "segoeuiz.ttf", (true, false) => "segoeuib.ttf",
                (false, true) => "segoeuii.ttf", _ => "segoeui.ttf"
            };
            var arial = (bold, italic) switch
            {
                (true, true) => "arialbi.ttf", (true, false) => "arialbd.ttf",
                (false, true) => "ariali.ttf", _ => "arial.ttf"
            };
            return new FontResolverInfo(File.Exists(Path.Combine(Dossier, segoe)) ? segoe : arial);
        }

        public byte[]? GetFont(string faceName) =>
            _cache.GetOrAdd(faceName, f => File.ReadAllBytes(Path.Combine(Dossier, f)));
    }

    /// <summary>Largeur utile d'une page (A4 paysage moins les marges).</summary>
    private static readonly Unit Largeur = Unit.FromCentimeter(27.3);

    /// <param name="nomsMembres">Id → prénom + nom des membres de l'équipe (colonne Responsable).</param>
    /// <param name="historique">Historique des tickets : jours en attente et résolution, calculés comme dans l'export Excel.</param>
    /// <param name="criteres">Ce que contient le rapport (« Traités par Karim Trabelsi · Produit Divalto », « Sélection de 5 tickets »…).</param>
    public static byte[] Generer(IReadOnlyList<TicketList> tickets, IReadOnlyDictionary<int, string> nomsMembres,
        ILookup<int, EvenementTicket> historique, string criteres, DateTime genereLe)
    {
        var doc = new Document();
        doc.Info.Title = "Rapport des tickets";
        doc.Info.Author = "Mediasoft";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = Police;
        normal.Font.Size = 8;
        normal.Font.Color = Encre;

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        // A4 paysage : largeur et hauteur données explicitement (MigraDoc 6 ne permute plus avec Orientation).
        section.PageSetup.PageWidth = Unit.FromCentimeter(29.7);
        section.PageSetup.PageHeight = Unit.FromCentimeter(21);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.2);
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.7);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.HeaderDistance = Unit.FromCentimeter(0.7);
        section.PageSetup.FooterDistance = Unit.FromCentimeter(0.6);
        // Page 1 : grand en-tête dans le corps ; pages suivantes : bandeau discret.
        section.PageSetup.DifferentFirstPageHeaderFooter = true;

        BandeauPagesSuivantes(section.Headers.Primary, criteres);
        PiedDePage(section.Footers.FirstPage, genereLe);
        PiedDePage(section.Footers.Primary, genereLe);
        EnTete(section, criteres);
        Synthese(section, tickets);
        Tableau(section, tickets, nomsMembres, historique);

        var rendu = new PdfDocumentRenderer { Document = doc };
        rendu.RenderDocument();
        using var sortie = new MemoryStream();
        rendu.PdfDocument.Save(sortie, false);
        return sortie.ToArray();
    }

    private static void PiedDePage(HeaderFooter pied, DateTime genereLe)
    {
        var p = pied.AddParagraph();
        p.Format.Font.Size = 7;
        p.Format.Font.Color = Gris;
        p.Format.Borders.Top.Width = 0.5;
        p.Format.Borders.Top.Color = Trait;
        p.Format.Borders.DistanceFromTop = Unit.FromMillimeter(1.5);
        p.Format.TabStops.AddTabStop(Largeur, TabAlignment.Right);
        p.AddText($"Mediasoft · Rapport des tickets · édité le {genereLe:dd/MM/yyyy} à {genereLe:HH:mm}");
        p.AddTab();
        p.AddText("Page ");
        p.AddPageField();
        p.AddText(" / ");
        p.AddNumPagesField();
    }

    /// <summary>Pages 2 et suivantes : « Rapport des tickets · critères » à gauche, « Mediasoft » à droite, filet dessous.</summary>
    private static void BandeauPagesSuivantes(HeaderFooter entete, string criteres)
    {
        var p = entete.AddParagraph();
        p.Format.Font.Size = 7.5;
        p.Format.Font.Color = Gris;
        p.Format.Borders.Bottom.Width = 0.5;
        p.Format.Borders.Bottom.Color = Trait;
        p.Format.Borders.DistanceFromBottom = Unit.FromMillimeter(1.5);
        p.Format.TabStops.AddTabStop(Largeur, TabAlignment.Right);
        var titre = p.AddFormattedText("Rapport des tickets", TextFormat.Bold);
        titre.Color = Bleu;
        p.AddText($"  ·  {criteres}");
        p.AddTab();
        p.AddFormattedText("Mediasoft", TextFormat.Bold);
    }

    /// <summary>
    /// Page 1 : logo (compact) à gauche, titre et critères centrés sur la page (colonne de droite
    /// vide, de même largeur que celle du logo), puis un filet bleu sur toute la largeur.
    /// </summary>
    private static void EnTete(Section section, string criteres)
    {
        var t = section.AddTable();
        t.LeftPadding = t.RightPadding = Unit.Zero;
        t.AddColumn(Unit.FromCentimeter(5.5));
        t.AddColumn(Unit.FromCentimeter(16.3));
        t.AddColumn(Unit.FromCentimeter(5.5));
        var ligne = t.AddRow();
        ligne.VerticalAlignment = VerticalAlignment.Center;

        if (Logo.Value is { } logo)
        {
            var image = ligne.Cells[0].AddImage(logo);
            image.Width = Unit.FromCentimeter(3.3);
            image.LockAspectRatio = true;
        }

        var centre = ligne.Cells[1];
        centre.Format.Alignment = ParagraphAlignment.Center;
        var titre = centre.AddParagraph("Rapport des tickets");
        titre.Format.Font.Size = 20;
        titre.Format.Font.Bold = true;
        titre.Format.Font.Color = Bleu;
        var sousTitre = centre.AddParagraph(criteres);
        sousTitre.Format.Font.Size = 9;
        sousTitre.Format.Font.Color = Gris;
        sousTitre.Format.SpaceBefore = Unit.FromPoint(2);

        var filet = section.AddParagraph();
        filet.Format.SpaceBefore = Unit.FromPoint(8);
        filet.Format.Borders.Bottom.Width = 1.2;
        filet.Format.Borders.Bottom.Color = Bleu;
        filet.Format.Font.Size = 1;
        filet.Format.SpaceAfter = Unit.FromPoint(12);
    }

    /// <summary>
    /// Cartes de synthèse, centrées : le total puis une carte par état présent (ordre du cycle de vie),
    /// chiffre en grand dans la couleur de l'état et liseré de la même couleur en haut.
    /// </summary>
    private static void Synthese(Section section, IReadOnlyList<TicketList> tickets)
    {
        var parEtat = tickets.GroupBy(t => TicketEtat.Normaliser(t.Etat) ?? "Non défini")
            .ToDictionary(g => g.Key, g => g.Count());
        var etats = TicketEtat.Tous.Where(parEtat.ContainsKey).Concat(parEtat.Keys.Except(TicketEtat.Tous)).ToList();

        var cartes = new List<(string Nombre, string Libelle, Color Texte, Color Fond)>
        {
            (tickets.Count.ToString(), tickets.Count > 1 ? "Tickets" : "Ticket", Bleu, Rgb(0xEA, 0xF1, 0xFA))
        };
        foreach (var e in etats)
        {
            var (texte, fond) = CouleursEtat.GetValueOrDefault(e, (Gris, Zebre));
            cartes.Add((parEtat[e].ToString(), e, texte, fond));
        }

        // Cartes de 3,2 cm au plus, réduites si besoin pour que la rangée (total + 7 états) tienne dans la page.
        const double espace = 0.35;
        var carte = Math.Min(3.2, (Largeur.Centimeter - espace * (cartes.Count - 1)) / cartes.Count);
        var t = section.AddTable();
        t.Rows.Alignment = RowAlignment.Center;
        t.TopPadding = t.BottomPadding = Unit.FromMillimeter(2.2);
        t.LeftPadding = t.RightPadding = Unit.FromMillimeter(1);
        for (var i = 0; i < cartes.Count; i++)
        {
            if (i > 0) t.AddColumn(Unit.FromCentimeter(espace));
            t.AddColumn(Unit.FromCentimeter(carte));
        }
        var ligne = t.AddRow();
        ligne.VerticalAlignment = VerticalAlignment.Center;

        for (var i = 0; i < cartes.Count; i++)
        {
            var (nombre, libelle, texte, fond) = cartes[i];
            var c = ligne.Cells[i * 2];
            c.Shading.Color = fond;
            c.Borders.Top.Width = 2;
            c.Borders.Top.Color = texte;
            c.Format.Alignment = ParagraphAlignment.Center;
            var n = c.AddParagraph(nombre);
            n.Format.Font.Size = 15;
            n.Format.Font.Bold = true;
            n.Format.Font.Color = texte;
            var l = c.AddParagraph(libelle);
            l.Format.Font.Size = 7.5;
            l.Format.Font.Color = i == 0 ? Gris : texte;
        }

        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(10);
    }

    private static void Tableau(Section section, IReadOnlyList<TicketList> tickets, IReadOnlyDictionary<int, string> nomsMembres,
        ILookup<int, EvenementTicket> historique)
    {
        var t = section.AddTable();
        t.Format.Font.Size = 7.5;
        t.TopPadding = t.BottomPadding = Unit.FromMillimeter(1.3);
        t.LeftPadding = t.RightPadding = Unit.FromMillimeter(1.3);
        t.Rows.LeftIndent = Unit.Zero;   // explicite : fond et filets alignés sur les marges (sinon décalés du padding)
        foreach (var (_, largeur) in Colonnes) t.AddColumn(Unit.FromCentimeter(largeur));

        // En-tête répété en haut de chaque page.
        var entete = t.AddRow();
        entete.HeadingFormat = true;
        entete.Shading.Color = Bleu;
        entete.Format.Font.Color = Colors.White;
        entete.Format.Font.Bold = true;
        entete.VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < Colonnes.Length; i++) entete.Cells[i].AddParagraph(Colonnes[i].Titre);
        t.Columns[ColonneJours].Format.Alignment = ParagraphAlignment.Center;

        for (var n = 0; n < tickets.Count; n++)
        {
            var tk = tickets[n];
            // Mêmes calculs que l'export Excel : jours en attente (fin − ouverture, ou jusqu'à aujourd'hui), résolution.
            var suivi = RapportTicketsExcel.Suivi(tk, historique[tk.Id]);
            var ligne = t.AddRow();
            ligne.VerticalAlignment = VerticalAlignment.Top;
            ligne.Borders.Bottom.Width = 0.5;
            ligne.Borders.Bottom.Color = Trait;
            if (n % 2 == 1) ligne.Shading.Color = Zebre;

            ligne.Cells[0].AddParagraph().AddFormattedText($"#{tk.Id}", TextFormat.Bold);
            ligne.Cells[1].AddParagraph((tk.DateDemande2 ?? tk.DateDemande?.ToDateTime(TimeOnly.MinValue))?.ToString("dd/MM/yyyy") ?? "");
            ligne.Cells[ColonneJours].AddParagraph(suivi.JoursEnAttente?.ToString() ?? "");
            DeuxNiveaux(ligne.Cells[3], tk.Client, tk.Email, coupureAvant: '@');
            DeuxNiveaux(ligne.Cells[4], Joindre(tk.Produit, tk.Version), tk.Module);
            DeuxNiveaux(ligne.Cells[5], tk.Intitule, Extrait(tk.Description, ExtraitMax));
            ligne.Cells[6].AddParagraph(Propre(tk.TypeTicket) ?? "");

            var importance = ligne.Cells[7].AddParagraph(Propre(tk.Importance) ?? "");
            if (string.Equals(tk.Importance?.Trim(), "Bloquante", StringComparison.OrdinalIgnoreCase))
            { importance.Format.Font.Color = Rgb(0xB9, 0x1C, 0x1C); importance.Format.Font.Bold = true; }
            else if (string.Equals(tk.Importance?.Trim(), "Grave", StringComparison.OrdinalIgnoreCase))
                importance.Format.Font.Color = Rgb(0xC2, 0x41, 0x0C);

            var etat = TicketEtat.Normaliser(tk.Etat) ?? Propre(tk.Etat) ?? "";
            var pEtat = ligne.Cells[8].AddParagraph(etat);
            pEtat.Format.Font.Bold = true;
            if (CouleursEtat.TryGetValue(etat, out var couleur)) pEtat.Format.Font.Color = couleur.Texte;

            var responsable = tk.CollaborateurId is int id && nomsMembres.TryGetValue(id, out var nom) ? nom : Propre(tk.Collaborateur);
            ligne.Cells[9].AddParagraph(responsable ?? "—");
            ligne.Cells[10].AddParagraph(Extrait(suivi.Resolution, ResolutionMax) ?? "");
        }
    }

    /// <summary>
    /// Texte principal en gras, détail en petit gris dessous (ex. client + e-mail du contact).
    /// Un détail long sans espace (e-mail) passe à la ligne avant <paramref name="coupureAvant"/> au lieu d'être rogné.
    /// </summary>
    private static void DeuxNiveaux(Cell cellule, string? principal, string? detail, char? coupureAvant = null)
    {
        var p = cellule.AddParagraph();
        p.AddFormattedText(Propre(principal) ?? "—", TextFormat.Bold);
        if (Propre(detail) is not { } d) return;

        var sous = cellule.AddParagraph();
        sous.Format.Font.Size = 6.8;
        sous.Format.Font.Color = Gris;
        sous.Format.SpaceBefore = Unit.FromPoint(1);
        var i = coupureAvant is { } c && d.Length > 30 ? d.IndexOf(c) : -1;
        if (i > 0)
        {
            sous.AddText(d[..i]);
            sous.AddLineBreak();
            sous.AddText(d[i..]);
        }
        else sous.AddText(d);
    }

    private static string? Joindre(string? produit, string? version) =>
        Propre(produit) is { } p ? (Propre(version) is { } v ? $"{p} {v}" : p) : Propre(version);

    /// <summary>Texte nettoyé : espaces de fin (colonnes nchar), balises HTML d'un e-mail, blancs compactés.</summary>
    public static string? Propre(string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return null;
        var s = texte.Trim();
        if (Html.IsMatch(s)) s = WebUtility.HtmlDecode(Balise.Replace(s, " "));
        s = NettoyageTexte.Nettoyer(s) ?? "";   // tickets déjà enregistrés : « ? ? ? », [Image], « _____ »
        s = Espaces.Replace(s, " ").Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>Début du texte, coupé proprement sur un mot ; les « mots » géants (liens, traits ____) sont raccourcis.</summary>
    public static string? Extrait(string? texte, int max)
    {
        var s = Propre(texte);
        if (s == null) return null;
        s = string.Join(' ', s.Split(' ').Select(m => m.Length > 40 ? m[..37] + "…" : m));
        if (s.Length <= max) return s;
        var coupe = s.LastIndexOf(' ', max);
        return s[..(coupe > max / 2 ? coupe : max)].TrimEnd(',', ';', ':', '.') + "…";
    }

    private static Color Rgb(byte r, byte g, byte b) => new(r, g, b);
}
