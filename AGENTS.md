# AGENTS.md — working on AltiumSpike

Read this before answering any question or making any change in this repo.
`README.md` says what the extension does; this file says how to work on it
without breaking it, and what a good answer looks like here.

---

## 0. Operating contract

1. **Verify before you claim.** Never say a function "works", "should work" or
   "is fixed" unless it has been built, deployed, and exercised by the
   self-test on a board. If it has only been compiled, say "compiles, not yet
   run on a board".
2. **Confirm every SDK member in metadata before using it** (§4). No guessed
   member names, ever — they compile and fail silently.
3. **Answer with numbers, not adjectives.** "39 nets scanned (== board count),
   0 single-pin" beats "the check passed".
4. **Separate the three possible culprits** when something fails: the
   function, the self-test assertion, or the board itself. All three have been
   the real cause here, in roughly equal measure (§8).
5. **Two misses, then instrument.** If a fix has failed twice, stop guessing.
   Make the code report what it actually saw and ask for another run.
6. **Never touch the user's real board files on disk,** and never save a
   document you modified. Work on scratch copies.
7. **Work autonomously, and ship what is green.** The owner has asked for
   this as a standing rule: make the calls yourself, and after every change
   that builds clean and passes every harness, commit, `git push origin main`,
   and deploy (`./Deploy.ps1`). Never push or deploy a red build. If Altium
   is running, `Deploy.ps1` refuses -- say so and deploy on the next turn;
   never pass `-Force`, the DLL is locked while Altium runs. Stop and ask only
   before something destructive or before breaking another rule in this file.
8. **Say what to press and what to expect.** You cannot run Altium. After a
   deploy, name the exact button or command, the scratch document to run it
   on, and the result that would mean it works.

---

## 1. Facts

| | |
| --- | --- |
| Host | Altium Designer 26.x on Windows, .NET 8 + WPF in-process |
| Target | `net8.0-windows`, `UseWPF`, x64 |
| SDK refs | `Assemblies/Altium.SDK.dll`, `Assemblies/Altium.SDK.Interfaces.dll` — copied from the user's own install, **never committed** |
| Install folder | `C:\Program Files\Altium\AD<nn>` — `<nn>` is the version first installed, so Altium 26 may live in `AD22` |
| Deploy folder | `C:\ProgramData\Altium\Altium Designer {GUID}\Extensions\AltiumSpike\` |
| Locale trap | Developer machines may be non-English. **All** number formatting uses `CultureInfo.InvariantCulture` |
| Coordinates | Internal units; convert with `EDP.Utils.CoordToMMs` / `MMsToCoord` |

---

## 2. Commands (all verified)

```powershell
# build the plugin (from the repo root)
dotnet build -c Debug

# run every pure-logic harness; each exits 0 only if all assertions pass
foreach ($t in Get-ChildItem tests -Directory) { dotnet run --project $t.FullName }

# look up an SDK type before writing code against it (§4)
cd tools/SdkDump
dotnet run -- ../../Assemblies/Altium.SDK.Interfaces.dll "IPCB_Polygon(Helper)?$" members

# deploy: close Altium first -- it holds the DLL open while running
./Deploy.ps1
```

`global.json` pins the .NET 8 SDK (`8.0.425`, rolling forward within the 8.0
feature band). A newer SDK on the machine is ignored on purpose.

The extension is loaded at Altium startup. **A new DLL needs an Altium
restart**; reopening the window is not enough. If a self-test report looks
unchanged after a fix, check its timestamp and the section list first — the
old DLL is the most common explanation.

---

## 3. Layout

| Path | Role |
| --- | --- |
| `PluginFactory.cs` | Entry point. The class **must** be `CSharpPlugin.PluginFactory` exactly, and `InvokePluginFactory(IClient)` must be an instance method. Otherwise Altium loads nothing and reports nothing. |
| `SpikeModule.cs` | `ServerModule`; registers commands in `InitializeCommands()`. A new command needs an entry in `AltiumSpike.Ins` **and** `AltiumSpike.rcs` as well. The window is the main entry point, so a new tool usually needs only a card. **Menus are per editor:** each `.rcs` `Insertion` targets one editor's menu ID (`MNPCB_*`, `MNSchematic_*`, …) and the `.Ins` must list that editor's server under `Updates`. Take IDs from Altium's own `System\*.rcs`, never guess them. |
| `SpikeWindow.cs` | The whole UI, in code (no XAML), ~200 KB -- search it, don't read it whole. `Sections()` near the top is the sidebar table; append new sections at the end so the remembered tab index of the others does not move. |
| `<Area>.cs` | One file per group of functions (`Cleanup`, `Placement`, `Polygons`, `Connectivity`, …). Static methods returning a `Result`. |
| `PcbDraw.cs` | Shared primitives: `Layer`, `Line`, `Text`, `ClearArea`. Use these rather than calling the object factory directly. |
| `SchPlacement.cs` | Schematic: places components from CSV on the focused `.SchDoc` and connects them with wire stubs and net labels. Symbols from YouEDA's `youeda.SchLib` or a `Library` column. |
| `Ipc2221.cs`, `FilletGeometry.cs`, `FenceGeometry.cs`, `PolyGeometry.cs`, `ReleaseBundle.cs`, `SchPlacementPlan.cs` | **Altium-free.** No `PCB`, `SCH`, `DXP`, `EDP` or WPF types. Compiled directly by `tests/`. |
| `SelfTest.cs`, `ImportSelfTest.cs`, `SchSelfTest.cs` | One `partial` class. `Run` checks every function against the open board and writes `spike_selftest.md` (the Import commands' checks live in `ImportSelfTest.cs`, in their own strip below the scratch origin); `RunSchematic` checks schematic placement against the focused sheet and writes `spike_selftest_sch.md`. |
| `tests/<Name>Tests/` | Console harnesses. Each `.csproj` includes the shipping source file by relative path — **never a copy**. |
| `tools/SdkDump/` | Metadata dumper for the SDK assemblies. |

`tests/` and `tools/` are excluded from the plugin build in the root `.csproj`.
Do not remove that exclusion: without it the plugin compiles the harnesses in,
and fails with duplicate assembly attributes once any harness has been run.

**Style** (also in `.editorconfig`): a header comment on every file saying
what it is for and what was learned the hard way; explicit types, no `var`;
Allman braces; 4-space indent; block-scoped `namespace AltiumSpike`. `PCB`
and `SCH` both define `TObjectId`, `TObjectSet` and `CoordRect`, so a file
imports one or the other, never both.

---

## 4. The SDK — confirm, then write

There is no usable documentation for the C# SDK and it is **not** a rename of
the DelphiScript API. Before writing any call:

1. Dump the interface **and its helper** with `tools/SdkDump` (§2) — use a
   regex like `"IPCB_Polygon(Helper)?$"`.
2. Many useful calls are not on the interface at all. They are extension-style
   wrappers on a class named **`<Interface>Helper` in the same assembly**
   (`IPCB_PolygonHelper.GetState_Segments`, `IPCB_LayerUtilsHelper.FromString`
   / `MechanicalLayer`). The interface carries an `Internal_` twin instead,
   which returns a different and usually less useful type. Prefer the helper.
3. Copy the exact name and signature, including the return type.

### Traps — each returns a plausible wrong answer without throwing

| Trap | What the wrong answer looks like | Do this |
| --- | --- | --- |
| `GetState_Moveable()` is **inverted** | Locking unlocks | `false` means locked, on components *and* primitives |
| `FromString("Mechanical 1")` | Returns a non-null layer; objects drawn there can't be found | `LayerUtils.MechanicalLayer((uint)n)` — see `PcbDraw.Layer` |
| `AsString` renders `"Mechanical Layer 15"` | Name matching misses | Don't round-trip layer names through strings |
| Every physical layer casts to `IPCB_ElectricalLayer` | Paste/overlay/core typed as copper; 1.6 mm board reads 0.07 mm | Test membership of `eLayerClass_Electrical`, not the cast |
| Is this primitive on copper? | Name matching excluded every track, admitted every pad | `LayerUtils.IsElectricalLayer(p.GetState_V7Layer())`; admit pads and vias by kind (they report Multi-Layer) |
| A track is not necessarily copper | 408 of 416 "tracks" were silkscreen outlines | Filter by layer **before** counting or classifying |
| `IPCB_Polygon.GetState_AreaSize()` | `0.00` on a poured polygon | Shoelace over the outline (`PolyGeometry.PolygonArea`) |
| `Internal_GetState_Segments(i)` | Empty `IPolySegment` | `GetState_Segments(i)` → `PolySegment` struct |
| `GetState_IslandAreaThreshold()` | `250000000000` for 1.6 mm² | Square coords: multiply by `CoordToMMs(1)²` |
| Free text via `PCBObjectFactory` | Object added, nothing drawn, not findable by string | Set **both** `SetState_UnderlyingString` and `SetState_Text` (`PcbDraw.Text` does) |
| Pad mask/paste expansion | Value reverts next time rules run | Set the matching `…Valid = TCacheState.eCacheManual` in `V7_PadCache` |
| `RoutedLength64` / `SignalLength` | `0` for every net with connectivity not built; `SignalLength` non-zero on some | Measure geometry (`Connectivity.MeasureNets`) and report both |
| Fill location | Off by half a fill | `LocationX/Y` is the **lower-left corner**; `Length` = X extent, `Width` = Y extent |
| Component body vs anchor | Pick-and-place off by ~0.18 mm | Use `BoundingRectangleNoNameComment()` for the body; the anchor is not the centre |
| `Math.Round` | `33.416` where Altium prints `33.417` | `MidpointRounding.AwayFromZero`; also normalise `-0.000` to `0.000` |
| `SetState_Rotation` | — | Absolute, not relative |
| `IIntegratedLibraryManager.PlaceLibraryComponent` | Leaves the part it just placed selected; after a batch the last part shows selection handles, one Delete key from gone | `GetState_Selection()` / `SetState_Selection(false)` on each placed part (verified: exactly 1 deselected per 14-part run) |
| `ISch_Component.SetState_IsMirrored(true)` | Part placed, labelled correctly, but not mirrored: pins and artwork unchanged | Call `Mirror(location)` on the component, then read orientation and location back; verify by pin geometry, not by the flag |
| `doc is ISch_Lib` | Succeeds on a plain `.SchDoc` (object id `eSheet`), so a sheet is refused as a "library" | Compare `doc.GetState_ObjectId()` with `TObjectId.eSchLib`; never trust an SDK cast to classify |
| `ILibCompInfoReader` on a YouEDA `.SchLib` | `NumComponentInfos() == 0` for a 2 MB library | It lists the `FileHeader` index (`CompCount=`, `LibRef0=`…), which YouEDA/AltiumSharp does not write. Zero means "no index", not "no symbols" |

Add to this table whenever a new one is found. It is the most valuable part of
this file.

---

## 5. Recipes

### Add a new function

1. **Confirm every SDK member** you need with `tools/SdkDump`.
2. **Split pure logic out.** Anything that is arithmetic — geometry, formulae,
   naming, sorting rules — goes in an Altium-free file (or an existing one like
   `PolyGeometry.cs`) with assertions in `tests/`. Write the test cases that
   break a naive implementation: boundaries, wrap-around, empty input, both
   winding orders.
3. **Write the board-facing part** in the matching `<Area>.cs`, following the
   existing shape:
   - `public static Result Name(IPCB_ServerInterface s, IPCB_Board b, …)`
   - Board iterators: create, `AddFilter_ObjectSet`, `AddFilter_AllLayers()`,
     `AddFilter_Method(eProcessAll)`, and **`BoardIterator_Destroy` in `finally`**.
   - Board changes inside `pcbServer.PreProcess()` / `PostProcess()`, and each
     object inside `BeginModify()` / `EndModify()` with `EndModify` in `finally`
     — a component left mid-modify blocks File › Save with no useful message.
   - Collect errors into `Result.Errors`; never throw at the UI.
   - Write CSVs with `new UTF8Encoding(false)` and invariant formatting.
4. **Add the UI card** in `SpikeWindow.cs`: a `ToolCard(...)` in the right
   `Build…Section`, a `Do…` handler that validates input with `TryMM` /
   `Complain`, persists fields with `Settings.SetValue`, and reports through
   `ShowResult` + `SetStatus`. Mark anything that changes the board
   **`MODIFIES BOARD`** on its card.
5. **Add a self-test check** (§6). A function without one is unverified.
6. Build, run the harnesses, deploy, run the self-test, read the report.

### Fix a self-test failure

1. Read the whole report, not just the failure. Look for **two checks that
   contradict each other** — that is usually the real clue.
2. Decide which is wrong: the function, the assertion, or your assumption about
   the board. Check the board census section before anything else.
3. If you can't tell, instrument: make the check print what it measured.
4. Fix one thing, redeploy, re-run. Compare verdicts with the previous report.

---

## 6. Self-test rules

- **A check is not a pass because it did not throw.** Every check compares its
  result against something independent: a count that must equal another count,
  a coordinate that must land where requested, a file read back from disk.
- Return `"PASS: …"`, `"FAIL: …"`, `"INFO: …"` or `"SKIP: …"` with the numbers
  in the message.
- A `FAIL` message must say what was expected, what was found, and — where
  known — the likely cause.
- **Read-only checks** must not change the board. Assert that (e.g. track count
  before == after).
- **Modifying checks** build their own geometry in the scratch area (`ox, oy`,
  well clear of the outline), verify it, and leave nothing behind: the final
  sweep removes the scratch area, and any check that touches an existing object
  records its state first and restores it in `finally`.
- A zero is only a pass if the board makes zero correct. Check the census:
  **an unrouted board makes every routing-dependent result zero legitimately**,
  and a check that fails on that is a broken assertion.

Assertions here have been wrong as often as code. Known bad patterns:
comparing counts across an operation that deletes before it adds; searching a
whole CSV line for a word that can also be a layer *name*; assuming every track
is copper.

---

## 7. Definition of done

- [ ] `dotnet build` clean, no new warnings
- [ ] Every harness in `tests/` exits 0
- [ ] New pure logic has harness coverage, including edge cases
- [ ] New board function has a self-test check that compares, not just runs
- [ ] Deployed, Altium restarted, self-test run on a scratch board
- [ ] Report read; any verdict change explained
- [ ] New SDK trap, if found, added to the table in §4

If the last three can't be done (no Altium access, say), stop at "compiles and
harnesses pass" and **say so explicitly** in your answer.

---

## 8. Answering questions about the board

When the user asks why a report says something, check in this order:

1. **Is the board what you think it is?** Census counts, per-layer primitives,
   whether it is routed at all. Two runs on the "same" file can differ if one
   was against an unsaved in-memory state.
2. **Is the assertion right?**
3. **Is the function right?**

Say which of the three it was. If it is the board, say that plainly — it is a
finding, not an excuse.

---

## 9. Safety

- **Never commit** anything from `Assemblies/`, `bin/`, `obj/`, or any `.dll`.
  The SDK is proprietary and this repo is public.
- **Never save** a PCB or schematic document the plugin has modified. Nothing
  here writes a document to disk; keep it that way.
- **Machine-specific notes** -- test boards, output folders, library
  locations -- go in `CLAUDE.local.md`, which is git-ignored. This repo is
  public; no personal path is ever committed.
- Board-modifying functions are opt-in, clearly labelled, and report exactly
  what they changed.
- Renumbering designators desynchronises the PCB from the schematic. It writes
  a proposal first and applies only when asked again.

---

## 10. Commits

- Messages explain **why**, including what was found and how it was verified.
- Commits carry only the author's name. **Do not add `Co-Authored-By`, session
  links, "generated with" lines or any other tool attribution** to commit
  messages, PR descriptions, code comments or files.
- Strip embedded provenance metadata (e.g. C2PA chunks in PNGs) from any
  generated image before committing it.
