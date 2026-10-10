namespace ScoreStudio.Services;

/// <summary>
/// A project file could not be read or written. <see cref="Exception.Message"/> is written
/// for the user and can be shown in a dialog as-is.
/// </summary>
public sealed class ProjectFileException : Exception
{
    public ProjectFileException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
