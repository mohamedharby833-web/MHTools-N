using System;
using System.Collections.Generic;
using System.Drawing;
using Autodesk.Navisworks.Api;
using MHToolsN.Services;

namespace MHToolsN.Bridge
{
    /// <summary>A picture of what the Navisworks view shows now, for the agent to look at.</summary>
    internal static class ViewMethods
    {
        public static object Capture(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            int width = BridgeWire.Int(args, "width", 1024, 200, 1920);
            int height = BridgeWire.Int(args, "height", 768, 150, 1440);
            if (doc.ActiveView == null) throw new BridgeException("NO_VIEW", "Navisworks has no active 3D view.");

            byte[] jpeg;
            int w, h;
            using (Bitmap bitmap = doc.ActiveView.GenerateImage(ImageGenerationStyle.ScenePlusOverlay, width, height, true))
            {
                if (bitmap == null) throw new BridgeException("FAILED", "Navisworks made no picture of the view.");
                jpeg = ClashResultsReader.Jpeg(bitmap);
                w = bitmap.Width;
                h = bitmap.Height;
            }

            double mmPerUnit = UnitConverter.MillimetresPerUnit(doc.Units);
            var result = new Dictionary<string, object> { { "selectedItems", doc.CurrentSelection.SelectedItems.Count } };
            Viewpoint vp = ModelMethods.Safe(() => doc.CurrentViewpoint.Value);
            if (vp != null)
                result["cameraMeters"] = ModelMethods.Point(vp.Position, mmPerUnit);
            result["image"] = new Dictionary<string, object>
            {
                { "mimeType", "image/jpeg" }, { "width", w }, { "height", h }, { "base64", Convert.ToBase64String(jpeg) }
            };
            return result;
        }
    }
}
