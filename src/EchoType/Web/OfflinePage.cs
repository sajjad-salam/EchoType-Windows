namespace EchoType.Web;

/// <summary>
/// Placeholder shown in the login window while offline (port of the offlineHTML in
/// ChatGPTWebController.swift). The Retry button posts a WebView2 web message that
/// the web controller turns into a reload.
/// </summary>
internal static class OfflinePage {

    public static string HtmlFor(string productName) => $$"""
        <!doctype html><html><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>
          html,body{height:100%;margin:0}
          body{display:flex;align-items:center;justify-content:center;
            font:15px 'Segoe UI',system-ui,sans-serif;color:#e5e5e5;background:#1e1e1e}
          .card{text-align:center;max-width:340px;padding:0 24px}
          .icon{font-size:44px;margin-bottom:12px}
          h1{font-size:19px;font-weight:600;margin:0 0 8px}
          p{color:#a0a0a0;line-height:1.5;margin:0 0 24px}
          button{font:inherit;font-weight:600;color:#fff;background:#10a37f;border:0;
            border-radius:8px;padding:10px 22px;cursor:pointer}
          button:hover{background:#0e8f6f}
        </style></head><body>
          <div class="card">
            <div class="icon">&#128225;</div>
            <h1>No internet connection</h1>
            <p>EchoType can't reach {{productName}}. Check your network, then try again.</p>
            <button onclick="window.chrome.webview.postMessage('retry')">Retry</button>
          </div>
        </body></html>
        """;
}
