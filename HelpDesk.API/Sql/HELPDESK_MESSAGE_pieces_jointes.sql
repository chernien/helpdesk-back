-- ============================================================
--  Pièces jointes dans la discussion (chat) : colonnes ajoutées à HELPDESK_MESSAGE.
--  À exécuter sur ERP222E AVANT de redémarrer l'API avec la nouvelle version.
--  (Sinon les requêtes du chat échoueront : l'entité attend ces colonnes.)
-- ============================================================
IF COL_LENGTH('dbo.HELPDESK_MESSAGE', 'FichierNom') IS NULL
    ALTER TABLE dbo.HELPDESK_MESSAGE ADD FichierNom NVARCHAR(255) NULL;
GO
IF COL_LENGTH('dbo.HELPDESK_MESSAGE', 'FichierStockage') IS NULL
    ALTER TABLE dbo.HELPDESK_MESSAGE ADD FichierStockage NVARCHAR(255) NULL;
GO
IF COL_LENGTH('dbo.HELPDESK_MESSAGE', 'FichierType') IS NULL
    ALTER TABLE dbo.HELPDESK_MESSAGE ADD FichierType NVARCHAR(100) NULL;
GO
IF COL_LENGTH('dbo.HELPDESK_MESSAGE', 'FichierTaille') IS NULL
    ALTER TABLE dbo.HELPDESK_MESSAGE ADD FichierTaille BIGINT NULL;
GO
-- Vérification
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'HELPDESK_MESSAGE' AND COLUMN_NAME LIKE 'Fichier%'
ORDER BY COLUMN_NAME;
