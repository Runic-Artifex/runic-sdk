using System.Net;

namespace Runic.Application.Views.CsWebUi;

// One request to the CS-WebUI blob endpoint, as its listener received it: http.sys's HttpListener on Windows
// (CsWebUiHttpSysListener), Runic's own loopback server elsewhere (LoopbackHttpServer). Its response is the exchange's.
internal interface ICsWebUiBlobRequest : IBridgeBlobExchange
{
    // The request target's path, without the query.
    string Path { get; }
    // The client's address as the listener saw it; the relay's own socket for a relayed connection.
    IPEndPoint? RemoteEndPoint { get; }
}

// The blob endpoint's listener. Serve starts handing it requests: a request ends when the handler's task does, with its
// response sent, or with the connection reset when the task fails.
internal interface ICsWebUiBlobListener : IDisposable
{
    void Serve(Func<ICsWebUiBlobRequest, Task> handle);
}
