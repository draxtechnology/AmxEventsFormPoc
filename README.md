# AMX Events — native Windows Forms version of the DraxView POC

A native WinForms port of the Events screen from your uploaded `DraxView`
proof of concept (the Blazor web app) — same fields, same filter/active-only/
panel-control behaviour, and now the same live MQTT feed, wired up the same
way DraxView itself does it. Built as a proper Windows Forms `Form` you can
open in Visual Studio's visual designer and rearrange by dragging things
around, rather than a web page.

## What this ports from DraxView

Built directly from your `DraxView/DraxView/Components/Pages/Events.razor`
(the event list page), `DraxView/DraxView/Services/DraxEvent.cs` (the event
model), `DraxView/DraxView/Services/MqttService.cs` (the MQTT connection),
and the root `README.md` (the topic layout and how it fits the service):

- **Status bar** — Broker connection state, panels seen, and total event
  count, matching the `<span class="led">` / `status-meta` elements at the
  top of `Events.razor`. (The active-alarm count now lives in its own strip
  at the bottom left — see "Active count moved to a bottom-left footer"
  below.)
- **Toolbar** — just the filter box now. (The panel picker and the
  "Active only" checkbox have both been removed — see "Removing the panel
  filter and column" and "Removing the Active-only checkbox" below.)
- **Event grid** — Date, Time, Type, AMX ref, Input Type, Text, colour-coded
  by the same "family" grouping (alarm / fault / isolation / control) as
  `DraxEvent.Family`. (The original State and Panel columns were removed and
  Date / Input Type added per your own edits — see "Date and Input Type
  columns" and "Removing the panel filter and column" below.)
- **`Models/DraxEventRow.cs`** mirrors `DraxEvent.cs` field-for-field, and its
  new `Parse(seq, json)` method is the same parsing logic as
  `DraxEvent.Parse` — same `decoded.node/loop/input/inputType` shape, same
  top-level fields — so a payload from the real service parses identically
  here as it does in DraxView.
- **`Services/MqttEventsClient.cs`** is the same connection as
  `MqttService.cs`: same MQTTnet version (`5.2.0.1603`, pinned in the
  `.csproj` to match `DraxView.csproj` exactly), the same 5-second
  connect/retry loop, and the same topics (`drax/<panel>/event`,
  `drax/<panel>/log`, `drax/<panel>/cmd`). Its `SendCommandAsync` method
  (the same pipe-command strings, e.g. `SILENCE|0,0,0,0`, that
  `MqttService.SendCommandAsync` publishes) is still there and ready to use —
  see "No control buttons" below for why nothing currently calls it.
- **`Services/MqttSettings.cs`** reads the same `Mqtt` section shape from
  `appsettings.json` as DraxView's `MqttOptions` — copy `DraxView`'s own
  `appsettings.json` over this project's and it'll just work.

## Running it against a real broker

Same prerequisites your `DraxView/README.md` states: a Mosquitto broker on
`localhost:1883` (or whatever `appsettings.json` here points at) and the
Drax Technology service running with `MqttEnabled=true`. Point this at the
same broker DraxView itself uses, run it alongside (or instead of) DraxView,
and it'll show the same live events.

No panel handy to test with? The same trick your README documents for
DraxView applies here too: publish `TEST BOX|15,1,0,54` to `drax/<panel>/cmd`
to have the service emit a synthetic event back on `drax/<panel>/event`.

## What's real here

The MQTT connection and the parsing are the real thing, not mocked — same as
DraxView's. There's no sample or fabricated data: the grid starts empty and
shows "Waiting for events from the broker." until a real event arrives over
MQTT, exactly like DraxView's own empty-state message in `Events.razor`.

## No control buttons

Silence, Reset, and Clear list have been removed — there's no toolbar button
for any of them. `MqttEventsClient.SendCommandAsync` (the method that would
publish `SILENCE|0,0,0,0` / `RESET|0,0,0,0` to `drax/<panel>/cmd`) is still
there in `Services/MqttEventsClient.cs`, unused for now; wiring a button back
up to it is just adding a `Button` in the Designer and one line in its
`Click` handler.

**Why removing a button via the Designer caused extra columns to appear:**
`eventsGrid` is bound to `_visibleEvents` with `AutoGenerateColumns = false`
and six explicit columns (Time, Panel, State, Type, AMX ref, Text). If that
flag ever reverts to `true` — which can happen when the Designer resaves the
form after an edit, if it loses track of a property that was set outside of
what it considers "designer-owned" — then the moment `DataSource` is
assigned, `DataGridView` adds one column per *public property* on
`DraxEventRow`: `Seq`, `TypeId`, `Value`, `InputType`, `Raw`, and the rest,
on top of the six you already have. That's the "load more columns" you saw.
Fixed it by re-asserting `eventsGrid.AutoGenerateColumns = false;` in code in
`MainForm.cs`, immediately before `DataSource` is set — so even if a future
Designer edit ever loses track of that setting again, the six explicit
columns are guaranteed at runtime regardless of what the Designer file says.

**Why editing the panel dropdown made the column header row disappear:**
same underlying cause, different symptom. `pnlStatus` and `pnlToolbar` are
both docked to the top of the form, and `eventsGrid` fills what's left
(`Dock = Fill`). For that stacking to render correctly — status bar at the
very top, toolbar below it, grid filling the rest — the three have to be
added to the form's `Controls` collection in a specific relative order.
Editing any control on the form (even one as unrelated as the panel
dropdown) can make the Designer resave `InitializeComponent()`, and that
resave doesn't always preserve that relative order. If it gets scrambled,
`pnlToolbar` or `pnlStatus` can end up drawn over the grid's top edge —
exactly where the column header row sits — hiding it, while the data rows
underneath still show. Fixed the same way as the columns issue: `MainForm.cs`
now calls `Controls.SetChildIndex(...)` right after `InitializeComponent()`
to force that order back to eventsGrid, then pnlToolbar, then pnlStatus,
every time the form is constructed — so this can't recur even if a future
Designer resave reorders things again.

**Why resizing `pnlStatus`, and later adding a form icon, both made the grid
go blank entirely (no rows, no columns, nothing):** a third variant of the
same underlying cause, and it happened twice, from two completely unrelated
edits. This time the Designer resave didn't revert `AutoGenerateColumns` to
`true` (the first bug) or scramble the docking order (the second bug) — it
dropped the six explicit column definitions (`colDate`, `colTime`, `colType`,
`colAmxRef`, `colInputType`, `colText`) out of `InitializeComponent()`
entirely, both times. With no columns and `AutoGenerateColumns` still
`false`, the grid had nothing to render a row into, so it looked exactly
like the data had stopped arriving even though `_allEvents`/`_visibleEvents`
were still updating normally underneath.

Neither the `pnlStatus` resize nor adding the icon was the real cause — any
save of the form through the visual designer regenerates the whole of
`InitializeComponent()` from Visual Studio's in-memory model of the form,
and that model doesn't reliably preserve these six columns on every resave,
because they were originally written directly in code rather than added
one-by-one through the designer's own "Edit Columns…" dialog.

Since it happened twice, this is now fixed structurally instead of patched
again: the columns aren't in `MainForm.Designer.cs` at all any more. They're
declared as fields and built in a new `ConfigureEventsGridColumns()` method
in `MainForm.cs`, called from the constructor before `DataSource` is set.
**To add, remove, reorder, rename, or resize a column now, edit
`ConfigureEventsGridColumns()` in `MainForm.cs` directly** — the Designer's
"Edit Columns…" dialog won't show any columns to edit any more, since as far
as the Designer is concerned `eventsGrid` doesn't have any. This trades away
being able to tweak column widths by dragging in the visual designer, in
exchange for this specific breakage no longer being possible at all,
regardless of what else you change through the Designer.

## Form icon

`MainForm.Designer.cs` now loads an icon via
`Icon = (Icon)resources.GetObject("$this.Icon");`, using a
`System.ComponentModel.ComponentResourceManager`. When you set the form's
`Icon` property through the visual designer, Visual Studio also creates (or
updates) a `MainForm.resx` file next to `MainForm.Designer.cs`, and that's
where the actual icon image bytes are stored — this project doesn't have
that file, since only the two `.cs` files were shared for this fix. Your own
local copy of `MainForm.resx` already has it, so just make sure it stays
alongside these files when you copy them back in; without it, the code
above still runs fine, it just won't find an icon to load (the window will
show no custom icon rather than failing to build).

## Note: MainForm.cs and MainForm.Designer.cs have to move together now

Since the grid's columns are built in `ConfigureEventsGridColumns()` in
`MainForm.cs` rather than in `MainForm.Designer.cs` (see above), the two
files are now a matched pair for this specific piece: `MainForm.Designer.cs`
declares `eventsGrid` with no columns at all, and `MainForm.cs` is what adds
them, in the constructor, before `DataSource` is set. If you ever copy just
one of these files without the other — for instance, taking a fresh
`MainForm.Designer.cs` from a delivered zip but keeping an older
`MainForm.cs` (or the reverse) — the grid will go back to showing nothing,
not because either file is broken on its own, but because the piece that
used to live entirely in the Designer file is now split across both. Always
copy both together.

## Note: the top status bar's Broker / Panels / event-count text

`UpdateStatusBar()` in the `MainForm.cs` you sent no longer sets
`lblBroker.Text`, `lblPanels.Text`, or `lblMeta.Text` — it only sets
`lblActive.Text` now (to "N Current Events"). That looks like your own
edit rather than a side effect of anything above, so it's been left exactly
as you had it: those three labels will just stay blank at the top of the
form. Flagging it in case it wasn't deliberate — if you want the broker
connection state and panel list back, say so and they can be restored.

## Date and Input Type columns

Added a `Date` column (`dd/MM/yyyy`, separate from the existing `Time` column,
which stays `HH:mm:ss`) and an `Input Type` column bound straight to the raw
`InputType` number from the event payload (`decoded.inputType` in the MQTT
message — see `DraxEventRow.Parse`).

The blank cells you saw after adding the Date column in the Designer were
expected: a `DataGridViewColumn.DataPropertyName` has to match an actual
public property on `DraxEventRow`, and there wasn't a `Date` property yet —
only `Time`. Added `public string Date => ReceivedLocal.ToString("dd/MM/yyyy");`
to `Models/DraxEventRow.cs` to fix that; any other column you add in the
Designer will show blank the same way until its `DataPropertyName` points at
a real property.

`Input Type` is shown as the raw numeric code for now (`InputType` is an
`int` on `DraxEventRow`, same as `DraxEvent.InputType` in DraxView — there's
no label lookup for what each number means in either app). If you know what
the codes map to (e.g. 0 = smoke detector, 1 = call point, etc.), say so and
a `InputTypeText` display property with that mapping can replace the raw
number in the column.

## Removing the panel filter and column

The panel picker dropdown (`cmbPanel`) in the toolbar and the Panel column
(`colPanel`) in the grid have both been removed — the toolbar now has just
the filter box and "Active only" checkbox, and the grid no longer shows a
Panel column.

This is a different bug from the two above, and worth understanding if you
delete other controls yourself later: deleting `cmbPanel` through the visual
designer only edits `MainForm.Designer.cs` (the field, its `Controls.Add`,
its properties) — it does **not** touch `MainForm.cs`, which had its own,
hand-written code reading `cmbPanel` directly (`SyncPanelDropdown`, called
every time an event arrived, to keep the dropdown's list of panel names in
sync). With the control gone but that method still calling into it, every
event caused a `NullReferenceException` inside the code path that also calls
`ApplyFilter()` — so the exception stopped `ApplyFilter()` from ever running
again, which is exactly "removing the filter stops the data row appearing":
the grid was still receiving events internally, it just never got asked to
redisplay them.

Fixed by removing `SyncPanelDropdown` and both places it was called from in
`MainForm.cs`, alongside deleting `cmbPanel`/`colPanel` from the Designer
file — so there's nothing left in the code referencing the removed control.
The general lesson: the Designer only ever edits `MainForm.Designer.cs`.
Deleting a control that `MainForm.cs` also references by name (search the
control's name across both files) needs that reference removed by hand too,
or the build itself would normally fail to compile — a working build that
then silently stops updating, like this one did, means some catch block
somewhere (here, the `catch (ObjectDisposedException)` around `BeginInvoke`)
swallowed the real exception instead of surfacing it.

## Active count moved to a bottom-left footer

The active-event count (`lblActive`, showing "N active") moved out of the
top status bar and into its own thin strip docked to the bottom of the
form — `pnlFooter`, a `FlowLayoutPanel` with `Dock = Bottom`. It flows
left-to-right from the panel's left edge, same as the other status strips,
so the count naturally sits bottom-left rather than needing special
alignment code.

Nothing about how the count is calculated or updated changed — `lblActive`
is still just a `Label` field, `UpdateStatusBar()` in `MainForm.cs` still
sets `lblActive.Text` the same way it always did. Only which panel it's
parented under changed. `MainForm.cs`'s `Controls.SetChildIndex(...)` guard
(see "Why editing the panel dropdown made the column header row disappear"
above) now also forces `pnlFooter` into place on every construction, so it
can't end up hidden behind the grid the same way a top-docked panel once
did.

## Removing the Active-only checkbox

The "Active only" checkbox has been removed from the toolbar along with the
code behind it (`chkActiveOnly.CheckedChanged` and the `rows.Where(e => e.On)`
filter step in `ApplyFilter`). This didn't need the same care as the panel
picker above, since nothing else in the code referenced `chkActiveOnly`.

It's also functionally a no-op to remove: the grid already only ever holds
"on" events — an "off" event never gets added as its own row, it just
removes the matching "on" row (see "'Off' events clear a row rather than
adding one" below) — so `_allEvents` was already active-events-only by the
time the checkbox's filter ran. The checkbox and the concept of "active
only" duplicated that behaviour rather than adding anything.

## "Off" events clear a row rather than adding one

This is one deliberate difference from DraxView, which lists every event —
on or off — as its own row. Here, an "on" event (`On: true`) adds a new row
as before, but an "off" event doesn't get a row of its own: it removes
whichever existing "on" row shares the same panel, type, and node/loop/input
(see `RemoveMatchingOnRow` in `MainForm.cs`), the way a panel's *current*
event list works — an alarm that clears drops off the list instead of adding
a second "cleared" line underneath it. If no matching "on" row is found
(nothing to clear), the "off" event is simply dropped.

## A repeat "on" for the same point doesn't add a second row

If an "on" event arrives for a point that's already showing as active — same
AMX ref (Panel + Node + Loop + Input) and same Input Type — it's dropped
rather than added as another row (see `IsDuplicateActiveRow` in
`MainForm.cs`). This is the same idea as "off" events clearing a row instead
of adding one (below), applied the other direction: a panel re-announcing an
alarm that's already on the list shouldn't make it appear twice.

This match deliberately does **not** check `Type` — only AMX ref and Input
Type — unlike `RemoveMatchingOnRow` just below it, which does include `Type`
when deciding what an "off" event clears. That means an Alarm and a Fault
arriving for the exact same physical point would currently be treated as
duplicates of each other, and only the first shows. If you'd rather those
both show as separate rows, say so and `Type` can be added into
`IsDuplicateActiveRow`'s match too, the same as it's already used in
`RemoveMatchingOnRow`.

## Minimize is blocked, same as the other POCs

Ported the same technique from `AmxShimPoc` / `AmxNativePoc` / `AmxWinFormsPoc`
into this form too, in `MainForm.MinimizeGuard.cs`: `MinimizeBox = false`
removes the minimize button cosmetically, a `WndProc` override intercepts
`WM_SYSCOMMAND`/`SC_MINIMIZE` (the taskbar icon, the title bar, Alt+Space →
Minimize), and a low-level keyboard hook swallows Win+D ("show desktop") and
Win+M ("minimize all windows") before Explorer ever sees them — those two
bypass `WM_SYSCOMMAND` entirely, so the message intercept alone can't catch
them. Same known gap as those other POCs: clicking the small "Show desktop"
sliver at the end of the taskbar isn't caught, since it's a mouse action, not
a keystroke.

## Fields easily visible for amending the layout

This is built the way Visual Studio itself builds a form — `MainForm.cs` (the
logic) is split from `MainForm.Designer.cs` (every control's type, position,
and properties) — so the visual designer can read and edit it:

1. Open `AmxEventsFormPoc.sln` in Visual Studio.
2. In Solution Explorer, double-click `MainForm.cs` (not `MainForm.Designer.cs`
   directly) — this opens the **visual design surface**, not code.
3. Every label, the filter box, the checkbox, and — usefully — **each grid
   column individually** is its own selectable object.
   Click any of them to see and edit its properties (position, size, text,
   colour, etc.) in the Properties panel, drag them to reposition, or delete
   them. Right-click the grid and choose "Edit Columns…" to add, remove,
   reorder, or rename columns without touching code at all.
4. Whatever you change there gets written back into `MainForm.Designer.cs`
   automatically. If you add or remove a `DataGridView` column this way,
   just double-check `eventsGrid.AutoGenerateColumns` is still `false` in the
   regenerated code afterward — see "No control buttons" above for why that
   matters. And before deleting any *named* control (a button, a dropdown,
   anything with a `Name` you'd recognise) search for that name in
   `MainForm.cs` first — the Designer only rewrites `MainForm.Designer.cs`,
   so a control the code behind refers to directly needs those references
   removed by hand too. See "Removing the panel filter and column" below for
   what happens if you don't.

Since `DraxEventRow` already carries every field from the original
`DraxEvent` — including ones not currently shown as a column, like `Value`,
`TypeId`, `InputType`, and `Ext` — adding one of those to the grid is just
adding a new column in the designer and setting its `DataPropertyName` to
the matching property name. No other code needs to change.

## Prerequisites

- Windows 10 or 11
- .NET 10 SDK (project targets `net10.0-windows`)
- A reachable MQTT broker (Mosquitto on `localhost:1883` by default) if you
  want live events rather than the sample-data placeholder

## How to run it

In Visual Studio: open `AmxEventsFormPoc.sln`, make sure the ".NET desktop
development" workload is installed, and press F5.

From the command line:

```
dotnet restore
dotnet run --project AmxEventsFormPoc
```

## Building a standalone .exe

```
dotnet publish AmxEventsFormPoc -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

`appsettings.json` will sit alongside the published `.exe` — ship both
together, and edit that file (or copy DraxView's own) to point at a
different broker without rebuilding.
