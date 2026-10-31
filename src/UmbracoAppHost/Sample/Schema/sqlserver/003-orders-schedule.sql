-- The SQLite sample's Orders schedule bars on SQL Server: the first 48 orders moved to the weeks around today, pending
-- ones open-ended. The date arithmetic differs by database, so it is here and the data source reads this view on either.
CREATE OR ALTER VIEW dbo.ORDERS_SCHEDULE_BARS AS
SELECT ORDER_ID AS [KEY],
       CUSTOMER AS [ROW],
       DATEADD(day, (ORDER_ID * 7) % 24 - 6, CAST(GETDATE() AS date)) AS [START],
       CASE WHEN STATUS = 'PENDING' THEN NULL
            ELSE DATEADD(day, (ORDER_ID * 7) % 24 - 6 + ORDER_ID % 5 + 1, CAST(GETDATE() AS date)) END AS [END],
       CONCAT(N'Order ', ORDER_ID) AS [LABEL],
       STATUS AS [KIND],
       CAST(NULL AS nvarchar(400)) AS [LINK]
FROM dbo.ORDERS
WHERE ORDER_ID <= 48;
