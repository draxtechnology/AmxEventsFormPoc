using System;
using System.Linq;
using System.Text.Json;

namespace AmxEventsFormPoc.Models;

// Mirrors DraxView's DraxEvent record (DraxView/Services/DraxEvent.cs) field-for-field,
// reshaped as a plain mutable class so it binds cleanly to a DataGridView row.
//
// To show an extra field in the grid, add a DataGridViewColumn in the form's Designer
// (drag one onto eventsGrid, or add it in MainForm.Designer.cs) and set its
// DataPropertyName to one of the properties below — the field is already here even
// if it's not currently shown as a column (Ext, TypeId, Value, InputType, Raw).
public sealed class DraxEventRow
{
    public long Seq { get; set; }
    public DateTime ReceivedLocal { get; set; }
    public string Panel { get; set; } = "";
    public string Ext { get; set; } = "";
    public string Type { get; set; } = "";
    public int TypeId { get; set; }
    public bool On { get; set; }
    public int Value { get; set; }
    public int Node { get; set; }
    public int Loop { get; set; }
    public int Input { get; set; }
    public int InputType { get; set; }
    public string Text { get; set; } = "";
    public string Text2 { get; set; } = "";
    public string Text3 { get; set; } = "";
    public string Raw { get; set; } = "";

    // --- Display-only properties the grid columns actually bind to ---

    public string Time => ReceivedLocal.ToString("HH:mm:ss");

    public string Date => ReceivedLocal.ToString("dd/MM/yyyy");

    public string State => On ? "ON" : "OFF";

    public string AmxRef => $"N{Node} L{Loop} D{Input}";

    public string CombinedText =>
        string.Join(" · ", new[] { Text, Text2, Text3 }.Where(s => !string.IsNullOrEmpty(s)));

    // Same grouping DraxView's DraxEvent.Family uses, for row colouring in the grid.
    public string Family
    {
        get
        {
            var t = Type.ToLowerInvariant();
            if (t.Contains("alarm")) return On ? "alarm" : "cleared";
            if (t.Contains("fault") || t.Contains("error")) return On ? "fault" : "cleared";
            if (t.Contains("isolation") || t.Contains("disable") || t.Contains("isolate")) return "isolation";
            if (t.Contains("reset") || t.Contains("silence")) return "control";
            return "info";
        }
    }

    // Same parsing DraxEvent.Parse does in DraxView, field for field, so a
    // payload from the real drax/<panel>/event topic maps onto this row exactly
    // as it would onto a DraxEvent there.
    public static DraxEventRow? Parse(long seq, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            int node = 0, loop = 0, input = 0, inputType = 0;
            if (r.TryGetProperty("decoded", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                node = Int(d, "node");
                loop = Int(d, "loop");
                input = Int(d, "input");
                inputType = Int(d, "inputType");
            }

            return new DraxEventRow
            {
                Seq = seq,
                ReceivedLocal = DateTime.Now,
                Panel = Str(r, "panel"),
                Ext = Str(r, "ext"),
                Type = Str(r, "type"),
                TypeId = Int(r, "typeId"),
                On = r.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True,
                Value = Int(r, "value"),
                Node = node,
                Loop = loop,
                Input = input,
                InputType = inputType,
                Text = Str(r, "text"),
                Text2 = Str(r, "text2"),
                Text3 = Str(r, "text3"),
                Raw = json,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
