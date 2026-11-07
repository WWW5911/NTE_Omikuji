using System.Reflection;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace NteOmikuji.App;

/// <summary>
/// Serves the page from resources inside the assembly. A single-file publish
/// leaves web files beside the exe, where the extracted app cannot find them.
/// </summary>
public sealed class EmbeddedBlazorWebView : BlazorWebView
{
    public override IFileProvider CreateFileProvider(string contentRootDir) => new WebRootProvider();

    private sealed class WebRootProvider : IFileProvider
    {
        private const string Prefix = "webroot/";
        private static readonly Assembly Assembly = typeof(WebRootProvider).Assembly;

        public IFileInfo GetFileInfo(string subpath) =>
            new WebRootFile(Prefix + subpath.TrimStart('/', '\\').Replace('\\', '/'));

        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

        private sealed class WebRootFile(string resource) : IFileInfo
        {
            private readonly long _length = ReadLength(resource);

            public bool Exists => _length >= 0;
            public long Length => _length;
            public string? PhysicalPath => null;
            public string Name => Path.GetFileName(resource);
            public DateTimeOffset LastModified => DateTimeOffset.MinValue;
            public bool IsDirectory => false;

            public Stream CreateReadStream() =>
                Assembly.GetManifestResourceStream(resource) ?? throw new FileNotFoundException(resource);

            private static long ReadLength(string name)
            {
                using var stream = Assembly.GetManifestResourceStream(name);
                return stream?.Length ?? -1;
            }
        }
    }
}
