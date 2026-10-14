-- ============================================================
--  Discussion (chat) par ticket — tables maison (hors scaffolding)
--  A executer une fois sur la base ERP222E.
-- ============================================================

-- Messages de la discussion (append-only : 1 ligne = 1 message).
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'HELPDESK_MESSAGE')
BEGIN
    CREATE TABLE dbo.HELPDESK_MESSAGE
    (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_HELPDESK_MESSAGE PRIMARY KEY,
        TicketId    INT               NOT NULL,
        AuteurId    INT               NOT NULL,
        AuteurNom   NVARCHAR(200)         NULL,
        -- Role de l'auteur au moment de l'envoi (ROLE_CLIENT vs equipe) :
        -- sert a distinguer les messages du client de ceux de l'equipe.
        AuteurRole  VARCHAR(20)           NULL,
        Contenu     NVARCHAR(4000)    NOT NULL,
        DateEnvoi   DATETIME          NOT NULL
    );

    CREATE INDEX IX_HELPDESK_MESSAGE_Ticket
        ON dbo.HELPDESK_MESSAGE (TicketId, DateEnvoi);
END
GO

-- Repere de lecture INDIVIDUEL : une ligne par (utilisateur, ticket).
-- Lire n'avance que son propre repere ; le "lu pour toute l'equipe" a la
-- reponse est deduit des messages (une reponse d'equipe plus recente).
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'HELPDESK_MESSAGE_LU')
BEGIN
    CREATE TABLE dbo.HELPDESK_MESSAGE_LU
    (
        UserId            INT      NOT NULL,
        TicketId          INT      NOT NULL,
        DerniereLectureLe DATETIME NOT NULL,
        CONSTRAINT PK_HELPDESK_MESSAGE_LU PRIMARY KEY (UserId, TicketId)
    );
END
GO
