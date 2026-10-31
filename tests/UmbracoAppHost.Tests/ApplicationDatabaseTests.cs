using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UmbracoAppHost.Application;
using AppBlocks.Databases;

namespace UmbracoAppHost.Tests;

/// <summary>The sample's application database: its embedded schema and seed, on both dialects.</summary>
public sealed class ApplicationDatabaseTests
{
    [Fact]
    public void The_sample_schema_is_embedded_and_seeds_the_sample_orders()
    {
        Assert.Equal("UmbracoAppHost:Sample/Schema", SampleData.Schema.Id);
        var file = Path.Combine(Folder(), "sample.db");
        SampleData.EnsureSeeded(file);
        Assert.Equal(500L, Scalar(file, "SELECT COUNT(*) FROM ORDERS"));
        Assert.Equal(5L, Scalar(file, "SELECT COUNT(*) FROM pragma_table_info('ORDERS') WHERE name IN ('VERSION', 'CREATED_BY', 'CREATED_AT', 'UPDATED_BY', 'UPDATED_AT')"));
        Assert.Equal("001-orders.sql", Scalar(file, "SELECT SCRIPT FROM APP_SCHEMA"));
        // One to three lines for each of the first 120 orders.
        Assert.Equal(120L, Scalar(file, "SELECT COUNT(DISTINCT ORDER_ID) FROM ORDER_LINES WHERE ORDER_ID BETWEEN 1 AND 120"));
        Assert.InRange((long)Scalar(file, "SELECT COUNT(*) FROM ORDER_LINES")!, 120L, 360L);
        Assert.Equal(0L, Scalar(file, "SELECT COUNT(*) FROM ORDER_LINES WHERE QUANTITY NOT BETWEEN 1 AND 5 OR UNIT_PRICE NOT BETWEEN 9.99 AND 498.99"));
    }

    [Fact]
    public void A_sample_database_from_before_the_audit_columns_gains_them_in_place()
    {
        var file = Path.Combine(Folder(), "old-sample.db");
        Execute(file, """
            CREATE TABLE ORDERS (ORDER_ID INTEGER PRIMARY KEY, CUSTOMER TEXT NOT NULL, REGION TEXT NOT NULL, STATUS TEXT NOT NULL,
                AMOUNT NUMERIC NOT NULL, ORDER_DATE TEXT NOT NULL, NOTES TEXT NULL);
            INSERT INTO ORDERS (CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE) VALUES ('Kept', 'NORTH', 'NEW', 1, '2025-01-01');
            """);
        SampleData.EnsureSeeded(file);
        Assert.Equal(1L, Scalar(file, "SELECT COUNT(*) FROM ORDERS")); // Seeded only when empty.
        Assert.Equal(1L, Scalar(file, "SELECT VERSION FROM ORDERS"));
        // Lines are seeded only for the orders that exist.
        Assert.Equal(0L, Scalar(file, "SELECT COUNT(*) FROM ORDER_LINES WHERE ORDER_ID <> 1"));
        Assert.NotEqual(0L, Scalar(file, "SELECT COUNT(*) FROM ORDER_LINES"));
        Assert.Equal(5L, Scalar(file, "SELECT COUNT(*) FROM pragma_table_info('ORDERS') WHERE name IN ('VERSION', 'CREATED_BY', 'CREATED_AT', 'UPDATED_BY', 'UPDATED_AT')"));
    }

    [LocalDbFact]
    public async Task The_sample_seeds_through_a_registered_sql_server_connection()
    {
        const string master = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Connect Timeout=90;TrustServerCertificate=true";
        var database = "app_db_test_" + Guid.NewGuid().ToString("N")[..12];
        ExecuteSqlServer(master, $"CREATE DATABASE [{database}]");
        var connectionString = master + $";Initial Catalog={database};Pooling=false";
        try
        {
            // The sample's SQL Server script and seed, through the hook of a registered connection opened asynchronously.
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SampleDb"] = connectionString }).Build();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IConfiguration>(configuration);
            collection.AddInteractiveReports(configuration).AddAppBlocksConnections(collection, connections => connections.AddApplicationDatabase("SampleDb", SampleData.Schema));
            await using (var provider = collection.AddAppBlocksReportTestSupport().BuildServiceProvider())
            await using (var sample = provider.GetRequiredService<IReportConnectionFactory>().CreateConnection("SampleDb"))
            {
                Assert.IsType<SqlConnection>(sample);
                await sample.OpenAsync();
            }
            Assert.Equal(500, ScalarSqlServer(connectionString, "SELECT COUNT(*) FROM dbo.ORDERS"));
            Assert.Equal(0, ScalarSqlServer(connectionString, "SELECT COUNT(*) FROM dbo.FORM_ORDER_RECEIPTS"));
            // The seed is the same sequence on both dialects.
            var sqlite = Path.Combine(Folder(), "sample.db");
            SampleData.EnsureSeeded(sqlite);
            Assert.Equal(Scalar(sqlite, "SELECT CUSTOMER || ' ' || ORDER_DATE || ' ' || printf('%.2f', AMOUNT) FROM ORDERS WHERE ORDER_ID = 500"),
                ScalarSqlServer(connectionString, "SELECT CUSTOMER + ' ' + CONVERT(char(10), ORDER_DATE, 23) + ' ' + CONVERT(varchar(20), AMOUNT) FROM dbo.ORDERS WHERE ORDER_ID = 500"));
            Assert.Equal(Convert.ToInt32(Scalar(sqlite, "SELECT COUNT(*) FROM ORDER_LINES")), ScalarSqlServer(connectionString, "SELECT COUNT(*) FROM dbo.ORDER_LINES"));
            Assert.Equal(Scalar(sqlite, "SELECT ITEM || ' ' || QUANTITY || ' ' || printf('%.2f', UNIT_PRICE) FROM ORDER_LINES ORDER BY LINE_ID DESC LIMIT 1"),
                ScalarSqlServer(connectionString, "SELECT TOP 1 ITEM + ' ' + CONVERT(varchar(10), QUANTITY) + ' ' + CONVERT(varchar(20), UNIT_PRICE) FROM dbo.ORDER_LINES ORDER BY LINE_ID DESC"));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            ExecuteSqlServer(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    private static string Folder() => Directory.CreateTempSubdirectory("app-db-test-").FullName;

    private static SqliteConnection Open(string file)
    {
        var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Execute(string file, string sql)
    {
        using var connection = Open(file);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string file, string sql)
    {
        using var connection = Open(file);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void ExecuteSqlServer(string connection, string sql)
    {
        using var database = new SqlConnection(connection);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 90;
        command.ExecuteNonQuery();
    }

    private static object? ScalarSqlServer(string connection, string sql)
    {
        using var database = new SqlConnection(connection);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
