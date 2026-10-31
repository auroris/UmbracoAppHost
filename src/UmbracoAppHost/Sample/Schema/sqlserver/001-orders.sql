-- The SQLite sample's tables on SQL Server, for when ConnectionStrings:ReportsSampleDb points the "orders"
-- report, the Orders data sources and the sample.orders resource there.
IF OBJECT_ID(N'dbo.ORDERS', N'U') IS NULL
CREATE TABLE dbo.ORDERS (
    ORDER_ID   int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    CUSTOMER   nvarchar(200)  NOT NULL,
    REGION     nvarchar(20)   NOT NULL,
    STATUS     nvarchar(20)   NOT NULL,
    AMOUNT     decimal(12, 2) NOT NULL,
    ORDER_DATE date           NOT NULL,
    NOTES      nvarchar(max)  NULL,
    VERSION    int            NOT NULL DEFAULT 1,
    CREATED_BY nvarchar(255)  NULL,
    CREATED_AT datetime2      NULL,
    UPDATED_BY nvarchar(255)  NULL,
    UPDATED_AT datetime2      NULL
);
GO

-- The columns the SQLite sample gained after its first release, kept so both scripts read alike.
IF COL_LENGTH('dbo.ORDERS', 'VERSION') IS NULL ALTER TABLE dbo.ORDERS ADD VERSION int NOT NULL DEFAULT 1;
IF COL_LENGTH('dbo.ORDERS', 'CREATED_BY') IS NULL ALTER TABLE dbo.ORDERS ADD CREATED_BY nvarchar(255) NULL;
IF COL_LENGTH('dbo.ORDERS', 'CREATED_AT') IS NULL ALTER TABLE dbo.ORDERS ADD CREATED_AT datetime2 NULL;
IF COL_LENGTH('dbo.ORDERS', 'UPDATED_BY') IS NULL ALTER TABLE dbo.ORDERS ADD UPDATED_BY nvarchar(255) NULL;
IF COL_LENGTH('dbo.ORDERS', 'UPDATED_AT') IS NULL ALTER TABLE dbo.ORDERS ADD UPDATED_AT datetime2 NULL;
GO

IF OBJECT_ID(N'dbo.FORM_ORDER_RECEIPTS', N'U') IS NULL
CREATE TABLE dbo.FORM_ORDER_RECEIPTS (TOKEN nvarchar(400) NOT NULL PRIMARY KEY, ORDER_ID int NOT NULL, BODY_HASH nvarchar(64) NOT NULL);
