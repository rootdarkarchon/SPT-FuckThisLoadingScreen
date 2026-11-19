using System;
using BepInEx.Configuration;

namespace FuckThisLoadingScreen
{
    // ConfigurationManager discovers these tag fields by name through reflection, so this plugin has no
    // dependency on its assembly: settings still work when the menu is absent. Null leaves the default.
    // The public field names are that external contract: never rename them. Reference:
    // https://github.com/BepInEx/BepInEx.ConfigurationManager
#pragma warning disable 0169, 0414, 0649
    internal sealed class ConfigurationManagerAttributes
    {
        /// <summary>Category the setting is shown under; null puts it directly under the plugin.</summary>
        public string Category;
        /// <summary>Name shown instead of the config key.</summary>
        public string DispName;
        /// <summary>Description shown on hover (prefer the ConfigDescription text).</summary>
        public string Description;
        /// <summary>Position inside a category; a higher number is higher in the list.</summary>
        public int? Order;
        /// <summary>Hide the setting until "Advanced settings" is enabled or it is searched for.</summary>
        public bool? IsAdvanced;
        /// <summary>Show a ranged setting as a percentage.</summary>
        public bool? ShowRangeAsPercent;
        /// <summary>Replaces the default editor (OnGUI code).</summary>
        public Action<ConfigEntryBase> CustomDrawer;
        /// <summary>Force the reset-to-default button to be hidden.</summary>
        public bool? HideDefaultButton;
        /// <summary>Hide the setting name; use with CustomDrawer.</summary>
        public bool? HideSettingName;
        /// <summary>When false the setting is not shown at all.</summary>
        public bool? Browsable;
        /// <summary>Show the value but do not allow editing.</summary>
        public bool? ReadOnly;
    }
#pragma warning restore 0169, 0414, 0649
}
