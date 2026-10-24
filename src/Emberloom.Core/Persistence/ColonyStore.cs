using System.Text.Json;
using System.Text.Json.Serialization;
using Emberloom.Core.Colonists;
using Emberloom.Core.Simulation;
using Microsoft.Data.Sqlite;

namespace Emberloom.Core.Persistence;

/// <summary>
/// Colonies on disk — M11. One SQLite file holds many saves; a save is one row.
/// <para>
/// SQLite rather than a folder of JSON files because of what comes next: beliefs carry embeddings,
/// retrieval searches them, and sqlite-vec is how that search stops being a linear scan once a
/// colony has run for a few thousand days. The vectors are written as their own rows for exactly
/// that reason — the index will be built over them in place, and it stays local. Only
/// "text in, vector out" ever crosses the wire, which is the golden rule's one hard edge.
/// </para>
/// <para>
/// The colony itself is a JSON document rather than a hundred tables. Four of the next five
/// milestones add fields to it, and a schema that mirrored the domain would need a migration for
/// each of them; a document needs none, because an older save simply arrives with the newer fields
/// at their defaults. What is worth querying — which saves exist, how old they are, how big the
/// colony got — is lifted out into columns beside it.
/// </para>
/// </summary>
public sealed class ColonyStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // A colonist who has never had a child carries negative infinity as the day it last
        // happened, which plain JSON cannot write at all. Named literals round-trip it.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _connectionString;

    public ColonyStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);

        var folder = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path }.ToString();
        Initialise();
    }

    public string Path { get; }

    private void Initialise()
    {
        using var db = Open();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS colony (
                name         TEXT PRIMARY KEY,
                version      INTEGER NOT NULL,
                saved_at     TEXT NOT NULL,
                seed         INTEGER NOT NULL,
                day          REAL NOT NULL,
                living       INTEGER NOT NULL,
                ever_lived   INTEGER NOT NULL,
                document     TEXT NOT NULL
            );

            -- Beliefs and their vectors, beside the document rather than inside it. The document
            -- is rewritten whole on every save; these are what a vector index will be built over,
            -- and an index wants rows.
            CREATE TABLE IF NOT EXISTS belief (
                colony    TEXT NOT NULL,
                id        TEXT NOT NULL,
                owner     TEXT NOT NULL,
                claim     TEXT NOT NULL,
                embedding BLOB,
                PRIMARY KEY (colony, id)
            );

            CREATE INDEX IF NOT EXISTS belief_by_owner ON belief (colony, owner);
            """);
    }

    /// <summary>Writes a colony under a name, replacing whatever was saved under it before.</summary>
    public void Save(string name, Colony colony, BodySimulation body)
    {
        var snapshot = ColonySnapshot.Capture(colony, body);

        using var db = Open();
        using var transaction = db.BeginTransaction();

        using (var write = db.CreateCommand())
        {
            write.CommandText = """
                INSERT INTO colony (name, version, saved_at, seed, day, living, ever_lived, document)
                VALUES ($name, $version, $saved, $seed, $day, $living, $ever, $document)
                ON CONFLICT(name) DO UPDATE SET
                    version = excluded.version, saved_at = excluded.saved_at, seed = excluded.seed,
                    day = excluded.day, living = excluded.living, ever_lived = excluded.ever_lived,
                    document = excluded.document;
                """;

            write.Parameters.AddWithValue("$name", name);
            write.Parameters.AddWithValue("$version", snapshot.Version);
            write.Parameters.AddWithValue("$saved", snapshot.SavedAt.ToString("o"));
            write.Parameters.AddWithValue("$seed", snapshot.Seed);
            write.Parameters.AddWithValue("$day", colony.Day);
            write.Parameters.AddWithValue("$living", colony.Living.Count());
            write.Parameters.AddWithValue("$ever", colony.Colonists.Count);
            write.Parameters.AddWithValue("$document", JsonSerializer.Serialize(snapshot, Format));
            write.ExecuteNonQuery();
        }

        using (var clear = db.CreateCommand())
        {
            clear.CommandText = "DELETE FROM belief WHERE colony = $name;";
            clear.Parameters.AddWithValue("$name", name);
            clear.ExecuteNonQuery();
        }

        using (var insert = db.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO belief (colony, id, owner, claim, embedding)
                VALUES ($colony, $id, $owner, $claim, $embedding);
                """;

            var colonyName = insert.Parameters.Add("$colony", SqliteType.Text);
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var owner = insert.Parameters.Add("$owner", SqliteType.Text);
            var claim = insert.Parameters.Add("$claim", SqliteType.Text);
            var embedding = insert.Parameters.Add("$embedding", SqliteType.Blob);

            foreach (var person in colony.Colonists)
            {
                foreach (var belief in person.Memory.Beliefs)
                {
                    colonyName.Value = name;
                    id.Value = belief.Id;
                    owner.Value = belief.OwnerId;
                    claim.Value = belief.Claim;
                    embedding.Value = belief.Embedding is { } vector ? Bytes(vector) : DBNull.Value;
                    insert.ExecuteNonQuery();
                }
            }
        }

        transaction.Commit();
    }

    /// <summary>The colony saved under this name, or null if there is none.</summary>
    public (Colony Colony, BodySimulation Body)? Load(string name)
    {
        using var db = Open();
        using var read = db.CreateCommand();
        read.CommandText = "SELECT document FROM colony WHERE name = $name;";
        read.Parameters.AddWithValue("$name", name);

        if (read.ExecuteScalar() is not string document)
        {
            return null;
        }

        var snapshot = JsonSerializer.Deserialize<ColonySnapshot>(document, Format)
                       ?? throw new InvalidDataException($"The colony saved as '{name}' could not be read.");

        return snapshot.Restore();
    }

    public bool Delete(string name)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();

        using var remove = db.CreateCommand();
        remove.CommandText = "DELETE FROM belief WHERE colony = $name; DELETE FROM colony WHERE name = $name;";
        remove.Parameters.AddWithValue("$name", name);
        var gone = remove.ExecuteNonQuery() > 0;

        transaction.Commit();
        return gone;
    }

    /// <summary>What is on the shelf: newest first.</summary>
    public IReadOnlyList<SavedColony> List()
    {
        using var db = Open();
        using var read = db.CreateCommand();
        read.CommandText = """
            SELECT name, saved_at, seed, day, living, ever_lived, version
            FROM colony ORDER BY saved_at DESC;
            """;

        var saves = new List<SavedColony>();
        using var rows = read.ExecuteReader();
        while (rows.Read())
        {
            saves.Add(new SavedColony(
                rows.GetString(0),
                DateTimeOffset.Parse(rows.GetString(1)),
                rows.GetInt32(2),
                rows.GetDouble(3),
                rows.GetInt32(4),
                rows.GetInt32(5),
                rows.GetInt32(6)));
        }

        return saves;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();

        // A simulation writes far more often than it reads, and a half-written save is worse than
        // an old one; write-ahead logging gives both without holding the tick loop open.
        Execute(db, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static byte[] Bytes(IReadOnlyList<float> vector)
    {
        var bytes = new byte[vector.Count * sizeof(float)];
        for (var i = 0; i < vector.Count; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }
}

/// <summary>One colony on the shelf, without reading the whole thing back.</summary>
public sealed record SavedColony(
    string Name,
    DateTimeOffset SavedAt,
    int Seed,
    double Day,
    int Living,
    int EverLived,
    int Version);
