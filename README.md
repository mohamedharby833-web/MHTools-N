# MHTools-N — Navisworks add-in

The Navisworks sibling of MHTools-R (Revit), by Mohamed Ali Harby. It adds an
**MHTools-N** ribbon tab to Navisworks Manage 2024–2027 with:

- **Clash Matrix** — turns an Excel **clash matrix** (or the model's own categories)
  into Navisworks **Clash Detective** tests, in one click. No more pasting the matrix
  into an AI chat and importing a hand-made XML.
- **Export Results** — clash results to Excel and an A4 PDF report.
- **AI Bridge** — **Agent Bridge**, **Trusted C#** and **Connect AI App**: an MCP
  server so an AI app (Claude Desktop, Claude Code, Cursor, Codex...) can work with
  the open Navisworks. See [Agent Bridge](#agent-bridge).
- **About**.

**Renamed in 2.0.0.** Up to 1.8.3 the add-in was called *ClashMatrix*
(`ClashMatrix-x.msi`, `ClashMatrix.bundle`). Installing `MHTools-N-2.0.0.msi`
replaces it (same upgrade code), and the settings in `%AppData%\ClashMatrix` are
copied to `%AppData%\MHTools-N` the first time they are needed. What did **not**
change, on purpose: the **Clash Matrix** button and window, and the **`Clash Matrix`
folder of search sets inside your models** — renaming that folder would make the
add-in lose track of the tests it created.

## Clash Matrix

The search sets the add-in creates live in a **`Clash Matrix` folder** in the
*Sets* window. Clash Detective has no test folders, so the tests sit at the top
level — and a test counts as the add-in's **only when both its sides are linked to
sets in that folder**. Sets elsewhere, and any test linked to anything else, are
never changed, so your own sets and tests are safe. Every run that changes the
model is a single **Undo** step.

Targets **Navisworks Manage 2024, 2025, 2026 and 2027** — one project, four build
configurations, all on **.NET Framework 4.8** (the Navisworks .NET API has stayed on
4.8 through 2027).

---

## How it works

- **Sets are split by discipline.** Every appended file has a discipline - **AR**
  architecture, **ST** structure, **MEC** mechanical (HVAC, plumbing, drainage, fire
  fighting), **EL** electrical, **ICT** information & communication (data,
  telephone, security, nurse call, audio-visual devices), or **GEN**. Two choices
  split a file by category instead: **EL+ICT** (an electrical file that also holds the
  ICT devices: those go to ICT, the rest to EL) and **MEP** (one file with all
  services: MEC / EL / ICT).
  The discipline is guessed and shown in the **Models** table, where it can be changed
  (the choice is remembered per file name, in `%AppData%\MHTools-N\ModelDisciplines.txt`).
  A word in the file name decides (ARCH, STR, MECH, ELEC, ICT, ELV, MEP, ...); a
  one-letter role code (`…-1A0-…` AR, `…-1S0-…` ST, `…-1M0-…` MEC, `…-1EA-…` EL) only
  when the file's content agrees, since a letter like the D of `1DS` can mean
  drainage or data systems; otherwise the content decides.
  Each set is one discipline's elements of one category - **AR Walls** and **ST
  Walls**, **MEC Pipes**, **EL Cable Trays**, **ICT Security Devices** - and searches
  only the files that send that category to that discipline, so **an element is
  never in two sets**.
- **A category of another discipline is never put in a file's sets.** Mechanical
  categories (ducts, pipes, air terminals, sprinklers, mechanical and plumbing
  equipment and fixtures, ...) only go to **MEC**; electrical ones (electrical
  equipment and fixtures, lighting, fire alarm, ...) only to **EL**; ICT devices to
  **ICT** or **EL**; cable trays and conduits to **EL** or **ICT**. **AR** and **ST**
  sets take none of these (nor MEP fabrication hangers). So there is no *EL Ducts*,
  *ICT Pipes*, *MEC Cable Trays* or *AR Plumbing Fixtures*: such elements are left out
  of every set, and Scan Model lists each file and the categories left out of it.
  Neutral categories (Generic Models, Specialty Equipment, ...) stay with their file.
- **Search sets are real searches** on the Revit *Element > Category* property
  (addressed by its internal name, so it works in any Navisworks language), within
  the files of its discipline. After appending a file, **Update Search Sets** adds it
  to its discipline's sets - in place, so tests stay linked. Related categories are folded into their
  parent: *Ducts* also matches *Duct Fittings*, *Duct Accessories*, *Duct
  Insulations* and *Flex Ducts*; *Pipes* matches *Pipe Fittings*, *Pipe
  Accessories*, *Pipe Insulations* and *Flex Pipes*; *Cable Trays* and *Conduits*
  include their fittings; *Walls* includes *Wall Sweeps*; and one **Curtain Walls**
  set holds *Curtain Panels*, *Curtain Wall Mullions* and *Curtain Systems*.
- **The clash matrix defines which pairs to test.** Each filled cell = one clash test,
  named `[Number] - [Discipline 1] [CATEGORY 1] - [Discipline 2] [CATEGORY 2]`, e.g.
  `381 - AR WALLS - ST FLOORS`. Tests are numbered reading the matrix row by row,
  sets grouped by discipline (AR, ST, MEC, EL, ICT, …; `GEN` when a set's discipline is
  not known). Each side is linked to the search set of the same name in the
  `Clash Matrix` folder.
- **Re-running is safe.** A test is recognised by the **two sets it links**, not by
  its name, so when the numbers shift (a category comes or goes) the test is just
  **renamed in place** and keeps its results, statuses and comments. Unchanged tests
  are left alone; changed ones are updated in place; tests the matrix no longer has
  are removed only if you say so (together with Clash Matrix sets nothing uses any
  more, e.g. an old *Duct Accessories* set).

### The matrix

The workbook must contain a **`Matrix`** sheet (and optionally a **`Tolerance`** sheet):

- Rows 1–5 are column headers: `Discipline` / `Description` / `Weighting` /
  `Abbreviation` / `Revit Category`; columns A–E are the matching row labels; data
  starts at F6.
- **Description** (column B / row 2) is the search-set name. It can span several
  rows, one per **Revit Category** (column E / row 5); the set then matches all of
  those categories. With no Revit Category, the Description itself is taken as the
  category name.
- A **filled cell** = one test between that row's set and that column's set. Either
  side of the diagonal works; a pair filled on both sides is still one test. The
  value is the test **priority**: 1 = High, 2 = Medium, 3 = Low — the only three
  Navisworks has. A matrix on a 1–5 scale is folded into them (1 → High, 2–3 →
  Medium, 4–5 → Low). Priority is set on the test in Navisworks 2026+; 2024/2025
  have no priority in the API.

### Tolerance

Tolerance is read **per test from the `Tolerance` sheet** (in **mm**; `0` is
allowed). Tests without a value use the dialog's *Tolerance (mm)* (default **10**),
read at the moment you create the tests — the grid shows which is which. It is
converted to the document's length unit for both the API and the XML.

Only **typed** tolerance values count. A formula on the Tolerance sheet is ignored
(with a warning): such cells are usually leftover copies of the priority formula,
and their values are priorities, not millimetres.

### Default tests: the built-in clash standard

**Scan Model** builds its tests (one per pair of model categories) from the **built-in
clash standard**, a clash detection matrix embedded in the add-in:

- **Priority** is the standard's cell value (row weighting + column weighting − 1, on
  a 1–5 scale) folded into Navisworks' three: 1 → **High**, 2–3 → **Medium**, 4–5 →
  **Low**. It is set on the test in **Navisworks 2026 and 2027**; 2024 and 2025 have no
  priority in their API, so there it is kept in the matrix, the grid and the export
  only. (Navisworks cannot show any other value — a test with priority 4 or 5 has a
  blank priority, and clicking it crashes Clash Detective.)
- **Tolerance** is the standard's typed tolerance for the pair, else the dialog's default.
- Pairs the standard leaves **blank are not tested**. Categories it does not list
  (Generic Models, Hardscape, …) are tested against everything, with weighting 3 and
  the default tolerance, and are named in a warning.
- Each set uses the standard's rows **of its own discipline**: *AR Walls* the
  architectural Walls row, *ST Walls* the structural one; MEC, EL and ICT sets use the
  services rows. A category the standard has no row for in that discipline falls
  back to its other rows. Where several rows apply (Pipes sits under two services
  groups), the **most important** priority and the **tightest** tolerance win.
  Folded categories count with their parent (Pipes includes Pipe Fittings,
  Accessories, Insulations and Flex Pipes).

The standard lives in `src/MHToolsN/Resources/ClashStandard.tsv`; regenerate it
with `tools/ClashStandard` when the source workbook changes (see its README).

### Exported workbook

**Export Matrix…** writes the matrix look: dark discipline banners, grey Description
and beige Revit Category headers, the weighting column and row, and the standard
priority colours (1 High red, 2 Medium amber, 3 Low yellow) as conditional formatting, so
they follow when a priority is edited in Excel, plus a Priority Key. The export reads
back in with **Import Matrix…** unchanged.

---

## Build

The Navisworks API comes from the **`Speckle.Navisworks.API`** NuGet package (pinned
per year in the csproj), so **no Navisworks install is needed** to build any year —
the same way MH Tools builds for every Revit year. No Visual Studio is strictly
required; `dotnet build` works.

```
dotnet build src\MHToolsN\MHToolsN.csproj -c "Release N27"
```

| Configuration | Navisworks | Framework |
|---|---|---|
| `Debug/Release N24` | 2024 | .NET Framework 4.8 |
| `Debug/Release N25` | 2025 | .NET Framework 4.8 |
| `Debug/Release N26` | 2026 | .NET Framework 4.8 |
| `Debug/Release N27` | 2027 | .NET Framework 4.8 |

Build all four with **Build → Batch Build** in Visual Studio, or run
`install\build-installer.cmd` to build all four and package the per-user MSI.

`NavisworksPath` (the local install, or `-p:NavisworksPath=...`) is only used for the
F5 launch and for the no-NuGet `<Reference>` fallback in the csproj — the API itself
is restored from NuGet.

Debug builds also **deploy to the per-user bundle** (no admin rights needed) and
launch `Roamer.exe` on F5.

---

## Install

Navisworks loads .NET plugins from an Autodesk **ApplicationPlugins bundle** — there
is no `.addin` manifest. Install per-user (no admin rights) to:

```
%AppData%\Autodesk\ApplicationPlugins\MHTools-N.bundle\
    PackageContents.xml
    Contents\
        21\ ...   Navisworks 2024 (N24 build)
        22\ ...   Navisworks 2025 (N25 build)
        23\ ...   Navisworks 2026 (N26 build)
        24\ ...   Navisworks 2027 (N27 build)
```

Each `Contents\<NN>` folder holds the four build outputs for that release:

```
    MHTools-N.dll
    AddinRibbon.xaml
    ClashMatrix16.png
    ClashMatrix32.png
    en-US\AddinRibbon.xaml        (also mirrored into the locale folder)
    en-US\ClashMatrix16.png
    en-US\ClashMatrix32.png
```

`PackageContents.xml` lives at `install\PackageContents.xml`; it already lists all
four releases (`Nw21`..`Nw24`). The build output for release `NN` is
`src\MHToolsN\bin\<year>\`.

Restart Navisworks. An **MHTools-N** tab appears with **Clash Matrix** and **Export
Results** (panel *Clash*) and **About** (panel *Help*).

---

## Use

1. Open the coordination model in Navisworks Manage (with all files appended).
2. Click **MHTools-N → Clash Matrix**. The window has two tabs.
3. **Search Sets** tab:
   - **Models** - each appended file and its discipline; correct a wrong guess.
   - **Sets** - *Best practice* (each discipline keeps only its own categories; EL and
     ICT may share theirs) or *All categories* (every category in every file gets a
     set: *AR Plumbing Fixtures*, *MEC Cable Trays*, ...).
   - **Scan Model** lists one set per discipline and category, and the default tests
     (priorities and tolerances from the built-in clash standard). Or **Import
     Matrix…** an `.xlsx`. Nothing in the model changes yet.
   - The **search box** above the list shows only the sets whose name, discipline,
     categories, files or status contain what you type (e.g. `AR` or `pipes`).
     The **tick box in the column header** ticks or unticks every set shown (a dash:
     some are ticked). With several rows selected (Ctrl / Shift + click), ticking or
     unticking one of them does the same to all of them.
   - In the list, **untick** a set to leave it out (with its tests), double-click its
     **name** to rename it (its tests follow) or its **Revit categories** to edit what
     it matches. *Status* says whether it exists; **Count Items** shows how much each
     set finds.
   - **Create Search Sets** creates the ticked sets, updates existing ones in place
     (tests stay linked) and deletes the unticked ones that exist - unless a test
     still uses them. You confirm first.
   - **Next: Clash Tests →** moves to the second tab.
4. **Clash Tests** tab: the **search box** filters the tests by name, left or right
   set, priority or status (`AR WALLS` finds `3 - AR WALLS - AR DOORS` but not *AR
   CURTAIN WALLS*); the header tick box and the selected rows work as on the first
   tab. Tick or untick tests, double-click a **name** or a
   **tolerance** to edit it (*own* = the test's, *default* = the box above), pick a
   **priority** (High / Medium / Low / None). *Status* shows New, In model, Rename or
   Excluded. **Renumber** names the tests again in matrix order (typed names are
   kept).
5. **Create Clash Tests** - the main button on this tab (**← Back** returns to the
   sets) - creates every ticked set and test, also those hidden by the search. A confirmation says
   exactly how many of the add-in's tests will be added, updated, renamed, kept and
   (optionally) removed - including those now unticked. A test whose name is already
   taken by a test that isn't linked to the `Clash Matrix` sets is skipped, never
   overwritten.
6. **Export Matrix…** writes the ticked sets and tests to Excel to edit and re-import.

After appending a file, **Scan Model** again (it rebuilds the list - ticks and typed
names are asked about first) and **Create Search Sets**.

**Generate XML…** writes the exchange file for *Clash Detective → Batch → Import*.
Its locators point at `Clash Matrix/{set}`, so create the sets first.

### Export Results

**MHTools-N → Export Results** writes the Clash Detective results of the ticked tests
(all, none, only the Clash Matrix tests, or only tests with clashes), with the same
choices as Clash Detective's own Report dialog - remembered for next time:

- **Contents**: Summary, Clash Point, Date Found, Assigned To, Date Approved, Approved
  By, Date Resolved, Resolved By, Layer Name, Item Path, Item ID, Status, Priority,
  Distance, Description, Comments, Image, Clash Group. (A clash's own Priority, Date
  Resolved and Resolved By exist from Navisworks 2026. Grid Location, Quick Properties
  and the simulation fields are not in the API.)
- **Include clashes**: New, Active, Reviewed, Approved, Resolved.
- **Output**: Excel workbook and / or PDF report.

- **Excel workbook** - a *Summary* sheet (one row per test: type, tolerance, priority,
  status, last run, and the count of New / Active / Reviewed / Approved / Resolved,
  with a total) and a *Results* sheet (one row per clash: group, status, distance in
  mm, location in m, and for both items the category, type, element ID, level and
  source file, plus assigned to, approved by, dates, description and comments).
  Header row frozen and filtered; status cells coloured as in Clash Detective.
- **PDF report** - A4 portrait. A summary page (totals by status, a table of the
  tests), then every test with its clashes in a table as in Clash Detective's own
  report - *Image | Clash | Item 1 (Item ID, Path) | Item 2 (Item ID, Path)* - about
  11 clashes a page with pictures, about 20 without. Tests follow on from one another;
  a table that runs over a page repeats its heading. Page header and numbering on
  every page.
- **Pictures** are zoomed onto each clash from a corner above, item 1 red, item 2
  green, the rest of the model faded - not the whole building with the clash a dot in
  it. A clash with a saved viewpoint keeps Clash Detective's own picture. The main
  view is used while the pictures are made and put back afterwards.
  Written by the add-in itself, no PDF printer or library needed. (Text outside the
  Western character set, e.g. Arabic, prints as `?` - the standard PDF fonts have no
  such glyphs.)

Columns and cell lines follow the chosen contents (Item ID, Item Path or the element
and file, Layer Name, Status, Priority, Distance, dates and people in the clash cell;
Clash Point, Comments and a long Description on a line under the items). Pictures (*Image*) take a moment
per clash; for more than 400 clashes the add-in asks first, and Cancel on the progress
bar stops.

### Agent Bridge

The Navisworks counterpart of MHTools-R's Agent Bridge: an **MCP server** that an AI
app starts, talking to the add-in inside Navisworks over a named pipe.

```
AI app ──MCP (stdio)──► MHTools-N.AgentServer.exe ──named pipe──► MHTools-N add-in ──► Navisworks UI thread
```

- **Agent Bridge** (ribbon, panel *AI Bridge*) starts it for this Navisworks: a
  session file in `%LocalAppData%\MHTools-N\sessions` (readable by this Windows user
  only - it holds the pipe's token) and a pipe only this user can open. The button
  reads **Agent Bridge On** while it runs; it stops again on a second click or when
  Navisworks closes.
- **Trusted C#** starts **Off** every time, as in MHTools-R. *Ask* or *Auto* switches it
  on, until it is switched off, the bridge stops or Navisworks closes. (Running C# is a
  later step; the switch and its state are there now.)
- **Connect AI App** lists the AI apps found on the PC (installed programs, Store apps,
  commands on PATH, settings folders) and adds the bridge to Claude Desktop, Claude
  Code, Cursor or Codex in one click, as the entry **`mhtools-n-bridge`**. The app's
  settings file is backed up first (`.bak-<date>` beside it) and nothing else in it
  changes - MHTools-R's `mhtools-r-bridge` stays as it is. For other MCP apps it puts
  the entry on the clipboard in that app's format.
- The **server** is `MHTools-N.bundle\Contents\Server\MHTools-N.AgentServer.exe`,
  published self-contained by the MSI build: no Python, Node or .NET to install. It
  logs to `%LocalAppData%\MHTools-N\AgentServer.log`.
- **Navisworks' API runs on its UI thread only.** Each request is posted there with a
  deadline (20 s). If Navisworks has not *started* it by then (a dialog open, a long
  command), it is dropped and never runs - the AI is told Navisworks is busy and
  nothing was done. If it started but has not finished, the AI gets an operation id
  to poll with `get_execution`. (Idea from NavisCoord, HorizunGroup, MIT.)

Tools (with one Navisworks running, `sessionId` can be left empty):

| Group | Tools |
|---|---|
| Session | `list_navisworks_sessions`, `get_context` (file, units, appended models, selection, sets, viewpoints, clash tests, Trusted C# state), `get_execution` |
| Model | `get_model_tree` (paged), `find_items` (Revit category + property conditions, per model file), `get_item_properties` (all property categories, path, bounding box), `get_selection` |
| Sets and views | `list_sets` (search / selection, Clash Matrix or not, optional counts), `get_set_items`, `list_viewpoints` |
| Clash | `get_clash_summary` (start here: tests and clashes by status, top tests by open clashes), `list_clash_tests` (paged, filters), `get_clash_results` (paged, both items described), `get_clash_image` |
| Pictures | `capture_view` (the view as the user sees it), `get_clash_image` (zoomed onto the clash, item 1 red, item 2 green, the rest faded) |
| View changes | `select_items`, `zoom_to_items`, `set_visibility` (isolate / hide / show / show all), `color_items` and `reset_appearance` (temporary, not saved), `go_to_viewpoint` |
| Model changes | `create_set` (search or selection set, folder *AI Sets*), `save_viewpoint` (folder *AI Viewpoints*), `set_clash_status` (status, assignee, comment, description), `run_clash_tests`, `preview_clash_matrix` / `apply_clash_matrix` (the Clash Matrix window's Scan + Create) |

The view tools act on item ids, a saved set, the user's selection, or a clash's two
items. **Model changes preview by default** (`dryRun=true`, nothing changes) and need
**Trusted C#** on to run: in *Ask* mode Navisworks tells the user what the AI wants to
do and does it only on **Yes** (No is the default button); in *Auto* it is logged. Each
change is one Ctrl+Z step; nothing saves the file; the `Clash Matrix` set folder can
only be changed through `apply_clash_matrix`.

Items get short ids (`n12`) the agent passes back to other tools; they last until
models are opened, appended or reloaded. Distances are in mm, points in meters.

### About

**MHTools-N → About** shows the version, the Navisworks it runs in and the runtime,
with links to the author's profile and to the log file.

Long operations show the Navisworks progress bar and can be cancelled. A
diagnostic log is written to `%TEMP%\MHTools-N_Log.txt`.

> **Upgrading from 1.0:** 1.0 created plain (static) sets at the root of the Sets
> window and replaced *every* clash test. 1.1 leaves those old sets alone, and its
> tests that are linked to them count as "not the add-in's" — so a matrix test with
> the same name is skipped. Delete the 1.0 tests and root-level sets by hand once
> you no longer need them, then run Create Clash Tests again.

---

## Layout

```
MHTools-N.sln
src/MHToolsN/
  App.cs                              plugin entry + [Command] wiring
  Ribbon/AddinRibbon.xaml             ribbon tab/button definition
  Resources/ClashMatrix16.png         16px + 32px ribbon icons
  Resources/ClashMatrix32.png
  Commands/ClashMatrixCommand.cs      opens the dialog (owned by the Navisworks window)
  Model/
    ClashTestDefinition.cs            one test: left/right set names, tolerance, priority
    MatrixParseResult.cs              sets, their Revit categories, tests, warnings
  Services/
    XlsxMatrixReader.cs               pure .NET xlsx reader (no third-party DLLs)
    XlsxMatrixWriter.cs               xlsx writer (same layout, round-trips)
    ClashXmlWriter.cs                 Navisworks exchange XML writer
    NavisClashCreator.cs              model scan, search sets, clash tests, progress
    UnitConverter.cs                  mm <-> document unit, tolerance parsing/format
    Watchdog.cs                       diagnostic log
  UI/
    ClashMatrixWindow.xaml(.cs)       the dialog (MH Tools skin)
```

## Licence

MIT - see [LICENSE](LICENSE). The installer also places `LICENSE.txt` and
`THIRD-PARTY-NOTICES.txt` (the .NET runtime, Microsoft.Extensions and the
ModelContextProtocol SDK inside the bridge server) in the installed bundle.
Navisworks and Autodesk are trademarks of Autodesk, Inc.

### Trusted C#

For anything the bridge's tools do not cover, the AI can write C#: `compile_code`
compiles a method body in the server, in memory, against the Navisworks API of the
running Navisworks (read from its process: Api, Clash, Takeoff, Timeliner, ComApi) and
.NET Framework 4.8; `execute_code` runs it on Navisworks' UI thread with `doc`,
`inputsJson` and a cancellation token in scope. It goes through the same gate as every
change - Trusted C# Off refuses, **Ask shows the user the code** and runs it only on
Yes, Auto logs it. `wrap=true` makes the run one undo step; `dryRun=true` undoes it
right after and reports whether the undo took. Not sandboxed; the file is never saved
by the bridge. `MHTools-N.AgentServer.exe --compile-test 2027` proves a PC can compile.

## Quantities

**MHTools-N → Quantities** fills Navisworks **Quantification** from the model, the way
dragging elements onto catalog items does, for every type at once:

- **Item groups:** one per discipline (from the file, as Clash Matrix decides it:
  `MECHANICAL - MEC`, ...), and under it one per Revit category (Ducts, Duct Fittings...).
- **Ductwork by size (for the factory):** Ducts, Duct Fittings, Duct Accessories and
  Duct Insulations get an item group per Revit type and **an item per size**
  (`1000 x 700`, `Ø250`, a taper `800 x 400 - 600 x 400`, insulation `650 x 300 - 25 mm`).
  Revit gives a duct's Width/Height as text in mm and no thickness or weight, so each
  element gets, in metres: Width, Height, Perimeter `2(W+H)` (or πD), sheet **Thickness**
  by the longest side, and for straight ducts **Weight = sheet area × thickness ×
  density × (1 + allowance)**, the sheet area being Revit's duct Area (outer surface).
  Description1 = the size, Description2 = the sheet. The rule is the **Duct sheet** row of
  the Take off tab (`%AppData%\MHTools-N\DuctSheet.txt`); defaults - provisional, check
  them against the project spec:

  | Longest side | Sheet (DW/144) |
  |---|---|
  | up to 600 mm | 0.6 mm |
  | 601-750 mm | 0.8 mm |
  | 751-1250 mm | 1.0 mm |
  | 1251-2500 mm | 1.2 mm |

  Galvanised steel 7850 kg/m³, allowance 0 %. Fittings and accessories: count and
  sheet thickness, no weight (Revit gives them no area).
- **Pipework by size:** Pipes, Pipe Fittings, Pipe Accessories, Pipe Insulations and Flex
  Pipes likewise - an item per nominal size (`Ø25`, a reducer `Ø32 - Ø25`, insulation
  `Ø25 - 20 mm`), with the length; Width/Height = the diameter.
- **Export Excel:** the factory workbook - Ductwork (type, size, W, H, pieces, length,
  perimeter, sheet, sheet area, weight, totals), Fittings and accessories, Pipework,
  Insulation, All items, Sheet rule.
- **Update Take Off:** takes the ticked categories off again - their elements already
  taken off are removed from their items (anywhere in Quantification) and taken off
  afresh, following the current structure, duct sheet rule and model; items and groups
  left empty are removed, nothing else is touched; one Undo. (Take Off alone adds only
  elements not taken off yet.) AI Bridge: `apply_takeoff` with `replace=true`.
- **Ticking several rows:** select them (Ctrl/Shift + click), tick one - all follow.
- **Items:** otherwise one per Revit type, holding that type's elements. Navisworks maps each
  element's properties (Length, Area, Volume...) and works out the quantities; items
  use the table's own formulas (`=ModelLength`, `=ModelArea`, ..., `Count = 1`).
- **Scan Model** lists each discipline's categories with their types and elements;
  untick what not to take off; **Take Off**. The **Quantities** tab shows the sums per item.
- **Again after a model update:** only elements not yet taken off are added (by GUID);
  groups and items of the same name are reused, never changed or removed.
- **One Undo (Ctrl+Z)** removes the whole takeoff again.
- Quantification must be set up first (Navisworks: Quantification > **Project Setup**,
  a catalog and the units).
- Through the AI Bridge: `preview_takeoff`, `apply_takeoff`, `get_takeoff_summary`.

Found on a live model (Navisworks 2027) and built in: the takeoff must map the
element's properties first (`MapModelItemPropertiesToInputVariables`) or every quantity
stays empty; quantities are calculated when the database transaction commits; the
item table needs WBS, Transparency, Color, LineThickness, CountSymbol and CountSize.
Example (Waste Building, MEC ducts): 597 elements, 17 items, in about 0.1 s - Ducts
140.46 m / 248.34 m², Duct Insulations 102.09 m / 191.19 m² / 4.78 m³.
