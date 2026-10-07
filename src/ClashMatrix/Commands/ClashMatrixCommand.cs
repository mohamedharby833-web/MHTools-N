using Autodesk.Navisworks.Api;
using ClashMatrix.UI;

namespace ClashMatrix.Commands
{
    public static class ClashMatrixCommand
    {
        public static void Run(string[] parameters)
        {
            // The command runs on Navisworks' UI thread. A document may be null
            // (nothing open) - XML generation still works then; creating tests does not.
            Document document = null;
            try
            {
                document = Application.ActiveDocument;
            }
            catch
            {
                document = null;
            }

            var window = new ClashMatrixWindow(document);
            window.ShowDialog();
        }
    }
}
