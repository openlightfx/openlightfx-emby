namespace OpenLightFX.Emby.Studio;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

/// <summary>
/// One pass over movies that already exist when the plugin starts. New and edited
/// movies are linked from <see cref="ServerEntryPoint"/>; this task fills the rest.
/// Startup runs it each boot, and a movie that already has the right id is skipped.
/// </summary>
public class StudioMovieLinkTask : IScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly ILogger _logger;

    public StudioMovieLinkTask(ILibraryManager library, ILogManager logManager)
    {
        _library = library;
        _logger = logManager.GetLogger("OpenLightFX");
    }

    public string Name => "Link movies to OpenLightFX Studio";

    public string Key => "OpenLightFXStudioMovieLinks";

    public string Description =>
        "Adds an OpenLightFX Studio link to each movie that does not already have one.";

    public string Category => "OpenLightFX";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        new[] { new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup } };

    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var movies = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { typeof(Movie).Name },
            Recursive = true
        });

        var updated = 0;
        for (var i = 0; i < movies.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StudioExternalId.Ensure(movies[i], _library))
                updated++;

            if (movies.Length > 0)
                progress.Report(100d * (i + 1) / movies.Length);
        }

        if (movies.Length == 0)
            progress.Report(100);

        _logger.Info("OpenLightFX Studio movie links: updated {0} of {1}", updated, movies.Length);
        return Task.CompletedTask;
    }
}
