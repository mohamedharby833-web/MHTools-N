using System;
using System.Globalization;
using Autodesk.Navisworks.Api;

namespace ClashMatrix.Services
{
    /// <summary>
    /// Millimetre-to-document-unit conversion, the unit names used by the Navisworks
    /// exchange XML, and the tolerance text format (10 decimal places, invariant culture).
    /// </summary>
    public static class UnitConverter
    {
        /// <summary>Millimetres in one of the given unit. Covers every <see cref="Units"/> value.</summary>
        public static double MillimetresPerUnit(Units units)
        {
            switch (units)
            {
                case Units.Kilometers: return 1000000.0;
                case Units.Meters: return 1000.0;
                case Units.Centimeters: return 10.0;
                case Units.Millimeters: return 1.0;
                case Units.Micrometers: return 0.001;
                case Units.Miles: return 1609344.0;
                case Units.Yards: return 914.4;
                case Units.Feet: return 304.8;
                case Units.Inches: return 25.4;
                case Units.Mils: return 0.0254;
                case Units.Microinches: return 0.0000254;
                default:
                    throw new NotSupportedException("Unsupported document unit: " + units);
            }
        }

        /// <summary>Converts millimetres to the document's length unit (for the .NET API).</summary>
        public static double MmToUnit(double mm, Units units)
        {
            return mm / MillimetresPerUnit(units);
        }

        /// <summary>
        /// The unit the exchange XML is written in. The XML carries its own unit, so
        /// a document unit the format may not name (km, yd, mi, µm, mil...) is written
        /// in metres instead - the tolerance is converted to match.
        /// </summary>
        public static string XmlUnit(Units? units)
        {
            if (units == null)
                return "m";
            switch (units.Value)
            {
                case Units.Meters: return "m";
                case Units.Centimeters: return "cm";
                case Units.Millimeters: return "mm";
                case Units.Feet: return "ft";
                case Units.Inches: return "in";
                default: return "m";
            }
        }

        /// <summary>Converts millimetres to one of the XML unit names from <see cref="XmlUnit"/>.</summary>
        public static double MmToXmlUnit(double mm, string xmlUnit)
        {
            switch (xmlUnit)
            {
                case "mm": return mm;
                case "cm": return mm / 10.0;
                case "m": return mm / 1000.0;
                case "in": return mm / 25.4;
                case "ft": return mm / 304.8;
                default:
                    throw new NotSupportedException("Unsupported XML unit: " + xmlUnit);
            }
        }

        /// <summary>Formats a tolerance the way Navisworks' own batch XML does.</summary>
        public static string FormatTolerance(double valueInUnits)
        {
            return valueInUnits.ToString("0.0000000000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses a millimetre value typed by the user, accepting either a '.' or a ','
        /// decimal separator. Zero is allowed (a touching clash still counts).
        /// </summary>
        public static bool TryParseMm(string text, out double mm)
        {
            mm = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            string s = text.Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out mm)
                && mm >= 0 && !double.IsInfinity(mm);
        }
    }
}
