-- The SQLite sample's row versions on SQL Server: VERSION counts every change to an order or a line, whoever writes it.
-- The sample.orders class advances it itself; the Orders data sources compare a checksum and leave it to the database.
CREATE OR ALTER TRIGGER dbo.ORDERS_VERSION ON dbo.ORDERS AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    UPDATE o SET VERSION = o.VERSION + 1
    FROM dbo.ORDERS o
    JOIN inserted i ON i.ORDER_ID = o.ORDER_ID
    JOIN deleted d ON d.ORDER_ID = i.ORDER_ID
    WHERE i.VERSION = d.VERSION;
END
GO

CREATE OR ALTER TRIGGER dbo.ORDER_LINES_VERSION ON dbo.ORDER_LINES AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    UPDATE l SET VERSION = l.VERSION + 1
    FROM dbo.ORDER_LINES l
    JOIN inserted i ON i.LINE_ID = l.LINE_ID
    JOIN deleted d ON d.LINE_ID = i.LINE_ID
    WHERE i.VERSION = d.VERSION;
END
