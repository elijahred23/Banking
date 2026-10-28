IF OBJECT_ID(N'dbo.StudioScenarios', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.StudioScenarios (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_StudioScenarios PRIMARY KEY,
 BankId uniqueidentifier NOT NULL,
 Kind nvarchar(8) NOT NULL,
 Title nvarchar(100) NOT NULL,
 PlanJson nvarchar(4000) NOT NULL,
 CreatedBy nvarchar(120) NOT NULL,
 CreatedAt datetimeoffset NOT NULL,
 CONSTRAINT FK_StudioScenarios_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id));
 CREATE INDEX IX_StudioScenarios_BankId_Kind_CreatedAt ON dbo.StudioScenarios([BankId], [Kind], [CreatedAt]);
END;
IF OBJECT_ID(N'dbo.StudioCampaigns', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.StudioCampaigns (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_StudioCampaigns PRIMARY KEY,
 BankId uniqueidentifier NOT NULL,
 Kind nvarchar(8) NOT NULL,
 Title nvarchar(100) NOT NULL,
 PlanJson nvarchar(4000) NOT NULL,
 PlannedDays int NOT NULL,
 FirstDate date NOT NULL,
 CreatedAt datetimeoffset NOT NULL,
 CONSTRAINT FK_StudioCampaigns_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id));
 CREATE INDEX IX_StudioCampaigns_BankId_Kind_CreatedAt ON dbo.StudioCampaigns([BankId], [Kind], [CreatedAt]);
END;
IF COL_LENGTH(N'dbo.StudioDays', N'CampaignId') IS NULL
 ALTER TABLE dbo.StudioDays ADD CampaignId uniqueidentifier NULL;
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_StudioDays_StudioCampaigns_CampaignId')
 EXEC(N'ALTER TABLE dbo.StudioDays ADD CONSTRAINT FK_StudioDays_StudioCampaigns_CampaignId FOREIGN KEY (CampaignId) REFERENCES dbo.StudioCampaigns(Id)');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.StudioDays') AND name = 'IX_StudioDays_CampaignId')
 EXEC(N'CREATE INDEX IX_StudioDays_CampaignId ON dbo.StudioDays(CampaignId)');
IF OBJECT_ID(N'dbo.StudioDebitAuthorizations', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.StudioDebitAuthorizations (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_StudioDebitAuthorizations PRIMARY KEY,
 BankId uniqueidentifier NOT NULL,
 OriginatingAccountId uniqueidentifier NOT NULL,
 Routing nvarchar(9) NOT NULL,
 Account nvarchar(17) NOT NULL,
 [Limit] decimal(19,4) NOT NULL,
 ValidThrough date NOT NULL,
 GrantedBy nvarchar(120) NOT NULL,
 CreatedAt datetimeoffset NOT NULL,
 RevokedAt datetimeoffset NULL,
 CONSTRAINT FK_StudioDebitAuthorizations_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id),
 CONSTRAINT FK_StudioDebitAuthorizations_Accounts_OriginatingAccountId FOREIGN KEY (OriginatingAccountId) REFERENCES dbo.Accounts(Id));
 CREATE INDEX IX_StudioDebitAuthorizations_BankId_OriginatingAccountId_Routing_Account ON dbo.StudioDebitAuthorizations([BankId], [OriginatingAccountId], [Routing], [Account]);
 CREATE INDEX IX_StudioDebitAuthorizations_OriginatingAccountId ON dbo.StudioDebitAuthorizations([OriginatingAccountId]);
END;
IF OBJECT_ID(N'dbo.StudioAchRepairs', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.StudioAchRepairs (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_StudioAchRepairs PRIMARY KEY,
 BankId uniqueidentifier NOT NULL,
 OriginalEntryId uniqueidentifier NOT NULL,
 ReplacementEntryId uniqueidentifier NOT NULL,
 Reason nvarchar(500) NOT NULL,
 CreatedBy nvarchar(120) NOT NULL,
 CreatedAt datetimeoffset NOT NULL,
 CONSTRAINT FK_StudioAchRepairs_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id),
 CONSTRAINT FK_StudioAchRepairs_AchEntries_OriginalEntryId FOREIGN KEY (OriginalEntryId) REFERENCES dbo.AchEntries(Id),
 CONSTRAINT FK_StudioAchRepairs_AchEntries_ReplacementEntryId FOREIGN KEY (ReplacementEntryId) REFERENCES dbo.AchEntries(Id));
 CREATE INDEX IX_StudioAchRepairs_BankId ON dbo.StudioAchRepairs([BankId]);
 CREATE UNIQUE INDEX IX_StudioAchRepairs_OriginalEntryId ON dbo.StudioAchRepairs([OriginalEntryId]);
 CREATE UNIQUE INDEX IX_StudioAchRepairs_ReplacementEntryId ON dbo.StudioAchRepairs([ReplacementEntryId]);
END;
IF OBJECT_ID(N'dbo.StudioNotes', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.StudioNotes (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_StudioNotes PRIMARY KEY,
 BankId uniqueidentifier NOT NULL,
 CampaignId uniqueidentifier NULL,
 WireId uniqueidentifier NULL,
 Kind nvarchar(24) NOT NULL,
 RecipientRole nvarchar(40) NOT NULL,
 Actor nvarchar(120) NOT NULL,
 Text nvarchar(1000) NOT NULL,
 CreatedAt datetimeoffset NOT NULL,
 AvailableAt datetimeoffset NOT NULL,
 CONSTRAINT FK_StudioNotes_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id),
 CONSTRAINT FK_StudioNotes_StudioCampaigns_CampaignId FOREIGN KEY (CampaignId) REFERENCES dbo.StudioCampaigns(Id),
 CONSTRAINT FK_StudioNotes_WireTransfers_WireId FOREIGN KEY (WireId) REFERENCES dbo.WireTransfers(Id));
 CREATE INDEX IX_StudioNotes_BankId_AvailableAt ON dbo.StudioNotes([BankId], [AvailableAt]);
 CREATE INDEX IX_StudioNotes_CampaignId ON dbo.StudioNotes([CampaignId]);
 CREATE INDEX IX_StudioNotes_WireId ON dbo.StudioNotes([WireId]);
END;
IF OBJECT_ID(N'dbo.StudioFundingRequests', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.StudioFundingRequests (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_StudioFundingRequests PRIMARY KEY,
 BankId uniqueidentifier NOT NULL,
 Amount decimal(19,4) NOT NULL,
 Reason nvarchar(500) NOT NULL,
 RequestedBy nvarchar(120) NOT NULL,
 CreatedAt datetimeoffset NOT NULL,
 IncomingWireId uniqueidentifier NULL,
 ReconciledAt datetimeoffset NULL,
 CONSTRAINT FK_StudioFundingRequests_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id),
 CONSTRAINT FK_StudioFundingRequests_WireTransfers_IncomingWireId FOREIGN KEY (IncomingWireId) REFERENCES dbo.WireTransfers(Id));
 CREATE INDEX IX_StudioFundingRequests_BankId_CreatedAt ON dbo.StudioFundingRequests([BankId], [CreatedAt]);
 CREATE UNIQUE INDEX IX_StudioFundingRequests_IncomingWireId ON dbo.StudioFundingRequests([IncomingWireId]) WHERE IncomingWireId IS NOT NULL;
END;
