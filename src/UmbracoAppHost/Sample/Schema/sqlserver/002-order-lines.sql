-- The SQLite sample's order lines on SQL Server, for when ConnectionStrings:ReportsSampleDb points the Orders lines
-- data source there.
IF OBJECT_ID(N'dbo.ORDER_LINES', N'U') IS NULL
CREATE TABLE dbo.ORDER_LINES (
    LINE_ID    int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    ORDER_ID   int            NOT NULL REFERENCES dbo.ORDERS (ORDER_ID),
    ITEM       nvarchar(200)  NOT NULL,
    QUANTITY   int            NOT NULL DEFAULT 1,
    UNIT_PRICE decimal(12, 2) NOT NULL DEFAULT 0,
    NOTES      nvarchar(max)  NULL,
    VERSION    int            NOT NULL DEFAULT 1,
    CREATED_BY nvarchar(255)  NULL,
    CREATED_AT datetime2      NULL,
    UPDATED_BY nvarchar(255)  NULL,
    UPDATED_AT datetime2      NULL
);
GO

-- A tabular form lists one order's lines at a time.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ORDER_LINES_ORDER_ID' AND object_id = OBJECT_ID(N'dbo.ORDER_LINES'))
CREATE INDEX IX_ORDER_LINES_ORDER_ID ON dbo.ORDER_LINES (ORDER_ID);
