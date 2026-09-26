# AltiumSpike

A C# extension for **Altium Designer 26**: CSV import/export, JLCPCB assembly
output, high-speed and EMC checks, board cleanup, silkscreen, placement and
geometry editing, fabrication documentation, release packaging, and placing
components and nets on a **schematic** from CSV — in one window.

It exports board data for external tooling, places components, tracks, vias,
pours and regions back from CSV, locks and unlocks components, generates
JLCPCB-ready BOM and pick-and-place files, exports per-net and per-pin-pair
length and delay, builds via fences along selected RF traces, draws the layer
stackup and fabrication notes onto the board, packages a dated release
archive, and builds a schematic sheet from a components CSV and a nets CSV.
A self-test runs every function against the open document and reports what it
found.

The window opens from both the PCB and the schematic editor (**AltiumSpike…**
on the menu bar, and under **Tools**) and is organised into seventeen sections
down a left sidebar:

| Group | Sections |
| --- | --- |
| **Data** | Import / Export · Reports |
| **High-speed** | Via fence · Via tools · Copper & current |
| **Design** | Design rules · Testpoints · Cleanup |
| **Edit** | Silkscreen · Placement · Geometry · Layers · Polygons |
| **Output** | Fabrication · Variants · Release |
| **Schematic** | Place from CSV |

A sidebar rather than tabs: seventeen tab labels come to well over 2000px of tab
row in a 900px window, a second row moves as the window resizes so you can
never learn where anything is, and a scrolling row hides whatever is
off-screen. A vertical list scales, keeps every name readable, and lets
sections carry group headers.

The board header and the result strip stay across the top, above both the
sidebar and the content: which board is open, and what the last action did,
are true whichever section you are in.

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

### Schematic: place components and nets from CSV

**Schematic → Place from CSV** builds a sheet from two files, onto the
schematic that has focus:

| Input | Contents |
| --- | --- |
| components CSV | `Designator`, `LCSC` or `LibRef`, then optional `Library`, `X`, `Y`, `Rotation` (0/90/180/270), `Mirror`. X/Y are mil unless the header says mm (`X mm`). Rows with no position are laid out on a grid |
| nets CSV | `Net,Designator,Pin` — or `Net,Node` with nodes like `R1.2`. A pin is matched by number first, then by name |

Each connected pin gets a short wire stub pointing away from the part, with the
net label on it — exact, never crosses a symbol, never makes an accidental
connection. **Dry run** checks everything and changes nothing; **Place**
writes `sch_placement.csv` and `sch_net_labels.csv` reports; **Schematic
self-test** exercises the whole path in a scratch area off the sheet and removes
what it added.

Both CSVs are validated in full before anything is placed — duplicate
designators, a pin listed in two nets (which would short them), bad rotations,
half-given positions, unresolvable symbols, and designators already used on
**another sheet of the project**. One error and nothing is placed. Designators
already on the same sheet are left alone, so a second run places nothing new.
Nothing is saved.

Symbols come from a `.SchLib` — by default the shared library written by
YouEDA, the author's EasyEDA/LCSC converter — or from a `Library` column. See
[Where the LCSC part number comes from](#where-the-lcsc-part-number-comes-from).

### High-speed

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

#### Via tools

**Return via check** finds signal vias with no return via nearby. When a
high-speed signal changes layer its return current has to change reference
plane with it, and it can only do that through a stitching via close by.
Without one the return takes a long detour, and that loop area is radiated
emission and crosstalk. It is invisible in DRC, invisible on screen, and one
of the most common EMC findings on an otherwise clean board.

The check is deliberately simple — nearest via on the reference net, flagged
past a threshold. It does *not* work out which planes the signal actually
transitions between, because that needs the full stackup and plane assignment
of both layers and getting it subtly wrong would produce confident wrong
answers. What it gives you is the list worth looking at. Offenders are selected
on the board, not just listed, so you can step through them.

**Tenting** and **barrel relief** set solder mask expansion in bulk. Tenting is
not a flag in the SDK — it is a mask opening small enough to vanish, so tenting
sets a negative expansion past the pad radius. Barrel relief opens the mask a
fixed distance from the **hole** edge rather than the pad edge, so the annular
opening is consistent whatever the pad size; a large plated hole under mask is
a mask-cracking risk because the mask bridges the barrel with nothing under it.

#### Copper & current

**Current capacity** rates every net by IPC-2221:

    I = k · ΔT^0.44 · A^0.725     k = 0.048 external, 0.024 internal

Two things this gets right that a naive implementation does not. It rates each
net by its **narrowest** track — a power net 2 mm wide for 40 mm and 0.2 mm
through one BGA escape is a 0.2 mm net as far as heating goes, and that escape
is the bit nobody looks at. And it uses each track's **own layer**: a buried
conductor sheds heat only by conduction through laminate and carries roughly
half what the same track carries outside, so applying the external constant
everywhere overstates an inner-layer track by 2×. Copper weight and inner/outer
status both come from the real stackup.

Output is `net_current_capacity.csv` with the limiting width, its layer and
coordinates, the capacity, and — given a target current — the width that would
be needed and a pass/fail. Failing nets are selected at their narrowest point.

**Copper areas** reports polygon and region area per layer and per net.
Polygon area is the real filled area after thermal reliefs and island removal;
regions expose no area member in the SDK and are reported by bounding box,
labelled as such in the CSV.

### Design

#### Design rules

**Export** puts every rule in a CSV — kind, priority, scope, enabled state, and
for the kinds whose constraint member is confirmed in the SDK, its value. Other
kinds export with a blank value rather than a guess; a wrong clearance number
in a rules report is worse than a blank one, because a blank invites you to go
and look.

**Audit** finds rules that exist but are not doing their job: disabled ones,
ones with an empty scope that match nothing, and two rules of a kind sharing a
priority so which wins depends on ordering rather than intent. None of these is
something Altium reports — DRC simply passes.

#### Testpoints

Coverage per net, assignment, and a pad-centre check. Testpoint state is **four
flags, not one**: `IsTestPoint_Top`/`_Bottom` for the fabrication testpoint and
`IsAssyTestPoint_Top`/`_Bottom` for assembly. They are independent, and a report
that conflates them says a net is covered when the test house cannot reach it,
so all four are reported separately.

Assignment marks vias and optionally through-hole pads. Surface-mount pads are
never marked — a bed of nails probing an SMD pad damages the joint it exists to
verify.

#### Cleanup

**Invalid objects** — polygons with fewer than three vertices, which cannot
enclose anything but survive save and reload and surface later as Gerber
artefacts. Find-only selects them; deleting is a separate button.

**Via antennas** — vias with copper on only one layer. **Dangling copper** —
track ends coinciding with nothing on their net.

Both infer connectivity geometrically, because the SDK exposes no connectivity
query that can be trusted across pours and planes. Two consequences stated
plainly: copper poured over a via connects it in a way a coincident-end test
cannot see, so vias inside pours are excluded by default or every stitching via
reads as an antenna; and a track ending part-way along another track is a real
connection in Altium that will not register here.

Nothing here deletes without being told to.

### Edit

#### Silkscreen

Centre designators, hand them back to autoposition, show/hide in bulk, and
normalise height and stroke.

Centring uses the middle of `BoundingRectangleNoNameComment` — the component
body with the designator and comment excluded — **not** the component's X/Y.
That is the anchor, wherever pin 1 or the library origin happens to sit, and on
plenty of footprints it is nowhere near the middle. Designators are switched to
manual positioning first, or autoposition pulls them straight back.

#### Placement

**Off-board components** tests every component body against the board outline;
**Component collisions** finds overlapping bodies pairwise. **Align rotation**
and **Snap to grid** fix the selection. **Renumber designators** writes a
proposal first and applies only when asked again — renumbering desynchronises
the PCB from its schematic.

#### Geometry

**Fillet** rounds the corner between every pair of selected tracks sharing an
endpoint. Each corner is either filleted completely or left exactly as it was —
a track shortened without its arc is a broken connection.

**Distribute** spaces three or more objects evenly, sorted by position first so
nothing shuffles past anything else. **Scale** scales geometry about the centre
of the selection and deliberately leaves track widths, hole sizes and pad sizes
alone; scaling those turns a manufacturable board into one that misses its
design rules everywhere at once.

#### Layers

**Move to layer** moves selected copper and places a via wherever that would
break a connection. The move is one line; keeping the board connected is the
actual job, and without the vias the net breaks *silently* — the track still
looks connected. Internal planes are refused: a plane is negative artwork, so a
track dropped on one is a void, not a conductor.

Also exports the stack as CSV, toggles signal-layer visibility, and maps which
mechanical layers are in use.

#### Polygons

**Polygon report** lists every polygon with its settings and whether its copper
is stale; **Repour** repours all of them, or only the stale ones.

### Variants

Every variant with the components it populates, and how many are actually
ordered.

The way in is `IPCB_BoardEx`, not `IPCB_Board` — there is no variant accessor
on the plain board interface, which is the dead end that makes people conclude
variants are unreachable from the PCB side:

    IPCB_BoardEx.GetState_FullComponents().GetComponentsForAllVariants()
        → IPCB_FullComponent.GetDesignVariant() / .GetKind()

`TComponentKind` separates `Standard` from `Standard_NoBOM`, `Mechanical`,
`Graphical` and the net-tie kinds. A part marked NoBOM or Graphical is on the
board and must not be ordered; counting it is how a build ends up over on parts.

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
- **.NET 8 SDK** — `winget install Microsoft.DotNet.SDK.8`. `global.json` pins
  `8.0.425` and rolls forward within the 8.0 feature band, so a newer SDK on
  the machine is ignored on purpose
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

Two layers: standalone harnesses for the logic, and a self-test inside Altium
for everything that touches a document.

### Harnesses (no Altium needed)

```powershell
foreach ($t in Get-ChildItem tests -Directory) { dotnet run --project $t.FullName }
```

Each compiles the shipped source file directly — `FenceGeometry.cs`,
`ReleaseBundle.cs`, `Ipc2221.cs`, `FilletGeometry.cs`, `PolyGeometry.cs`,
`SchPlacementPlan.cs` — rather than a copy, so the assertions cannot drift from
what runs. None of those files references an Altium or WPF type, which is what
makes this possible: the tests need no Altium installation and no SDK
assemblies.

That split is deliberate throughout. Wherever there is maths or file handling
that can be wrong in a way you cannot see on screen, it lives in an
Altium-free file with a test harness, and the Altium-facing file does only the
board work.

- **FenceGeometryTests** — 35 assertions: wall spacing on straight, diagonal
  and curved runs, junction de-duplication, the inner-wall fold case, the via
  cap, option validation.
- **ReleaseBundleTests** — 30 assertions: archive naming and filename
  sanitising, extension filtering, refusing to overwrite an existing release,
  nested-release exclusion, same-leaf-name collisions, dry run, and that every
  refusal is explained rather than silent.
- **Ipc2221Tests** — 25 assertions: the constants, a worked example against the
  published formula, the exact 2× internal/external split, the inverse round
  trip, scaling exponents, and that degenerate input returns zero rather than
  letting a NaN into a CSV a fab house reads.
- **FilletGeometryTests** — 32 assertions: tangent points exactly the radius
  from the centre across a sweep of angles and radii, the arc taken is always
  the minor one, degenerate corners refused with a reason, an oversized radius
  refused with a usable limit, and tangents never falling beyond their segment.
- **PolyGeometryTests** — 74 assertions: point-in-outline on the cases that
  break a naive crossing count (points level with a vertex or on an edge,
  concave shapes, both winding orders, degenerate input), distance to an edge,
  rectangle overlap, designator-prefix and row clustering for renumbering,
  polygon area and arc length.
- **SchPlacementTests** — 109 assertions: CSV parsing and every rejection,
  a pin listed in two nets, automatic layout in natural designator order, the
  YouEDA symbol-name rule, pin tips in all four directions, stub and label
  geometry, pin matching by number then name, and designators already in use
  on this sheet versus another sheet of the project.

305 assertions in all. Exit code is 0 when they all pass.

### Self-test (inside Altium)

- **Reports → Self-test** (commands `AltiumSpike:SelfTest` and
  `AltiumSpike:SelfTestFull`) runs every PCB function against the open board
  and writes `spike_selftest.md`. The full run adds the board-modifying checks:
  they build their own geometry in a clear area off the board, verify it, and
  remove it; anything that touches a real object is recorded and restored.
- **Schematic → Place from CSV → Schematic self-test** places a library symbol
  at 0° and 90°, mirrored at 0° and at 90°, connects pins, checks every pin tip
  and label by geometry, removes it all, and writes `spike_selftest_sch.md`.

A check passes only when its result is compared against something independent
— a count, a coordinate, a file read back. Run them on a scratch copy; nothing
is ever saved, but the document is changed in memory while they run.

`tools/SdkDump` prints the SDK assemblies' types and members from metadata,
which is how every Altium call here was confirmed before being written. See
[AGENTS.md](AGENTS.md) for how to work on this repo.

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
```

`-Force` exists to proceed while Altium is running, but Altium holds the DLL
open, so it rarely helps. Altium can also hold the DLL for up to ~90 seconds
after exit; if the copy fails with a file lock, wait and re-run.

Altium loads extensions at startup, so **a new build needs an Altium restart**.
Then open a `.PcbDoc` or `.SchDoc` and choose **AltiumSpike…** on the menu bar
(after Help) or under **Tools**.

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

**Schematic placement** goes the other way: from an LCSC number in the CSV to a
symbol in a library. The default library is the shared `youeda.SchLib` written
by YouEDA, whose symbols are named from the EasyEDA component name (cleaned to
`[A-Za-z0-9_+-.]`, runs of `_` collapsed, at most 120 characters), **not** from
the LCSC number — that is only a hidden `LCSC Part` parameter. The mapping comes
from the `family-classification.csv` files YouEDA writes next to the library,
and every resolved name is then checked against the library's own component
list. A row can bypass all of this with an explicit `LibRef`.

---

## Notes for anyone extending this

Hard-won details that cost real time to establish. The maintained list — each
trap with what its wrong answer looks like — is the table in
[AGENTS.md](AGENTS.md) §4; the schematic ones (a mirror flag that mirrors
nothing, a cast that calls every sheet a library, a placement call that leaves
the part selected) are recorded there.

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
- The PCB self-test's last full run on that board: 37 passed, 5 failed, 2
  skipped (no vias to check). Of the five failures, three were wrong assertions on a board that is not routed
  (fixed: an unrouted board makes every routing-dependent result zero
  legitimately); two are open and instrumented — polygon area measured from
  the outline, and the stackup table's text.
- **Schematic placement**, end to end: a 14-part, 47-pin test circuit placed
  from CSV with all four rotations, a mirrored part, pins given by number, by
  name and as `Des.Pin` nodes, and automatic layout — and the resulting netlist
  identical, pin for pin, to one predicted offline from the same CSVs. The
  compiler traces each net label through its stub to the pin; designator
  clashes with other sheets of the project are refused. The schematic
  self-test passed all 10 of its checks on its last run.
- 305 automated assertions across six suites, all passing.

Built and deployed, not yet run on a live board: the self-test checks for the
four Import commands and for a part both mirrored and rotated; the Output Job
launch inside the release packager; and the functions that have no self-test
check yet (among them flip and scale, layer visibility, testpoint assignment,
lock net routing, solder-mask barrel relief, and silkscreen centring and
autoposition). Every one of those compiles against the real SDK and every
Altium call in them was read out of assembly metadata rather than guessed, but
that is not the same as having been run.

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
- Schematic placement places only the first part of a multi-part symbol, and
  parts without a position are laid out on rows starting at Y = 1000 mil rather
  than beside the positioned ones.
- A library whose file header carries no symbol index (YouEDA's does not)
  cannot have its symbol names checked before placing; a wrong name then fails
  at placement and is reported there.
- The UI is dark-themed to sit beside Altium's default theme.

---

## Licence

GPL-3.0-or-later. See [LICENSE](LICENSE).

The Altium SDK assemblies this builds against are proprietary and are not
included or redistributed here.

The plugin entry-point pattern (`CSharpPlugin.PluginFactory`) follows
[expired6978/EasyEDALoader](https://github.com/expired6978/EasyEDALoader), which
was the only working example of a C# Altium extension I could find.
