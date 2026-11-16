using System.Globalization;

namespace GlobalMenuCheck;

/// <summary>The page each window shows: live status from .NET, the command log and a manual checklist.</summary>
internal static class Page
{
    public static string Html(int number) => Template.Replace("{{N}}", number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <script src="webui.js"></script>
          <title>Global Menu Check #{{N}}</title>
          <style>
            :root { color-scheme: light dark; --bg:#f6f7f9; --card:#fff; --fg:#1d2330; --muted:#5d6678; --line:#d9dde5;
                    --ok:#1f8a4c; --warn:#b26a00; --bad:#c0392b; --accent:#3557d6; }
            @media (prefers-color-scheme: dark) { :root { --bg:#16181d; --card:#1f2229; --fg:#e6e8ee; --muted:#9aa3b5;
                    --line:#333845; --ok:#4cc38a; --warn:#f0a73a; --bad:#ff6b5e; --accent:#8aa2ff; } }
            * { box-sizing: border-box; }
            body { margin:0; font: 14px/1.45 system-ui, sans-serif; background:var(--bg); color:var(--fg); }
            main { max-width: 1100px; margin: 0 auto; padding: 16px; display:grid; gap:14px;
                   grid-template-columns: minmax(0,1fr) minmax(0,1fr); }
            header { grid-column: 1 / -1; display:flex; align-items:center; gap:14px; flex-wrap:wrap; }
            h1 { font-size: 20px; margin:0; }
            h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .06em; color: var(--muted); margin: 0 0 8px; }
            .card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:14px; min-width:0; }
            .wide { grid-column: 1 / -1; }
            .placement { font-weight:700; font-size:15px; padding:4px 12px; border-radius:999px; border:2px solid currentColor; }
            .Global { color: var(--ok); } .Window { color: var(--warn); } .None, .Page, .unknown { color: var(--bad); }
            dl { display:grid; grid-template-columns: max-content 1fr; gap: 4px 14px; margin:0; }
            dt { color: var(--muted); } dd { margin:0; font-family: ui-monospace, monospace; overflow-wrap:anywhere; }
            #verdict { margin: 8px 0 0; color: var(--muted); }
            ol { margin:0; padding-left: 0; list-style: none; display:grid; gap:6px; }
            li label { display:flex; gap:8px; align-items:flex-start; cursor:pointer; }
            li input { margin-top: 3px; }
            li.done span { color: var(--muted); text-decoration: line-through; }
            #log { font: 12px/1.5 ui-monospace, monospace; height: 260px; overflow:auto; white-space: pre-wrap;
                   background: var(--bg); border:1px solid var(--line); border-radius:6px; padding:8px; margin:0; }
            #log .hit { color: var(--ok); } #log .err { color: var(--bad); }
            code { font-family: ui-monospace, monospace; }
            button { font: inherit; padding: 4px 10px; border-radius:6px; border:1px solid var(--line); background:var(--card); color:var(--fg); cursor:pointer; }
            @media (max-width: 760px) { main { grid-template-columns: minmax(0,1fr); } }
          </style>
        </head>
        <body>
        <main>
          <header>
            <h1>Global Menu Check · window #{{N}}</h1>
            <span id="placement" class="placement unknown">waiting…</span>
          </header>

          <section class="card">
            <h2>Status (from .NET)</h2>
            <dl>
              <dt>Menu bar placement</dt><dd id="s-placement">–</dd>
              <dt>Backend</dt><dd id="s-backend">–</dd>
              <dt>GlobalMenu option</dt><dd id="s-option">–</dd>
              <dt>Frameless</dt><dd id="s-frameless">–</dd>
              <dt>Session / desktop</dt><dd id="s-session">–</dd>
              <dt>Open windows</dt><dd id="s-windows">–</dd>
              <dt>Menu state</dt><dd id="s-state">–</dd>
            </dl>
            <p id="verdict"></p>
          </section>

          <section class="card">
            <h2>Checklist (tick as you verify)</h2>
            <ol id="checklist">
              <li><label><input type="checkbox"><span>Placement badge says <b>Global</b> and the in-window bar is hidden.</span></label></li>
              <li><label><input type="checkbox"><span>The global menu shows File, Edit, View, Tools (rev N), Help — Help last.</span></label></li>
              <li><label><input type="checkbox"><span>File ▸ New logs <code>file.new</code> from window #{{N}}.</span></label></li>
              <li><label><input type="checkbox"><span>Shortcut text is shown (Ctrl+N, Ctrl+O, Ctrl+Shift+W…); Ctrl+N in the page also logs <code>file.new</code>.</span></label></li>
              <li><label><input type="checkbox"><span>File ▸ Open Recent submenu opens; each entry logs its parameter.</span></label></li>
              <li><label><input type="checkbox"><span>Edit ▸ Undo is disabled; untick “Enable Cut/Copy/Paste” and Cut/Copy/Paste become disabled.</span></label></li>
              <li><label><input type="checkbox"><span>View ▸ Word Wrap toggles its check mark (declared, rebuilt).</span></label></li>
              <li><label><input type="checkbox"><span>View ▸ Live Check toggles its mark without a rebuild (IsChecked when the menu opens).</span></label></li>
              <li><label><input type="checkbox"><span>View ▸ Theme shows radio marks, exactly one selected, following the choice.</span></label></li>
              <li><label><input type="checkbox"><span>View ▸ Nested ▸ Level 2 ▸ Level 3 ▸ Deepest item works; “Disabled submenu” is disabled.</span></label></li>
              <li><label><input type="checkbox"><span>Tools ▸ Rebuild Menu Bar updates the title “Tools (rev N)” and the “Item added in rev N” entry live.</span></label></li>
              <li><label><input type="checkbox"><span>Tools ▸ Show Extra Menu adds/removes an “Extra” menu before Help.</span></label></li>
              <li><label><input type="checkbox"><span>Unicode, long and “Under_score &amp; ampersand” titles render literally.</span></label></li>
              <li><label><input type="checkbox"><span>File ▸ New Window: switching focus between windows switches the global menu; commands log the focused window’s number.</span></label></li>
              <li><label><input type="checkbox"><span>Remove the Global Menu applet (or log out of it): placement changes to <b>Window</b> and the in-window bar appears; adding it back returns to <b>Global</b>.</span></label></li>
              <li><label><input type="checkbox"><span>Alt+F (with the bar in the window) opens File; access keys are underlined.</span></label></li>
            </ol>
            <p><button id="reset">Reset checklist</button></p>
          </section>

          <section class="card wide">
            <h2>Command and event log (all windows)</h2>
            <pre id="log"></pre>
          </section>
        </main>
        <script>
          const $ = (id) => document.getElementById(id);
          const verdicts = {
            Global: "The bar is in the desktop's global menu.",
            Window: "The bar is in the window: no registrar / no org_kde_kwin_appmenu, or GlobalMenu is off.",
            None: "No native bar (closed, frameless without global menu, kiosk, or None presentation).",
            Page: "The page would render the bar itself.",
          };
          window.gmStatus = (s) => {
            const badge = $("placement");
            badge.textContent = s.placement;
            badge.className = "placement " + (verdicts[s.placement] ? s.placement : "unknown");
            $("s-placement").textContent = s.placement;
            $("s-backend").textContent = s.backend;
            $("s-option").textContent = String(s.globalMenuOption);
            $("s-frameless").textContent = String(s.frameless);
            $("s-session").textContent = s.session + " / " + s.desktop;
            $("s-windows").textContent = String(s.windows);
            const st = s.state;
            $("s-state").textContent = `wrap=${st.wrap} live=${st.live} clipboard=${st.clipboard} theme=${st.theme} extra=${st.extra} rev=${st.revision}`;
            $("verdict").textContent = verdicts[s.placement] || "Unknown placement value.";
          };
          const addLine = (line) => {
            const log = $("log");
            const div = document.createElement("div");
            div.textContent = line;
            if (line.includes("✔")) div.className = "hit";
            if (line.includes("✘") || /Diagnostic (Error|Warning)/.test(line)) div.className = "err";
            log.appendChild(div);
            log.scrollTop = log.scrollHeight;
          };
          window.gmLog = addLine;
          window.gmReplaceLog = (lines) => { $("log").textContent = ""; lines.forEach(addLine); };

          const KEY = "gm-checklist";
          const boxes = [...document.querySelectorAll("#checklist input")];
          let saved = [];
          try { saved = JSON.parse(localStorage.getItem(KEY) || "[]"); } catch { }
          const sync = () => boxes.forEach((b) => b.closest("li").classList.toggle("done", b.checked));
          boxes.forEach((b, i) => {
            b.checked = !!saved[i];
            b.addEventListener("change", () => {
              try { localStorage.setItem(KEY, JSON.stringify(boxes.map((x) => x.checked))); } catch { }
              sync();
            });
          });
          $("reset").onclick = () => { boxes.forEach((b) => (b.checked = false)); try { localStorage.removeItem(KEY); } catch { } sync(); };
          sync();
        </script>
        </body>
        </html>
        """;
}
