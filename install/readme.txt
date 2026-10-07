Clash Matrix for Navisworks
==========================

Navisworks Manage add-in by Mohamed Ali Harby.

Turns an Excel clash matrix into Navisworks Clash Detective tests:

  Search sets       Scans every appended NWC/NWD file and creates one flat
                    search set per Revit category (Doors, Walls, Ducts, Pipes,
                    ...), with fittings folded into their parent category.

  Clash tests       Creates one clash test per matrix pair, each side linked to
                    its category search set. Tolerance and priority come from
                    the matrix.

  Matrix export     Generates the full clash matrix from the model's own
                    categories and writes it to Excel so you can edit it, then
                    re-import it with Update Matrix.

  Generate XML      Writes the Navisworks clash-test exchange file for
                    Clash Detective > Batch > Import.

Installing
----------
1. Close Navisworks first. If Navisworks is open the installer stops with a
   "files in use" message: close Navisworks, then click Retry. It never closes
   Navisworks for you.
2. Double-click the MSI. No administrator rights are needed.
3. Start Navisworks. A Clash Matrix tab appears with the Clash Matrix button.

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
