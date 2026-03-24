namespace OpenLightFX.Emby.Configuration;

using global::Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

public class PluginOptions : EditableOptionsBase
{
    public override string EditorTitle => "OpenLightFX Settings";
    public override string EditorDescription => "Configure smart bulb ambient lighting for movie playback. " +
        "Use the Bulb Setup Wizard at openlightfx.com/config to discover and configure bulbs, " +
        "then paste the exported JSON below.";

    // --- Bulb Configuration ---
    [DisplayName("Bulb Configuration JSON")]
    [Description("Paste the bulb configuration JSON exported from openlightfx.com/config. " +
        "This defines your smart bulb inventory (names, protocols, IP addresses).")]
    [EditMultiline(6)]
    public string BulbConfigJson { get; set; } = "[]";

    // --- Mapping Profiles ---
    [DisplayName("Mapping Profiles JSON")]
    [Description("Paste the mapping profiles JSON from openlightfx.com/config. " +
        "Profiles define how track channels map to your physical bulbs.")]
    [EditMultiline(6)]
    public string MappingProfilesJson { get; set; } = "[]";

    [DisplayName("Active Profile Name")]
    [Description("Name of the mapping profile to use during playback.")]
    public string ActiveProfileName { get; set; } = "Default";

    [DisplayName("Device Profile Overrides JSON")]
    [Description("JSON object mapping deviceId to profileName for per-device mapping profile overrides. " +
        "Example: {\"device-guid\": \"Bedroom 2-bulb\"}")]
    [EditMultiline(4)]
    public string DeviceProfileOverridesJson { get; set; } = "{}";

    // --- Playback Settings ---
    [DisplayName("Global Time Offset (ms)")]
    [Description("Offset in milliseconds (positive = delay lights, negative = advance lights). " +
        "Use this to compensate for timing differences between the track and your movie file.")]
    public int GlobalTimeOffsetMs { get; set; } = 0;

    [DisplayName("Global Brightness Cap (%)")]
    [Description("Maximum brightness for all bulbs (0-100). Scales all track brightness values.")]
    [Required]
    public int GlobalBrightnessCap { get; set; } = 100;

    [DisplayName("Lookahead Buffer (ms)")]
    [Description("How far ahead to pre-buffer keyframes to compensate for scheduling jitter.")]
    public int LookaheadBufferMs { get; set; } = 2000;

    [DisplayName("Position Poll Interval (ms)")]
    [Description("How often to check Emby's playback position for drift correction.")]
    public int PollIntervalMs { get; set; } = 500;

    [DisplayName("Start Behavior Override")]
    [Description("Override the track's start behavior. 'Use Track Default' respects the track author's choice.")]
    public BehaviorOverride StartBehaviorOverride { get; set; } = BehaviorOverride.UseTrackDefault;

    [DisplayName("End Behavior Override")]
    [Description("Override the track's end behavior when playback stops.")]
    public BehaviorOverride EndBehaviorOverride { get; set; } = BehaviorOverride.UseTrackDefault;

    [DisplayName("Credits Behavior Override")]
    [Description("Override lighting behavior when the movie reaches credits.")]
    public CreditsBehaviorOverride CreditsBehaviorOverride { get; set; } = CreditsBehaviorOverride.UseTrackDefault;

    [DisplayName("Pre-Show Sequence")]
    [Description("Enable the pre-show dimming sequence before the movie starts (if the track defines one).")]
    public bool PreShowEnabled { get; set; } = true;

    // --- Safety Settings ---
    [DisplayName("Photosensitivity Mode")]
    [Description("When enabled, automatically softens flashing and strobing effects: " +
        "clamps flash frequency to ≤3 Hz, limits brightness changes, converts strobes to slow pulses.")]
    public bool PhotosensitivityMode { get; set; } = false;

    [DisplayName("Show Flashing Content Warnings")]
    [Description("Display a warning before playback when a track contains flashing or strobing effects.")]
    public bool ShowFlashingWarnings { get; set; } = true;

    // --- Track Library ---
    [DisplayName("Additional Track Scan Paths")]
    [Description("Additional directories to search for .lightfx files (one path per line). " +
        "These are searched by IMDB ID in addition to the sidecar/subfolder discovery.")]
    [EditMultiline(4)]
    public string AdditionalScanPaths { get; set; } = "";

    // --- Logging ---
    [DisplayName("Log Level")]
    [Description("Controls the verbosity of OpenLightFX log messages.")]
    public PluginLogLevel PluginLogLevel { get; set; } = PluginLogLevel.Info;
}

public enum BehaviorOverride
{
    [Description("Use Track Default")]
    UseTrackDefault,
    [Description("Leave")]
    Leave,
    [Description("Off")]
    Off,
    [Description("On")]
    On
}

public enum CreditsBehaviorOverride
{
    [Description("Use Track Default")]
    UseTrackDefault,
    [Description("Raise Lights")]
    RaiseLights,
    [Description("Dim")]
    Dim,
    [Description("Off")]
    Off,
    [Description("Continue")]
    Continue
}

public enum PluginLogLevel
{
    Debug,
    Info,
    Warning,
    Error
}
