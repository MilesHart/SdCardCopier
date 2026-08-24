using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SDCardImporter;

/// <summary>
/// Minimal HTTP server for progress monitoring. Uses TcpListener to avoid HttpListener's Host-header "Invalid Hostname" on LAN.
/// </summary>
public class WebServer
{
    private TcpListener? _listener;
    private readonly int _preferredPort;
    private int _boundPort;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private static readonly SemaphoreSlim _concurrency = new(20, 20);

    /// <summary>Port the server actually bound to (after Start). May differ if preferred port was in use.</summary>
    public int BoundPort => _boundPort;

    public WebServer(int port = 5050)
    {
        _preferredPort = port;
        _boundPort = port;
    }

    /// <summary>Starts the web server. Tries preferred port, then 5051, 5052 if in use. Returns true if started.</summary>
    public bool Start()
    {
        foreach (var port in new[] { _preferredPort, 5051, 5052 }.Distinct().OrderBy(p => p))
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _boundPort = port;
                _cts = new CancellationTokenSource();
                _runTask = Task.Run(() => RunAsync(_cts.Token));
                var ip = GetLanIp();
                Console.WriteLine($"Web UI: http://localhost:{_boundPort}/");
                if (!string.IsNullOrEmpty(ip))
                    Console.WriteLine($"         http://{ip}:{_boundPort}/ (LAN access)");
                if (port != _preferredPort)
                    Console.WriteLine($"         (Port {_preferredPort} was in use, using {_boundPort})");
                return true;
            }
            catch (Exception ex)
            {
                if (port == _preferredPort)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Web server: port {port} failed ({ex.Message}), trying alternatives...");
                    Console.ResetColor();
                }
                try { _listener?.Stop(); } catch { }
                _listener = null;
            }
        }
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Web server failed to start on any port (5050, 5051, 5052). Check firewall or free a port.");
        Console.ResetColor();
        return false;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _runTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    private static string? GetLanIp()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var ip = host.AddressList.FirstOrDefault(a =>
                a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            return ip?.ToString();
        }
        catch { /* ignore */ }
        return null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientWithSemaphoreAsync(client);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Console.WriteLine($"Web server error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientWithSemaphoreAsync(TcpClient client)
    {
        await _concurrency.WaitAsync();
        try { await HandleClientAsync(client); }
        finally { _concurrency.Release(); }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                using (var stream = client.GetStream())
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                {
                var buffer = new byte[4096];
                var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
                if (count <= 0) return;

                var request = Encoding.UTF8.GetString(buffer, 0, count);
                var path = ParsePath(request);
                byte[] body;
                string contentType;
                int status = 200;

                if (path == "/api/status" || path == "/api/status/")
                {
                    var statusObj = ProgressMonitor.GetSnapshot();
                    var json = JsonSerializer.Serialize(statusObj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    body = Encoding.UTF8.GetBytes(json);
                    contentType = "application/json";
                }
                else if (path == "/" || path == "")
                {
                    body = Encoding.UTF8.GetBytes(GetHtml());
                    contentType = "text/html";
                }
                else
                {
                    body = Encoding.UTF8.GetBytes("Not Found");
                    contentType = "text/plain";
                    status = 404;
                }

                var headers = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                    $"Content-Type: {contentType}; charset=utf-8\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n" +
                    "Cache-Control: no-cache\r\n\r\n";
                var headerBytes = Encoding.UTF8.GetBytes(headers);
                await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), cts.Token);
                await stream.WriteAsync(body.AsMemory(0, body.Length), cts.Token);
                }
            }
        }
        catch (Exception ex)
        {
            try { client.Dispose(); } catch { }
            if (ex is not OperationCanceledException and not ObjectDisposedException)
                Console.WriteLine($"Web request error: {ex.Message}");
        }
    }

    private static string ParsePath(string request)
    {
        var firstLine = request.Split('\n')[0];
        var parts = firstLine.Split(' ');
        if (parts.Length >= 2)
        {
            var rawPath = parts[1];
            var queryStart = rawPath.IndexOf('?');
            var path = queryStart >= 0 ? rawPath[..queryStart] : rawPath;
            return path.TrimEnd('/') switch { "" => "/", var p => p };
        }
        return "/";
    }

    private static string GetHtml()
    {
        return """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="color-scheme" content="dark">
  <title>SD Card Importer</title>
  <style>
    :root {
      --bg: #0c0c0f;
      --surface: #141418;
      --card: #1a1a20;
      --border: #2a2a32;
      --text: #e8e8ed;
      --muted: #9898a4;
      --accent: #3ee8a9;
      --accent-dim: #2bb87a;
      --warn: #f5c542;
      --info: #6cb3ff;
    }
    * { box-sizing: border-box; }
    body {
      font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
      margin: 0;
      min-height: 100vh;
      background: var(--bg);
      color: var(--text);
      line-height: 1.45;
      font-size: 15px;
    }
    .wrap {
      max-width: 1100px;
      margin: 0 auto;
      padding: 1rem 1.1rem 2.5rem;
    }
    @media (min-width: 960px) {
      .wrap { padding: 1.5rem 1.75rem 3rem; }
      .grid {
        display: grid;
        grid-template-columns: 1fr min(380px, 34vw);
        gap: 1.25rem;
        align-items: start;
      }
    }
    header {
      margin-bottom: 1.25rem;
      display: flex;
      flex-wrap: wrap;
      align-items: flex-start;
      gap: 0.75rem 1rem;
    }
    header h1 {
      font-size: clamp(1.15rem, 2.5vw, 1.45rem);
      font-weight: 650;
      margin: 0;
      letter-spacing: -0.02em;
    }
    .sub {
      width: 100%;
      flex-basis: 100%;
      color: var(--muted);
      font-size: 0.88rem;
    }
    .dest {
      font-family: ui-monospace, 'Cascadia Code', monospace;
      font-size: 0.8rem;
      color: var(--accent);
      word-break: break-all;
      margin-top: 0.35rem;
    }
    .badges { display: flex; flex-wrap: wrap; gap: 0.4rem; align-items: center; }
    .badge {
      font-size: 0.72rem;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      padding: 0.28rem 0.55rem;
      border-radius: 6px;
      background: var(--surface);
      border: 1px solid var(--border);
      color: var(--muted);
    }
    .badge.live-host { border-color: var(--accent-dim); color: var(--accent); }
    .card {
      background: var(--card);
      border: 1px solid var(--border);
      border-radius: 12px;
      padding: 1.1rem 1.2rem;
      margin-bottom: 1rem;
    }
    .card h2 {
      font-size: 0.78rem;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--muted);
      margin: 0 0 0.75rem;
      font-weight: 600;
    }
    .status { font-weight: 600; font-size: 1.05rem; margin-bottom: 0.35rem; }
    .status.idle { color: var(--muted); }
    .status.detecting { color: var(--warn); }
    .status.copying { color: var(--accent); }
    .status.complete { color: var(--info); }
    .info { color: var(--muted); font-size: 0.9rem; margin-top: 0.25rem; }
    .progress-wrap { margin: 0.85rem 0; }
    .progress-bar { height: 10px; background: #25252c; border-radius: 6px; overflow: hidden; }
    .progress-fill {
      height: 100%;
      background: linear-gradient(90deg, var(--accent-dim), var(--accent));
      border-radius: 6px;
      transition: width 0.35s ease;
    }
    .progress-text { font-size: 0.8rem; color: var(--muted); margin-top: 0.35rem; }
    .heartbeat {
      font-size: 0.78rem;
      color: var(--muted);
      margin-top: 1rem;
      display: flex;
      align-items: center;
      gap: 0.4rem;
    }
    .heartbeat.live { color: var(--accent); }
    .heartbeat.stale { color: var(--warn); }
    .heartbeat.dead { color: #f87171; }
    .heartbeat-dot { width: 7px; height: 7px; border-radius: 50%; background: currentColor; flex-shrink: 0; }
    .heartbeat.live .heartbeat-dot { animation: pulse 1.4s ease-in-out infinite; }
    @keyframes pulse { 0%,100% { opacity: 1; } 50% { opacity: 0.35; } }
    .log {
      margin-top: 1rem;
      max-height: 180px;
      overflow-y: auto;
      font-family: ui-monospace, monospace;
      font-size: 0.72rem;
      line-height: 1.5;
      color: #b4b4c0;
      background: #101014;
      border-radius: 8px;
      padding: 0.65rem 0.75rem;
      border: 1px solid var(--border);
    }
    .log:empty { display: none; }
    .file-list { margin-top: 1rem; max-height: 220px; overflow-y: auto; font-size: 0.8rem; }
    .file-list .file-item { margin: 0.3rem 0; display: flex; align-items: center; gap: 0.5rem; min-height: 28px; }
    .file-list .file-name { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--muted); }
    .file-list .file-badge { flex-shrink: 0; font-size: 0.68rem; padding: 0.18rem 0.45rem; border-radius: 4px; font-weight: 500; }
    .file-list .file-badge.copied { background: #14532d; color: #86efac; }
    .file-list .file-badge.skipped { background: #713f12; color: #fde047; }
    table.ref { width: 100%; border-collapse: collapse; font-size: 0.82rem; }
    table.ref th, table.ref td { text-align: left; padding: 0.45rem 0.35rem; border-bottom: 1px solid var(--border); vertical-align: top; }
    table.ref th { color: var(--muted); font-weight: 600; font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.04em; }
    table.ref td .mono { font-family: ui-monospace, monospace; font-size: 0.78rem; color: #c4c4d4; }
    .pattern { font-family: ui-monospace, monospace; font-size: 0.8rem; background: #101014; padding: 0.65rem 0.85rem; border-radius: 8px; border: 1px solid var(--border); overflow-x: auto; color: #c4c4d4; }
    .tip { display: none; font-size: 0.86rem; color: #c8c8d4; }
    .tip p { margin: 0.5rem 0; }
    .tip ul { margin: 0.35rem 0 1.1rem; padding: 0; }
    .tip li { margin: 0.35rem 0; }
    body.host-pi .tip-pi { display: block; }
    body.host-windows .tip-windows { display: block; }
    body.host-pc-linux .tip-pc-linux { display: block; }
    .ref-note { font-size: 0.8rem; color: var(--muted); margin-top: 0.75rem; }
    @media (max-width: 599px) {
      table.ref { display: block; }
      table.ref tbody, table.ref tr, table.ref td { display: block; }
      table.ref tr { margin-bottom: 0.75rem; border-bottom: 1px solid var(--border); padding-bottom: 0.5rem; }
      table.ref td { border: 0; padding: 0.2rem 0; }
      table.ref th { display: none; }
      table.ref td::before {
        content: attr(data-label);
        display: block;
        font-size: 0.65rem;
        text-transform: uppercase;
        color: var(--muted);
        margin-bottom: 0.15rem;
      }
    }
  </style>
</head>
<body>
  <div class="wrap">
    <header>
      <div style="flex:1;min-width:200px">
        <h1>SD Card Importer</h1>
        <p class="sub">FPV and action camera media → organized folders. USB SD readers supported.</p>
        <div id="destLine" class="dest" style="display:none"></div>
      </div>
      <div class="badges">
        <span id="hostBadge" class="badge">…</span>
        <span class="badge live-host">Watch mode</span>
      </div>
    </header>
    <div class="grid">
      <div class="main-col">
        <div class="card">
          <h2>Live status</h2>
          <div id="status" class="status idle">Idle</div>
          <div id="detail" class="info">Waiting for SD card...</div>
          <div class="progress-wrap" id="progressWrap" style="display:none">
            <div class="progress-bar"><div id="progressFill" class="progress-fill" style="width:0%"></div></div>
            <div id="progressText" class="progress-text"></div>
          </div>
          <div id="heartbeat" class="heartbeat">
            <span class="heartbeat-dot"></span>
            <span id="heartbeatText">Connecting…</span>
          </div>
          <div id="activityLog" class="log" aria-live="polite"></div>
          <div id="fileListWrap" class="file-list" style="display:none"></div>
        </div>
      </div>
      <aside class="side-col">
        <div class="card">
          <h2>This machine</h2>
          <div class="tip tip-pi">
            <p><strong>Raspberry Pi (Linux ARM)</strong></p>
            <ul>
              <li>Default destination if unset: <span class="mono">~/FPVFootage</span></li>
              <li>Drives under <span class="mono">/media</span>, <span class="mono">/mnt</span>, <span class="mono">/run/media/…</span></li>
              <li>Network shares must be mounted first; point <span class="mono">DESTINATION_PATH</span> at the mount (not a dead UNC path).</li>
              <li>Watch mode waits for USB drives to become ready after insert.</li>
            </ul>
          </div>
          <div class="tip tip-windows">
            <p><strong>Windows PC</strong></p>
            <ul>
              <li>Default destination: <span class="mono">Documents\FPVFootage</span></li>
              <li>USB SD adapters detected via WMI; falls back to removable drives.</li>
            </ul>
          </div>
          <div class="tip tip-pc-linux">
            <p><strong>Linux (desktop / server)</strong></p>
            <ul>
              <li>Same mount paths as Pi; this host is not ARM — tips above still apply.</li>
            </ul>
          </div>
          <p class="ref-note">Telegram: set <span class="mono">TELEGRAM_CHAT_ID</span> (optional <span class="mono">TELEGRAM_BOT_TOKEN</span>). Use <span class="mono">WEB_URL</span> in <span class="mono">.env</span> so links use your LAN IP.</p>
        </div>
        <div class="card">
          <h2>Output pattern</h2>
          <div class="pattern mono">{destination}/{year}/{Jan|Feb|…}/{day}/{DeviceFolder}/</div>
          <p class="ref-note">Example: <span class="mono">…/2026/Feb/16/GoggleDJI/DJI_0001.MP4</span></p>
        </div>
      </aside>
    </div>
    <div class="card">
      <h2>Supported devices</h2>
      <table class="ref">
        <thead><tr><th>Device</th><th>Folder</th><th>Detection</th></tr></thead>
        <tbody>
          <tr><td data-label="Device">DJI Goggles 3</td><td data-label="Folder"><span class="mono">GoggleDJI</span></td><td data-label="Detection">DCIM /100MEDIA, DJI_*</td></tr>
          <tr><td data-label="Device">DJI Flip</td><td data-label="Folder"><span class="mono">DJIFlip</span></td><td data-label="Detection">Metadata / file patterns</td></tr>
          <tr><td data-label="Device">SkyZone Analog FPV</td><td data-label="Folder"><span class="mono">GoggleSZ</span></td><td data-label="Detection">MOV/AVI in root or VIDEO</td></tr>
          <tr><td data-label="Device">BetaPavo20 Pro (DJI O4)</td><td data-label="Folder"><span class="mono">DJI04</span></td><td data-label="Detection">DCIM, SRT, large 4K</td></tr>
          <tr><td data-label="Device">GoPro (Hero family, incl. Session 5)</td><td data-label="Folder"><span class="mono">GP13</span></td><td data-label="Detection">100GOPRO, GOPR*/GX*</td></tr>
          <tr><td data-label="Device">Generic / other</td><td data-label="Folder"><span class="mono">Other</span></td><td data-label="Detection">Unrecognized card with media</td></tr>
        </tbody>
      </table>
    </div>
    <div class="card">
      <h2>CLI quick reference</h2>
      <table class="ref">
        <thead><tr><th>Option</th><th>Meaning</th></tr></thead>
        <tbody>
          <tr><td data-label="Option"><span class="mono">-d</span> / <span class="mono">--destination</span></td><td data-label="Meaning">Footage root folder</td></tr>
          <tr><td data-label="Option"><span class="mono">-w</span> / <span class="mono">--watch</span></td><td data-label="Meaning">Keep running; import on insert</td></tr>
          <tr><td data-label="Option"><span class="mono">-y</span> / <span class="mono">--yes</span></td><td data-label="Meaning">Auto-confirm copy</td></tr>
          <tr><td data-label="Option"><span class="mono">--web</span></td><td data-label="Meaning">This dashboard (port 5050 or <span class="mono">WEB_PORT</span>)</td></tr>
          <tr><td data-label="Option"><span class="mono">-p</span> / <span class="mono">--port</span></td><td data-label="Meaning">Web UI port</td></tr>
          <tr><td data-label="Option"><span class="mono">--safe</span></td><td data-label="Meaning">Dry run (plan only)</td></tr>
        </tbody>
      </table>
      <p class="ref-note">No arguments: watch + auto-confirm + web UI (see project README).</p>
    </div>
  </div>
  <script>
    let lastHeartbeat = 0;
    function applyHostClass(d) {
      document.body.className = '';
      var hb = document.getElementById('hostBadge');
      if (d.hostProfile === 'pi') {
        document.body.classList.add('host-pi');
        hb.textContent = 'Raspberry Pi';
        hb.className = 'badge live-host';
      } else if (d.hostOs === 'windows') {
        document.body.classList.add('host-windows');
        hb.textContent = 'Windows PC';
        hb.className = 'badge live-host';
      } else if (d.hostOs === 'linux') {
        document.body.classList.add('host-pc-linux');
        hb.textContent = 'Linux';
        hb.className = 'badge live-host';
      } else {
        hb.textContent = 'Importer';
        hb.className = 'badge';
      }
      var dl = document.getElementById('destLine');
      if (d.destinationPath) {
        dl.style.display = 'block';
        dl.textContent = 'Destination: ' + d.destinationPath;
      } else {
        dl.style.display = 'none';
      }
    }
    async function poll() {
      try {
        const r = await fetch('/api/status');
        const d = await r.json();
        lastHeartbeat = d.serverTime ? new Date(d.serverTime).getTime() : Date.now();
        applyHostClass(d);
        const s = document.getElementById('status');
        const detail = document.getElementById('detail');
        const wrap = document.getElementById('progressWrap');
        const fill = document.getElementById('progressFill');
        const text = document.getElementById('progressText');
        const raw = (d.status || 'idle').toLowerCase();
        const statusLabel = raw.charAt(0).toUpperCase() + raw.slice(1);
        s.textContent = statusLabel;
        s.className = 'status ' + raw.replace(/\s+/g, '');
        if (d.status === 'copying') {
          wrap.style.display = 'block';
          fill.style.width = (d.percent || 0) + '%';
          const spd = d.bytesPerSecond ? ' @ ' + formatSpeed(d.bytesPerSecond) : '';
          text.textContent = d.fileIndex + '/' + d.totalFiles + ' files, ' + formatBytes(d.bytesCopied) + ' / ' + formatBytes(d.totalBytes) + spd + (d.currentFile ? ' — ' + d.currentFile : '');
          detail.textContent = (d.deviceType ? d.deviceType + ' — ' : '') + (d.sourcePath || '');
        } else if (d.status === 'detecting') {
          detail.textContent = d.sourcePath || 'Scanning…';
          wrap.style.display = 'none';
        } else if (d.status === 'idle') {
          detail.textContent = d.lastMessage || 'Waiting for SD card…';
          wrap.style.display = 'none';
        } else if (d.status === 'complete') {
          detail.textContent = d.lastMessage || 'Done.';
          wrap.style.display = 'none';
        }
        const logEl = document.getElementById('activityLog');
        if (d.recentMessages && d.recentMessages.length) {
          logEl.textContent = d.recentMessages.slice(-40).join('\n');
          logEl.scrollTop = logEl.scrollHeight;
        } else {
          logEl.textContent = '';
        }
        const fileListWrap = document.getElementById('fileListWrap');
        if (d.fileResults && d.fileResults.length > 0) {
          fileListWrap.style.display = 'block';
          fileListWrap.innerHTML = '<div style="font-weight:600;margin-bottom:0.5rem;color:#9898a4;font-size:0.8rem">Files</div>' + d.fileResults.map(f => '<div class="file-item"><span class="file-name" title="' + escapeHtml(f.name) + '">' + escapeHtml(f.name) + '</span><span class="file-badge ' + (f.skipped ? 'skipped' : 'copied') + '">' + (f.skipped ? 'Skipped' : 'Copied') + '</span></div>').join('');
        } else {
          fileListWrap.style.display = 'none';
          fileListWrap.innerHTML = '';
        }
        updateHeartbeat();
      } catch (e) {
        updateHeartbeat();
      }
    }
    function updateHeartbeat() {
      const el = document.getElementById('heartbeat');
      const txt = document.getElementById('heartbeatText');
      const age = lastHeartbeat ? Math.floor((Date.now() - lastHeartbeat) / 1000) : -1;
      if (age < 0) {
        el.className = 'heartbeat';
        txt.textContent = 'Connecting…';
      } else if (age <= 5) {
        el.className = 'heartbeat live';
        txt.textContent = age === 0 ? 'Live' : 'Updated ' + age + 's ago';
      } else if (age <= 15) {
        el.className = 'heartbeat stale';
        txt.textContent = 'Updated ' + age + 's ago';
      } else {
        el.className = 'heartbeat dead';
        txt.textContent = 'Updated ' + age + 's ago — server may be unresponsive';
      }
    }
    function formatSpeed(bps) {
      if (bps < 1024) return bps.toFixed(0) + ' B/s';
      if (bps < 1048576) return (bps/1024).toFixed(1) + ' KB/s';
      return (bps/1048576).toFixed(1) + ' MB/s';
    }
    function formatBytes(n) {
      if (n < 1024) return n + ' B';
      if (n < 1048576) return (n/1024).toFixed(1) + ' KB';
      if (n < 1073741824) return (n/1048576).toFixed(1) + ' MB';
      return (n/1073741824).toFixed(2) + ' GB';
    }
    function escapeHtml(s) {
      const div = document.createElement('div');
      div.textContent = s || '';
      return div.innerHTML;
    }
    poll();
    setInterval(poll, 2000);
    setInterval(updateHeartbeat, 1000);
  </script>
</body>
</html>
""";
    }
}
