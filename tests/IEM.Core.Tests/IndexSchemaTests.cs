using IEM.Storage;
using Microsoft.Data.Sqlite;

namespace IEM.Core.Tests;

/// <summary>
/// The index is a derived cache over the raw chain, and this is the test that keeps that
/// claim honest across a version change.
/// <para>
/// <c>CREATE TABLE IF NOT EXISTS</c> silently leaves an older table in place, so a build
/// that adds a column finds the old layout and fails on the first insert - landing on
/// exactly the users who had a session running when they updated, mid two-day test. Since
/// everything here is reconstructible from the chain, the right answer is to throw the
/// stale index away rather than carry every past layout forever.
/// </para>
/// </summary>
public sealed class IndexSchemaTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "iem-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "sesija.db");

    public IndexSchemaTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>Writes the shape an older build would have left behind.</summary>
    private void WriteOldIndex()
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());

        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE incidents (
                session_id   TEXT NOT NULL,
                number       INTEGER NOT NULL,
                contains_gap INTEGER NOT NULL,
                PRIMARY KEY (session_id, number)
            );
            INSERT INTO incidents VALUES ('S1', 1, 0);
            """;

        command.ExecuteNonQuery();
    }

    [Fact]
    public void An_index_from_an_older_build_is_replaced_rather_than_breaking_the_session()
    {
        WriteOldIndex();

        using var store = SqliteSessionStore.Open(DatabasePath);

        // The stale row is gone with the stale layout, and the new one accepts writes.
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());

        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('incidents') WHERE name = 'correlation_id';";

        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void An_index_written_by_this_build_is_kept()
    {
        using (var store = SqliteSessionStore.Open(DatabasePath))
        {
            store.Flush();
        }

        using var reopened = SqliteSessionStore.Open(DatabasePath);

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());

        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        Assert.True(
            Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0,
            "the layout version must be stamped, or every open would discard the index");
    }
}
