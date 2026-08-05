using System;
using RunicToolkit.Collections;
using RunicToolkit.Hosting;
using RunicToolkit.Hosting.Build;
using RunicToolkit.MVVM;

var values = new ObservableRangeCollection<int>();
values.AddRange([1, 2, 3]);
var launch = new DefaultLaunchIntentResolver().Resolve([]);
var manifest = new FrontendAssetManifestBuilder().Build(
    [new FrontendAssetBuildItem("index.html", "<html/>"u8.ToArray(), isEntryPoint: true)]);
var contract = new MvvmContract("package-canary");

Console.WriteLine($"{values.Count}|{launch.Kind}|{manifest.Assets.Count}|{contract.Value}");
