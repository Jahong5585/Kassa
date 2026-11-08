using Npgsql;
using NpgsqlTypes;

namespace Kasa.Api;

internal static class Database
{
    public static NpgsqlCommand Sql(this NpgsqlConnection db, NpgsqlTransaction tx, string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, db, tx);
        foreach (var value in values)
        {
            command.Parameters.Add(value is null
                ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Numeric, Value = DBNull.Value }
                : new NpgsqlParameter { Value = value });
        }
        return command;
    }

    public static async Task<int> Run(this NpgsqlConnection db, NpgsqlTransaction tx, CancellationToken ct,
        string sql, params object?[] values)
    {
        await using var command = db.Sql(tx, sql, values);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public static decimal? DecimalOrNull(this NpgsqlDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : reader.GetDecimal(i);

    public static DateTime? TimeOrNull(this NpgsqlDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : reader.GetDateTime(i);
}
