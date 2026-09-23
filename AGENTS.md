# Working on AltiumSpike

Orientation for anyone — person or coding agent — picking this up cold. It is
about *how to work on it safely*; `README.md` says what it does, and the
SDK-level facts live in the API notes referenced at the bottom.

## What this is

A C# extension for Altium Designer 26 (.NET 8 + WPF), built against the WPF
assemblies that ship inside Altium rather than against a NuGet reference pack.
One window, a left sidebar of sections, and about fifty functions that read or
edit the open PCB document.

## Layout

| Path | What it is |
| --- | --- |
| `PluginFactory.cs`, `SpikeModule.cs` | Altium entry points. Touch with care — see the API notes. |
| `SpikeWindow.cs` | The whole UI, built in code, no XAML. Sections table near the top. |
| `*.cs` (Geometry, Cleanup, Placement, …) | One file per group of functions. Each returns a `Result` with counts, notes and errors rather than throwing at the UI. |
| `Ipc2221.cs`, `FilletGeometry.cs`, `FenceGeometry.cs`, `ReleaseBundle.cs`, `PolyGeometry.cs` | **Altium-free.** No `PCB`/`DXP`/WPF types. This is what makes them testable. |
| `SelfTest.cs` | Runs every function against the open board and writes `spike_selftest.md`. |
| `tests/` | Console harnesses that compile the Altium-free files *directly from source*. |
| `docs/` | Screenshots. |

## Build, test, deploy

```
dotnet build -c Debug                       # from the repo root
cd tests/PolyGeometryTests && dotnet run    # and the other three; exit 0 == all pass
```

Deploy by copying `AltiumSpike.dll` (and `.pdb`) to:

```
C:\ProgramData\Altium\Altium Designer {GUID}\Extensions\AltiumSpike\
```

`Deploy.ps1` does this and registers the extension. **Altium holds the DLL
open while it runs** — close it first, or the copy is refused. The extension is
loaded at startup, so a new DLL needs an Altium restart, not just a new window.

## The four rules that matter

**1. Read the API from assembly metadata before writing a call.**
There is no usable documentation for the C# SDK, and it is not a rename of the
DelphiScript one. Guessing a member name produces code that compiles against
`dynamic`-ish surfaces and fails silently at runtime. Dump the real signatures
from `Altium.SDK.Interfaces.dll` and work from those.

**2. Pure logic goes in an Altium-free file with a test harness.**
Anything that is arithmetic rather than API calls — fillet tangents, fence
spacing, IPC-2221, point-in-polygon, archive naming — belongs in a file with no
Altium types, compiled *directly from source* by a harness in `tests/`. Never a
copy: a copied file drifts from what ships and the tests start passing against
code nobody runs. This has caught real bugs that looked fine on screen:
a via fence 10% too open on a curve, row clustering that split parts 20 µm
apart, a path sanitiser that passed on Linux and would have written `C:` into a
filename on Windows.

**3. A check is not a pass because it did not throw.**
The failure mode worth catching is a function that returns cleanly having done
nothing. Every self-test check states what it expected and compares against
something real: a count that must equal another count, a coordinate that must
land where it was asked to, a file read back from disk. `SelfTest.cs` says this
at the top and the report says it at the bottom, because it is the whole point.

**4. When a fix misses twice, instrument — do not guess a third time.**
Two wrong guesses in a row mean the mental model is wrong, and a third guess
usually confirms it rather than fixing it. Make the run report the measurement
instead: what the layer name actually resolved to, whether any object was
created at all, what the declared count was and what exception was swallowed.
Several bugs here were only found because two checks in the same report
contradicted each other.

A corollary: **the self-test's own assertions have been wrong about as often as
the code.** When a check fails, establish whether the function or the assertion
is at fault before changing either. Assertions that have been wrong here
compared text counts before and after an operation that deletes first, searched
whole CSV lines for a word that turned out to be a layer *name*, and demanded
that a copper scan see every track on a board whose tracks are silkscreen.

## Safety

- **Never commit the Altium SDK DLLs.** `.gitignore` excludes `Assemblies/`,
  `bin/`, `obj/` and `*.dll`. They are proprietary and this repo is public.
- **The full self-test modifies the open board.** It builds scratch geometry
  well clear of the outline, verifies it, removes it again, and restores
  anything it touched on the real board. Nothing is written to disk, so an
  unsaved document is the backstop — but do not rely on that. Prefer a scratch
  copy, and prefer the read-only half when you only need the reports.
- Functions that change the board are marked `MODIFIES BOARD` on their card and
  say so in the results panel. Keep that up.
- Renumbering designators desynchronises the PCB from the schematic. It writes a
  proposal CSV first and changes nothing until asked twice.

## Verifying a change

1. `dotnet build` clean, and all four test harnesses at 0 failures.
2. Deploy, restart Altium, run **Reports → Run full self-test** on a scratch
   board, and read `spike_selftest.md`.
3. Compare against the previous run. A check that changed verdict is either the
   fix working or a second bug; the report gives the numbers to tell which.

An unrouted board cannot exercise the routing-dependent half — current capacity,
dangling copper, return vias, measured net lengths, testpoint assignment all
have nothing to bite on and will *correctly* report zero. Use a routed board
when touching any of those.

## Further reading

Two sets of SDK notes are kept outside this repo, in the project knowledge base:

- **API notes** — how to get an extension to load at all, the deployment layout,
  the DelphiScript→C# mapping, fill geometry, number formatting and the JLCPCB
  specifics.
- **PCB object model traps** — the accessors that return a plausible wrong
  answer without throwing: inverted `Moveable`, mechanical layer names that do
  not round-trip through `AsString`/`FromString`, every physical layer casting
  successfully to `IPCB_ElectricalLayer`, `GetState_AreaSize` returning zero on
  a poured polygon, island thresholds in square coords, and free text that needs
  its underlying string set before it will draw.
