using System;
using RunicArtifex.Examples.TextResources.Generated;
using RunicTextResources;

ITextResourceManager manager = await CanaryTextCatalog.CreateManagerAsync();
var text = new CanaryText(manager);
string greeting = text.Greeting("Runic Artifex");

if (!string.Equals(greeting, "Hello, Runic Artifex!", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Unexpected generated greeting: {greeting}");
}

Console.WriteLine(greeting);
