using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AmxEventsFormPoc.FloorPlan;

// Reads the older Advanced configuration format (.ncf). There is no published
// specification: the layout below was worked out from the UOS B53-59 sample
// (Mike, 2026-11-25) and should be confirmed against the Advanced software.
//
// An .ncf is a zip holding:
//   SITE         the network: one record per panel (name, node number) and one per
//                zone (number, name)
//   <panel>.pcf  one binary file per panel, named as in SITE, holding a 224-byte
//                record per point (loop device channel or on-board circuit)
//   AppDefaults.ini, <panel>.txt   not needed here
//
// A point record, relative to T = the first character of its location text:
//   T-9          device type code
//   T-5, -4, -3  sub-address, address, loop (loop 0 = the panel's own circuits)
//   T-2          0
//   T-1          text length (1-40); the text field is 40 bytes, padded with
//                whatever was there before, so only `length` characters count
//   T+99..+102   1E 00 00 00, then sub-address, address, loop again at T+103..+105
//                (the repeat is what tells a point record from other text)
//   T+125        zone number, 2 bytes little-endian
// Loop numbers run on across the network rather than restarting at 1 per panel
// (the UOS sample's CUB AREAS panel, node 6, has loops 11-13).
internal static class NcfReader
{
    private const int MaxText = 40;
    private const int EchoOffset = 99;
    private const int ZoneOffset = 125;
    private const int RecordTail = 128;

    public static PanelConfig Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var siteEntry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("SITE", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("No SITE file in the .ncf: not an Advanced network configuration.");
        var (panels, zones) = ReadSite(ReadAll(siteEntry));

        var points = new List<(int Node, int Loop, int Address, int Sub, int Zone, int Type, string Text)>();
        foreach (var (name, node) in panels)
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(name + ".pcf", StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue; // a panel listed in SITE without its file
            var data = ReadAll(entry);
            foreach (var p in ReadPoints(data))
                points.Add((node, p.Loop, p.Address, p.Sub, p.Zone, p.Type, p.Text));
        }

        var names = panels.ToDictionary(p => p.Node, p => p.Name);
        var devices = points
            .GroupBy(p => (p.Node, p.Loop, p.Address))
            .Select(g =>
            {
                var first = g.OrderBy(p => p.Sub).First();
                return new PanelDevice
                {
                    Format = "NCF",
                    Node = g.Key.Node,
                    Loop = g.Key.Loop,
                    Address = g.Key.Address,
                    Zone = first.Zone,
                    ZoneText = zones.TryGetValue(first.Zone, out var zt) ? zt : "",
                    NodeName = names.TryGetValue(g.Key.Node, out var nn) ? nn : "",
                    Location = g.OrderBy(p => p.Sub).Select(p => p.Text).FirstOrDefault(t => t.Length > 0) ?? "",
                    DeviceId = first.Type,
                    Channels = g.Count(),
                };
            });

        return new PanelConfig
        {
            Format = "NCF",
            SiteName = PanelConfig.SiteFromFileName(path),
            Devices = PanelConfig.Order(devices),
            PointCount = points.Count,
            PanelCount = panels.Count,
        };
    }

    // SITE holds length-prefixed names in two kinds of record (after a 0x70-byte header):
    //   panel:  ... 0x12, length, name ...; the node number sits 34 bytes after the name start
    //   zone:   zone number (4 bytes big-endian), length (4 bytes big-endian), name
    private static (List<(string Name, int Node)> Panels, Dictionary<int, string> Zones) ReadSite(byte[] d)
    {
        var panels = new List<(string, int)>();
        var zones = new Dictionary<int, string>();
        for (int t = 0x70; t < d.Length; t++)
        {
            int len = d[t - 1];
            if (len < 1 || len > MaxText || t + len > d.Length || !Printable(d, t, len)) continue;
            // The zero bytes / 0x12 marker in front rule out positions inside a name.
            string text = Encoding.Latin1.GetString(d, t, len);

            if (d[t - 2] == 0x12 && t + 34 < d.Length)
            {
                panels.Add((text, d[t + 34])); // exact name: it is also the panel's .pcf file name
                t += len;
            }
            else if (t >= 8 && d[t - 2] == 0 && d[t - 3] == 0 && d[t - 4] == 0 && d[t - 7] == 0 && d[t - 8] == 0)
            {
                int zone = (d[t - 6] << 8) | d[t - 5];
                if (zone > 0 && !zones.ContainsKey(zone)) zones[zone] = text.Trim();
                t += len;
            }
        }
        return (panels, zones);
    }

    private static IEnumerable<(int Loop, int Address, int Sub, int Zone, int Type, string Text)> ReadPoints(byte[] d)
    {
        for (int t = 9; t + RecordTail <= d.Length; t++)
        {
            int len = d[t - 1];
            if (len < 1 || len > MaxText || d[t - 2] != 0) continue;
            int sub = d[t - 5], address = d[t - 4], loop = d[t - 3];
            int e = t + EchoOffset;
            if (d[e] != 0x1E || d[e + 1] != 0 || d[e + 2] != 0 || d[e + 3] != 0) continue;
            if (d[e + 4] != sub || d[e + 5] != address || d[e + 6] != loop) continue;
            if (!Printable(d, t, len)) continue;

            int zone = d[t + ZoneOffset] | (d[t + ZoneOffset + 1] << 8);
            yield return (loop, address, sub, zone, d[t - 9], Encoding.Latin1.GetString(d, t, len).Trim());
            t += RecordTail; // records are 224 bytes apart; skip past this one
        }
    }

    private static bool Printable(byte[] d, int start, int len)
    {
        if (start < 0 || start + len > d.Length) return false;
        for (int i = start; i < start + len; i++)
            if (d[i] < 0x20 || d[i] > 0x7E) return false;
        return true;
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
