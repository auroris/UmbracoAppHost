using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using AppBlocks.Authentication;
using AppBlocks.Connections;
using AppBlocks.Databases;
using AppBlocks.Forms;

namespace UmbracoAppHost.Application;

public sealed record OrderCommand(
    [property: Required, StringLength(200)] string Customer,
    [property: Required, RegularExpression("^(NORTH|SOUTH|EAST|WEST)$")] string Region,
    [property: Required, RegularExpression("^(NEW|PENDING|SHIPPED|CANCELLED)$")] string Status,
    [property: Range(typeof(decimal), "0", "999999999")] decimal Amount,
    DateOnly OrderDate,
    [property: StringLength(4000)] string? Notes);

/// <summary>
/// Working sample policy: authenticated website members and backoffice Administrators can read and change sample orders.
/// Replace AllowedAsync with application rules, including row-specific checks, for a real resource.
/// Neither backoffice publication nor displayed capabilities grants a database operation.
/// It reads and writes the <see cref="SampleReport.Connection"/> connection, as the Orders report does, so it needs
/// <see cref="SampleReport.AddSampleOrders(AppBlocksBuilder, IServiceCollection)"/> and follows
/// <c>ConnectionStrings:ReportsSampleDb</c>: the SQLite file in the instance folder unless that names SQL Server.
/// </summary>
public sealed class OrdersFormResource(IApplicationConnectionFactory connections, ApplicationAccessPolicy? access = null)
    : FormResource<OrderCommand, OrderCommand>
{
    public override string Alias => "sample.orders";
    public override IReadOnlyList<FormField> Fields { get; } = [
        new("customer", "Customer", "text", Required: true, MaxLength: 200),
        new("region", "Region", "text", Required: true, Choices: Choices("NORTH", "SOUTH", "EAST", "WEST")),
        new("status", "Status", "text", Required: true, Choices: Choices("NEW", "PENDING", "SHIPPED", "CANCELLED")),
        new("amount", "Amount", "number", Required: true, Minimum: 0, Maximum: 999999999),
        new("orderDate", "Order date", "date", Required: true),
        new("notes", "Notes", "text", MaxLength: 4000),
    ];
    private static FormChoice[] Choices(params string[] values) => values.Select(v => new FormChoice(v, v)).ToArray();
    private async Task<bool> AllowedAsync(FormExecutionContext context) => context.Member.Identity?.IsAuthenticated == true
        && context.Member.FindFirstValue(ClaimTypes.NameIdentifier) is not null
        && (context.Member.Identity.AuthenticationType == IdentityConstants.ApplicationScheme
            || access is not null && await access.IsBackOfficeAdministratorAsync(context.Member));
    private DbConnection Connection() => connections.CreateConnection(SampleReport.Connection);
    private static bool ParseKey(string key, out long id) => long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    public override async Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct)
        => !await AllowedAsync(context) ? FormResult.Denied() : FormResult.Ok(new(null, null,
            new Dictionary<string, object?> { ["customer"] = "", ["region"] = "NORTH", ["status"] = "NEW", ["amount"] = "0.00", ["orderDate"] = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"), ["notes"] = null }, new(Create: true)));

    public override async Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct)
    {
        if (!await AllowedAsync(context)) return FormResult.Denied();
        if (!ParseKey(key, out var id)) return FormResult.Missing();
        await using var connection = Connection(); await connection.OpenAsync(ct);
        return await Read(connection, id, ct);
    }

    private static async Task<FormResult> Read(DbConnection connection, long id, CancellationToken ct, DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES, VERSION FROM ORDERS WHERE ORDER_ID = @id";
        Add(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return FormResult.Missing();
        return FormResult.Ok(new(id.ToString(CultureInfo.InvariantCulture), Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, object?> {
                ["customer"] = reader.GetString(0), ["region"] = reader.GetString(1), ["status"] = reader.GetString(2),
                ["amount"] = reader.GetDecimal(3).ToString(CultureInfo.InvariantCulture), ["orderDate"] = reader.GetValue(4) is DateTime date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : reader.GetString(4),
                ["notes"] = reader.IsDBNull(5) ? null : reader.GetString(5),
            }, new(Update: true, Delete: true)));
    }

    protected override async Task<FormResult> CreateAsync(OrderCommand input, FormExecutionContext context, CancellationToken ct)
    {
        if (!await AllowedAsync(context)) return FormResult.Denied();
        if (!Guid.TryParse(context.SubmissionKey, out var submission)) return FormResult.Invalid("", "A submission identifier is required.");
        if (input.OrderDate == default) return FormResult.Invalid("orderDate", "An order date is required.");
        var token = $"{ApplicationAccessPolicy.UserKey(context.Member)}:{context.PageKey:N}:{context.FormKey:N}:{submission:N}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input, Json))));
        await using var connection = Connection(); await connection.OpenAsync(ct);
        // Serializable, so two sends of one submission cannot both miss the receipt on SQL Server; SQLite's writers already take turns.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var receipt = connection.CreateCommand(); receipt.Transaction = transaction;
        receipt.CommandText = "SELECT ORDER_ID, BODY_HASH FROM FORM_ORDER_RECEIPTS WHERE TOKEN = @token";
        Add(receipt, "@token", token);
        await using (var reader = await receipt.ExecuteReaderAsync(ct)) {
            if (await reader.ReadAsync(ct)) {
                var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture); var same = reader.GetString(1) == hash;
                await reader.DisposeAsync();
                return same ? await Read(connection, id, ct, transaction) : FormResult.Invalid("", "This submission was already used with different values. Reload the form.");
            }
        }
        await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        var sqlServer = ApplicationDatabase.Dialect(connection) == "sqlserver";
        insert.CommandText = $"""
            INSERT INTO ORDERS (CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES) {(sqlServer ? "OUTPUT INSERTED.ORDER_ID" : "")}
            VALUES (@customer, @region, @status, @amount, @date, @notes) {(sqlServer ? "" : "RETURNING ORDER_ID")}
            """;
        Bind(insert, input);
        var newId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        receipt.CommandText = "INSERT INTO FORM_ORDER_RECEIPTS (TOKEN, ORDER_ID, BODY_HASH) VALUES (@token, @id, @hash)";
        Add(receipt, "@id", newId); Add(receipt, "@hash", hash);
        await receipt.ExecuteNonQueryAsync(ct);
        var result = await Read(connection, newId, ct, transaction);
        await transaction.CommitAsync(ct);
        return result;
    }

    protected override async Task<FormResult> UpdateAsync(string key, OrderCommand input, string? version, FormExecutionContext context, CancellationToken ct)
    {
        if (!await AllowedAsync(context)) return FormResult.Denied();
        if (!ParseKey(key, out var id)) return FormResult.Missing();
        if (!long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)) return FormResult.Conflict();
        if (input.OrderDate == default) return FormResult.Invalid("orderDate", "An order date is required.");
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = """
            UPDATE ORDERS SET CUSTOMER=@customer, REGION=@region, STATUS=@status, AMOUNT=@amount,
                ORDER_DATE=@date, NOTES=@notes, VERSION=VERSION+1 WHERE ORDER_ID=@id AND VERSION=@version
            """;
        Bind(update, input); Add(update, "@id", id); Add(update, "@version", revision);
        if (await update.ExecuteNonQueryAsync(ct) != 1) return FormResult.Conflict();
        var result = await Read(connection, id, ct, transaction);
        await transaction.CommitAsync(ct);
        return result;
    }

    public override async Task<FormResult> DeleteAsync(string key, string? version, FormExecutionContext context, CancellationToken ct)
    {
        if (!await AllowedAsync(context)) return FormResult.Denied();
        if (!ParseKey(key, out var id)) return FormResult.Missing();
        if (!long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)) return FormResult.Conflict();
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM ORDERS WHERE ORDER_ID=@id AND VERSION=@version";
        Add(delete, "@id", id); Add(delete, "@version", revision);
        return await delete.ExecuteNonQueryAsync(ct) == 1 ? new(204) : FormResult.Conflict();
    }

    private static void Bind(DbCommand command, OrderCommand input)
    {
        Add(command, "@customer", input.Customer);
        Add(command, "@region", input.Region);
        Add(command, "@status", input.Status);
        Add(command, "@amount", input.Amount);
        // Text on both: SQLite keeps dates as text, and SQL Server converts ISO dates to its date column.
        Add(command, "@date", input.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "@notes", input.Notes);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
