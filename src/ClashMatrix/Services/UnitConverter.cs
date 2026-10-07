using System;
using System.Globalization;

namespace ClashMatrix.Services
{
    /// <summary>
    /// Millimetre-to-document-unit conversion and the tolerance text format used by
    /// the Navisworks exchange XML (10 decimal places, invariant culture).
    /// </summary>
    public static class UnitConverter
    {
        public static double MmToFeet(double mm)
        {
            return mm / 304.8;
        }

        public static double MmToUnit(double mm, string unit)
        {
            switch ((unit ?? "ft").ToLowerInvariant())
            {
                case "mm":
                case "millimeters": return mm;
                case "cm":
                case "centimeters": return mm / 10.0;
                case "m":
                case "meters": return mm / 1000.0;
                case "in":
                case "inches": return mm / 25.4;
                case "ft":
                case "feet": return mm / 304.8;
                default: return mm / 304.8;
            }
        }

        /// <summary>Formats a tolerance the way Navisworks' own batch XML does.</summary>
        public static string FormatTolerance(double valueInUnits)
        {
            return valueInUnits.ToString("0.0000000000", CultureInfo.InvariantCulture);
        }
    }
}
