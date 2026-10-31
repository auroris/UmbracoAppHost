-- The bars of the Orders schedule: the first 48 orders moved to the weeks around today, pending ones open-ended. The
-- date arithmetic differs by database, so it is here and the data source that reads this view is the same on each.
DROP VIEW IF EXISTS ORDERS_SCHEDULE_BARS;
CREATE VIEW ORDERS_SCHEDULE_BARS AS
SELECT ORDER_ID AS "KEY",
       CUSTOMER AS "ROW",
       date('now', 'localtime', ((ORDER_ID * 7) % 24 - 6) || ' days') AS "START",
       CASE WHEN STATUS = 'PENDING' THEN NULL
            ELSE date('now', 'localtime', ((ORDER_ID * 7) % 24 - 6 + ORDER_ID % 5 + 1) || ' days') END AS "END",
       'Order ' || ORDER_ID AS "LABEL",
       STATUS AS "KIND",
       NULL AS "LINK"
FROM ORDERS
WHERE ORDER_ID <= 48;
