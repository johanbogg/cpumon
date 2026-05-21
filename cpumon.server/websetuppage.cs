using System.Net;

// Minimal first-run HTML page surfaced at GET /setup?t=<bootstrapToken>.
// The one page the operator hits during initial provisioning. Inline CSS so
// there are no asset routes to mount; the page POSTs JSON to /api/auth/bootstrap
// and on success the browser is redirected to the dashboard at /.
public static class WebSetupPage
{
    public static string Render(bool operatorExists, string token)
    {
        if (operatorExists)
            return Wrap("Setup complete",
                "<p>The operator account already exists. <a href=\"/login\">Sign in here</a> to open the dashboard.</p>");

        if (string.IsNullOrWhiteSpace(token))
            return Wrap("Missing setup token",
                "<p>Open the one-time URL printed by the server console (or the modal dialog) — it carries the <code>?t=…</code> parameter required to create the operator account.</p>");

        var tok = WebUtility.HtmlEncode(token);
        // method=post + action=/api/auth/bootstrap is the defence against the inline JS
        // failing for any reason (CSP, disabled scripts, syntax error). Without it the
        // browser falls back to GET-to-current-URL and the password ends up in the address
        // bar + history. The bootstrap endpoint will reject form-encoded bodies (it wants
        // JSON), but the password never reaches a URL.
        return Wrap("First-run setup", $@"
<form id=""f"" method=""post"" action=""/api/auth/bootstrap"" autocomplete=""off"">
  <input type=""hidden"" name=""bootstrapToken"" value=""{tok}"">
  <label>Username
    <input name=""username"" required minlength=""3"" maxlength=""32"" pattern=""[A-Za-z0-9_\-]+"" autocomplete=""username"" autofocus>
  </label>
  <label>Password
    <input name=""password"" type=""password"" required minlength=""12"" autocomplete=""new-password"">
  </label>
  <button type=""submit"">Create operator account</button>
  <p id=""msg"" class=""msg""></p>
  <p class=""msg dim"">JavaScript is required to complete setup; without it the POST will return 400 but no credentials leak to a URL.</p>
</form>
<script>
document.getElementById('f').addEventListener('submit', async (ev) => {{
  ev.preventDefault();
  const f = ev.target;
  const msg = document.getElementById('msg');
  msg.className = 'msg';
  msg.textContent = 'Working…';
  try {{
    const r = await fetch('/api/auth/bootstrap', {{
      method: 'POST',
      headers: {{ 'Content-Type': 'application/json' }},
      body: JSON.stringify({{
        username: f.username.value,
        password: f.password.value,
        bootstrapToken: f.bootstrapToken.value
      }})
    }});
    if (r.status === 204) {{
      msg.className = 'msg ok';
      msg.textContent = 'Operator account created. Opening dashboard…';
      f.querySelector('button').disabled = true;
      setTimeout(() => {{ window.location = '/'; }}, 600);
      return;
    }}
    const err = await r.json().catch(() => null);
    msg.className = 'msg err';
    msg.textContent = (err && err.message) || ('Setup failed (HTTP ' + r.status + ').');
  }} catch (e) {{
    msg.className = 'msg err';
    msg.textContent = 'Network error: ' + e.message;
  }}
}});
</script>");
    }

    static string Wrap(string title, string body) => $@"<!doctype html>
<html lang=""en""><head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>cpumon — {WebUtility.HtmlEncode(title)}</title>
<style>
  :root {{ color-scheme: dark; }}
  html, body {{ background:#121216; color:#e6e6f0; font:14px/1.45 'Segoe UI', system-ui, sans-serif; margin:0; height:100%; }}
  body {{ display:flex; align-items:center; justify-content:center; }}
  main {{ background:#24242c; border:1px solid #373741; border-radius:8px; padding:28px 32px; width:min(520px, 92vw); box-shadow:0 8px 24px rgba(0,0,0,.4); }}
  h1 {{ margin:0 0 6px; font-size:18px; color:#50dc8c; letter-spacing:.2px; }}
  h1 + p, p {{ color:#8c8c9b; margin:0 0 18px; }}
  label {{ display:block; margin-bottom:14px; color:#8c8c9b; font-size:12px; text-transform:uppercase; letter-spacing:.6px; }}
  input {{ display:block; margin-top:6px; width:100%; padding:9px 11px; background:#121216; color:#e6e6f0; border:1px solid #373741; border-radius:5px; font:14px Consolas, 'JetBrains Mono', monospace; box-sizing:border-box; }}
  input:focus {{ outline:none; border-color:#50a0ff; }}
  button {{ display:block; width:100%; padding:10px 14px; background:#1e3c1e; color:#50dc8c; border:1px solid #50dc8c; border-radius:5px; font:14px 'Segoe UI', system-ui, sans-serif; cursor:pointer; }}
  button[disabled] {{ opacity:.4; cursor:default; }}
  .msg {{ margin-top:14px; min-height:1.4em; font-size:13px; }}
  .msg.ok  {{ color:#50dc8c; }}
  .msg.err {{ color:#ff5050; }}
  .msg.dim {{ color:#8c8c9b; font-size:11px; margin-top:18px; }}
  code {{ background:#121216; padding:2px 5px; border-radius:3px; color:#ffdc50; font-size:13px; }}
</style>
</head><body><main>
  <h1>cpumon — {WebUtility.HtmlEncode(title)}</h1>
  {body}
</main></body></html>";
}
