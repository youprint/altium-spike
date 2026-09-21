# AltiumSpike

A C# extension for **Altium Designer 26** that adds CSV import/export, JLCPCB
assembly output, high-speed routing tools and fabrication documentation, in
one window.

It exports board data for external tooling, places components, tracks, vias,
pours and regions back from CSV, locks and unlocks components, generates
JLCPCB-ready BOM and pick-and-place files, exports per-net and per-pin-pair
length and delay, and builds via fences along selected RF traces.

![The AltiumSpike window](docs/window.png)

> The images in this README are renderings of the UI design, not photographs of
> a running session. The shipped window is built from these designs and matches
> them closely, but treat them as illustrations rather than screenshots.

---

## What it does

### Export

| Output | Contents |
| --- | --- |
| `footprint_sizes.csv` | `Designator,Pattern,Width,Height,CenterOffsetX,CenterOffsetY,X,Y,Rotation,Layer,Locked` |
| `pad_nets.csv` | `Designator,PadNumber,NetName,X,Y` |
| `board_geometry.csv` | `Type,Name,Index,X,Y,Diameter` — ordered outline vertices and mounting holes |

All coordinates are millimetres in absolute board coordinates.

### JLCPCB assembly

| Output | Contents |
| --- | --- |
| `bom_jlcpcb.csv` | `Comment,Designator,Footprint,LCSC Part #` |
| `cpl_jlcpcb.csv` | `Designator,Mid X,Mid Y,Layer,Rotation` |
| `bom_missing_lcsc.csv` | Components with no LCSC number — your checklist, not for upload |

Components without an LCSC part number are excluded from **both** deliverables,
because JLCPCB can neither quote nor place them. They appear only in the
missing-parts file.

`Mid X` / `Mid Y` are the true component **centroid**, computed from the
bounding rectangle — not Altium's footprint anchor, which can differ by a
fraction of a millimetre and would offset parts on the assembly line.

### Import

| Input | Contents |
| --- | --- |
| `objects.csv` | 17 columns, dispatched by `ObjectType`: Component, Track, Via, Arc, Fill, Text, Coordinate |
| `pours.csv` | `Net,Layer,X1,Y1,X2,Y2` — rectangular solid polygon pours |
| `regions.csv` | `RegionKind,Layer,Net,X1,Y1,X2,Y2` — copper or keep-out regions |
| lock list | One designator per line |

Every batch is wrapped in `PreProcess`/`PostProcess` and each component edit in
`BeginModify`/`EndModify` with `try/finally`, so a bad row cannot leave a
dangling edit transaction that blocks **File → Save** afterwards.

![After a JLCPCB export](docs/window-result.png)

### High-speed

Two tools aimed at RF and fast digital work, in their own strip at the bottom
of the window because neither is an ordinary "dump the board to CSV" action.

#### Net lengths

| Output | Contents |
| --- | --- |
| `net_lengths.csv` | `Net,Class,PinCount,ViaCount,RoutedLength,SignalLength,DelayTotal_Raw,SignalDelay_Raw,InDiffPair` |
| `pin_pair_lengths.csv` | `Net,Class,PinPair,FromPin,ToPin,RoutedLength,UnroutedLength,TotalLength,DelayTotal_Raw,NodeCount` |

Lengths are millimetres. The numbers come from **Altium's own calculators**
(`IPCB_Net2`, `IPCB_PinPair`), not from summing track geometry, so they agree
with the PCB panel by construction. Summing tracks yourself is the obvious
implementation and it is wrong: it misses via barrel contributions, double
counts overlapping segments after a loop removal, and knows nothing about
xSignals spanning series terminators.

Both files exist because a net-level total is the wrong number the moment a
net has more than two nodes. A DDR4 address line routed fly-by to four DRAMs
is one net with one `RoutedLength` but four distinct pin-to-pin distances, and
it is those you match against the clock.

> The delay columns are suffixed `_Raw`. The SDK returns a bare `Double` with
> no unit in the signature and no documentation, so the value is passed
> through unscaled rather than being given a unit it might not have. Check one
> net against Altium's own panel to pin the unit down for your install.

#### Via fence

Places a via shield along the tracks and arcs **currently selected** in the
PCB editor, at a fixed pitch and offset. Altium's native via stitching floods
a polygon at a grid pitch; it has no notion of *follow this trace at 0.4 mm on
both sides every 1.2 mm*. That distinction is the whole feature — the leakage
you are suppressing depends on the gap between adjacent vias, not on average
via density.

1. Select the trace segments in the PCB editor
2. Set pitch / offset / via Ø / hole / net, tick the walls you want
3. **Fence selected traces**

Settings are remembered between sessions, and the `ViaFence` command repeats
the last fence on a new selection without opening the window.

**No clearance check is performed.** Every candidate via is placed; nothing
consults the clearance rules or tests for existing copper. This is deliberate
— placement stays fast and completely predictable, and DRC is the authority on
what is legal. Run **Design → Rule Check** afterwards. The result panel says so
on every run.

Two details that are easy to get wrong and are covered by tests:

- **Each wall is stepped at its own radius on an arc.** Stepping the
  centreline at `pitch / radius` and dropping a via either side puts the outer
  wall at `pitch × (radius + offset) / radius` — 10% too open on a 5 mm bend,
  50% too open on a 1 mm bend. Pitch means via-to-via spacing everywhere.
- **Junction de-duplication is per wall.** With a tight offset the mirrored
  pair can sit closer to each other than half the pitch, so a single shared
  list would silently delete one wall of the fence.

### Fabrication

#### Layer stackup table

Walks the **physical** layer stack — copper and dielectric interleaved in true
Z order — and draws it as a ruled table on a mechanical or drill-drawing
layer: layer name, type, material, thickness in mm and mil, copper weight and
Er, with a totals row carrying the finished board thickness.

`eLayerClass_Electrical` would give you the copper only, so an eight-layer
board would render as eight rows with nothing between them. That is a layer
list, not a stackup. Copper weight is derived from thickness (1 oz ≡ 34.79 µm)
and left blank when the foil is not within 10% of a common weight, rather than
mislabelling an unusual foil as 1 oz.

Re-running replaces the previous table rather than drawing a second one on top.

#### Assembly notes

Emits a numbered set of fabrication notes as text on a chosen layer, built
from the board itself: size, copper layer count, finished thickness, narrowest
track, smallest drilled hole, minimum clearance, plus the boilerplate (IPC
class, surface finish, mask and silkscreen colours, electrical test).

The distinction this feature is built around is **measured versus specified**:

| Value | Source |
| --- | --- |
| Minimum trace width | **Measured** — every track and arc on copper layers |
| Smallest hole | **Measured** — every pad and via |
| Minimum clearance | **From the rule**, and labelled `PER DESIGN RULES` in the note text |

They are different numbers and they fail differently. A clearance rule set to
0.1 mm on a board whose tightest gap is 0.2 mm quotes you for finer work than
you need; a rule looser than the artwork tells the fab nothing. True minimum
copper-to-copper clearance needs an all-pairs geometric analysis across every
layer — that is what DRC is for, and duplicating it badly would produce a
number worse than no number. So the rule value is reported and *said to be* a
rule value, in the artwork, not just here.

Silkscreen and mechanical tracks are excluded from the width measurement, or a
text stroke would set your "minimum trace width".

#### Release candidate package

Collects the fabrication outputs, renames to `Project_RevA_20260921.zip`, zips
them, verifies the archive reopens with the expected entry count, and delivers
it to a folder or network share.

> **On "bypassing the Output Job".** There is no SDK call that writes a Gerber,
> an NC drill file, an ODB++ archive or a STEP model. Both Altium SDK
> assemblies were searched: what exists is `IWSM_OutputJobDocument`, which
> enumerates and creates *outputers* — the Output Job machinery itself — and
> `CommandLauncher.LaunchCommand`, which runs Altium's own processes.
> Reimplementing RS-274X and Excellon here was considered and rejected: the
> hard parts are aperture generation, polygon fill decomposition and drill
> symbol assignment, and a subtly wrong writer produces files that look fine
> in a viewer and fail at the fab.
>
> So this is delivered as *you never open the Output Job dialogs*: keep one
> `.OutJob` with your fab settings, and one click runs it and does everything
> after it.

File safety, all enforced and all tested:

- Source files are **copied, never moved** — a failed run costs nothing
- An existing destination archive is **never overwritten**; the run fails and
  says so, because two different builds under one release name is worse than a
  failed run
- The archive is reopened and its entries counted **before** delivery, while
  it is still in a temp folder rather than on the release share
- A previous release archive sitting in the output folder is excluded, so
  releases cannot nest inside each other
- Two files with the same leaf name from different subfolders are both kept

**Run the Output Job first** is off by default. That launch is the one call in
this project that could not be confirmed from SDK metadata — the process name
lives in Altium's process reference — so it is opt-in until it has been seen
to work on your installation. Everything after generation works regardless,
and **Dry run** reports exactly what would be packaged without writing
anything.

---

## Requirements

- **Altium Designer 26** (this targets the .NET 8 SDK it ships; see below)
- **.NET 8 SDK** — `winget install Microsoft.DotNet.SDK.8`
- Windows x64

Altium 26 hosts **.NET 8 with WPF**, shipping its own runtime under
`System\DotNet\Runtime\shared\Microsoft.NETCore.App\8.0.x`. An extension built
against .NET Framework will not load.

---

## Build

```powershell
git clone https://github.com/<you>/altium-spike.git
cd altium-spike
```

Copy the two SDK assemblies out of your Altium installation into an
`Assemblies` folder — they are proprietary and are not included here:

```powershell
mkdir Assemblies
copy "C:\Program Files\Altium\AD22\System\Altium.SDK.dll" Assemblies\
copy "C:\Program Files\Altium\AD22\System\Altium.SDK.Interfaces.dll" Assemblies\
```

> The folder is named after the version that **first** installed Altium and
> keeps that name across upgrades — a current Altium 26 often lives in `AD22`.
> Check your actual path.

Then:

```powershell
dotnet build -c Debug
```

## Test

The via fence geometry has a standalone test harness. It compiles
`FenceGeometry.cs` directly — the same file the plugin ships, not a copy — so
the assertions cannot drift from what runs. `FenceGeometry.cs` references no
Altium type, so the tests need no Altium installation and no SDK assemblies:

```powershell
dotnet run --project tests/FenceGeometryTests
dotnet run --project tests/ReleaseBundleTests
```

Both compile the shipped source file directly — `FenceGeometry.cs` and
`ReleaseBundle.cs` — rather than a copy, so the assertions cannot drift from
what runs. Neither file references an Altium or WPF type, which is what makes
this possible: the tests need no Altium installation and no SDK assemblies.

- **FenceGeometryTests** — 35 assertions: wall spacing on straight, diagonal
  and curved runs, junction de-duplication, the inner-wall fold case, the via
  cap, option validation.
- **ReleaseBundleTests** — 30 assertions: archive naming and filename
  sanitising, extension filtering, refusing to overwrite an existing release,
  nested-release exclusion, same-leaf-name collisions, dry run, and that every
  refusal is explained rather than silent.

Exit code is 0 when they all pass.

The rest needs a live board and was verified by hand (see **Status**).

## Deploy

With **Altium closed**:

```powershell
.\Deploy.ps1
```

`Deploy.ps1` builds, copies the output into Altium's extensions folder and
registers the extension. It discovers the installation rather than hardcoding
a GUID, backs up `ExtensionsRegistry.xml` before editing it, and inherits the
DXP/EDP build numbers already present so the entry is accepted.

```
.\Deploy.ps1 -SkipBuild   # copy and register an existing build
.\Deploy.ps1 -Force       # proceed even if Altium is running
```

Altium can hold the DLL for up to ~90 seconds after exit; if the copy fails
with a file lock, wait and re-run.

Start Altium, open a `.PcbDoc`, then **PCB → Tools → AltiumSpike…**

### Uninstalling

Delete the `AltiumSpike` folder from
`C:\ProgramData\Altium\Altium Designer {GUID}\Extensions\` and restore
`ExtensionsRegistry.xml.bak` from the same folder.

---

## Where the LCSC part number comes from

There is **no** standard Altium parameter called `LCSC Part #`, despite what
most JLCPCB tutorials assume. Components imported by the
[EasyEDA Loader](https://github.com/expired6978/EasyEDALoader) carry four
parameters:

```
Supplier          e.g. "LCSC"
Supplier Part     the LCSC part number   <-- this one
Manufacturer
Manufacturer Part
```

`JlcExport.cs` reads `Supplier Part` first and validates that `Supplier`
actually names LCSC, so a Digi-Key or Mouser code sitting in that field is not
passed off as an LCSC number. Other spellings are tried as fallbacks, and every
parameter name found on the board is written to the log so an unmatched field
can be identified rather than guessed at.

---

## Notes for anyone extending this

Hard-won details that cost real time to establish:

- **The factory type must be named `CSharpPlugin.PluginFactory`** — that exact
  fully-qualified name. Altium looks it up by name. Put it in your own
  namespace and the server "starts" in `DXP_Startup.log` while none of your
  code ever runs, with no error anywhere.
- `InvokePluginFactory(IClient)` must be an **instance** method with a public
  parameterless constructor. Static does not bind.
- The module derives from `DXP.ServerModule`; its only abstract member is
  `NewDocumentInstance`. Register commands in `InitializeCommands()`, and note
  that the inherited `CommandLauncher` property is typed `ICommandLauncher`
  while `RegisterCommand` lives on the concrete `DXP.CommandLauncher`.
- A **submenu** in a `.rcs` is a nested `Tree … End` block, injected with
  `TreeCopy … OriginalID='…'`. Altium's own `AdvPcb.rcs` (UTF-16, under
  `System\Resources\<lang>\`) is the only real reference for the format.
- The C# API is **not** a rename of DelphiScript. `Arc.XCenter` is
  `SetState_CenterX`; `Via.LowLayer` takes an `IV7_Layer` obtained via
  `pcbServer.LayerUtils().FromString("Top Layer")`; `AddFilter_LayerSet(AllLayers)`
  is the no-argument `AddFilter_AllLayers()`.
- `IPCB_Fill`'s `SetState_LocationX/Y` is the **lower-left corner**, not the
  centre, and `Length` is the X extent while `Width` is the Y extent. Altium's
  Properties panel displays the centre, which makes a wrong implementation look
  right.
- `Component.Moveable` is inverted: **`false` means locked**.
- The selection is `IPCB_Board.GetState_SelectecObjectCount()` — Altium's own
  typo, preserved in the public API. The typed accessor is
  `GetState_SelectecObject(board, i)` on `IPCB_BoardHelper`. Snapshot the
  selection before placing anything; adding objects can disturb it, and
  walking it by index while it changes underneath silently skips segments.
- Routed length and delay are **already computed** and exposed:
  `IPCB_Net.GetState_RoutedLength()`, and on `IPCB_Net2`
  `GetState_RoutedLength64()`, `GetState_SignalLength()`,
  `GetState_DelayTotal()`, `GetState_SignalDelay()`. Pin pairs come from
  `board.GetState_PinPairsManager()`, and `IPCB_PinPair` carries routed,
  unrouted and total length plus delay. Do not re-derive any of it from track
  geometry.
- Delays are cached. Call `ResetDelaysCalculator()` on the board cast to
  `IPCB_BoardEx` first, or a board edited since the last calculation hands
  back stale numbers.
- Net classes are `TObjectId.eClassObject` with
  `GetState_MemberKind() == eClassMemberKind_Net`. `IPCB_ObjectClass` exposes
  `IsMember(string)` but no member enumeration, so build the net→class map by
  asking each class about each net, and skip `All Nets` — every net is in it.
- Lengths are `Int64` but `EDP.Utils.CoordToMMs` only takes `Int32`. The
  Int32 range is about 54 m so any real net fits, but derive the ratio from
  `CoordToMMs` itself rather than hardcoding 393700.787 if you need a fallback.
- The layer stack is `board.GetState_LayerStack()` walked with
  `IPCB_LayerStackBaseHelper.First/Next(TLayerClassID.eLayerClass_Physical)`.
  There is no single `thickness` member: copper is
  `IPCB_ElectricalLayer.GetState_CopperThickness()`, dielectric is
  `IPCB_DielectricLayer.GetState_DielectricHeight()`.
- Resolve layers by name through `pcbServer.LayerUtils().FromString()` and set
  them with `SetState_V7Layer`, rather than switching over `TV6_Layer`. The
  enum has `eV6_Mechanical1..16` plus the drill and overlay layers, and a
  hand-written switch is both long and wrong the moment someone renames a
  mechanical layer in Layer Stack Manager.
- `TObjectSet` has no `Include`; build it from an array:
  `new TObjectSet(new TObjectId[] { ... })`.
- Collect primitives to delete during iteration and remove them **after** the
  iterator is destroyed. Deleting from under a live iterator crashes Altium
  rather than throwing something catchable.
- **There is no fabrication-output API.** No SDK call writes a Gerber, NC
  drill, ODB++ or STEP file. `IWSM_OutputJobDocument` manipulates Output Job
  outputers; actual generation goes through `CommandLauncher.LaunchCommand`
  and Altium's process names, which are not in the metadata.
- Do not use `Path.GetInvalidFileNameChars()` to sanitise a name that must be
  valid on Windows. It is platform-dependent — on Linux it returns only `/`
  and NUL, so `:`, `|`, `?` and `*` pass straight through. Write the set out.
- To match DelphiScript's numeric output exactly: format with
  `CultureInfo.InvariantCulture`, normalise negative zero (.NET prints
  `-0.000` where Delphi prints `0.000`), and round with
  `MidpointRounding.AwayFromZero` — `Math.Round` defaults to banker's rounding.

The window is built entirely in code with no XAML, so it compiles both with
`UseWPF`/`net8.0-windows` and against Altium's own WPF assemblies on a plain
`net8.0` target.

---

## Status

Verified against a real 2-layer board in Altium 26.8.1:

- Board-data export is **byte-identical** to the DelphiScript implementation it
  replaces (all three files, md5-matched).
- JLCPCB export cross-checked — no excluded designator reaches either
  deliverable, BOM and CPL cover the identical component set, and the CPL
  passes format conformance.
- All seven `objects.csv` types, pours, regions and lock/unlock placed
  correctly and were read back to confirm.
- Via fence geometry: 35 automated assertions, all passing.
- Release bundling: 30 automated assertions, all passing.

Not yet exercised on a live board — built and deployed, verification pending:
the stackup table, the assembly notes, and the Output Job launch inside the
release packager.

Known rough edges:

- Rectangular pours and regions only; arbitrary outlines are not implemented.
- Standalone pads and dimensions are not handled by `objects.csv`.
- The via fence spans Top → Bottom only; blind and buried spans are not
  offered, and there is no clearance check (see above).
- The delay columns are unscaled SDK values — see the note under
  **Net lengths**.
- The stackup table and notes are drawn as tracks and text, not as a native
  Altium table object, so they do not update when the stackup changes — re-run
  the generator.
- The release packager's Output Job launch is unverified (see above); the
  packaging half is not.
- The UI is dark-themed to sit beside Altium's default theme.

---

## Licence

GPL-3.0-or-later. See [LICENSE](LICENSE).

The Altium SDK assemblies this builds against are proprietary and are not
included or redistributed here.

The plugin entry-point pattern (`CSharpPlugin.PluginFactory`) follows
[expired6978/EasyEDALoader](https://github.com/expired6978/EasyEDALoader), which
was the only working example of a C# Altium extension I could find.
