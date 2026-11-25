using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace AmxEventsFormPoc.FloorPlan;

// One AMX point on an Advanced panel: the panel's network address, loop and loop
// address. Advanced sub-addresses (multi-channel modules) share the one AMX point —
// PanelAdvanced in the service sends node + loop + device address and drops the
// sub-address — so the SKF's points are grouped on these three numbers.
public sealed class SkfDevice
{
    public int Node { get; init; }
    public int Loop { get; init; }
    public int Address { get; init; }
    public int Zone { get; init; }
    public string ZoneText { get; init; } = "";
    public string NodeName { get; init; } = "";
    public string Location { get; init; } = "";
    public bool IsInput { get; init; }
    public bool IsOutput { get; init; }
    public int DeviceId { get; init; }
    public int Channels { get; init; }
    // From the SKF's Purpose field on each channel: 5 = sounder output, 7 = relay /
    // door / access output (inferred from the Lambeth sample's location texts).
    public bool HasSounderOutput { get; init; }
    public bool HasRelayOutput { get; init; }

    // Loops -100 and -90 in the SKF are the panel's own on-board circuits, not a
    // detection loop.
    public bool IsPanelCircuit => Loop < 0;

    public string Key => $"{Node}/{Loop}/{Address}";
    public string RefText => IsPanelCircuit ? $"N{Node} panel {Address}" : $"N{Node} L{Loop} D{Address}";

    // The AMX point this device reports on, once the site's node offset is applied.
    public bool Matches(int node, int loop, int input, int nodeOffset)
        => node == Node + nodeOffset && loop == Loop && input == Address;
}

// An Advanced panel configuration file (.skf): a zip of FireDAC (Delphi) dataset
// exports, one JSON file per table. Only the tables the floor plan needs are read:
// tblPoint (every point with its address, zone and location text), tblZone (zone
// names) and tblNode (panel names), plus the site name.
public sealed class SkfConfig
{
    public string SiteName { get; private init; } = "";
    public IReadOnlyList<SkfDevice> Devices { get; private init; } = Array.Empty<SkfDevice>();
    public int PointCount { get; private init; }
    public int PanelCount { get; private init; }

    public static SkfConfig Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var nodeNames = ReadTable(zip, "tblNode")
            .ToDictionary(r => Int(r, "NetworkAddress"), r => Str(r, "NodeName").Trim());
        var zoneTexts = ReadTable(zip, "tblZone")
            .GroupBy(r => Int(r, "ZoneNumber"))
            .ToDictionary(g => g.Key, g => Str(g.First(), "ZoneText").Trim());
        var site = ReadTable(zip, "tblSite").FirstOrDefault();
        var points = ReadTable(zip, "tblPoint");

        var devices = points
            .GroupBy(p => (Node: Int(p, "NetworkAddress"), Loop: Int(p, "LoopNumber"), Address: Int(p, "LoopAddress")))
            .Select(g =>
            {
                // The first channel carries the device's own location text; later
                // channels of a module usually repeat it.
                var first = g.OrderBy(p => Int(p, "SubAddress")).First();
                int zone = Int(first, "ZoneNumber");
                return new SkfDevice
                {
                    Node = g.Key.Node,
                    Loop = g.Key.Loop,
                    Address = g.Key.Address,
                    Zone = zone,
                    ZoneText = zoneTexts.TryGetValue(zone, out var zt) ? zt : "",
                    NodeName = nodeNames.TryGetValue(g.Key.Node, out var nn) ? nn : "",
                    Location = g.Select(p => Str(p, "Location").Trim()).FirstOrDefault(s => s.Length > 0) ?? "",
                    // FireDAC booleans are -1 (true) / 0 (false).
                    IsInput = g.Any(p => Int(p, "InputPoint") != 0),
                    IsOutput = g.Any(p => Int(p, "OutputPoint") != 0),
                    DeviceId = Int(first, "DeviceID"),
                    Channels = g.Count(),
                    HasSounderOutput = g.Any(p => Int(p, "Purpose") == 5),
                    HasRelayOutput = g.Any(p => Int(p, "Purpose") == 7),
                };
            })
            .OrderBy(d => d.Node).ThenBy(d => d.Loop < 0 ? 1 : 0).ThenBy(d => Math.Abs(d.Loop)).ThenBy(d => d.Address)
            .ToList();

        return new SkfConfig
        {
            // The site name is often left blank in the panel software; the file name
            // usually names the site.
            SiteName = site != null && Str(site, "SiteName").Trim().Length > 0
                ? Str(site, "SiteName").Trim()
                : Path.GetFileNameWithoutExtension(path).Trim(),
            Devices = devices,
            PointCount = points.Count,
            PanelCount = nodeNames.Count,
        };
    }

    // FireDAC JSON: { "FDBS": { "Manager": { "TableList": [ { "ColumnList": [...],
    // "RowList": [ { "Original": { column: value, ... } } ] } ] } } }. Rows edited
    // since the last save carry "Current" as well; that wins when present.
    private static List<Dictionary<string, JsonElement>> ReadTable(ZipArchive zip, string table)
    {
        var entry = zip.GetEntry(table + ".json");
        if (entry == null) return new();

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var rows = new List<Dictionary<string, JsonElement>>();
        var tableList = doc.RootElement.GetProperty("FDBS").GetProperty("Manager").GetProperty("TableList");
        foreach (var t in tableList.EnumerateArray())
        {
            if (!t.TryGetProperty("RowList", out var rowList)) continue;
            foreach (var row in rowList.EnumerateArray())
            {
                var values = row.TryGetProperty("Current", out var cur) ? cur
                           : row.TryGetProperty("Original", out var org) ? org
                           : default;
                if (values.ValueKind != JsonValueKind.Object) continue;
                var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in values.EnumerateObject()) dict[p.Name] = p.Value.Clone();
                rows.Add(dict);
            }
        }
        return rows;
    }

    private static int Int(Dictionary<string, JsonElement> r, string name)
        => r.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)
            ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : 0;

    private static string Str(Dictionary<string, JsonElement> r, string name)
        => r.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
