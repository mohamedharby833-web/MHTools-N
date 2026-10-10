Clash Matrix for Navisworks
==========================

Navisworks Manage add-in by Mohamed Ali Harby.

Turns an Excel clash matrix into Navisworks Clash Detective tests:

  Search sets       Scans every appended NWC/NWD file and creates one search
                    set per Revit category (Doors, Walls, Ducts, Pipes, ...),
                    with fittings folded into their parent category. The sets
                    are real searches, so newly appended files are picked up.

  Clash tests       Creates one clash test per matrix pair, each side linked to
                    its search set. Tolerance and priority come from the
                    matrix. You confirm what changes first; one Undo reverts it.

  Matrix export     Generates the full clash matrix from the model's own
                    categories and writes it to Excel so you can edit it, then
                    re-import it with Import Matrix.

  Safe by design    Sets live in a "Clash Matrix" folder in the Sets window.
                    Only clash tests linked to those sets are ever changed;
                    your own sets and tests are left alone.

  Export Results    Clash Detective results to an Excel workbook and a PDF
                    report (summary per test, every clash with a picture).

  Generate XML      Writes the Navisworks clash-test exchange file for
                    Clash Detective > Batch > Import.

Installing
----------
1. Close Navisworks first. If Navisworks is open the installer stops with a
   "files in use" message: close Navisworks, then click Retry. It never closes
   Navisworks for you.
2. Double-click the MSI. No administrator rights are needed.
3. Start Navisworks. An MHTools-N tab appears with Clash Matrix, Export
   Results and About.

Installing a newer (or the same) version replaces the one already there.

Logs
----
Diagnostics are written to %TEMP%\ClashMatrix_Log.txt. When the add-in
misbehaves, send that log with a screenshot.

Where it lives
--------------
%AppData%\Autodesk\ApplicationPlugins\ClashMatrix.bundle

One bundle covers Navisworks Manage 2024, 2025, 2026 and 2027. Navisworks reads
PackageContents.xml at startup and loads only the build matching the release you
are running.

Uninstalling
------------
Settings > Apps > Clash Matrix for Navisworks > Uninstall.
