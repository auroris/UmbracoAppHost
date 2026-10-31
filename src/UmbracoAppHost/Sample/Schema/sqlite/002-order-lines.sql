-- The lines of the sample orders, which the Orders lines data source and the Orders form's tabular form read and write.
CREATE TABLE IF NOT EXISTS ORDER_LINES (
    LINE_ID    INTEGER PRIMARY KEY,
    ORDER_ID   INTEGER NOT NULL REFERENCES ORDERS (ORDER_ID),
    ITEM       TEXT    NOT NULL,
    QUANTITY   INTEGER NOT NULL DEFAULT 1,
    UNIT_PRICE NUMERIC NOT NULL DEFAULT 0,
    NOTES      TEXT    NULL,
    VERSION    INTEGER NOT NULL DEFAULT 1,
    CREATED_BY TEXT    NULL,
    CREATED_AT TEXT    NULL,
    UPDATED_BY TEXT    NULL,
    UPDATED_AT TEXT    NULL
);

-- A tabular form lists one order's lines at a time.
CREATE INDEX IF NOT EXISTS IX_ORDER_LINES_ORDER_ID ON ORDER_LINES (ORDER_ID);
