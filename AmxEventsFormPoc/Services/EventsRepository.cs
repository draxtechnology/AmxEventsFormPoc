using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using AmxEventsFormPoc.Models;

namespace AmxEventsFormPoc.Services;

/// <summary>
/// SQLite-backed storage for events. Every event MQTT delivers is inserted here as
/// not-accepted (Accepted = 0) the moment it arrives, so Current Events survives an
/// app restart; a row becomes Accepted = 1 either when an operator clicks
/// "Accept the Alarm" (Accept) or automatically when the panel reports the point
/// cleared — an MQTT "off" event (AcceptMatching) — at which point it drops off
/// Current Events and becomes visible on the History page instead.
///
/// Deliberately built on Microsoft.Data.Sqlite.Core + SQLitePCLRaw.bundle_e_sqlcipher
/// (see the .csproj) rather than the default Microsoft.Data.Sqlite package, so the
/// database can be switched to encrypted later just by setting EncryptionKey below —
/// no provider change and no file-format migration needed when that requirement lands.
/// </summary>
public sealed class EventsRepository : IDisposable
{
    private readonly string _databasePath;
    private SqliteConnection _connection;

    /// <summary>
    /// Leave null/blank for plain, unencrypted SQLite. Set this to a real key before
    /// calling Initialize() to turn encryption on — see the warning on the constant in
    /// MainForm.cs about where this value actually needs to live before this is
    /// anything more than a POC. Initialize() handles both a brand-new database (it's
    /// created encrypted from the first write) and an existing plain database left over
    /// from before encryption was turned on (it's rekeyed — encrypted in place — the
    /// one time that's needed).
    /// </summary>
    public string? EncryptionKey { get; set; }

    public EventsRepository(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmxEventsFormPoc", "events.db");

        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        var builder = new SqliteConnectionStringBuilder { DataSource = _databasePath };
        _connection = new SqliteConnection(builder.ConnectionString);
    }

    /// <summary>Opens the database (encrypting or rekeying it first if EncryptionKey is set) and creates the schema if it doesn't exist yet. Call once, at startup.</summary>
    public void Initialize()
    {
        // Must run once before any SQLite connection is opened anywhere in the process.
        SQLitePCL.Batteries_V2.Init();

        var databaseAlreadyExisted = File.Exists(_databasePath);

        _connection.Open();

        if (!string.IsNullOrEmpty(EncryptionKey))
        {
            SetKey(_connection, EncryptionKey);

            if (databaseAlreadyExisted && !CanReadSchema(_connection))
            {
                // The file already existed but isn't readable under this key — it's an
                // old plain (unencrypted) database from a run before encryption was
                // turned on. SQLCipher can't just start reading one of those once a key
                // is set, so reopen it as plain and rekey it (encrypt it in place) —
                // a one-off migration step, not something that runs again once it's done.
                _connection.Close();
                using (var plainConnection = new SqliteConnection(_connection.ConnectionString))
                {
                    plainConnection.Open();
                    Rekey(plainConnection, EncryptionKey);
                }

                _connection = new SqliteConnection(_connection.ConnectionString);
                _connection.Open();
                SetKey(_connection, EncryptionKey);
            }
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Events (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                ReceivedLocal TEXT    NOT NULL,
                Panel         TEXT    NOT NULL,
                Ext           TEXT    NOT NULL,
                Type          TEXT    NOT NULL,
                TypeId        INTEGER NOT NULL,
                IsOn          INTEGER NOT NULL,
                Value         INTEGER NOT NULL,
                Node          INTEGER NOT NULL,
                Loop          INTEGER NOT NULL,
                Input         INTEGER NOT NULL,
                InputType     INTEGER NOT NULL,
                Text          TEXT    NOT NULL,
                Text2         TEXT    NOT NULL,
                Text3         TEXT    NOT NULL,
                Raw           TEXT    NOT NULL,
                Accepted      INTEGER NOT NULL DEFAULT 0,
                AcceptedLocal TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Events_Accepted ON Events(Accepted);
            CREATE INDEX IF NOT EXISTS IX_Events_ReceivedLocal ON Events(ReceivedLocal);
            """;
        cmd.ExecuteNonQuery();
    }

    // PRAGMA key/rekey take the passphrase as a quoted SQL string literal, not as a
    // bound parameter — SQLCipher's own pragmas aren't parameterizable the way a normal
    // command is, so the value is escaped (doubling any embedded single quote, standard
    // SQL-string escaping) and embedded directly in the PRAGMA text instead.
    private static void SetKey(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA key = '{EscapeForPragma(key)}';";
        cmd.ExecuteNonQuery();
    }

    private static void Rekey(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA rekey = '{EscapeForPragma(key)}';";
        cmd.ExecuteNonQuery();
    }

    private static string EscapeForPragma(string value) => value.Replace("'", "''");

    // The only reliable way to tell "wrong/no key" apart from "fine" with SQLCipher:
    // try to actually read something. An encrypted file opened with the wrong key (or
    // a plain file opened with any key set) still "opens" without error — SQLite only
    // discovers the problem once it tries to parse a page, which a real read forces.
    private static bool CanReadSchema(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Inserts a newly-arrived "on" event as not accepted, and stamps row.Id with the new row's id.</summary>
    public void Insert(DraxEventRow row)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Events
                (ReceivedLocal, Panel, Ext, Type, TypeId, IsOn, Value, Node, Loop, Input, InputType, Text, Text2, Text3, Raw, Accepted)
            VALUES
                ($receivedLocal, $panel, $ext, $type, $typeId, $isOn, $value, $node, $loop, $input, $inputType, $text, $text2, $text3, $raw, 0);
            SELECT last_insert_rowid();
            """;
        AddRowParameters(cmd, row);
        row.Id = (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Manual "Accept the Alarm": marks one event, by its database Id, as accepted now.</summary>
    public void Accept(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE Events SET Accepted = 1, AcceptedLocal = $acceptedLocal WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$acceptedLocal", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Automatic accept: when the panel reports a point cleared (an "off" event),
    /// finds the still-active, not-yet-accepted row it corresponds to and marks it
    /// accepted — the same Panel/Node/Loop/Input/Type match MainForm already uses
    /// in memory (RemoveMatchingOnRow) to pair an "off" with the "on" it clears.
    /// </summary>
    public void AcceptMatching(DraxEventRow offRow)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Events SET Accepted = 1, AcceptedLocal = $acceptedLocal
            WHERE Accepted = 0 AND IsOn = 1
              AND Panel = $panel AND Node = $node AND Loop = $loop
              AND Input = $input AND Type = $type;
            """;
        cmd.Parameters.AddWithValue("$acceptedLocal", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$panel", offRow.Panel);
        cmd.Parameters.AddWithValue("$node", offRow.Node);
        cmd.Parameters.AddWithValue("$loop", offRow.Loop);
        cmd.Parameters.AddWithValue("$input", offRow.Input);
        cmd.Parameters.AddWithValue("$type", offRow.Type);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Current Events page: everything not yet accepted, newest first. Used to repopulate on startup too.</summary>
    public List<DraxEventRow> GetCurrentEvents()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Events WHERE Accepted = 0 ORDER BY ReceivedLocal DESC;";
        return ReadRows(cmd);
    }

    /// <summary>History page: accepted events whose ReceivedLocal falls within [from, to], newest first.</summary>
    public List<DraxEventRow> GetHistory(DateTime from, DateTime to)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM Events
            WHERE Accepted = 1 AND ReceivedLocal >= $from AND ReceivedLocal <= $to
            ORDER BY ReceivedLocal DESC;
            """;
        cmd.Parameters.AddWithValue("$from", from.ToString("O"));
        cmd.Parameters.AddWithValue("$to", to.ToString("O"));
        return ReadRows(cmd);
    }

    private static void AddRowParameters(SqliteCommand cmd, DraxEventRow row)
    {
        cmd.Parameters.AddWithValue("$receivedLocal", row.ReceivedLocal.ToString("O"));
        cmd.Parameters.AddWithValue("$panel", row.Panel);
        cmd.Parameters.AddWithValue("$ext", row.Ext);
        cmd.Parameters.AddWithValue("$type", row.Type);
        cmd.Parameters.AddWithValue("$typeId", row.TypeId);
        cmd.Parameters.AddWithValue("$isOn", row.On ? 1 : 0);
        cmd.Parameters.AddWithValue("$value", row.Value);
        cmd.Parameters.AddWithValue("$node", row.Node);
        cmd.Parameters.AddWithValue("$loop", row.Loop);
        cmd.Parameters.AddWithValue("$input", row.Input);
        cmd.Parameters.AddWithValue("$inputType", row.InputType);
        cmd.Parameters.AddWithValue("$text", row.Text);
        cmd.Parameters.AddWithValue("$text2", row.Text2);
        cmd.Parameters.AddWithValue("$text3", row.Text3);
        cmd.Parameters.AddWithValue("$raw", row.Raw);
    }

    private static List<DraxEventRow> ReadRows(SqliteCommand cmd)
    {
        var results = new List<DraxEventRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new DraxEventRow
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                ReceivedLocal = DateTime.Parse(reader.GetString(reader.GetOrdinal("ReceivedLocal"))),
                Panel = reader.GetString(reader.GetOrdinal("Panel")),
                Ext = reader.GetString(reader.GetOrdinal("Ext")),
                Type = reader.GetString(reader.GetOrdinal("Type")),
                TypeId = reader.GetInt32(reader.GetOrdinal("TypeId")),
                On = reader.GetInt32(reader.GetOrdinal("IsOn")) != 0,
                Value = reader.GetInt32(reader.GetOrdinal("Value")),
                Node = reader.GetInt32(reader.GetOrdinal("Node")),
                Loop = reader.GetInt32(reader.GetOrdinal("Loop")),
                Input = reader.GetInt32(reader.GetOrdinal("Input")),
                InputType = reader.GetInt32(reader.GetOrdinal("InputType")),
                Text = reader.GetString(reader.GetOrdinal("Text")),
                Text2 = reader.GetString(reader.GetOrdinal("Text2")),
                Text3 = reader.GetString(reader.GetOrdinal("Text3")),
                Raw = reader.GetString(reader.GetOrdinal("Raw")),
            });
        }
        return results;
    }

    public void Dispose() => _connection.Dispose();
}
