using System;
using Runic.Application.Bridge.PostMvvmFixture;
using Runic.Application.Bridge.PostMvvmFixture.Generated;

var model = new NotesViewModel();
if (PostMvvmDiscoveryAdapter.ReadTitle(model) != "Draft")
    throw new InvalidOperationException("The generated accessor did not read Toolkit Title.");

int notifications = 0;
model.PropertyChanged += (_, args) =>
{
    if (args.PropertyName == nameof(NotesViewModel.Title)) notifications++;
};
PostMvvmDiscoveryAdapter.WriteTitle(model, "Edited");
if (model.Title != "Edited" || notifications != 1)
    throw new InvalidOperationException("The generated accessor did not write Toolkit Title with notification.");

await PostMvvmDiscoveryAdapter.InvokeSaveCommandAsync(model);
if (model.SaveCount != 1)
    throw new InvalidOperationException("The generated Toolkit command accessor did not execute SaveCommand.");

string refreshed = await PostMvvmDiscoveryAdapter.InvokeRefreshCommandAsync(model, "fresh");
if (refreshed != "fresh")
    throw new InvalidOperationException("The generated ReactiveUI command accessor did not await RefreshCommand.");

Console.WriteLine("POST_MVVM_SDK_TYPED_ACCESSOR_OK|title-read-write-notified|toolkit-save-executed|reactive-refresh-awaited");
