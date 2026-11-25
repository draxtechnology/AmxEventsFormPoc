using System;
using SkiaSharp;

namespace AmxEventsFormPoc.FloorPlan;

// The device symbols from the AMX graphics legend (see the Exeter sample).
public enum AmxSymbol
{
    Unassigned,
    OpticalDetector,
    HeatDetector,
    MultiDetector,
    VoidDetector,
    CallPoint,
    SounderBase,
    Interface,
}

// Live state of a plotted device, coloured as in the AMX legend.
public enum DeviceState { Normal, Alarm, PreAlarm, Fault, Disabled }

public static class AmxSymbols
{
    public static readonly SKColor AlarmRed = new(0xE0, 0x10, 0x10);
    public static readonly SKColor PreAlarmOrange = new(0xF5, 0x8A, 0x1F);
    public static readonly SKColor FaultYellow = new(0xF5, 0xE0, 0x10);
    public static readonly SKColor DisabledBlue = new(0x2D, 0x9C, 0xDB);
    private static readonly SKColor DetectorGreen = new(0x2E, 0xC2, 0x3A);
    private static readonly SKColor HeatPink = new(0xF2, 0x8C, 0xC4);

    public static string Label(AmxSymbol s) => s switch
    {
        AmxSymbol.OpticalDetector => "Optical Detector",
        AmxSymbol.HeatDetector => "Heat Detector",
        AmxSymbol.MultiDetector => "Multi Detector",
        AmxSymbol.VoidDetector => "Void Detector",
        AmxSymbol.CallPoint => "Call Point",
        AmxSymbol.SounderBase => "Sounder/Base",
        AmxSymbol.Interface => "Interface",
        _ => "Unassigned",
    };

    // A first guess until the Advanced device-code list is known: the SKF doesn't name
    // device types, but each channel's Purpose says sounder output or relay output.
    // Plain inputs (detectors, call points, monitored inputs) can't be told apart, so
    // they start Unassigned; picking a symbol for one sets it for its device code.
    public static AmxSymbol Default(SkfDevice d)
    {
        if (d.HasSounderOutput) return AmxSymbol.SounderBase;
        if (d.HasRelayOutput || d.Channels > 1) return AmxSymbol.Interface;
        return AmxSymbol.Unassigned;
    }

    public static SKColor StateColor(DeviceState s) => s switch
    {
        DeviceState.Alarm => AlarmRed,
        DeviceState.PreAlarm => PreAlarmOrange,
        DeviceState.Fault => FaultYellow,
        DeviceState.Disabled => DisabledBlue,
        _ => SKColors.Transparent,
    };

    // Draws a symbol centred on (x, y), r pixels in radius. `flash` is the blink
    // phase for active states: the halo shows on alternate ticks.
    public static void Draw(SKCanvas canvas, AmxSymbol symbol, float x, float y, float r,
                            DeviceState state, bool flash, bool selected)
    {
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var line = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = Math.Max(1, r / 7), Color = SKColors.Black };
        using var font = new SKFont { Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), Size = r * 1.25f };

        if (state != DeviceState.Normal && flash)
        {
            fill.Color = StateColor(state).WithAlpha(150);
            canvas.DrawCircle(x, y, r * 2.1f, fill);
        }
        if (selected)
        {
            using var sel = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 2, Color = new SKColor(0x1E, 0x6F, 0xD9) };
            canvas.DrawCircle(x, y, r * 1.7f, sel);
        }

        // In an active state the symbol body itself takes the state colour, so it reads
        // even between flashes.
        SKColor? stateBody = state == DeviceState.Normal ? null : StateColor(state);

        switch (symbol)
        {
            case AmxSymbol.OpticalDetector:
            case AmxSymbol.MultiDetector:
            case AmxSymbol.VoidDetector:
            case AmxSymbol.HeatDetector:
                fill.Color = stateBody ?? (symbol == AmxSymbol.HeatDetector ? HeatPink : DetectorGreen);
                canvas.DrawCircle(x, y, r, fill);
                canvas.DrawCircle(x, y, r, line);
                string letter = symbol switch
                {
                    AmxSymbol.HeatDetector => "H",
                    AmxSymbol.MultiDetector => "M",
                    AmxSymbol.VoidDetector => "V",
                    _ => "S",
                };
                Letter(canvas, letter, x, y, font, SKColors.White);
                break;

            case AmxSymbol.CallPoint:
                fill.Color = stateBody ?? AlarmRed;
                canvas.DrawRect(x - r, y - r * 0.8f, r * 2, r * 1.6f, fill);
                canvas.DrawRect(x - r, y - r * 0.8f, r * 2, r * 1.6f, line);
                fill.Color = SKColors.Black;
                canvas.DrawRect(x - r * 0.55f, y - r * 0.4f, r * 1.1f, r * 0.8f, fill);
                break;

            case AmxSymbol.SounderBase:
                fill.Color = stateBody ?? SKColors.White;
                canvas.DrawCircle(x, y, r, fill);
                line.Color = AlarmRed;
                canvas.DrawCircle(x, y, r, line);
                fill.Color = AlarmRed;
                canvas.DrawCircle(x, y, r * 0.38f, fill);
                break;

            case AmxSymbol.Interface:
                fill.Color = stateBody ?? SKColors.White;
                canvas.DrawRect(x - r * 1.2f, y - r * 0.75f, r * 2.4f, r * 1.5f, fill);
                canvas.DrawRect(x - r * 1.2f, y - r * 0.75f, r * 2.4f, r * 1.5f, line);
                font.Size = r * 0.95f;
                Letter(canvas, "I/O", x, y, font, SKColors.Black);
                break;

            default:
                fill.Color = stateBody ?? new SKColor(0xB0, 0xB0, 0xB0);
                canvas.DrawCircle(x, y, r, fill);
                canvas.DrawCircle(x, y, r, line);
                Letter(canvas, "?", x, y, font, SKColors.White);
                break;
        }
    }

    private static void Letter(SKCanvas canvas, string text, float x, float y, SKFont font, SKColor color)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true };
        font.MeasureText(text, out var bounds);
        canvas.DrawText(text, x, y - bounds.MidY, SKTextAlign.Center, font, p);
    }
}
