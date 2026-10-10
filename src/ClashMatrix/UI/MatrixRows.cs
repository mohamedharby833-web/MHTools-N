using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using ClashMatrix.Model;
using ClashMatrix.Services;

namespace ClashMatrix.UI
{
    /// <summary>Base for an editable table row: raises PropertyChanged, and tells the window it changed something.</summary>
    public abstract class RowBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Called after the user changed this row (the window refreshes what depends on it).</summary>
        public Action<RowBase> Edited { get; set; }

        protected void Raise(params string[] names)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h == null) return;
            foreach (string n in names) h(this, new PropertyChangedEventArgs(n));
        }

        protected void Changed()
        {
            Action<RowBase> edited = Edited;
            if (edited != null) edited(this);
        }

        /// <summary>Re-reads every value (after an edit elsewhere).</summary>
        public void Refresh()
        {
            Raise("");
        }
    }

    /// <summary>One search set in the Search Sets tab.</summary>
    public sealed class SetRow : RowBase
    {
        private readonly MatrixParseResult _matrix;
        private string _name;
        private string _status = "";
        private string _items = "";

        public SetRow(MatrixParseResult matrix, string name)
        {
            _matrix = matrix;
            _name = name;
        }

        public string Key { get { return _name; } }

        public bool Include
        {
            get { return _matrix.IsIncluded(_name); }
            set
            {
                if (value == Include) return;
                if (value) _matrix.ExcludedSets.Remove(_name);
                else _matrix.ExcludedSets.Add(_name);
                Raise("Include");
                Changed();
            }
        }

        public string Name
        {
            get { return _name; }
            set
            {
                if (_matrix.RenameSet(_name, value))
                {
                    _name = value.Trim();
                    Changed();
                }
                Raise("Name");
            }
        }

        public string Discipline
        {
            get { return MatrixLayout.DisciplineCode(MatrixLayout.DisciplineOf(_matrix, _name)); }
        }

        public string Categories
        {
            get { return string.Join(", ", _matrix.GetMembers(_name)); }
            set
            {
                _matrix.SetCategories(_name, new[] { value ?? "" });
                Raise("Categories");
                Changed();
            }
        }

        public string Files
        {
            get
            {
                List<string> files;
                if (!_matrix.SetModels.TryGetValue(_name, out files)) return "all models";
                return files.Count == 1 ? "1 file" : files.Count + " files";
            }
        }

        public string FilesTip
        {
            get
            {
                List<string> files;
                if (!_matrix.SetModels.TryGetValue(_name, out files)) return "Searches every model.";
                return files.Count == 0 ? "No model of this discipline is loaded." : string.Join("\n", files);
            }
        }

        /// <summary>"In model", "New", "Excluded" - set by the window.</summary>
        public string Status
        {
            get { return _status; }
            set { _status = value; Raise("Status"); }
        }

        /// <summary>Item count once the set exists.</summary>
        public string Items
        {
            get { return _items; }
            set { _items = value; Raise("Items"); }
        }
    }

    /// <summary>One clash test in the Clash Tests tab.</summary>
    public sealed class TestRow : RowBase
    {
        public static readonly string[] PriorityChoices = { "High", "Medium", "Low", "None" };

        private readonly MatrixParseResult _matrix;
        private readonly Func<double> _defaultTolerance;
        private string _status = "";

        public TestRow(MatrixParseResult matrix, ClashTestDefinition test, Func<double> defaultTolerance)
        {
            _matrix = matrix;
            Test = test;
            _defaultTolerance = defaultTolerance;
        }

        public ClashTestDefinition Test { get; private set; }

        /// <summary>False when one of its sets is excluded: the test cannot be created then.</summary>
        public bool SetsIncluded
        {
            get { return _matrix.IsIncluded(Test.LeftName) && _matrix.IsIncluded(Test.RightName); }
        }

        public bool Include
        {
            get { return Test.Include && SetsIncluded; }
            set
            {
                if (!SetsIncluded) { Raise("Include"); return; }
                Test.Include = value;
                Raise("Include");
                Changed();
            }
        }

        public string Name
        {
            get { return Test.Name; }
            set
            {
                string name = (value ?? "").Trim();
                if (name.Length > 0 && name != Test.Name)
                {
                    Test.Name = name;
                    Test.CustomName = true;
                    Changed();
                }
                Raise("Name");
            }
        }

        public string LeftName { get { return Test.LeftName; } }
        public string RightName { get { return Test.RightName; } }

        public string Tolerance
        {
            get
            {
                double mm = Test.ToleranceFromSheet ? Test.ToleranceMm : _defaultTolerance();
                return double.IsNaN(mm) ? "?" : mm.ToString("0.###", CultureInfo.InvariantCulture);
            }
            set
            {
                double mm;
                if (UnitConverter.TryParseMm(value, out mm))
                {
                    Test.ToleranceMm = mm;
                    Test.ToleranceFromSheet = true;
                    Changed();
                }
                Raise("Tolerance", "ToleranceSource");
            }
        }

        /// <summary>"standard" / "sheet" when the test carries its own, "default" when it follows the dialog.</summary>
        public string ToleranceSource
        {
            get { return Test.ToleranceFromSheet ? "own" : "default"; }
        }

        public IEnumerable<string> Priorities { get { return PriorityChoices; } }

        public string Priority
        {
            get
            {
                switch (Test.Priority)
                {
                    case 1: return "High";
                    case 2: return "Medium";
                    case 3: return "Low";
                    default: return "None";
                }
            }
            set
            {
                int p = Array.IndexOf(PriorityChoices, value) + 1;
                if (p == 4) p = 0;
                if (p < 0 || p == Test.Priority) return;
                Test.Priority = p;
                Raise("Priority");
                Changed();
            }
        }

        /// <summary>"In model", "New", "Rename", "Set excluded" - set by the window.</summary>
        public string Status
        {
            get { return _status; }
            set { _status = value; Raise("Status"); }
        }
    }
}
