# Clash Matrix — Navisworks add-in

Turns an Excel **clash matrix** into Navisworks **Clash Detective** tests, in one
click. No more pasting the matrix into an AI chat and importing a hand-made XML.

Two actions, both from the one dialog:

1. **Create sets + tests** — scans the **open model** (every appended file), creates
   one **search set per unique category** (e.g. one `Doors` set covering doors across
   all files), then builds every clash test directly in the open document through the
   Navisworks .NET API, each test's Left/Right linked to its category search set.
2. **Generate XML** — writes the Navisworks clash-test exchange file
   (`nw-exchange-12.0.xsd`) that you import through *Clash Detective → Batch → Import*.

Targets **Navisworks Manage 2024, 2025, 2026 and 2027** — one project, four build
configurations, all on **.NET Framework 4.8** (the Navisworks .NET API has stayed on
4.8 through 2027).

---

## How it works

- **Search sets come from the model, not the spreadsheet.** The add-in walks the model
  tree of every appended NWC/NWD file and collects the unique categories (e.g. `Doors`,
  `Walls`, `Ducts`, `Bridge`, `Piers`). It then creates **one flat search set per
  category**, named by the plain category name. No folders, no abbreviations.
- **The clash matrix just defines which pairs to test.** A filled cell below the
  diagonal = one clash test `{RowDescription}-vs-{ColDescription}` (e.g.
  `Doors-vs-Windows`). Each side is linked to the search set of the same name.

Because the search sets are *search-based* (they match a category across all links),
newly appended files are picked up automatically; the **Update sets** button re-scans
and reports the per-set item counts.

### The matrix

The workbook must contain a **`Matrix`** sheet (and optionally a **`Tolerance`** sheet):

- Rows 1–5 are column headers: `Discipline` / `Description` / `Weighting` /
  `Abbreviation` / `Revit Category`. Only **Description** (column B) is used — it must
  match the category names in the model.
- Columns A–E are the matching row labels; data starts at F6.
- A **filled cell below the diagonal** = one test between that row's category and that
  column's category. The value (1/2/3) is a weighting and is **ignored** (priority `0`).

### Tolerance

Tolerance is read **per test from the `Tolerance` sheet** (in **mm**), falling back to
the dialog's *Tolerance (mm)* value (default **10**) when a cell is blank or the sheet
is missing. It is converted to the document's length unit for both the XML and the API.

---

## Build

The Navisworks API comes from the **`Speckle.Navisworks.API`** NuGet package (pinned
per year in the csproj), so **no Navisworks install is needed** to build any year —
the same way MH Tools builds for every Revit year. No Visual Studio is strictly
required; `dotnet build` works.

```
dotnet build src\ClashMatrix\ClashMatrix.csproj -c "Release N27"
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
%AppData%\Autodesk\ApplicationPlugins\ClashMatrix.bundle\
    PackageContents.xml
    Contents\
        21\ ...   Navisworks 2024 (N24 build)
        22\ ...   Navisworks 2025 (N25 build)
        23\ ...   Navisworks 2026 (N26 build)
        24\ ...   Navisworks 2027 (N27 build)
```

Each `Contents\<NN>` folder holds the four build outputs for that release:

```
    ClashMatrix.dll
    AddinRibbon.xaml
    ClashMatrix16.png
    ClashMatrix32.png
    en-US\AddinRibbon.xaml        (also mirrored into the locale folder)
    en-US\ClashMatrix16.png
    en-US\ClashMatrix32.png
```

`PackageContents.xml` lives at `install\PackageContents.xml`; it already lists all
four releases (`Nw21`..`Nw24`). The build output for release `NN` is
`src\ClashMatrix\bin\<year>\`.

Restart Navisworks. A **Clash Matrix** tab appears with the **Clash Matrix** button.

---

## Use

1. Open the coordination model in Navisworks Manage (with all files appended).
2. Click **Clash Matrix → Clash Matrix**.
3. **Import Matrix…** and pick the `.xlsx`. The dialog previews the test count and the
   categories it references.
4. Set the tolerance if needed, then:
   - **Create sets + tests** — scans the model, creates the category search sets, then
     all clash tests, or
   - **Generate XML…** — writes the exchange file for *Clash Detective → Batch → Import*.
5. After appending new files, click **Update sets** to re-scan and report item counts.

Search sets that already exist are kept; nothing is silently dropped — any category
the matrix references but the model doesn't contain is reported.

---

## Layout

```
ClashMatrix.sln
src/ClashMatrix/
  App.cs                              plugin entry + [Command] wiring
  Ribbon/AddinRibbon.xaml             ribbon tab/button definition
  Resources/ClashMatrix16.png         16px + 32px ribbon icons
  Resources/ClashMatrix32.png
  Commands/ClashMatrixCommand.cs      opens the dialog
  Model/
    ClashTestDefinition.cs            one test: left/right category names, tolerance
    MatrixParseResult.cs              parse result
  Services/
    XlsxMatrixReader.cs               pure .NET xlsx reader (no third-party DLLs)
    ClashXmlWriter.cs                 Navisworks exchange XML writer
    NavisClashCreator.cs              scans model categories + creates sets/tests
    UnitConverter.cs                  mm <-> document unit + tolerance formatting
  UI/
    ClashMatrixWindow.xaml(.cs)       the dialog
```
