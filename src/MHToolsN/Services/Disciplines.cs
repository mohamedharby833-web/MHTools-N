using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MHToolsN.Services
{
    /// <summary>
    /// The disciplines sets are split by: AR (architecture), ST (structure), MEC
    /// (mechanical: HVAC, plumbing, drainage, fire fighting), EL (electrical), ICT
    /// (information and communication: data, telephone, security, nurse call, audio
    /// visual), and GEN for a model whose discipline is not known. Two choices split a
    /// file by category instead: MEP (one file with all services -> MEC / EL / ICT) and
    /// EL+ICT (an electrical file that also holds the ICT devices -> EL / ICT).
    ///
    /// A set is one discipline's elements of one category ("AR Walls", "ST Walls"),
    /// found only in the models of that discipline.
    /// </summary>
    public static class Disciplines
    {
        public const string Architecture = "AR";
        public const string Structure = "ST";
        public const string Mechanical = "MEC";
        public const string Electrical = "EL";
        public const string Ict = "ICT";
        public const string ElectricalAndIct = "EL+ICT";
        public const string Services = "MEP";
        public const string General = "GEN";

        /// <summary>The choices offered for a model, in display order.</summary>
        public static readonly string[] ModelChoices =
            { Architecture, Structure, Mechanical, Electrical, Ict, ElectricalAndIct, Services, General };

        // ---------------------------------------------------------- category families
        //
        // A category that explicitly belongs to one discipline is only ever put in a set
        // of that discipline: no "EL Ducts", "ICT Pipes", "MEC Cable Trays" or "AR
        // Plumbing Fixtures". Such elements in another discipline's file are left out
        // (and reported). Neutral categories (Generic Models, Specialty Equipment, ...)
        // go to the file's own discipline.

        /// <summary>Mechanical: HVAC, plumbing, drainage, fire fighting.</summary>
        private static readonly HashSet<string> MechanicalCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Ducts", "Duct Fittings", "Duct Accessories", "Duct Insulations", "Duct Linings", "Duct Placeholders",
            "Flex Ducts", "Air Terminals", "Pipes", "Pipe Fittings", "Pipe Accessories", "Pipe Insulations",
            "Pipe Placeholders", "Flex Pipes", "Sprinklers", "Mechanical Equipment", "Mechanical Control Devices",
            "Plumbing Fixtures", "Plumbing Equipment", "MEP Fabrication Ductwork", "MEP Fabrication Pipework",
            "HVAC Zones"
        };

        /// <summary>Electrical (not containment): power, lighting, fire alarm.</summary>
        private static readonly HashSet<string> ElectricalCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Electrical Equipment", "Electrical Fixtures", "Lighting Fixtures", "Lighting Devices",
            "Fire Alarm Devices", "Wires", "Electrical Circuits", "Electrical Analytical Loads"
        };

        /// <summary>Cable containment: electrical or ICT, never mechanical, architectural or structural.</summary>
        private static readonly HashSet<string> ContainmentCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cable Trays", "Cable Tray Fittings", "Cable Tray Runs", "Conduits", "Conduit Fittings", "Conduit Runs",
            "MEP Fabrication Containment"
        };

        /// <summary>ICT: data, telephone, security, nurse call, audio-visual.</summary>
        private static readonly HashSet<string> IctCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Communication Devices", "Data Devices", "Telephone Devices", "Security Devices",
            "Nurse Call Devices", "Audio Visual Devices"
        };

        /// <summary>Services in general (any of MEC / EL / ICT, never AR or ST).</summary>
        private static readonly HashSet<string> ServicesCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MEP Fabrication Hangers", "MEP Ancillary Framing"
        };

        /// <summary>The heading a discipline is grouped under (banners, ordering, test names).</summary>
        public static string Heading(string code)
        {
            switch (code)
            {
                case Architecture: return "ARCHITECTURE - AR";
                case Structure: return "STRUCTURE - ST";
                case Mechanical: return "MECHANICAL - MEC";
                case Electrical: return "ELECTRICAL - EL";
                case Ict: return "INFORMATION & COMMUNICATION - ICT";
                default: return "GENERAL - GEN";
            }
        }

        /// <summary>
        /// True when a set of <paramref name="setDiscipline"/> may hold this category:
        /// MEC takes no electrical, containment or ICT category; EL and ICT no mechanical one
        /// (they may hold each other's categories); AR and ST no services
        /// category at all. GEN takes anything.
        /// </summary>
        public static bool Allows(string setDiscipline, string category)
        {
            bool mec = MechanicalCategories.Contains(category);
            bool el = ElectricalCategories.Contains(category);
            bool box = ContainmentCategories.Contains(category);
            bool ict = IctCategories.Contains(category);
            bool services = ServicesCategories.Contains(category);
            switch (setDiscipline)
            {
                case Mechanical: return !el && !box && !ict;
                case Electrical: return !mec;
                case Ict: return !mec;
                case Architecture:
                case Structure: return !mec && !el && !box && !ict && !services;
                default: return true;
            }
        }

        /// <summary>
        /// The set discipline of a category found in a model of the given discipline -
        /// the model's own, except that an MEP file's elements go to MEC, EL or ICT and
        /// an EL+ICT file's to EL or ICT, by category - or null when the category belongs
        /// to another discipline than that (pipes in the electrical file): those
        /// elements are left out of every set. With <paramref name="bestPractice"/>
        /// false ("All categories") nothing is left out: "AR Plumbing Fixtures" and "MEC
        /// Cable Trays" are made as the files hold them.
        /// </summary>
        public static string ForCategory(string modelDiscipline, string category, bool bestPractice = true)
        {
            string target;
            switch (modelDiscipline)
            {
                case Services:
                    target = IctCategories.Contains(category) ? Ict
                           : ElectricalCategories.Contains(category) || ContainmentCategories.Contains(category) ? Electrical
                           : Mechanical;
                    break;
                case ElectricalAndIct:
                    target = IctCategories.Contains(category) ? Ict : Electrical;
                    break;
                case null:
                case "":
                    target = General;
                    break;
                default:
                    target = modelDiscipline;
                    break;
            }
            return !bestPractice || Allows(target, category) ? target : null;
        }

        /// <summary>
        /// True when a file of <paramref name="modelDiscipline"/> sends its
        /// <paramref name="category"/> elements to a set of <paramref name="setDiscipline"/>.
        /// A set searches exactly those files, so an element is never in two sets: the
        /// pipes of an MEP file are in "MEC Pipes", never also in "EL Pipes". "BS" /
        /// "SERVICES" (an imported matrix's all-services group) takes any services set.
        /// </summary>
        public static bool Routes(string modelDiscipline, string category, string setDiscipline, bool bestPractice = true)
        {
            string target = ForCategory(modelDiscipline, category, bestPractice);
            if (target == null)
                return false;
            if (setDiscipline == "BS" || setDiscipline == "SERVICES")
                return target == Mechanical || target == Electrical || target == Ict;
            return target == setDiscipline;
        }

        /// <summary>The word the built-in standard groups this discipline's rows under (null: any row).</summary>
        public static string StandardGroup(string setDiscipline)
        {
            switch (setDiscipline)
            {
                case Architecture: return "ARCHITECTURE";
                case Structure: return "STRUCTURE";
                case Mechanical:
                case Electrical:
                case Ict: return "SERVICES";
                default: return null;
            }
        }

        // Discipline codes in a file name, ISO 19650 style: a field such as "1A0",
        // "1S0", "1M0", "1EA", "A", "ST", "MEP" between - _ . or space.
        private static readonly Regex FieldCode = new Regex(@"^\d?([ASMEPDFH])[0-9A-Z]?$", RegexOptions.Compiled);

        /// <summary>
        /// Guesses a model's discipline. A word in the file name (ARCH, STR, MEP, ICT,
        /// ...) decides. A one-letter role code ("1M0", "1EA", "1DS") decides only when
        /// the file's content agrees - "D" may be drainage or data systems - and the
        /// content decides otherwise. GEN when nothing says.
        /// </summary>
        public static string Guess(string fileName, ICollection<string> categories)
        {
            string fromWord, fromLetter;
            FromFileName(fileName, out fromWord, out fromLetter);
            if (fromWord != null)
                return fromWord;

            string fromContent = FromCategories(categories);
            if (fromLetter != null && fromContent == null)
                return fromLetter;
            if (fromLetter != null && Agrees(fromLetter, fromContent))
            {
                // The content is the finer answer for a file mixing services.
                return fromContent == Services || fromContent == ElectricalAndIct ? fromContent : fromLetter;
            }
            return fromContent ?? General;
        }

        private static bool Agrees(string fromLetter, string fromContent)
        {
            if (fromLetter == fromContent)
                return true;
            // A services letter is fine for a mixed services file, and AR / ST content
            // guesses are weak (they are what is left when no services categories show).
            if (fromContent == Services)
                return fromLetter == Mechanical || fromLetter == Electrical;
            if (fromContent == ElectricalAndIct || fromContent == Ict)
                return fromLetter == Electrical;
            return fromContent == Architecture || fromContent == Structure
                ? fromLetter == Architecture || fromLetter == Structure
                : false;
        }

        private static void FromFileName(string fileName, out string word, out string letter)
        {
            word = null;
            letter = null;
            if (string.IsNullOrEmpty(fileName))
                return;

            string[] fields = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant()
                .Split(new[] { '-', '_', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // Whole words: "...-ARCH-...", "...-STR-...", "...-MEP-...", "...-ICT-...".
            foreach (string f in fields)
            {
                switch (f)
                {
                    case "AR": case "ARC": case "ARCH": word = Architecture; return;
                    case "ST": case "STR": case "STRUC": case "STRUCT": word = Structure; return;
                    case "MEP": word = Services; return;
                    case "ME": case "MEC": case "MECH": case "PL": case "PLU": case "FF": case "HVAC": word = Mechanical; return;
                    case "EL": case "ELE": case "ELEC": word = Electrical; return;
                    case "ICT": case "IT": case "ELV": case "TEL": case "TELECOM": case "COM": case "SEC": word = Ict; return;
                }
            }

            // Role codes: "1A0" -> A, "1S0" -> S, "1M0" -> M, "1EA" -> E, "1DS" -> D.
            foreach (string f in fields)
            {
                Match m = FieldCode.Match(f);
                if (!m.Success)
                    continue;
                switch (m.Groups[1].Value)
                {
                    case "A": letter = Architecture; return;
                    case "S": letter = Structure; return;
                    case "E": letter = Electrical; return;
                    case "M":
                    case "P":
                    case "D":
                    case "F":
                    case "H": letter = Mechanical; return;
                }
            }
        }

        /// <summary>The discipline the categories point to, or null when the file holds none.</summary>
        public static string FromCategories(ICollection<string> categories)
        {
            if (categories == null || categories.Count == 0)
                return null;

            int structural = categories.Count(c => c.StartsWith("Structural ", StringComparison.OrdinalIgnoreCase));
            int electrical = categories.Count(c => ElectricalCategories.Contains(c) || ContainmentCategories.Contains(c));
            int ict = categories.Count(c => IctCategories.Contains(c));
            int mechanical = categories.Count(c => MechanicalCategories.Contains(c));
            int wiring = electrical + ict;

            if (mechanical + wiring >= 2)
            {
                if (mechanical > 0 && wiring > 0 && Math.Min(mechanical, wiring) * 3 >= Math.Max(mechanical, wiring))
                    return Services;
                if (mechanical > wiring)
                    return Mechanical;
                if (electrical > 0 && ict > 0 && Math.Min(electrical, ict) * 3 >= Math.Max(electrical, ict))
                    return ElectricalAndIct;
                return ict > electrical ? Ict : Electrical;
            }
            return structural >= 2 ? Structure : Architecture;
        }
    }
}
