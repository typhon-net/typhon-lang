// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Typhon.Playground;

/// <summary>
/// Fetches the reference assemblies (refs/*.dll, listed with their hashes in the embedded playground-refs.txt) once,
/// in parallel, and creates the compiler and the runner from them. In a browser there are no assembly files to read:
/// Roslyn gets the references from these byte arrays.
/// </summary>
public sealed class ReferenceLoader(HttpClient http)
{
    Task<(PlaygroundCompiler Compiler, PlaygroundRunner Runner)>? _loading;

    public int Total { get; private set; }
    public int Loaded { get; private set; }
    public long Bytes { get; private set; }
    public TimeSpan Elapsed { get; private set; }

    /// <summary>Raised as each file arrives.</summary>
    public event Action? Progress;

    public Task<(PlaygroundCompiler Compiler, PlaygroundRunner Runner)> GetAsync() => _loading ??= LoadAsync();

    async Task<(PlaygroundCompiler, PlaygroundRunner)> LoadAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var files = ReadIndex();
        Total = files.Count;
        var images = await Task.WhenAll(files.Select(async f =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"refs/{f.Name}?v={f.Hash[..Math.Min(16, f.Hash.Length)]}");
            request.SetBrowserRequestCache(BrowserRequestCache.Default); // the version in the URL lets the browser cache it
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Loaded++;
            Bytes += bytes.Length;
            Progress?.Invoke();
            return new KeyValuePair<string, byte[]>(f.Name, bytes);
        }));
        Elapsed = sw.Elapsed;
        var runtime = images.First(i => i.Key.Equals(PlaygroundCompiler.RuntimeFileName, StringComparison.OrdinalIgnoreCase)).Value;
        return (PlaygroundCompiler.FromImages(images), new PlaygroundRunner(runtime));
    }

    static List<(string Name, string Hash)> ReadIndex()
    {
        using var stream = typeof(ReferenceLoader).Assembly.GetManifestResourceStream("playground-refs.txt")
                           ?? throw new InvalidOperationException("playground-refs.txt is missing (see Playground.csproj)");
        using var reader = new StreamReader(stream);
        var files = new List<(string, string)>();
        while (reader.ReadLine() is { } line)
            if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var name, var hash]) files.Add((name, hash));
        return files;
    }
}
