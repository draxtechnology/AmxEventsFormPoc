using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace AmxEventsFormPoc.FloorPlan;

// A floor: a DWG drawing, optionally cropped to one area of it (a multi-floor
// drawing holds every floor on one sheet), with the layers hidden for that floor.
public sealed class PlanFloor
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string DwgPath { get; set; } = "";   // the drawing file: a .dwg or a .pdf
    public int Page { get; set; }                // 0-based page, for a PDF
    // SHA-256 of the drawing file, so the same file is recognised again even when it
    // is saved somewhere else or renamed (resubmitted PDFs often are).  Empty for
    // floors set up before this was kept; worked out when first needed.
    public string FileHash { get; set; } = "";
    // Crop in true drawing coordinates (not screen pixels), or null for the whole drawing.
    public double? CropLeft { get; set; }
    public double? CropBottom { get; set; }
    public double? CropRight { get; set; }
    public double? CropTop { get; set; }
    public HashSet<string> HiddenLayers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Quarter turns clockwise (0, 90, 180, 270) a PDF page is shown at, for a sheet
    // drawn or scanned sideways.  The crop and placed devices are kept in the turned
    // page's coordinates, so they move with it when it's turned.
    public int Rotation { get; set; }
    public bool HasCrop => CropLeft.HasValue && CropBottom.HasValue && CropRight.HasValue && CropTop.HasValue;
    public override string ToString() => Name;
}

// A device placed on a floor, at a position in true drawing coordinates. Stored
// relative to the drawing rather than in screen pixels, so it stays in the right
// place at any window size or screen resolution — the problem today's AMX has.
public sealed record PlacedDevice(long FloorId, int Node, int Loop, int Address, double X, double Y)
{
    public string Key => $"{Node}/{Loop}/{Address}";
}

// Floor plans, device positions and the device-code-to-symbol map, kept in the same
// events.db as the events (same SQLCipher key), in tables of their own.
public sealed class FloorPlanRepository : IDisposable
{
    private readonly SqliteConnection _connection;

    public FloorPlanRepository(string? encryptionKey, string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmxEventsFormPoc", "events.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
        _connection.Open();
        if (!string.IsNullOrEmpty(encryptionKey))
        {
            using var key = _connection.CreateCommand();
            key.CommandText = $"PRAGMA key = '{encryptionKey.Replace("'", "''")}';";
            key.ExecuteNonQuery();
        }

        Exec("""
            CREATE TABLE IF NOT EXISTS PlanFloors (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                Name         TEXT NOT NULL,
                DwgPath      TEXT NOT NULL,
                CropLeft     REAL NULL,
                CropBottom   REAL NULL,
                CropRight    REAL NULL,
                CropTop      REAL NULL,
                HiddenLayers TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS PlanDevices (
                Node    INTEGER NOT NULL,
                Loop    INTEGER NOT NULL,
                Address INTEGER NOT NULL,
                FloorId INTEGER NOT NULL,
                X       REAL NOT NULL,
                Y       REAL NOT NULL,
                PRIMARY KEY (Node, Loop, Address)
            );
            CREATE TABLE IF NOT EXISTS PlanSymbolMap (
                DeviceCode INTEGER PRIMARY KEY,
                Symbol     TEXT NOT NULL
            );
            -- Symbols per device code, per configuration format ("SKF:41", "NCF:48"):
            -- the two Advanced formats number their device types differently.
            CREATE TABLE IF NOT EXISTS PlanSymbols (
                Code   TEXT PRIMARY KEY,
                Symbol TEXT NOT NULL
            );
            -- PlanSymbolMap (the first cut, SKF only) carried over once.
            INSERT OR IGNORE INTO PlanSymbols (Code, Symbol)
                SELECT 'SKF:' || DeviceCode, Symbol FROM PlanSymbolMap;
            CREATE TABLE IF NOT EXISTS PlanSettings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """);
        AddColumnIfMissing("PlanFloors", "Page", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("PlanFloors", "FileHash", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("PlanFloors", "Rotation", "INTEGER NOT NULL DEFAULT 0");
    }

    // PlanFloors gained Page for PDF floors; add it to a database made before that.
    private void AddColumnIfMissing(string table, string column, string definition)
    {
        using var check = _connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (Convert.ToInt64(check.ExecuteScalar()) == 0) Exec($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    // ------------------------------------------------------------------ floors

    public List<PlanFloor> GetFloors()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, DwgPath, CropLeft, CropBottom, CropRight, CropTop, HiddenLayers, Page, FileHash, Rotation FROM PlanFloors ORDER BY Id";
        using var r = cmd.ExecuteReader();
        var list = new List<PlanFloor>();
        while (r.Read())
        {
            list.Add(new PlanFloor
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                DwgPath = r.GetString(2),
                CropLeft = r.IsDBNull(3) ? null : r.GetDouble(3),
                CropBottom = r.IsDBNull(4) ? null : r.GetDouble(4),
                CropRight = r.IsDBNull(5) ? null : r.GetDouble(5),
                CropTop = r.IsDBNull(6) ? null : r.GetDouble(6),
                HiddenLayers = new HashSet<string>(
                    r.GetString(7).Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase),
                Page = r.GetInt32(8),
                FileHash = r.GetString(9),
                Rotation = r.GetInt32(10),
            });
        }
        return list;
    }

    public void SaveFloor(PlanFloor f)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = f.Id == 0
            ? "INSERT INTO PlanFloors (Name, DwgPath, CropLeft, CropBottom, CropRight, CropTop, HiddenLayers, Page, FileHash, Rotation) VALUES ($n, $p, $l, $b, $r, $t, $h, $pg, $fh, $rot); SELECT last_insert_rowid();"
            : "UPDATE PlanFloors SET Name=$n, DwgPath=$p, CropLeft=$l, CropBottom=$b, CropRight=$r, CropTop=$t, HiddenLayers=$h, Page=$pg, FileHash=$fh, Rotation=$rot WHERE Id=$id; SELECT $id;";
        cmd.Parameters.AddWithValue("$id", f.Id);
        cmd.Parameters.AddWithValue("$n", f.Name);
        cmd.Parameters.AddWithValue("$p", f.DwgPath);
        cmd.Parameters.AddWithValue("$l", (object?)f.CropLeft ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", (object?)f.CropBottom ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", (object?)f.CropRight ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)f.CropTop ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", string.Join("\n", f.HiddenLayers));
        cmd.Parameters.AddWithValue("$pg", f.Page);
        cmd.Parameters.AddWithValue("$fh", f.FileHash ?? "");
        cmd.Parameters.AddWithValue("$rot", f.Rotation);
        f.Id = Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void DeleteFloor(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM PlanDevices WHERE FloorId=$id; DELETE FROM PlanFloors WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ----------------------------------------------------------------- devices

    public List<PlacedDevice> GetPlacedDevices()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT FloorId, Node, Loop, Address, X, Y FROM PlanDevices";
        using var r = cmd.ExecuteReader();
        var list = new List<PlacedDevice>();
        while (r.Read())
            list.Add(new PlacedDevice(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetDouble(4), r.GetDouble(5)));
        return list;
    }

    // A device sits on one floor at a time: placing it again moves it.
    public void PlaceDevice(PlacedDevice d)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PlanDevices (Node, Loop, Address, FloorId, X, Y) VALUES ($n, $l, $a, $f, $x, $y)
            ON CONFLICT (Node, Loop, Address) DO UPDATE SET FloorId=$f, X=$x, Y=$y;
            """;
        cmd.Parameters.AddWithValue("$n", d.Node);
        cmd.Parameters.AddWithValue("$l", d.Loop);
        cmd.Parameters.AddWithValue("$a", d.Address);
        cmd.Parameters.AddWithValue("$f", d.FloorId);
        cmd.Parameters.AddWithValue("$x", d.X);
        cmd.Parameters.AddWithValue("$y", d.Y);
        cmd.ExecuteNonQuery();
    }

    public void RemoveDevice(int node, int loop, int address)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM PlanDevices WHERE Node=$n AND Loop=$l AND Address=$a";
        cmd.Parameters.AddWithValue("$n", node);
        cmd.Parameters.AddWithValue("$l", loop);
        cmd.Parameters.AddWithValue("$a", address);
        cmd.ExecuteNonQuery();
    }

    // ----------------------------------------------------------------- symbols

    public Dictionary<string, AmxSymbol> GetSymbolMap()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Code, Symbol FROM PlanSymbols";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, AmxSymbol>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
            if (Enum.TryParse<AmxSymbol>(r.GetString(1), out var s)) map[r.GetString(0)] = s;
        return map;
    }

    public void SetSymbol(string code, AmxSymbol symbol)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO PlanSymbols (Code, Symbol) VALUES ($c, $s) ON CONFLICT (Code) DO UPDATE SET Symbol=$s";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$s", symbol.ToString());
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- settings

    public string? GetSetting(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM PlanSettings WHERE Key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO PlanSettings (Key, Value) VALUES ($k, $v) ON CONFLICT (Key) DO UPDATE SET Value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
