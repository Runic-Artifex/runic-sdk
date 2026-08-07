using System;
using RunicToolkit.Collections;
using RunicToolkit.ApplicationBridge;
using RunicToolkit.Hosting;
using RunicToolkit.Hosting.Build;

var values = new ObservableRangeCollection<int>();
values.AddRange([1, 2, 3]);
var launch = new DefaultLaunchIntentResolver().Resolve([]);
var manifest = new FrontendAssetManifestBuilder().Build(
    [new FrontendAssetBuildItem("index.html", "<html/>"u8.ToArray(), isEntryPoint: true)]);
BridgeSessionId session = BridgeSessionId.New();

Console.WriteLine($"{values.Count}|{launch.Kind}|{manifest.Assets.Count}|{session.Value:D}");
