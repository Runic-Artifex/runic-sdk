using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WebUIToolkit.DependencyNotices.Security.Tests;

internal sealed class JsonDocumentFixture : IDisposable
{
    private readonly JsonDocument _document;

    public JsonDocumentFixture(string name)
    {
        _document = TestFiles.ReadFixture(name);
    }

    public string String(string name) => _document.RootElement.GetProperty(name).GetString()!;

    public string[] StringArray(string name)
    {
        List<string> values = [];
        foreach (JsonElement item in _document.RootElement.GetProperty(name).EnumerateArray())
        {
            values.Add(item.GetString()!);
        }

        return values.ToArray();
    }

    public void Dispose() => _document.Dispose();
}
