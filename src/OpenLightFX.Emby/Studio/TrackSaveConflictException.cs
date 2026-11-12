namespace OpenLightFX.Emby.Studio;

public sealed class TrackSaveConflictException : Exception
{
    public TrackSaveConflictException(string message)
        : base(message)
    {
    }
}
