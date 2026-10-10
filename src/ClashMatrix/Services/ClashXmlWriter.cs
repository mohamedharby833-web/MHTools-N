using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;
using ClashMatrix.Model;

namespace ClashMatrix.Services
{
    /// <summary>
    /// Writes the Navisworks clash-test exchange XML (the file imported through
    /// Clash Detective > Batch > Import), matching Navisworks' own batch format.
    /// Locators point at the add-in's set folder:
    /// "lcop_selection_set_tree/Clash Matrix/{Name}".
    /// </summary>
    public static class ClashXmlWriter
    {
        /// <param name="units">An XML unit name from <see cref="UnitConverter.XmlUnit"/>.</param>
        /// <param name="defaultToleranceMm">Tolerance for tests without one from the Tolerance sheet.</param>
        public static void Write(string path, string batchName, IList<ClashTestDefinition> tests,
            string units, double defaultToleranceMm)
        {
            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(false)
            };

            using (XmlWriter w = XmlWriter.Create(path, settings))
            {
                w.WriteStartDocument();

                w.WriteStartElement("exchange");
                w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
                w.WriteAttributeString("xsi", "noNamespaceSchemaLocation", null,
                    "http://download.autodesk.com/us/navisworks/schemas/nw-exchange-12.0.xsd");
                w.WriteAttributeString("units", units);
                w.WriteAttributeString("filename", "");
                w.WriteAttributeString("filepath", "");

                w.WriteStartElement("batchtest");
                w.WriteAttributeString("name", batchName);
                w.WriteAttributeString("internal_name", batchName);
                w.WriteAttributeString("units", units);

                w.WriteStartElement("clashtests");
                foreach (ClashTestDefinition t in tests)
                    WriteClashTest(w, t, units, defaultToleranceMm);
                w.WriteEndElement(); // clashtests

                w.WriteEndElement(); // batchtest
                w.WriteEndElement(); // exchange
                w.WriteEndDocument();
            }
        }

        private static void WriteClashTest(XmlWriter w, ClashTestDefinition t, string units, double defaultToleranceMm)
        {
            double toleranceMm = t.ToleranceFromSheet ? t.ToleranceMm : defaultToleranceMm;
            double tolerance = UnitConverter.MmToXmlUnit(toleranceMm, units);

            w.WriteStartElement("clashtest");
            w.WriteAttributeString("name", t.Name);
            w.WriteAttributeString("test_type", "hard");
            w.WriteAttributeString("status", "ok");
            w.WriteAttributeString("tolerance", UnitConverter.FormatTolerance(tolerance));
            w.WriteAttributeString("merge_composites", "1");
#if NAVIS2026 || NAVIS2027
            // Clash test priority exists from Navisworks 2026; older importers do not know it.
            w.WriteAttributeString("priority", t.Priority.ToString(CultureInfo.InvariantCulture));
#endif

            w.WriteStartElement("default_assignee");
            w.WriteAttributeString("display_name", "");
            w.WriteEndElement();

            w.WriteStartElement("linkage");
            w.WriteAttributeString("mode", "none");
            w.WriteEndElement();

            WriteSelection(w, "left", Locator(t.LeftName));
            WriteSelection(w, "right", Locator(t.RightName));

            w.WriteStartElement("rules");
            w.WriteEndElement();

            w.WriteEndElement(); // clashtest
        }

        /// <summary>Locator of a set in the add-in's folder. '/' separates locator levels, so it cannot be part of a name.</summary>
        public static string Locator(string setName)
        {
            return "lcop_selection_set_tree/" + NavisClashCreator.FolderName + "/" + setName;
        }

        /// <summary>Set names that cannot be written as a locator.</summary>
        public static List<string> FindUnsafeNames(IEnumerable<string> setNames)
        {
            var bad = new List<string>();
            foreach (string name in setNames)
            {
                if (name.IndexOf('/') >= 0)
                    bad.Add(name);
            }
            return bad;
        }

        private static void WriteSelection(XmlWriter w, string side, string locator)
        {
            w.WriteStartElement(side);
            w.WriteStartElement("clashselection");
            w.WriteAttributeString("selfintersect", "0");
            w.WriteAttributeString("primtypes", "1");
            w.WriteStartElement("locator");
            w.WriteString(locator);
            w.WriteEndElement(); // locator
            w.WriteEndElement(); // clashselection
            w.WriteEndElement(); // side
        }
    }
}
