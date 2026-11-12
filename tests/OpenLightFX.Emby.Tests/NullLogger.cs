using System.Text;
using MediaBrowser.Model.Logging;

namespace OpenLightFX.Emby.Tests;

sealed class NullLogger : ILogger
{
    public void Info(string message, params object[] paramList) { }
    public void Error(string message, params object[] paramList) { }
    public void Warn(string message, params object[] paramList) { }
    public void Debug(string message, params object[] paramList) { }
    public void Fatal(string message, params object[] paramList) { }
    public void FatalException(string message, Exception exception, params object[] paramList) { }
    public void ErrorException(string message, Exception exception, params object[] paramList) { }
    public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent) { }
    public void Log(LogSeverity severity, string message, params object[] paramList) { }
    public void Log(LogSeverity severity, ReadOnlyMemory<char> message) { }
    public void Error(ReadOnlyMemory<char> message) { }
    public void Warn(ReadOnlyMemory<char> message) { }
    public void Info(ReadOnlyMemory<char> message) { }
    public void Debug(ReadOnlyMemory<char> message) { }
}
