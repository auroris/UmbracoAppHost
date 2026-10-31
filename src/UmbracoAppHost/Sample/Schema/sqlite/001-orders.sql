-- The sample orders that the "orders" report, the Orders data source and the sample.orders resource read and write.
CREATE TABLE IF NOT EXISTS ORDERS (
    ORDER_ID   INTEGER PRIMARY KEY,
    CUSTOMER   TEXT    NOT NULL,
    REGION     TEXT    NOT NULL,
    STATUS     TEXT    NOT NULL,
    AMOUNT     NUMERIC NOT NULL,
    ORDER_DATE TEXT    NOT NULL,
    NOTES      TEXT    NULL,
    VERSION    INTEGER NOT NULL DEFAULT 1,
    CREATED_BY TEXT    NULL,
    CREATED_AT TEXT    NULL,
    UPDATED_BY TEXT    NULL,
    UPDATED_AT TEXT    NULL
);

-- Columns added after the first release, so a sample database created before them gains them in place:
-- the version both form examples compare, and the audit columns the Orders data source fills.
ALTER TABLE ORDERS ADD COLUMN VERSION INTEGER NOT NULL DEFAULT 1;
ALTER TABLE ORDERS ADD COLUMN CREATED_BY TEXT NULL;
ALTER TABLE ORDERS ADD COLUMN CREATED_AT TEXT NULL;
ALTER TABLE ORDERS ADD COLUMN UPDATED_BY TEXT NULL;
ALTER TABLE ORDERS ADD COLUMN UPDATED_AT TEXT NULL;

-- The sample.orders resource's create receipts.
CREATE TABLE IF NOT EXISTS FORM_ORDER_RECEIPTS (TOKEN TEXT PRIMARY KEY, ORDER_ID INTEGER NOT NULL, BODY_HASH TEXT NOT NULL);
