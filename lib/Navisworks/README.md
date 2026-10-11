# Navisworks API reference assemblies (offline / no-NuGet fallback)

The Navisworks API is **version-locked**: each release's API assemblies carry an
assembly version equal to the series number, and each release's loader redirects only
its own version.

| Navisworks | Series | `Autodesk.Navisworks.Api` assembly version |
|-----------|--------|--------------------------------------------|
| 2024      | Nw21   | 21.0.0.0 |
| 2025      | Nw22   | 22.0.0.0 |
| 2026      | Nw23   | 23.0.0.0 |
| 2027      | Nw24   | 24.0.0.0 |

## Normal build (recommended)

The project references the API through the **`Speckle.Navisworks.API`** NuGet
package, pinned per year in `MHToolsN.csproj` (`NavisApiVersion`). Each year's DLLs
are restored from NuGet, so **no Navisworks install is needed** to build any year.
This is the same idea as MH Tools' `Nice3point.Revit.Api` packages.

## Offline fallback

If a build machine has **no NuGet access**, comment the `PackageReference` in
`MHToolsN.csproj` and uncomment the `<Reference>` block there. It resolves against
`$(NavisworksPath)`, which falls back to this folder when the DLLs are present.

Copy the four API assemblies for a year into that year's folder here:

```
lib\Navisworks\
    2024\  Autodesk.Navisworks.Api.dll
            Autodesk.Navisworks.Clash.dll
            AdWindows.dll
            navisworks.gui.roamer.dll
    2025\  (same four, from Navisworks Manage 2025)
    2026\  (same four, from Navisworks Manage 2026)
    2027\  (same four, from Navisworks Manage 2027)
```

They can be copied from any machine with that Navisworks release installed (all under
`C:\Program Files\Autodesk\Navisworks Manage <year>\`).
