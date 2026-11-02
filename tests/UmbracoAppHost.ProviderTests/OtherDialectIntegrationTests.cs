using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using AppBlocks.Connections;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace UmbracoAppHost.ProviderTests;

/// <summary>Opt-in tests use a disposable table in the configured test database; no database is created or dropped.</summary>
public sealed class OtherDialectIntegrationTests
{
    [ExternalDatabaseFact("UMBRACOAPPHOST_TEST_POSTGRES")]
    public Task PostgreSql_reads_writes_and_checks_concurrency() => Exercise(DatabaseDialect.Postgres, "UMBRACOAPPHOST_TEST_POSTGRES");

    [ExternalDatabaseFact("UMBRACOAPPHOST_TEST_ORACLE")]
    public Task Oracle_identity_reads_writes_and_checks_concurrency() => Exercise(DatabaseDialect.Oracle, "UMBRACOAPPHOST_TEST_ORACLE");

    [ExternalDatabaseFact("UMBRACOAPPHOST_TEST_ORACLE11G")]
    public Task Oracle_sequences_read_write_and_check_concurrency() => Exercise(DatabaseDialect.Oracle11g, "UMBRACOAPPHOST_TEST_ORACLE11G");

    private static async Task Exercise(DatabaseDialect kind, string environment)
    {
        var connectionString = Environment.GetEnvironmentVariable(environment)!;
        DbConnection Connection() => kind == DatabaseDialect.Postgres ? new NpgsqlConnection(connectionString) : new OracleConnection(connectionString);
        var table = "DS_" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        var sequence = table + "_SEQ";
        var oracle = kind != DatabaseDialect.Postgres;
        var sourceName = "\"" + table + "\"";
        var sequenceCreated = false;
        var tableCreated = false;
        try
        {
            if (kind == DatabaseDialect.Oracle11g)
            {
                Execute(Connection, $"CREATE SEQUENCE {sequence} START WITH 1 INCREMENT BY 1");
                sequenceCreated = true;
            }
            var keyType = kind switch {
                DatabaseDialect.Postgres => "bigint GENERATED ALWAYS AS IDENTITY",
                DatabaseDialect.Oracle => "NUMBER GENERATED ALWAYS AS IDENTITY",
                _ => "NUMBER(18)",
            };
            var textType = oracle ? "VARCHAR2(100)" : "VARCHAR(100)";
            var integer = oracle ? "NUMBER(10)" : "INTEGER";
            var boolean = oracle ? "NUMBER(1) DEFAULT 0" : "BOOLEAN DEFAULT FALSE";
            Execute(Connection, $"""
                CREATE TABLE {sourceName} (
                    "ID" {keyType} PRIMARY KEY, "TITLE" {textType} NOT NULL,
                    "OWNER_NAME" {textType} NOT NULL, "VERSION" {integer} DEFAULT 1 NOT NULL,
                    "AMOUNT" DECIMAL(12,2) DEFAULT 0 NOT NULL, "DONE" {boolean} NOT NULL,
                    "HAPPENED_AT" TIMESTAMP NULL, "UPDATED_BY" {textType} NULL, "UPDATED_AT" TIMESTAMP NULL)
                """);
            tableCreated = true;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IApplicationConnectionFactory>(new TestConnections(Connection, kind));
            services.AddSingleton<DataSourceRuntime>();
            await using var provider = services.BuildServiceProvider();
            var runtime = provider.GetRequiredService<DataSourceRuntime>();
            var definition = new DataSourceDefinition {
                Access = DataSourceAccess.NotPublic,
                Key = Guid.NewGuid(), Name = "Tasks", Alias = "tasks", Connection = "TestDb", Source = sourceName,
                KeyColumns = ["ID"], AllowInsert = true, AllowUpdate = true, AllowDelete = true, VersionColumn = "VERSION",
                KeySequences = kind == DatabaseDialect.Oracle11g ? new Dictionary<string, string> { ["ID"] = sequence } : new Dictionary<string, string>(),
                RowRestriction = "\"OWNER_NAME\" = :USER_NAME",
                Mapping = [new("done", "DONE", Kind: FieldKinds.Checkbox)],
                InsertDefaults = [new("OWNER_NAME", new(BindVariableKind.UserName), null)],
                AuditColumns = new(UpdatedBy: "UPDATED_BY", UpdatedAt: "UPDATED_AT"),
            };
            Assert.Empty(runtime.Validate(definition));
            var source = new SqlDataSource(definition, runtime, new TestUsers(), []);
            Assert.Null(source.Problem);
            var context = Context("owner");
            var created = await source.CreateAsync(Json(new { title = "First", amount = "12.50", done = true, happenedAt = "2026-10-24T12:00:00-06:00" }), context, default);
            Assert.True(created.Status == 200, JsonSerializer.Serialize(created));
            Assert.NotNull(created.Record!.Key);
            Assert.Equal("12.50", decimal.Parse(created.Record.Values["amount"]!.ToString()!, System.Globalization.CultureInfo.InvariantCulture).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(true, created.Record.Values["done"]);
            var literal = oracle ? "q'[It's :NOT_A_BIND;]'" : "$text$It's :NOT_A_BIND;$text$";
            var query = new SqlDataSource(definition with { Source = $"SELECT t.*, {literal} AS \"LITERAL\" FROM {sourceName} t",
                AllowInsert = false, AllowUpdate = false, AllowDelete = false, KeySequences = new Dictionary<string, string>(),
                VersionColumn = null, AuditColumns = new(), InsertDefaults = [] }, runtime, new TestUsers(), []);
            Assert.Null(query.Problem);
            Assert.Equal("It's :NOT_A_BIND;", (await query.LoadAsync(created.Record.Key!, context, default)).Record!.Values["literal"]);
            Assert.Equal(404, (await source.LoadAsync(created.Record.Key!, Context("someone-else"), default)).Status);
            var updated = await source.UpdateAsync(created.Record.Key!, Json(new { title = "Second", done = false }), created.Record.Version!, context, default);
            Assert.True(updated.Status == 200, JsonSerializer.Serialize(updated));
            Assert.Equal("owner", updated.Record!.Values["updatedBy"]);
            Assert.NotEqual(created.Record.Version, updated.Record.Version);
            Assert.Equal(409, (await source.UpdateAsync(created.Record.Key!, Json(new { title = "Stale" }), created.Record.Version!, context, default)).Status);
            var checksum = new SqlDataSource(definition with { VersionColumn = null }, runtime, new TestUsers(), []);
            var before = await checksum.LoadAsync(created.Record.Key!, context, default);
            Execute(Connection, $"UPDATE {sourceName} SET \"TITLE\" = 'Outside change'");
            Assert.Equal(409, (await checksum.UpdateAsync(created.Record.Key!, Json(new { title = "Stale checksum" }), before.Record!.Version!, context, default)).Status);
            var fresh = await checksum.LoadAsync(created.Record.Key!, context, default);
            Assert.Equal(204, (await checksum.DeleteAsync(created.Record.Key!, fresh.Record!.Version!, context, default)).Status);
            Assert.Equal(404, (await source.LoadAsync(created.Record.Key!, context, default)).Status);
        }
        finally
        {
            if (tableCreated) Execute(Connection, $"DROP TABLE {sourceName}");
            if (sequenceCreated) Execute(Connection, $"DROP SEQUENCE {sequence}");
        }
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private sealed class TestConnections(Func<DbConnection> create, DatabaseDialect dialect) : IApplicationConnectionFactory
    {
        public DbConnection CreateConnection(string name) => create();
        public DatabaseDialect Dialect(string name) => dialect;
        public void SetDetectedDialect(string name, DatabaseDialect detected) => dialect = detected;
    }
    private static FormExecutionContext Context(string name) => new(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Name, name)], "test")),
        Guid.NewGuid(), Guid.NewGuid(), "test", Guid.NewGuid().ToString());

    private sealed class TestUsers : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult<DataSourceUser?>(
            new("1", null, principal.Identity!.Name!, null, [], false));
    }

    private static void Execute(Func<DbConnection> factory, string sql)
    {
        using var connection = factory();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        command.ExecuteNonQuery();
    }
}

public sealed class ExternalDatabaseFactAttribute : FactAttribute
{
    public ExternalDatabaseFactAttribute(string environment,
        [CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environment)))
            Skip = $"Set {environment} to an isolated test database to run this provider integration test.";
    }
}
