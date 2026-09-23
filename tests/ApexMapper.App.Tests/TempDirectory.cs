using System.IO;

namespace ApexMapper.App.Tests;

/// <summary>A fresh folder under the temp directory, deleted with everything in it at the end of the test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apex-app-tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file a failed test left open; the temp folder is cleaned eventually.
        }
    }
}
