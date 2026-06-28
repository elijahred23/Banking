IF OBJECT_ID(N'dbo.Accounts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Accounts
    (
        Id uniqueidentifier NOT NULL,
        CustomerId uniqueidentifier NOT NULL,
        AccountNumber nvarchar(34) NOT NULL,
        Balance decimal(19, 4) NOT NULL,
        HeldBalance decimal(19, 4) NOT NULL CONSTRAINT DF_Accounts_HeldBalance DEFAULT (0),
        CONSTRAINT PK_Accounts PRIMARY KEY (Id),
        CONSTRAINT FK_Accounts_Customers_CustomerId FOREIGN KEY (CustomerId)
            REFERENCES dbo.Customers (Id) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Accounts') AND name = N'IX_Accounts_AccountNumber')
    CREATE UNIQUE INDEX IX_Accounts_AccountNumber ON dbo.Accounts (AccountNumber);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Accounts') AND name = N'IX_Accounts_CustomerId')
    CREATE INDEX IX_Accounts_CustomerId ON dbo.Accounts (CustomerId);
