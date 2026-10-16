-- =============================================================================
-- SQL Migration Tool — full-feature test database
--
-- Builds a database that contains every object type and table feature the
-- tool must carry across a backup/install round trip. Used by
-- TestConsole ("setup" / "roundtrip") to verify each development step.
--
-- Run against an EMPTY database. Requires SQL Server 2016 SP1 or later.
-- =============================================================================

SET NOCOUNT ON;
GO

-- ── Schemas ──────────────────────────────────────────────────────────────────
CREATE SCHEMA Sales;
GO
CREATE SCHEMA Audit;
GO

-- ── User-Defined Data Types (alias types) ────────────────────────────────────
CREATE TYPE dbo.PhoneNumber FROM VARCHAR(20) NULL;
GO
CREATE TYPE Sales.Money2 FROM DECIMAL(19,4) NOT NULL;
GO

-- ── User-Defined Table Types ─────────────────────────────────────────────────
CREATE TYPE dbo.OrderLineTVP AS TABLE
(
    ProductId INT            NOT NULL PRIMARY KEY,
    Qty       INT            NOT NULL CHECK (Qty > 0),
    Price     DECIMAL(10,2)  NOT NULL DEFAULT (0)
);
GO
CREATE TYPE Sales.IdList AS TABLE
(
    Id INT NOT NULL,
    INDEX IX_IdList_Id NONCLUSTERED (Id)
);
GO

-- ── XML Schema Collection ────────────────────────────────────────────────────
CREATE XML SCHEMA COLLECTION Sales.OrderNotesSchema AS N'
<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
  <xs:element name="note" type="xs:string" />
</xs:schema>';
GO

-- ── Sequence ─────────────────────────────────────────────────────────────────
CREATE SEQUENCE Sales.OrderNumberSeq AS INT START WITH 1000 INCREMENT BY 1;
GO

-- ── Partition function / scheme ──────────────────────────────────────────────
CREATE PARTITION FUNCTION pfYear (INT) AS RANGE RIGHT FOR VALUES (2024, 2025);
GO
CREATE PARTITION SCHEME psYear AS PARTITION pfYear ALL TO ([PRIMARY]);
GO

-- ── Tables ───────────────────────────────────────────────────────────────────

-- Nonclustered PK + separate clustered index, alias type, case-sensitive
-- collation, named default, UNIQUE + CHECK constraints, rowversion,
-- datetime2(3) precision.
CREATE TABLE dbo.Customer
(
    CustomerId INT IDENTITY(1,1) NOT NULL,
    Code       VARCHAR(10) COLLATE Latin1_General_CS_AS NOT NULL,
    Name       NVARCHAR(100) NOT NULL,
    Phone      dbo.PhoneNumber,
    Email      VARCHAR(200) NULL,
    CreatedAt  DATETIME2(3) NOT NULL CONSTRAINT DF_Customer_CreatedAt DEFAULT (SYSUTCDATETIME()),
    RowVer     ROWVERSION,
    CONSTRAINT PK_Customer PRIMARY KEY NONCLUSTERED (CustomerId),
    CONSTRAINT UQ_Customer_Code UNIQUE (Code),
    CONSTRAINT CK_Customer_Code CHECK (LEN(Code) >= 3)
);
GO
CREATE CLUSTERED INDEX CIX_Customer_Name ON dbo.Customer (Name);
GO
CREATE NONCLUSTERED INDEX IX_Customer_Email ON dbo.Customer (Email) WHERE Email IS NOT NULL;
GO
CREATE NONCLUSTERED INDEX IX_CreatedAt ON dbo.Customer (CreatedAt DESC);
GO

-- Sequence default, alias type NOT NULL, typed XML, time/datetimeoffset precision.
CREATE TABLE Sales.[Order]
(
    OrderId     INT          NOT NULL CONSTRAINT PK_Order PRIMARY KEY CLUSTERED,
    OrderNumber INT          NOT NULL CONSTRAINT DF_Order_OrderNumber DEFAULT (NEXT VALUE FOR Sales.OrderNumberSeq),
    CustomerId  INT          NOT NULL,
    Total       Sales.Money2,
    OrderDate   DATE         NOT NULL,
    ShipTime    TIME(0)      NULL,
    PlacedAt    DATETIMEOFFSET(2) NULL,
    Notes       XML(Sales.OrderNotesSchema) NULL,
    CONSTRAINT FK_Order_Customer FOREIGN KEY (CustomerId)
        REFERENCES dbo.Customer (CustomerId) ON DELETE CASCADE
);
GO
CREATE NONCLUSTERED INDEX IX_Order_Customer
    ON Sales.[Order] (CustomerId) INCLUDE (Total, OrderDate)
    WITH (FILLFACTOR = 90, DATA_COMPRESSION = PAGE);
GO
CREATE NONCLUSTERED INDEX IX_Customer_Phone ON dbo.Customer (Phone);
GO
ALTER INDEX IX_Customer_Phone ON dbo.Customer DISABLE;   -- disabled index
GO

-- Composite PK, persisted computed column, FK left disabled (NOCHECK).
CREATE TABLE Sales.OrderLine
(
    OrderId   INT           NOT NULL,
    [LineNo]  INT           NOT NULL,
    ProductId INT           NOT NULL,
    Qty       INT           NOT NULL,
    Price     DECIMAL(10,2) NOT NULL,
    LineTotal AS (Qty * Price) PERSISTED,
    CONSTRAINT PK_OrderLine PRIMARY KEY (OrderId, [LineNo]),
    CONSTRAINT CK_OrderLine_Qty CHECK (Qty > 0)
);
GO
ALTER TABLE Sales.OrderLine WITH NOCHECK
    ADD CONSTRAINT FK_OrderLine_Order FOREIGN KEY (OrderId) REFERENCES Sales.[Order] (OrderId);
GO
ALTER TABLE Sales.OrderLine NOCHECK CONSTRAINT FK_OrderLine_Order;
GO

-- Heap (no PK), SPARSE, ROWGUIDCOL, MAX types.
CREATE TABLE dbo.HeapLog
(
    Msg       NVARCHAR(MAX)    NULL,
    Payload   VARBINARY(MAX)   NULL,
    RowGuid   UNIQUEIDENTIFIER ROWGUIDCOL NOT NULL CONSTRAINT DF_HeapLog_RowGuid DEFAULT (NEWID()),
    SparseCol INT SPARSE NULL,
    Precise   DATETIME2(7)     NULL,
    Amount    DECIMAL(19,4)    NULL,
    Ratio     FLOAT            NULL,
    CONSTRAINT CK_HeapLog_Sparse CHECK (SparseCol > 0)
);
GO
ALTER TABLE dbo.HeapLog NOCHECK CONSTRAINT CK_HeapLog_Sparse;   -- disabled CHECK
GO
CREATE UNIQUE NONCLUSTERED INDEX UX_HeapLog_RowGuid ON dbo.HeapLog (RowGuid) WITH (IGNORE_DUP_KEY = ON);
GO

-- Identity with non-default seed; index name shared with dbo.Customer.
CREATE TABLE Audit.ChangeLog
(
    ChangeId  INT IDENTITY(100,5) NOT NULL CONSTRAINT PK_ChangeLog PRIMARY KEY,
    TableName SYSNAME       NOT NULL,
    Action    CHAR(1)       NOT NULL CONSTRAINT CK_ChangeLog_Action CHECK (Action IN ('I','U','D')),
    CreatedAt DATETIME2(0)  NOT NULL CONSTRAINT DF_ChangeLog_CreatedAt DEFAULT (SYSUTCDATETIME())
);
GO
CREATE NONCLUSTERED INDEX IX_CreatedAt ON Audit.ChangeLog (CreatedAt);
GO
CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_ChangeLog ON Audit.ChangeLog (TableName, Action);
GO

-- Partitioned table.
CREATE TABLE Audit.Archive
(
    ArchiveId INT          NOT NULL,
    [Year]    INT          NOT NULL,
    Info      NVARCHAR(50) NULL,
    CONSTRAINT PK_Archive PRIMARY KEY CLUSTERED (ArchiveId, [Year])
) ON psYear ([Year]);
GO
-- FK with NOT FOR REPLICATION and SET NULL / CASCADE actions.
ALTER TABLE Audit.ChangeLog ADD CustomerId INT NULL
    CONSTRAINT FK_ChangeLog_Customer FOREIGN KEY REFERENCES dbo.Customer (CustomerId)
    ON DELETE SET NULL ON UPDATE NO ACTION NOT FOR REPLICATION;
GO

-- ── Synonym ──────────────────────────────────────────────────────────────────
CREATE SYNONYM dbo.Orders FOR Sales.[Order];
GO
-- Three-part name pointing at this same database: must be restored pointing at the target.
DECLARE @sql NVARCHAR(400) = N'CREATE SYNONYM Audit.SelfCustomer FOR ' + QUOTENAME(DB_NAME()) + N'.dbo.Customer';
EXEC (@sql);
GO

-- ── Functions (scalar, inline TVF, multi-statement TVF) ──────────────────────
CREATE FUNCTION dbo.fnLineTotal (@Qty INT, @Price DECIMAL(10,2))
RETURNS DECIMAL(19,2)
AS
BEGIN
    RETURN @Qty * @Price;
END
GO
CREATE FUNCTION Sales.fnOrdersByCustomer (@CustomerId INT)
RETURNS TABLE
AS
RETURN (SELECT OrderId, Total FROM Sales.[Order] WHERE CustomerId = @CustomerId);
GO
CREATE FUNCTION dbo.fnTopCustomers (@Top INT)
RETURNS @Result TABLE (CustomerId INT, Name NVARCHAR(100))
AS
BEGIN
    INSERT @Result SELECT TOP (@Top) CustomerId, Name FROM dbo.Customer ORDER BY Name;
    RETURN;
END
GO

-- ── Views (dependency order differs from alphabetical order) ─────────────────
CREATE VIEW dbo.vZ_CustomerBase
AS
SELECT CustomerId, Code, Name FROM dbo.Customer;
GO
CREATE VIEW dbo.vA_CustomerSummary
AS
SELECT b.CustomerId, b.Name, COUNT(o.OrderId) AS Orders
FROM dbo.vZ_CustomerBase b
LEFT JOIN Sales.[Order] o ON o.CustomerId = b.CustomerId
GROUP BY b.CustomerId, b.Name;
GO
CREATE VIEW dbo.vCustomerCodes WITH SCHEMABINDING
AS
SELECT CustomerId, Code FROM dbo.Customer;
GO
CREATE UNIQUE CLUSTERED INDEX CIX_vCustomerCodes ON dbo.vCustomerCodes (CustomerId);   -- indexed view
GO
CREATE VIEW Sales.vOrderLineTotals
AS
SELECT OrderId, [LineNo], dbo.fnLineTotal(Qty, Price) AS Amount FROM Sales.OrderLine;
GO

-- ── Stored procedures ────────────────────────────────────────────────────────
CREATE PROCEDURE dbo.uspSaveOrderLines
    @OrderId INT,
    @Lines   dbo.OrderLineTVP READONLY
AS
BEGIN
    SET NOCOUNT ON;
    INSERT Sales.OrderLine (OrderId, [LineNo], ProductId, Qty, Price)
    SELECT @OrderId, ROW_NUMBER() OVER (ORDER BY ProductId), ProductId, Qty, Price FROM @Lines;
END
GO
CREATE PROCEDURE Sales.uspFindCustomer
    @Phone dbo.PhoneNumber
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @p dbo.PhoneNumber = @Phone;
    SELECT CustomerId, Name FROM dbo.Customer WHERE Phone = @p;
END
GO
CREATE PROCEDURE Sales.uspGetOrders
    @Ids Sales.IdList READONLY
AS
SELECT o.* FROM dbo.Orders o JOIN @Ids i ON i.Id = o.OrderId;
GO

-- ── Triggers (enabled, disabled, database-level) ─────────────────────────────
CREATE TRIGGER Sales.trgOrder_Insert ON Sales.[Order]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT Audit.ChangeLog (TableName, Action) SELECT 'Sales.Order', 'I' FROM inserted;
END
GO
CREATE TRIGGER dbo.trgCustomer_Delete ON dbo.Customer
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT Audit.ChangeLog (TableName, Action) SELECT 'dbo.Customer', 'D' FROM deleted;
END
GO
DISABLE TRIGGER dbo.trgCustomer_Delete ON dbo.Customer;
GO
CREATE TRIGGER trgDdlNoop ON DATABASE
FOR CREATE_PROCEDURE
AS
BEGIN
    SET NOCOUNT ON;
END
GO
CREATE TRIGGER trgDdlBlockDrop ON DATABASE
FOR DROP_TABLE
AS
BEGIN
    RAISERROR ('Dropping tables is not allowed.', 16, 1);
    ROLLBACK;
END
GO
DISABLE TRIGGER trgDdlBlockDrop ON DATABASE;
GO

-- ── Extended properties ──────────────────────────────────────────────────────
EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Customer master',
     @level0type = N'SCHEMA', @level0name = N'dbo',
     @level1type = N'TABLE',  @level1name = N'Customer';
GO
EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Display name',
     @level0type = N'SCHEMA', @level0name = N'dbo',
     @level1type = N'TABLE',  @level1name = N'Customer',
     @level2type = N'COLUMN', @level2name = N'Name';
GO
EXEC sys.sp_addextendedproperty @name = N'Owner',     @value = N'Data team';                           -- database
EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Sales objects',
     @level0type = N'SCHEMA', @level0name = N'Sales';                                                  -- schema
EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Order lines to save',
     @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'PROCEDURE', @level1name = N'uspSaveOrderLines',
     @level2type = N'PARAMETER', @level2name = N'@Lines';                                              -- parameter
EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Codes are 3+ chars',
     @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'Customer',
     @level2type = N'CONSTRAINT', @level2name = N'CK_Customer_Code';                                   -- constraint
EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Lookup by e-mail',
     @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'Customer',
     @level2type = N'INDEX', @level2name = N'IX_Customer_Email';                                       -- index
EXEC sys.sp_addextendedproperty @name = N'Version',  @value = 3,
     @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TYPE', @level1name = N'OrderLineTVP'; -- type, int value
GO

-- ── Security ─────────────────────────────────────────────────────────────────
CREATE ROLE SalesReader;
GO
GRANT SELECT ON SCHEMA::Sales TO SalesReader;
GO
CREATE USER MigrationTestUser WITHOUT LOGIN;
GO
ALTER ROLE SalesReader ADD MEMBER MigrationTestUser;
GO
CREATE USER ReportUser WITHOUT LOGIN WITH DEFAULT_SCHEMA = Sales;
GO
ALTER ROLE db_datareader ADD MEMBER ReportUser;                         -- fixed role membership
GO
GRANT EXECUTE ON TYPE::dbo.OrderLineTVP TO SalesReader;                 -- type permission
GRANT SELECT ON dbo.Customer (Name) TO SalesReader WITH GRANT OPTION;   -- column-level, grant option
DENY DELETE ON dbo.Customer TO MigrationTestUser;                       -- deny
GRANT EXECUTE ON dbo.uspSaveOrderLines TO public;                       -- explicit grant to public
GO

-- ── Data ─────────────────────────────────────────────────────────────────────
INSERT dbo.Customer (Code, Name, Phone, Email, CreatedAt) VALUES
    ('ABC', N'Alpha Ltd',        '+44 20 7946 0000', 'a@example.com', '2025-01-02T03:04:05.123'),
    ('abc', N'Ålpha lowercase',  NULL,               NULL,            '2025-02-03T04:05:06.456'),
    ('XYZ', N'Zeta 株式会社',      '+81 3-0000-0000', NULL,            '2025-03-04T05:06:07.789');
GO
INSERT Sales.[Order] (OrderId, CustomerId, Total, OrderDate, ShipTime, PlacedAt, Notes) VALUES
    (1, 1, 12345678901234.5678, '2025-05-01', '13:45:00', '2025-05-01T13:45:00.12+05:30', N'<note>Urgent</note>'),
    (2, 1, 0.1000,              '2025-05-02', NULL,       NULL,                           NULL),
    (3, 3, 99.9900,             '2025-05-03', '08:00:00', '2025-05-03T08:00:00.00-04:00', N'<note>Gift</note>');
GO
DECLARE @lines dbo.OrderLineTVP;
INSERT @lines (ProductId, Qty, Price) VALUES (10, 2, 4.50), (11, 1, 99.99);
EXEC dbo.uspSaveOrderLines @OrderId = 1, @Lines = @lines;
GO
INSERT Sales.OrderLine (OrderId, [LineNo], ProductId, Qty, Price) VALUES (999, 1, 12, 1, 1.00); -- orphan row allowed by NOCHECK FK
GO
INSERT dbo.HeapLog (Msg, Payload, SparseCol, Precise, Amount, Ratio) VALUES
    (N'first',  0x0102030405, NULL, '2025-06-01T12:34:56.1234567', 12345678901234.5678, 0.1),
    (N'second', NULL,         42,   NULL,                          0.0001,              1.0E-300),
    (N'second', NULL,         42,   NULL,                          0.0001,              1.0E-300);  -- exact duplicate (heap)
GO
INSERT Audit.Archive (ArchiveId, [Year], Info) VALUES (1, 2023, N'old'), (2, 2024, N'mid'), (3, 2025, N'new');
GO
-- Enabled but untrusted CHECK: the 2023 row above violates it.
ALTER TABLE Audit.Archive WITH NOCHECK ADD CONSTRAINT CK_Archive_Year CHECK ([Year] >= 2024);
GO
-- Advance the sequence past the rows above so RESTART WITH is testable.
SELECT NEXT VALUE FOR Sales.OrderNumberSeq;
SELECT NEXT VALUE FOR Sales.OrderNumberSeq;
GO
