-- VERSION counts every change to an order or a line, whoever writes it. The sample.orders class compares and advances
-- it itself; the Orders data sources compare a checksum of the row and leave it alone, so the database advances it for
-- them, and the class still sees their changes as a newer version.
CREATE TRIGGER IF NOT EXISTS ORDERS_VERSION AFTER UPDATE ON ORDERS
FOR EACH ROW WHEN NEW.VERSION = OLD.VERSION
BEGIN
    UPDATE ORDERS SET VERSION = OLD.VERSION + 1 WHERE ORDER_ID = NEW.ORDER_ID;
END;

CREATE TRIGGER IF NOT EXISTS ORDER_LINES_VERSION AFTER UPDATE ON ORDER_LINES
FOR EACH ROW WHEN NEW.VERSION = OLD.VERSION
BEGIN
    UPDATE ORDER_LINES SET VERSION = OLD.VERSION + 1 WHERE LINE_ID = NEW.LINE_ID;
END;
