IF OBJECT_ID(N'dbo.CorrespondentRelationships', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CorrespondentRelationships
    (
        Id uniqueidentifier NOT NULL,
        FromBankId uniqueidentifier NOT NULL,
        ToBankId uniqueidentifier NOT NULL,
        CurrencyCode nvarchar(3) NOT NULL,
        Rail nvarchar(30) NOT NULL,
        RelationshipType nvarchar(40) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_CorrespondentRelationships_IsActive DEFAULT (1),
        Priority int NOT NULL CONSTRAINT DF_CorrespondentRelationships_Priority DEFAULT (100),
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT PK_CorrespondentRelationships PRIMARY KEY (Id),
        CONSTRAINT FK_CorrespondentRelationships_FromBank FOREIGN KEY (FromBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_CorrespondentRelationships_ToBank FOREIGN KEY (ToBankId) REFERENCES dbo.Banks(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentRelationships')
    AND name = N'IX_CorrespondentRelationships_FromBankId_ToBankId_CurrencyCode_Rail')
    CREATE UNIQUE INDEX IX_CorrespondentRelationships_FromBankId_ToBankId_CurrencyCode_Rail
        ON dbo.CorrespondentRelationships (FromBankId, ToBankId, CurrencyCode, Rail);
