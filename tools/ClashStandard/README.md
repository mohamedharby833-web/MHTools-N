# ClashStandard

Generates `src/ClashMatrix/Resources/ClashStandard.tsv`, the built-in clash
standard embedded in the add-in. **Scan Model** uses it to give each default
clash test (one per Revit category pair) its priority and tolerance.

Run it again whenever the source workbook changes, then rebuild the add-in:

```
dotnet run --project tools\ClashStandard -- "<path to the clash matrix workbook>.xlsx"
```

How the workbook is read (see `Program.cs` for the details):

- Rows are keyed by their **Revit Category** (column E). In the MEP block of the
  sample matrix the Description labels are shifted against the rows; the Revit
  Category, the weighting and the cells line up row by row.
- **Priority** = the Matrix cell value (row weighting + column weighting - 1;
  1 = High ... 5 = Low). An empty cell means no test for that pair.
- **Tolerance** = a constant on the Tolerance sheet. Formula cells there are
  leftover copies of the priority formula and are ignored (the pair then uses the
  dialog's default tolerance).
- Workbook spellings that are not Revit category names are mapped (Wall -> Walls,
  Floor -> Floors, Rainlings -> Railings, Cable Trays Accessories -> Cable Tray
  Fittings, ...).
