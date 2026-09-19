using ModSettings.Common;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace BeaverBuddies
{
    public enum PauseReductionLevel
    {
        Off = 0,
        MenuOnly = 1,
        NeverAutoPause = 2,
    }

    public enum PanelDisplayMode
    {
        Expanded = 0,
        Collapsed = 1,
        Hidden = 2,
    }

    public enum PanelCorner
    {
        TopLeft = 0,
        TopRight = 1,
        BottomLeft = 2,
        BottomRight = 3,
    }

    public class Settings : ModSettingsOwner
    {
        public ModSetting<string> ClientConnectionAddress { get; } =
            new("127.0.0.1",
                ModSettingDescriptor.CreateLocalized(
                    "BeaverBuddies.Settings.ClientConnectionAddress"
                ).SetLocalizedTooltip("BeaverBuddies.Settings.ClientConnectionAddress.Tooltip")
            );

        public ModSetting<int> DefaultPort { get; } =
            new(25565,
                ModSettingDescriptor.CreateLocalized(
                    "BeaverBuddies.Settings.Port"
                ).SetLocalizedTooltip("BeaverBuddies.Settings.Port.Tooltip")
            );

        public ModSetting<bool> ShowFirstTimerMessage { get; } =
            new(true,
                ModSettingDescriptor.CreateLocalized(
                    "BeaverBuddies.Settings.ShowFirstTimerMessage"
                )
        );

        public ModSetting<bool> ReportingConsent { get; } =
            new(false,
            ModSettingDescriptor.CreateLocalized(
                "BeaverBuddies.Settings.ReportingConsent"
            ).SetLocalizedTooltip("BeaverBuddies.ClientDesynced.ConsentMessage")
        );

        // ---- Steam Settings ----

        public ModSetting<bool> EnableSteamConnection { get; } =
            new(true,
            ModSettingDescriptor.CreateLocalized(
                "BeaverBuddies.Settings.EnableSteamConnection"
            ).SetLocalizedTooltip("BeaverBuddies.Settings.EnableSteamConnection.Tooltip")
        );

        public ModSetting<bool> FriendsCanJoinSteamGame { get; } =
            new(true,
            ModSettingDescriptor.CreateLocalized(
                "BeaverBuddies.Settings.FriendsCanJoinSteamGame"
            ).SetLocalizedTooltip("BeaverBuddies.Settings.FriendsCanJoinSteamGame.Tooltip")
        );

        // ---- Quality of Life Settings ----

        public LimitedStringModSetting PauseReduction { get; } =
            new(0, new[] {
                new LimitedStringModSettingValue("0", "BeaverBuddies.Settings.PauseReduction.Off"),
                new LimitedStringModSettingValue("1", "BeaverBuddies.Settings.PauseReduction.LowRisk"),
                new LimitedStringModSettingValue("2", "BeaverBuddies.Settings.PauseReduction.HighRisk")
            }, ModSettingDescriptor.CreateLocalized("BeaverBuddies.Settings.PauseReduction")
                .SetLocalizedTooltip("BeaverBuddies.Settings.PauseReduction.Tooltip")
        );

        // ---- Player Activity ----

        public ModSetting<bool> PlayerActivity { get; } =
            new(true, ModSettingDescriptor.CreateLocalized("BeaverBuddies.Settings.PlayerActivity")
                .SetLocalizedTooltip("BeaverBuddies.Settings.PlayerActivity.Tooltip"));

        // ---- Game speed ----

        public ModSetting<bool> RemoveLargeColonySpeedLimit { get; } =
            new(false, ModSettingDescriptor.CreateLocalized("BeaverBuddies.Settings.RemoveSpeedLimit")
                .SetLocalizedTooltip("BeaverBuddies.Settings.RemoveSpeedLimit.Tooltip"));

        // ---- Connection Panel ----

        public LimitedStringModSetting ConnectionPanelDisplay { get; } =
            new(0, new[] {
                new LimitedStringModSettingValue("0", "BeaverBuddies.Settings.ConnectionPanelDisplay.Expanded"),
                new LimitedStringModSettingValue("1", "BeaverBuddies.Settings.ConnectionPanelDisplay.Collapsed"),
                new LimitedStringModSettingValue("2", "BeaverBuddies.Settings.ConnectionPanelDisplay.Hidden")
            }, ModSettingDescriptor.CreateLocalized("BeaverBuddies.Settings.ConnectionPanelDisplay")
                .SetLocalizedTooltip("BeaverBuddies.Settings.ConnectionPanelDisplay.Tooltip")
        );

        public LimitedStringModSetting ConnectionPanelCorner { get; } =
            new(0, new[] {
                new LimitedStringModSettingValue("0", "BeaverBuddies.Settings.ConnectionPanelCorner.TopLeft"),
                new LimitedStringModSettingValue("1", "BeaverBuddies.Settings.ConnectionPanelCorner.TopRight"),
                new LimitedStringModSettingValue("2", "BeaverBuddies.Settings.ConnectionPanelCorner.BottomLeft"),
                new LimitedStringModSettingValue("3", "BeaverBuddies.Settings.ConnectionPanelCorner.BottomRight")
            }, ModSettingDescriptor.CreateLocalized("BeaverBuddies.Settings.ConnectionPanelCorner")
                .SetLocalizedTooltip("BeaverBuddies.Settings.ConnectionPanelCorner.Tooltip")
        );

        // ---- Developer Settings ----

        public ModSetting<bool> AlwaysTrace { get; } =
            new(false,
            ModSettingDescriptor.CreateLocalized(
                "BeaverBuddies.Settings.AlwaysTrace"
            ).SetLocalizedTooltip("BeaverBuddies.Settings.AlwaysTrace.Tooltip")
        );

        public ModSetting<bool> SilenceLogging { get; } =
            new(false,
                ModSettingDescriptor.CreateLocalized(
                    "BeaverBuddies.Settings.SilenceLogging"
                ).SetLocalizedTooltip("BeaverBuddies.Settings.SilenceLogging.Tooltip")
        );

        // ---- Ping Settings ----

        public const string DefaultPingPlayerName = "Player";
        public ModSetting<string> PingPlayerName { get; } =
            new(DefaultPingPlayerName,
                ModSettingDescriptor.CreateLocalized(
                    "BeaverBuddies.Settings.PingPlayerName"
                ).SetLocalizedTooltip("BeaverBuddies.Settings.PingPlayerName.Tooltip")
        );

        public ColorModSetting PingColor { get; } =
            new(UnityEngine.Color.yellow,
                ModSettingDescriptor.CreateLocalized(
                    "BeaverBuddies.Settings.PingColor"
                ).SetLocalizedTooltip("BeaverBuddies.Settings.PingColor.Tooltip"),
                useAlpha: false
        );

        // We keep a static instance because
        // 1) The settings are saved in a static manner, so all instances
        //    should be identical, and
        // 2) We need to access the settings frequently without an easy
        //    way to pass the instance around.
        private static Settings instance = null;

        /**
         * Indicates that the player has temporarily enabled debug mode.
         * Not serialized as a setting.
         */
        public static bool TemporarilyDebug { get; set; } = false;

        public static bool Debug => TemporarilyDebug || (instance?.AlwaysTrace.Value ?? false);

        public static bool VerboseLogging => !(instance?.SilenceLogging.Value == true);
        public static int Port => instance?.DefaultPort.Value ?? 25565;
        public static bool EnableSteam => instance?.EnableSteamConnection.Value ?? true;
        public static bool LobbyJoinable => instance?.FriendsCanJoinSteamGame.Value ?? true;
        public static bool ShouldShowFirstTimerMessage => instance?.ShowFirstTimerMessage.Value ?? true;
        public static bool PlayerActivityEnabled => instance?.PlayerActivity.Value ?? true;
        public static bool RemoveSpeedLimit => instance?.RemoveLargeColonySpeedLimit.Value ?? false;

        public static PanelDisplayMode ConnectionPanelDisplayMode =>
            ParseChoice(instance?.ConnectionPanelDisplay?.Value, PanelDisplayMode.Expanded);

        public static PanelCorner ConnectionPanelCornerValue =>
            ParseChoice(instance?.ConnectionPanelCorner?.Value, PanelCorner.TopLeft);

        /// <summary>Saves the panel's state, so collapsing it from the panel itself is remembered.</summary>
        public static void SetConnectionPanelDisplayMode(PanelDisplayMode mode) =>
            instance?.ConnectionPanelDisplay.SetValue(((int)mode).ToString());

        // Settings store the choice as its number; anything unrecognised falls back to the default.
        private static T ParseChoice<T>(string value, T fallback) where T : struct, System.Enum =>
            int.TryParse(value, out int number) && System.Enum.IsDefined(typeof(T), number) ? (T)(object)number : fallback;

        public static PauseReductionLevel PauseReductionSetting
        {
            get
            {
                if (instance?.PauseReduction?.Value == null)
                    return PauseReductionLevel.Off;

                if (int.TryParse(instance.PauseReduction.Value, out int level))
                    return (PauseReductionLevel)level;

                return PauseReductionLevel.Off;
            }
        }

        public static string PingDisplayName => instance?.PingPlayerName.Value ?? DefaultPingPlayerName;

        public static UnityEngine.Color PingColorValue => instance?.PingColor.Color ?? UnityEngine.Color.white;

        public Settings(ISettings settings,
                        ModSettingsOwnerRegistry modSettingsOwnerRegistry,
                        ModRepository modRepository) :
            base(settings, modSettingsOwnerRegistry, modRepository)
        {
            instance = this;
            // Outside a session the change applies at once. In a session the host's choice at the start stands.
            RemoveLargeColonySpeedLimit.ValueChanged += (_, _) => LargeColonySpeedLimit.Reapply();
        }

        protected override string ModId => Plugin.ID;

    }
}
