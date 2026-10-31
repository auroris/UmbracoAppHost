using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using AppBlocks.Databases;

namespace UmbracoAppHost.Application;

/// <summary>
/// The development sample database the "orders" report reads: the scripts in <c>Sample/Schema/</c> and a
/// deterministic seed, so every checkout shows the same rows. Production data sources replace this connection entirely.
/// </summary>
public static class SampleData
{
    private static readonly string[] Customers =
    [
        "Acme Corp", "Globex", "Initech", "Umbrella Group", "Stark Industries", "Wayne Enterprises",
        "Tyrell Corp", "Wonka Industries", "Cyberdyne Systems", "Aperture Science", "Soylent Corp", "Hooli",
    ];

    private static readonly string[] Regions = ["NORTH", "SOUTH", "EAST", "WEST"];
    private static readonly string[] Statuses = ["NEW", "PENDING", "SHIPPED", "CANCELLED"];

    private static readonly string[] Items =
    [
        "Bolt", "Washer", "Bracket", "Cable", "Connector", "Fuse", "Gasket", "Hinge", "Pulley", "Sensor", "Valve", "Switch",
    ];

    /// <summary>
    /// The sample's tables for SQLite and SQL Server, then 500 orders and the lines of the first 120 when the tables are
    /// empty. There are no scripts for Oracle or PostgreSQL, so the seed stops at its first query there, and it writes
    /// <c>@</c> parameters, which suit the two it runs on.
    /// </summary>
    public static ApplicationSchema Schema { get; } = ApplicationSchema.FromEmbedded(typeof(SampleData).Assembly, "Sample/Schema").Then(Seed).Then(SeedLines);

    /// <summary>Creates the SQLite file and ensures <see cref="Schema"/> on it.</summary>
    public static void EnsureSeeded(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();
        ApplicationDatabase.Ensure(connection, Schema);
    }

    private static void Seed(DbConnection connection, string dialect)
    {
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM ORDERS";
            if (Convert.ToInt64(count.ExecuteScalar()) > 0) return;
        }

        var rng = new Random(42);
        var start = new DateOnly(2025, 1, 1);

        using var tx = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO ORDERS (CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES)
            VALUES (@customer, @region, @status, @amount, @date, @notes)
            """;
        DbParameter Parameter(string name, DbType type)
        {
            var parameter = insert.CreateParameter();
            parameter.ParameterName = name;
            parameter.DbType = type;
            insert.Parameters.Add(parameter);
            return parameter;
        }
        var pCustomer = Parameter("@customer", DbType.String);
        var pRegion = Parameter("@region", DbType.String);
        var pStatus = Parameter("@status", DbType.String);
        var pAmount = Parameter("@amount", DbType.Double);
        // Text on both: SQLite keeps dates as text, and SQL Server converts ISO dates to its date column.
        var pDate = Parameter("@date", DbType.String);
        var pNotes = Parameter("@notes", DbType.String);

        for (var i = 0; i < 500; i++)
        {
            pCustomer.Value = Customers[rng.Next(Customers.Length)];
            pRegion.Value = Regions[rng.Next(Regions.Length)];
            pStatus.Value = Statuses[rng.Next(Statuses.Length)];
            pAmount.Value = Math.Round(rng.NextDouble() * 24_990 + 10, 2);
            pDate.Value = start.AddDays(rng.Next(550)).ToString("yyyy-MM-dd");
            pNotes.Value = rng.Next(4) == 0 ? (object)$"Priority handling #{i}" : DBNull.Value;
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// One to three lines for each of orders 1 to 120, for the Orders form's tabular form. A seed of its own, so a
    /// sample database created before the lines existed gains them without its orders changing.
    /// </summary>
    private static void SeedLines(DbConnection connection, string dialect)
    {
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM ORDER_LINES";
            if (Convert.ToInt64(count.ExecuteScalar()) > 0) return;
        }

        // A sample database whose orders were not seeded here may lack some of them; their lines are skipped, not orphaned.
        var orders = new HashSet<long>();
        using (var existing = connection.CreateCommand())
        {
            existing.CommandText = "SELECT ORDER_ID FROM ORDERS WHERE ORDER_ID BETWEEN 1 AND 120";
            using var reader = existing.ExecuteReader();
            while (reader.Read()) orders.Add(Convert.ToInt64(reader.GetValue(0)));
        }

        var rng = new Random(7);

        using var tx = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO ORDER_LINES (ORDER_ID, ITEM, QUANTITY, UNIT_PRICE, NOTES)
            VALUES (@order, @item, @quantity, @price, @notes)
            """;
        DbParameter Parameter(string name, DbType type)
        {
            var parameter = insert.CreateParameter();
            parameter.ParameterName = name;
            parameter.DbType = type;
            insert.Parameters.Add(parameter);
            return parameter;
        }
        var pOrder = Parameter("@order", DbType.Int32);
        var pItem = Parameter("@item", DbType.String);
        var pQuantity = Parameter("@quantity", DbType.Int32);
        var pPrice = Parameter("@price", DbType.Double);
        var pNotes = Parameter("@notes", DbType.String);

        var line = 0;
        for (var order = 1; order <= 120; order++)
        {
            var lines = rng.Next(1, 4);
            for (var i = 0; i < lines; i++, line++)
            {
                pOrder.Value = order;
                pItem.Value = Items[rng.Next(Items.Length)];
                pQuantity.Value = rng.Next(1, 6);
                pPrice.Value = Math.Round(rng.NextDouble() * 489 + 9.99, 2);
                pNotes.Value = line % 4 == 3 ? (object)$"Line note #{line + 1}" : DBNull.Value;
                // The values are drawn either way, so the lines of the orders that exist are always the same.
                if (orders.Contains(order)) insert.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }
}
