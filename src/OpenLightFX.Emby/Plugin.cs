using System;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using OpenLightFX.Emby.Configuration;

namespace OpenLightFX.Emby;

public class Plugin : BasePluginSimpleUI<PluginOptions>
{
    private readonly ILogger _logger;

    public Plugin(IApplicationHost applicationHost, ILogManager logManager)
        : base(applicationHost)
    {
        _logger = logManager.GetLogger("OpenLightFX");
        Instance = this;
        _logger.Info("OpenLightFX plugin loaded");
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "OpenLightFX";

    public override string Description =>
        "Real-time smart bulb ambient lighting synchronized with movie playback";

    public override Guid Id => new("a7b8c9d0-1e2f-3a4b-5c6d-7e8f9a0b1c2d");

    protected override void OnCreatePageInfo(PluginPageInfo pageInfo)
    {
        pageInfo.EnableInMainMenu = true;
        pageInfo.DisplayName = "OpenLightFX";
    }

    public PluginOptions GetPluginOptions() => GetOptions();

    public void SavePluginOptions(PluginOptions options) => SaveOptions(options);

    public void UpdateOptions(Action<PluginOptions> update)
    {
        var options = GetOptions();
        update(options);
        SaveOptions(options);
    }

    protected override void OnOptionsSaved(PluginOptions options)
    {
        _logger.Info("OpenLightFX configuration saved");
    }
}
