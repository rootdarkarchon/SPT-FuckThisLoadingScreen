using System;
using System.IO;
using System.Linq;
using FuckThisLoadingScreen;
using UnityEditor;
using UnityEngine;

internal static class TextureInventoryValidation
{
    internal static void Run(Action<bool, string> check, string project)
    {
        var plain = new Texture2D(64, 64, TextureFormat.RGBA32, true) { name = "inventory, \"plain\"" };
        Texture2D streamed = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/ftls-streaming-lifetime.png");
        int oldLimit = QualitySettings.globalTextureMipmapLimit;
        string output = Path.Combine(project, "texture-inventory-validation", Guid.NewGuid().ToString("N"));
        try
        {
            TextureInventory.Describe(plain, out long bytes, out string key);
            check(bytes == 4L * (4096 + 1024 + 256 + 64 + 16 + 4 + 1), "Full mip chain estimate sums every RGBA32 mip level");
            check(key.StartsWith("Texture2D|false|", StringComparison.Ordinal), "Grouping key separates type and streaming state");

            string summary = TextureInventory.Dump(output, "fixture");
            string csv = Directory.GetFiles(output, "*-textures.csv").Single();
            string[] lines = File.ReadAllLines(csv);
            check(lines[0] == TextureInventory.Header, "Inventory CSV starts with the documented header");
            check(lines.Any(l => l.Contains("\"inventory, \"\"plain\"\"\"")) && lines.Any(l => l.Contains("\"" + streamed.name + "\"") && l.Contains(",true,")),
                "Inventory lists runtime and streamed asset textures with quoted names");
            check(File.ReadAllText(Directory.GetFiles(output, "*-summary.txt").Single()).Contains("globalTextureMipmapLimit=") && summary.Contains("textures="),
                "Summary records global limit, Unity counters and grouped totals");

            // Record Unity's width/limit semantics under a global limit for interpreting live inventories.
            QualitySettings.globalTextureMipmapLimit = 1;
            Debug.Log("FTLS-INVENTORY-SEMANTICS asset width=" + streamed.width + " active=" + streamed.activeMipmapLimit + " ignore=" + streamed.ignoreMipmapLimit +
                " group='" + streamed.mipmapLimitGroup + "'; runtime width=" + plain.width + " active=" + plain.activeMipmapLimit + " ignore=" + plain.ignoreMipmapLimit);
        }
        finally
        {
            QualitySettings.globalTextureMipmapLimit = oldLimit;
            UnityEngine.Object.DestroyImmediate(plain);
        }
    }
}
