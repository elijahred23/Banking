IF OBJECT_ID(N'dbo.SimulationStates', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SimulationStates
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_SimulationStates PRIMARY KEY,
        Name nvarchar(60) NOT NULL,
        CurrentUtc datetimeoffset NOT NULL,
        IsPaused bit NOT NULL,
        SpeedMultiplier decimal(9, 2) NOT NULL,
        UpdatedDate datetimeoffset NOT NULL
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SimulationStates') AND name = N'IX_SimulationStates_Name')
    CREATE UNIQUE INDEX IX_SimulationStates_Name ON dbo.SimulationStates(Name);
