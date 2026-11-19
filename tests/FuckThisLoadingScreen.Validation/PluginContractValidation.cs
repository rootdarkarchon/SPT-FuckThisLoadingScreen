using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FuckThisLoadingScreen;

/// <summary>Persisted contracts (config keys, plugin GUID), the legacy data move and the debug log rate limiter.</summary>
internal static class PluginContractValidation
{
    private const string Guid = "com.rootdarkarchon.fuckthisloadingscreen";

    // Persisted "section|key" -> display category. The keys must never change; the category is display only.
    private static readonly Dictionary<string, string> Contract = new Dictionary<string, string>
    {
        ["General|Enabled"] = "1. General",
        ["Loading|Integration Priority"] = "2. Loading",
        ["Loading|Extend Priority Through Loot Bundles"] = "2. Loading",
        ["Loading|Loading Frame Cap Override"] = "2. Loading",
        ["Loading|Loading Frame Limit"] = "2. Loading",
        ["Loading|Reduce Bot Profile Preload Delay"] = "2. Loading",
        ["Loading|Scoped Spawn Zone Lookup"] = "2. Loading",
        ["Loading|Skip Built-In Scene Bundle Probes"] = "2. Loading",
        ["Loading|Stop Texture Worker Before Teardown"] = "2. Loading",
        ["Scenes|Bounded As-Ready Scene Activation"] = "3. Scenes",
        ["Scenes|Scene In-Flight Limit"] = "3. Scenes",
        ["Audio|Larger Reader Batches"] = "4. Audio",
        ["Audio|Reader Batch Size"] = "4. Audio",
        ["Mip Streaming|Synchronize Decal Results"] = "5. Mip Streaming",
        ["Mip Streaming|Resync Decal Texture Arrays"] = "5. Mip Streaming",
        ["Mip Streaming|Release Icon Mip Requests"] = "5. Mip Streaming",
        ["Mip Streaming|Account For Nonstreaming Textures"] = "5. Mip Streaming",
        ["Debug|Logging"] = "9. Debug",
        ["Debug|Write Loading Reports"] = "9. Debug",
        ["Debug|Dump Loaded Textures"] = "9. Debug",
        ["Debug|Write Hideout Research Reports"] = "9. Debug",
        ["Memory|Non-Collecting Diagnostic Measurements"] = "9. Debug",
        ["Experimental|Retain Unused Streamed Mips"] = "9. Debug",
        ["Experimental|Parallel Scene Loading"] = "9. Debug",
        ["Mip Streaming|Apply Texture Quality With Streaming"] = "5. Mip Streaming",
        ["Mip Streaming|Mark Item Textures Streamable"] = "5. Mip Streaming",
        ["Mip Streaming|Streaming Budget Override"] = "5. Mip Streaming",
        ["Mip Streaming|Measure Streaming Budget"] = "5. Mip Streaming",
        ["Mip Streaming|Apply Measured Budget"] = "5. Mip Streaming",
    };

    internal static void Run(Action<bool, string> check, string project)
    {
        string root = Path.Combine(project, "plugin-contract-validation", System.Guid.NewGuid().ToString("N"));
        Migration(check, root);
        RateLimiter(check);
    }

    private static void Migration(Action<bool, string> check, string root)
    {
        string legacy = Path.Combine(root, "BepInEx", "FuckThisLoadingScreen");
        string data = Path.Combine(root, "BepInEx", "config", "FuckThisLoadingScreen");
        string reports = Path.Combine(data, "captures", "loading-reports");
        string hideout = Path.Combine(data, "captures", "hideout-research");
        string inventory = Path.Combine(data, "dumps", "texture-inventory");
        string index = Path.Combine(data, "cache", "StreamableTextureIndex.txt");

        Write(Path.Combine(legacy, "20260101-000000-aaaa.txt"), "report");
        Write(Path.Combine(legacy, "20260101-000000-aaaa-frame-cap.csv"), "frame");
        Write(Path.Combine(legacy, "20260101-000000-aaaa-scene-scheduling.csv"), "scene");
        Write(Path.Combine(legacy, "StreamableTextureIndex.txt"), "index");
        Write(Path.Combine(legacy, "HideoutResearch", "run1", "events.csv"), "events");
        Write(Path.Combine(legacy, "TextureInventory", "t-textures.csv"), "textures");

        int moved = LegacyDataMigration.Run(legacy, reports, hideout, inventory, index);
        check(moved == 6, "Legacy migration moves every top-level entry: " + moved);
        check(File.ReadAllText(Path.Combine(reports, "20260101-000000-aaaa.txt")) == "report" && File.Exists(Path.Combine(reports, "20260101-000000-aaaa-frame-cap.csv"))
            && File.Exists(Path.Combine(reports, "20260101-000000-aaaa-scene-scheduling.csv")), "Loading reports move to captures/loading-reports");
        check(File.ReadAllText(Path.Combine(hideout, "run1", "events.csv")) == "events", "Hideout research captures move with their contents");
        check(File.ReadAllText(Path.Combine(inventory, "t-textures.csv")) == "textures", "Texture inventory moves to dumps/texture-inventory");
        check(File.ReadAllText(index) == "index", "Streamable texture index moves to cache");
        check(!Directory.Exists(legacy), "Empty legacy root is removed");
        check(LegacyDataMigration.Run(legacy, reports, hideout, inventory, index) == 0, "Migration is a no-op once the legacy root is gone");

        // A destination that already exists wins; the colliding source and any unknown folder stay in place.
        Write(Path.Combine(legacy, "20260101-000000-aaaa.txt"), "stale copy");
        Write(Path.Combine(legacy, "Unknown", "keep.txt"), "keep");
        check(LegacyDataMigration.Run(legacy, reports, hideout, inventory, index) == 0, "Collisions and unknown folders are not moved");
        check(File.ReadAllText(Path.Combine(reports, "20260101-000000-aaaa.txt")) == "report", "Existing destination files are never overwritten");
        check(File.Exists(Path.Combine(legacy, "20260101-000000-aaaa.txt")) && File.Exists(Path.Combine(legacy, "Unknown", "keep.txt")), "Unmoved legacy entries and the non-empty legacy root stay");
    }

    private static void RateLimiter(Action<bool, string> check)
    {
        var limiter = new LogRateLimiter(1000);
        check(limiter.TryAcquire("a", 0, out int suppressed) && suppressed == 0, "First message of a key is allowed");
        check(!limiter.TryAcquire("a", 500, out _) && !limiter.TryAcquire("a", 999, out _), "Messages inside the interval are suppressed");
        check(limiter.TryAcquire("b", 500, out _), "Keys are limited independently");
        check(limiter.TryAcquire("a", 1000, out suppressed) && suppressed == 2, "The next message reports how many were suppressed");
        check(!limiter.TryAcquire("a", 1500, out _) && limiter.TryAcquire("a", 2000, out suppressed) && suppressed == 1, "The interval restarts at every allowed message");
    }

    /// <summary>Binds the real Settings of the built plugin to a scratch config file and checks the persisted keys against the contract.</summary>
    internal static void BuiltPlugin(Action<bool, string> check, Assembly plugin, string project)
    {
        check(((Type)plugin.GetType("FuckThisLoadingScreen.PluginInfo", true)).GetField("Guid").GetRawConstantValue().Equals(Guid), "Plugin GUID is the shipped one");
        Type pluginType = plugin.GetType("FuckThisLoadingScreen.Plugin", true);
        object attribute = pluginType.GetCustomAttributes(false).Single(a => a.GetType().Name == "BepInPlugin");
        check((string)attribute.GetType().GetProperty("GUID").GetValue(attribute, null) == Guid, "BepInPlugin attribute carries the shipped GUID");

        Assembly bepInEx = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "BepInEx");
        Type configFile = bepInEx.GetType("BepInEx.Configuration.ConfigFile", true);
        string path = Path.Combine(project, "plugin-contract-validation", System.Guid.NewGuid().ToString("N") + ".cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        // ConfigFile's static constructor reads the BepInEx core config path, which only exists once Paths is initialised.
        Type paths = bepInEx.GetType("BepInEx.Paths", true);
        MethodInfo setExecutablePath = paths.GetMethod("SetExecutablePath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var pathArguments = new object[setExecutablePath.GetParameters().Length];
        pathArguments[0] = Path.Combine(Path.GetDirectoryName(path), "EscapeFromTarkov.exe");
        pathArguments[1] = Path.Combine(Path.GetDirectoryName(path), "BepInEx");
        setExecutablePath.Invoke(null, pathArguments);
        object config = Activator.CreateInstance(configFile, new object[] { path, false });
        Type settings = plugin.GetType("FuckThisLoadingScreen.Settings", true);
        settings.GetMethod("Bind", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { config });

        Type definitionType = bepInEx.GetType("BepInEx.Configuration.ConfigDefinition", true);
        PropertyInfo indexer = configFile.GetProperty("Item", new[] { definitionType });
        var seen = new Dictionary<string, object>();
        foreach (object definition in (IEnumerable)configFile.GetProperty("Keys").GetValue(config, null))
        {
            string key = (string)definitionType.GetProperty("Section").GetValue(definition, null) + "|" + (string)definitionType.GetProperty("Key").GetValue(definition, null);
            seen[key] = indexer.GetValue(config, new[] { definition });
        }

        check(seen.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(Contract.Keys.OrderBy(k => k, StringComparer.Ordinal)), "Persisted section/key set equals the contract: " + string.Join("; ", seen.Keys.Except(Contract.Keys).Concat(Contract.Keys.Except(seen.Keys))));

        var orders = new Dictionary<string, HashSet<int>>();
        foreach (var pair in seen)
        {
            object entry = pair.Value;
            object description = entry.GetType().GetProperty("Description").GetValue(entry, null);
            object range = description.GetType().GetProperty("AcceptableValues").GetValue(description, null);
            object tags = ((object[])description.GetType().GetProperty("Tags").GetValue(description, null)).Single();
            string category = (string)tags.GetType().GetField("Category").GetValue(tags);
            bool advanced = (bool?)tags.GetType().GetField("IsAdvanced").GetValue(tags) == true;
            int order = ((int?)tags.GetType().GetField("Order").GetValue(tags)).Value;
            Type valueType = (Type)entry.GetType().GetProperty("SettingType").GetValue(entry, null);

            check(category == Contract[pair.Key], pair.Key + " is displayed under " + Contract[pair.Key] + ", not " + category);
            check(advanced == (category == "9. Debug"), pair.Key + " is advanced exactly when it is a debug entry");
            check(valueType != typeof(int) || range != null, pair.Key + " has an acceptable value range");
            if (!orders.TryGetValue(category, out var used)) orders[category] = used = new HashSet<int>();
            check(order % 10 == 0 && used.Add(order), pair.Key + " has a unique Order in steps of 10");
        }

        object logging = seen["Debug|Logging"];
        check(Equals(logging.GetType().GetProperty("DefaultValue").GetValue(logging, null), false), "Debug logging is off by default");
        object dump = seen["Debug|Dump Loaded Textures"];
        check(Equals(dump.GetType().GetProperty("BoxedValue").GetValue(dump, null), false), "Dump Loaded Textures persisted value is cleared");
        object dumpTags = ConfigDescriptionTags(dump).Single();
        check(dumpTags.GetType().GetField("CustomDrawer").GetValue(dumpTags) != null && (bool?)dumpTags.GetType().GetField("HideDefaultButton").GetValue(dumpTags) == true
            && (bool?)dumpTags.GetType().GetField("HideSettingName").GetValue(dumpTags) == true, "Dump Loaded Textures is drawn as a button, not a checkbox");
        object budgetOverride = seen["Mip Streaming|Streaming Budget Override"];
        object overrideRange = budgetOverride.GetType().GetProperty("Description").GetValue(budgetOverride, null);
        object overrideValues = overrideRange.GetType().GetProperty("AcceptableValues").GetValue(overrideRange, null);
        check((int)budgetOverride.GetType().GetProperty("DefaultValue").GetValue(budgetOverride, null) == 0 && (int)overrideValues.GetType().GetProperty("MinValue").GetValue(overrideValues, null) == 0
            && (int)overrideValues.GetType().GetProperty("MaxValue").GetValue(overrideValues, null) == 65536, "Streaming Budget Override defaults to 0 (EFT's value) within 0..65536");
        check(((string)overrideRange.GetType().GetProperty("Description").GetValue(overrideRange, null)).Contains("Account For Nonstreaming Textures"), "Streaming Budget Override names its Account For Nonstreaming Textures requirement");
        foreach (string button in new[] { "Mip Streaming|Measure Streaming Budget", "Mip Streaming|Apply Measured Budget" })
        {
            object buttonTags = ConfigDescriptionTags(seen[button]).Single();
            check(buttonTags.GetType().GetField("CustomDrawer").GetValue(buttonTags) != null && (bool?)buttonTags.GetType().GetField("HideDefaultButton").GetValue(buttonTags) == true
                && (bool?)buttonTags.GetType().GetField("HideSettingName").GetValue(buttonTags) == true && (bool?)buttonTags.GetType().GetField("IsAdvanced").GetValue(buttonTags) != true,
                button + " is drawn as a button, not a checkbox, and is not advanced");
        }

        object measureDescription = seen["Mip Streaming|Measure Streaming Budget"].GetType().GetProperty("Description").GetValue(seen["Mip Streaming|Measure Streaming Budget"], null);
        check(((string)measureDescription.GetType().GetProperty("Description").GetValue(measureDescription, null)).Contains("load Streets (the heaviest map), press Measure"), "Measure button description carries the instructions");
        foreach (var pair in seen)
        {
            object entryDescription = pair.Value.GetType().GetProperty("Description").GetValue(pair.Value, null);
            string text = (string)entryDescription.GetType().GetProperty("Description").GetValue(entryDescription, null);
            check(text.IndexOf("resets itself", StringComparison.OrdinalIgnoreCase) < 0, pair.Key + " is not a self-resetting checkbox action");
        }
    }

    private static object[] ConfigDescriptionTags(object entry)
    {
        object description = entry.GetType().GetProperty("Description").GetValue(entry, null);
        return (object[])description.GetType().GetProperty("Tags").GetValue(description, null);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text);
    }
}
