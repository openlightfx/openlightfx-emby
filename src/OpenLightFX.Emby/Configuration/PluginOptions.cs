namespace OpenLightFX.Emby.Configuration;

using global::Emby.Web.GenericEdit;
using global::Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using OpenLightFX.Emby.Studio;
using System.ComponentModel;

public class PluginOptions : EditableOptionsBase
{
    public override string EditorTitle => "OpenLightFX Settings";
    public override string EditorDescription => "Set up rooms and bulbs in OpenLightFX Studio. " +
        "Open the room setup and track editor at /emby/OpenLightFX/studio/.";

    // Refreshed in Plugin.OnBeforeShowUI so a saved copy of this page cannot keep an old URL.
    public LabelItem StudioLink { get; set; } = new LabelItem("Open OpenLightFX Studio (bulbs)")
    {
        HyperLink = StudioPaths.BulbsUrl
    };

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

    // --- Studio Media Processing ---
    [DisplayName("Transcode Temporary Directory")]
    [Description("Optional local or fast-storage directory used while Studio analyzes movies. Studio copies the movie here when creating a track and removes the copy when the editor closes. The Emby service account needs permission to read, create, write, and delete files in this folder. On Linux, for example: sudo chown emby:emby /path/to/folder && sudo chmod 750 /path/to/folder (replace emby with the account running Emby). On Windows, grant the Emby service account Modify permission. Leave blank to process the library file directly.")]
    public string TranscodeTempDirectory { get; set; } = "";

    [DisplayName("Studio Scene-cut Threshold")]
    [Description("Global PySceneDetect content threshold used when Studio prepares an edit proxy. Values are 0.01–1.00; 0.27 (threshold 27) is the default. Lower values detect more cuts.")]
    public double StudioSceneCutThreshold { get; set; } = 0.27;

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
