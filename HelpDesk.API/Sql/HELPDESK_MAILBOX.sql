-- ============================================================
--  Boîte mail connectée par utilisateur (IMAP/SMTP).
--  Chaque membre connecte SA propre boîte (Gmail, etc.).
--  Le mot de passe n'est jamais stocké en clair : il est chiffré
--  (ASP.NET Data Protection) dans la colonne SecretChiffre.
--  À exécuter sur ERP222E.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'HELPDESK_MAILBOX')
BEGIN
    CREATE TABLE dbo.HELPDESK_MAILBOX
    (
        Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_HELPDESK_MAILBOX PRIMARY KEY,
        UserId        INT               NOT NULL,
        Provider      VARCHAR(20)       NOT NULL,   -- 'gmail' | 'outlook' | 'imap'
        Email         NVARCHAR(255)     NOT NULL,
        Login         NVARCHAR(255)     NOT NULL,
        SecretChiffre NVARCHAR(MAX)     NOT NULL,   -- secret chiffré (Data Protection) : mot de passe IMAP ou refresh token OAuth (long)
        ImapHost      NVARCHAR(255)     NOT NULL,
        ImapPort      INT               NOT NULL,
        ImapSsl       BIT               NOT NULL,   -- 1 = SSL implicite (993)
        SmtpHost      NVARCHAR(255)     NOT NULL,
        SmtpPort      INT               NOT NULL,
        SmtpStartTls  BIT               NOT NULL,   -- 1 = STARTTLS (587), 0 = SSL implicite (465)
        DateCreation  DATETIME          NOT NULL,
        -- une seule boîte connectée par utilisateur (v1)
        CONSTRAINT UQ_HELPDESK_MAILBOX_User UNIQUE (UserId)
    );
END
GO
