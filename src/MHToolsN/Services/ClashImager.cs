using System;
using System.Collections.Generic;
using System.Drawing;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace MHToolsN.Services
{
    /// <summary>
    /// A picture of each clash that shows the clash: the view zoomed onto it from a
    /// corner above, item 1 red and item 2 green, everything else faded.
    ///
    /// Clash Detective's own picture (TestsImageForResult) keeps the current view when a
    /// clash has no saved viewpoint - the whole building, with the clash a dot in it.
    /// A clash WITH a saved viewpoint keeps Clash Detective's picture: someone chose that
    /// view.
    ///
    /// Works on the main view and puts it back - the view, the colours, the fading -
    /// when disposed. Temporary colours and transparency the user had on the two items
    /// of a clash are reset with them.
    /// </summary>
    internal sealed class ClashImager : IDisposable
    {
        private const double Fade = 0.85;

        private readonly Document _document;
        private readonly DocumentClashTests _tests;
        private readonly Viewpoint _original;
        private readonly double _minSize;
        private readonly List<ModelItem> _roots = new List<ModelItem>();
        private bool _faded;

        /// <param name="mmPerUnit">Model units, so a clash is shown with at least 2 m around it.</param>
        public ClashImager(Document document, DocumentClashTests tests, double mmPerUnit)
        {
            _document = document;
            _tests = tests;
            _minSize = 2000.0 / Math.Max(1e-9, mmPerUnit);
            try
            {
                _original = document.ActiveView != null ? document.ActiveView.CreateViewpointCopy() : null;
            }
            catch (Exception ex)
            {
                Watchdog.Log("Clash images: no view to work in - " + ex.Message);
            }
        }

        public Bitmap Image(ClashResult r, int width, int height)
        {
            if (_original == null || r.HasSavedViewpoint)
                return _tests.TestsImageForResult(r, ImageGenerationStyle.ScenePlusOverlay, width, height);

            BoundingBox3D box = Around(r);
            if (box == null)
                return _tests.TestsImageForResult(r, ImageGenerationStyle.ScenePlusOverlay, width, height);

            FadeAll();
            var one = new List<ModelItem>();
            var two = new List<ModelItem>();
            if ((r.CompositeItem1 ?? r.Item1) != null) one.Add(r.CompositeItem1 ?? r.Item1);
            if ((r.CompositeItem2 ?? r.Item2) != null) two.Add(r.CompositeItem2 ?? r.Item2);
            var both = new List<ModelItem>(one);
            both.AddRange(two);
            try
            {
                Viewpoint vp = _original.CreateCopy();
                // From the south-east, above: walls, floors and what runs through them all show.
                Vector3D size = box.Size;
                double reach = Math.Max(size.X, Math.Max(size.Y, size.Z)) * 3;
                Point3D c = box.Center;
                vp.Position = new Point3D(c.X + reach, c.Y - reach, c.Z + reach * 0.8);
                vp.PointAt(c);
                vp.ZoomBox(box);
                _document.ActiveView.CopyViewpointFrom(vp, ViewChange.JumpCut);

                _document.Models.OverrideTemporaryTransparency(both, 0);
                if (one.Count > 0) _document.Models.OverrideTemporaryColor(one, Autodesk.Navisworks.Api.Color.Red);
                if (two.Count > 0) _document.Models.OverrideTemporaryColor(two, Autodesk.Navisworks.Api.Color.Green);

                return _document.ActiveView.GenerateImage(ImageGenerationStyle.ScenePlusOverlay, width, height, true);
            }
            finally
            {
                _document.Models.ResetTemporaryMaterials(both);
            }
        }

        /// <summary>The clash's box, grown to at least 2 m each way so its surroundings show.</summary>
        private BoundingBox3D Around(ClashResult r)
        {
            BoundingBox3D box = r.BoundingBox;
            if (box == null || box.IsEmpty) box = r.ViewBounds;
            if (box == null || box.IsEmpty) return null;

            Point3D c = box.Center;
            Vector3D s = box.Size;
            double hx = Math.Max(s.X, _minSize) / 2, hy = Math.Max(s.Y, _minSize) / 2, hz = Math.Max(s.Z, _minSize) / 2;
            return new BoundingBox3D(new Point3D(c.X - hx, c.Y - hy, c.Z - hz), new Point3D(c.X + hx, c.Y + hy, c.Z + hz));
        }

        /// <summary>Everything see-through once, so the two items of each clash stand out.</summary>
        private void FadeAll()
        {
            if (_faded) return;
            _faded = true;
            try
            {
                foreach (Autodesk.Navisworks.Api.Model model in _document.Models)
                    if (model.RootItem != null) _roots.Add(model.RootItem);
                _document.Models.OverrideTemporaryTransparency(_roots, Fade);
            }
            catch (Exception ex)
            {
                Watchdog.Log("Clash images: could not fade the model - " + ex.Message);
            }
        }

        public void Dispose()
        {
            try
            {
                if (_roots.Count > 0) _document.Models.ResetTemporaryMaterials(_roots);
                if (_original != null) _document.ActiveView.CopyViewpointFrom(_original, ViewChange.JumpCut);
            }
            catch (Exception ex)
            {
                Watchdog.Log("Clash images: could not restore the view - " + ex.Message);
            }
        }
    }
}
