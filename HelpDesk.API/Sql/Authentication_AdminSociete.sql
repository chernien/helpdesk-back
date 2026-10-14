-- Compte client « administrateur de sa société » : il voit les tickets de tous les membres de ses sociétés.
-- À lancer AVANT de redémarrer l'API (l'API lit cette colonne).

IF COL_LENGTH('dbo.Authentication', 'AdminSociete') IS NULL
    ALTER TABLE dbo.Authentication
        ADD AdminSociete bit NOT NULL CONSTRAINT DF_Authentication_AdminSociete DEFAULT (0);
GO

-- Reprise depuis Divalto : les comptes clients dont le contact a « Profil Admin helpdesk » coché
-- (T2.U_FLAG_ADMIN = 2) dans l'une de leurs sociétés. Dossier Divalto : '998' (test) — '1' en production.
UPDATE a SET a.AdminSociete = 1
FROM dbo.Authentication a
WHERE a.Role = 'ROLE_CLIENT'
  AND EXISTS (SELECT 1 FROM dbo.T2 t
              WHERE t.DOS = '998' AND t.U_FLAG_ADMIN = 2
                AND LOWER(RTRIM(t.EMAIL)) = LOWER(LTRIM(RTRIM(a.Username))));
GO
