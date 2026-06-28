IF OBJECT_ID(N'dbo.Customers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Customers
    (
        Id uniqueidentifier NOT NULL,
        BankId uniqueidentifier NOT NULL,
        Name nvarchar(120) NOT NULL,
        CONSTRAINT PK_Customers PRIMARY KEY (Id),
        CONSTRAINT FK_Customers_Banks_BankId FOREIGN KEY (BankId)
            REFERENCES dbo.Banks (Id) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Customers') AND name = N'IX_Customers_BankId')
    CREATE INDEX IX_Customers_BankId ON dbo.Customers (BankId);
