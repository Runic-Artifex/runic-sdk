namespace Runic.Application.Views;

/// <summary>
/// Publishes a read-only <see cref="string"/> property that grows at its end, such as a live log, as the text it
/// appends instead of its whole value.
/// </summary>
/// <remarks>
/// <para>
/// When the property changes on its own, the Bridge publishes a frame with the number of characters dropped from the
/// start of the value the page has and the text appended, keeping the longest text the two values share, as for a
/// log that keeps its last 128 KB. A <see langword="null"/> value, or a change together with other state, crosses
/// as a full state.
/// </para>
/// <para>
/// The page sees an ordinary <c>string</c> member. A ViewModel that implements
/// <see cref="System.ComponentModel.INotifyDataErrorInfo"/>, or whose state contains a type that does, publishes full
/// states only, as with <see cref="RunicCollectionAttribute"/>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RunicAppendOnlyTextAttribute : Attribute;
