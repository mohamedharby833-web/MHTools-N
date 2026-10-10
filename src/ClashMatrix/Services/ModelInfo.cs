using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;

namespace ClashMatrix.Services
{
    /// <summary>
    /// One appended model file: its Revit categories and its discipline. The
    /// discipline is guessed (<see cref="Disciplines.Guess"/>) unless the user chose
    /// one, which is remembered per file name (<see cref="DisciplineStore"/>).
    /// </summary>
    public sealed class ModelInfo : INotifyPropertyChanged
    {
        private string _discipline;

        public ModelInfo(string fileName, ICollection<string> categories)
        {
            FileName = fileName;
            Categories = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
            Guessed = Disciplines.Guess(fileName, Categories);
            _discipline = DisciplineStore.Get(fileName) ?? Guessed;
        }

        /// <summary>The model's file name (no folder) - how sets and the store refer to it.</summary>
        public string FileName { get; private set; }

        /// <summary>Its Revit categories, fittings already folded into their parents.</summary>
        public HashSet<string> Categories { get; private set; }

        /// <summary>What the file name / content suggests.</summary>
        public string Guessed { get; private set; }

        /// <summary>The discipline in use: the user's choice, else <see cref="Guessed"/>. Setting it remembers it.</summary>
        public string Discipline
        {
            get { return _discipline; }
            set
            {
                if (string.IsNullOrEmpty(value) || value == _discipline)
                    return;
                _discipline = value;
                DisciplineStore.Set(FileName, value == Guessed ? null : value);
                OnPropertyChanged("Discipline");
                OnPropertyChanged("CategorySummary");
            }
        }

        public string CategorySummary
        {
            get { return Categories.Count + " categories"; }
        }

        public IEnumerable<string> Choices
        {
            get { return Disciplines.ModelChoices; }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// The user's discipline choices, per model file name, in
    /// %AppData%\ClashMatrix\ModelDisciplines.txt ("file name TAB code" per line).
    /// Best effort: a store that cannot be read or written just means guessing again.
    /// </summary>
    public static class DisciplineStore
    {
        private static Dictionary<string, string> _choices;

        private static string StorePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ClashMatrix", "ModelDisciplines.txt");
            }
        }

        public static string Get(string fileName)
        {
            string code;
            return Load().TryGetValue(fileName ?? "", out code) ? code : null;
        }

        /// <summary>Remembers a choice; null forgets it (back to guessing).</summary>
        public static void Set(string fileName, string code)
        {
            Dictionary<string, string> choices = Load();
            if (code == null) choices.Remove(fileName);
            else choices[fileName] = code;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
                File.WriteAllLines(StorePath, choices.OrderBy(kv => kv.Key).Select(kv => kv.Key + "\t" + kv.Value), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Save model disciplines", ex);
            }
        }

        private static Dictionary<string, string> Load()
        {
            if (_choices != null)
                return _choices;

            _choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(StorePath))
                {
                    foreach (string line in File.ReadAllLines(StorePath, Encoding.UTF8))
                    {
                        string[] f = line.Split('\t');
                        if (f.Length == 2 && Disciplines.ModelChoices.Contains(f[1]))
                            _choices[f[0]] = f[1];
                    }
                }
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Load model disciplines", ex);
            }
            return _choices;
        }
    }
}
