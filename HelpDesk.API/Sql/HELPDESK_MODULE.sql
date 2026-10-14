-- ============================================================
--  Modules gérés par l'admin (liste du menu « Module » des tickets).
--  Remplace la liste figée dans le code. À exécuter sur ERP222E.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'HELPDESK_MODULE')
BEGIN
    CREATE TABLE dbo.HELPDESK_MODULE
    (
        Id  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_HELPDESK_MODULE PRIMARY KEY,
        Nom NVARCHAR(100)      NOT NULL CONSTRAINT UQ_HELPDESK_MODULE_Nom UNIQUE
    );

    -- Valeurs de départ (celles déjà utilisées).
    INSERT INTO dbo.HELPDESK_MODULE (Nom) VALUES
        (N'Affaire'), (N'Administration'), (N'Règlement'), (N'Comptabilité'),
        (N'ERP'), (N'CRM'), (N'Paie'), (N'Gestion');
END
GO
