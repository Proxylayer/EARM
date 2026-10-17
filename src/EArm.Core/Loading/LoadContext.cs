using System.Collections.Generic;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Loading;

/// <summary>The state one template load shares between its readers: the file, its source map and the non-fatal findings so far.</summary>
internal sealed class LoadContext
{
    public LoadContext(string? sourcePath, SourceMap sourceMap)
    {
        SourcePath = sourcePath;
        SourceMap = sourceMap;
    }

    public string? SourcePath { get; }

    public SourceMap SourceMap { get; }

    public List<Diagnostic> Diagnostics { get; } = [];

    public void Error(string code, string message, JsonPath path)
    {
        Diagnostics.Add(Diagnostic.Error(code, message, path, SourceMap.FindNearest(path), SourcePath));
    }

    public void Warning(string code, string message, JsonPath path)
    {
        Diagnostics.Add(Diagnostic.Warning(code, message, path, SourceMap.FindNearest(path), SourcePath));
    }
}
