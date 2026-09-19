using Haley.Enums;
using Haley.Models;
using Haley.Utils;
using MySqlConnector;

namespace WFE.Test.Fixtures;

internal sealed class TestDatabase : IAsyncDisposable {
    private readonly string _adminConnection;
    public string Name { get; } = "haleyflow_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; }
    public AdapterGateway Gateway { get; } = new(false) { ThrowCRUDExceptions = true };

    private TestDatabase(string connection) {
        var builder = new MySqlConnectionStringBuilder(connection);
        if (builder.Server != "127.0.0.1" && builder.Server != "localhost")
            throw new InvalidOperationException("Regression databases must run on a disposable local server.");
        builder.Database = string.Empty;
        builder.AllowUserVariables = true;
        _adminConnection = builder.ConnectionString;
        builder.Database = Name;
        ConnectionString = builder.ConnectionString;
        Gateway.Add(new AdapterConfig {
            AdapterKey = "test", DBName = Name, DBType = TargetDB.maria,
            ConnectionString = ConnectionString,
            ConnectionInfo = new ConInfo { ConString = ConnectionString, Target = TargetDB.maria, IgnoreSsl = true }
        });
    }

    public static async Task<TestDatabase> CreateAsync(string schema = "engine.sql") {
        var database = new TestDatabase(Environment.GetEnvironmentVariable("HALEYFLOW_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Missing test database connection."));
        await using var admin = new MySqlConnection(database._adminConnection);
        await admin.OpenAsync();
        await new MySqlCommand($"CREATE DATABASE `{database.Name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", admin).ExecuteNonQueryAsync();
        try {
            await database.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", schema)));
            return database;
        } catch {
            await database.DisposeAsync();
            throw;
        }
    }

    public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters) {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters) {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var value = await command.ExecuteScalarAsync();
        return typeof(T) == typeof(string) ? (T)(object)value!.ToString()! : (T)Convert.ChangeType(value!, typeof(T));
    }

    public async ValueTask DisposeAsync() {
        MySqlConnection.ClearAllPools();
        await using var admin = new MySqlConnection(_adminConnection);
        await admin.OpenAsync();
        // Name is generated locally and never accepted from configuration or a caller.
        await new MySqlCommand($"DROP DATABASE IF EXISTS `{Name}`", admin).ExecuteNonQueryAsync();
    }
}
