using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ImagineWeb.Infrastructure.Configuration;

public class DbConfigurationProvider : ConfigurationProvider
{
    private readonly string _connectionString;

    public DbConfigurationProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // Check if table exists
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='AppSettings'";
            if (checkCmd.ExecuteScalar() is null)
                return;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Key, Value FROM AppSettings";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var value = reader.IsDBNull(1) ? null : reader.GetString(1);
                data[key] = value;
            }
        }
        catch
        {
            // DB not ready yet — fall through to appsettings.json values
        }

        Data = data;
    }

    public void Reload()
    {
        Load();
        OnReload();
    }
}

public class DbConfigurationSource : IConfigurationSource
{
    private readonly string _connectionString;

    public DbConfigurationSource(string connectionString)
    {
        _connectionString = connectionString;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new DbConfigurationProvider(_connectionString);
    }
}

public static class DbConfigurationExtensions
{
    public static IConfigurationBuilder AddDbConfiguration(this IConfigurationBuilder builder, string connectionString)
    {
        builder.Add(new DbConfigurationSource(connectionString));
        return builder;
    }
}
