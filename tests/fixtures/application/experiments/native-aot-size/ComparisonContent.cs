namespace NativeAotSizeComparison;

internal static class ComparisonContent
{
    internal const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <script src="webui.js"></script>
          <title>NativeAOT desktop size comparison</title>
        </head>
        <body>
          <output id="result">starting</output>
          <script>
            addEventListener("DOMContentLoaded", () => {
              let started = false;
              const run = async () => {
                if (started) return;
                started = true;
                const reply = await ping("comparison");
                document.querySelector("#result").textContent = reply;
                await complete(reply);
              };
              webui.setEventCallback(event => {
                if (event === webui.event.CONNECTED) run();
              });
              if (webui.isConnected()) run();
            });
          </script>
        </body>
        </html>
        """;

    // Runic Desktop keeps its capability transport internal (0.10.0), so its baseline makes the same round trip from
    // .NET: it runs page script that answers "pong:comparison" and checks the answer.
    internal const string ScriptedHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <script src="webui.js"></script>
          <title>NativeAOT desktop size comparison</title>
        </head>
        <body>
          <output id="result">starting</output>
          <script>
            function ping(value) {
              const reply = "pong:" + value;
              document.querySelector("#result").textContent = reply;
              return reply;
            }
          </script>
        </body>
        </html>
        """;
}
