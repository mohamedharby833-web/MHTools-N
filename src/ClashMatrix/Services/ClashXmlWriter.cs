using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using ClashMatrix.Model;

namespace ClashMatrix.Services
{
    /// <summary>
    /// Writes the Navisworks clash-test exchange XML (the file imported through
    /// Clash Detective > Batch > Import), matching Navisworks' own batch format.
    /// Search sets are flat, so locators are "lcop_selection_set_tree/{Name}".
    /// </summary>
    public static class ClashXmlWriter
    {
        public static void Write(string path, string batchName,
            IList<ClashTestDefinition> tests, string units)
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
                {
                    WriteClashTest(w, t, units);
                }
                w.WriteEndElement(); // clashtests

                w.WriteEndElement(); // batchtest
                w.WriteEndElement(); // exchange
                w.WriteEndDocument();
            }
        }

        private static void WriteClashTest(XmlWriter w, ClashTestDefinition t, string units)
        {
            double tolerance = UnitConverter.MmToUnit(t.ToleranceMm, units);

            w.WriteStartElement("clashtest");
            w.WriteAttributeString("name", t.Name);
            w.WriteAttributeString("test_type", "hard");
            w.WriteAttributeString("status", "ok");
            w.WriteAttributeString("tolerance", UnitConverter.FormatTolerance(tolerance));
            w.WriteAttributeString("merge_composites", "1");
            w.WriteAttributeString("priority", t.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));

            w.WriteStartElement("default_assignee");
            w.WriteAttributeString("display_name", "");
            w.WriteEndElement();

            w.WriteStartElement("linkage");
            w.WriteAttributeString("mode", "none");
            w.WriteEndElement();

            WriteSelection(w, "left", "lcop_selection_set_tree/" + t.LeftName);
            WriteSelection(w, "right", "lcop_selection_set_tree/" + t.RightName);

            w.WriteStartElement("rules");
            w.WriteEndElement();

            w.WriteEndElement(); // clashtest
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
