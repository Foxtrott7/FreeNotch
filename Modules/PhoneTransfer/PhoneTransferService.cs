using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsNotch.Services;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace WindowsNotch.Modules.PhoneTransfer
{
    public sealed record PhoneTransferFile(string Id, string FilePath, string FileName, long FileSize);

    /// <summary>
    /// Lokaler WLAN-HTTP-Server (TcpListener ohne Admin-Rechte) und QR-Code-Generator
    /// für die direkte Dateiübertragung auf das Smartphone.
    /// </summary>
    public sealed class PhoneTransferService : IDisposable
    {
        private readonly object _lock = new();
        private readonly SemaphoreSlim _clientGate = new(16, 16);
        private readonly List<PhoneTransferFile> _files = new();
        private readonly List<PhoneTransferFile> _receivedFiles = new();
        private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private string _token = string.Empty;
        private int _port;
        private int _downloadCount;
        private string _lastDownloadedFile = string.Empty;
        private string _onlineUrl = string.Empty;
        private bool _isReceiveMode;
        private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

        public event Action? StateChanged;

        public bool IsRunning => _listener != null;
        public bool IsReceiveMode => _isReceiveMode;
        public int DownloadCount => _downloadCount;
        public string LastDownloadedFile => _lastDownloadedFile;
        public string ShareUrl { get; private set; } = string.Empty;
        public string OnlineUrl => _onlineUrl;
        public string ActiveQrUrl => _isReceiveMode ? ShareUrl : (!string.IsNullOrEmpty(_onlineUrl) ? _onlineUrl : ShareUrl);
        public bool IsOnlineMode => !_isReceiveMode && !string.IsNullOrEmpty(_onlineUrl);

        public static string ReceiveFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "WindowsNotch");

        public IReadOnlyList<PhoneTransferFile> GetFiles()
        {
            lock (_lock)
                return _files.ToList();
        }

        public IReadOnlyList<PhoneTransferFile> GetReceivedFiles()
        {
            lock (_lock)
                return _receivedFiles.ToList();
        }

        public void SetReceiveMode(bool receive, bool startIfReceive = true)
        {
            if (_isReceiveMode == receive)
            {
                if (receive && startIfReceive)
                    EnsureServerStarted();
                return;
            }

            _isReceiveMode = receive;
            if (receive && startIfReceive)
                EnsureServerStarted();
            StateChanged?.Invoke();
        }

        public void AddFiles(IEnumerable<string> paths)
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (var raw in paths)
                {
                    if (string.IsNullOrWhiteSpace(raw) || !File.Exists(raw)) continue;
                    if (_files.Any(f => string.Equals(f.FilePath, raw, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var info = new FileInfo(raw);
                    _files.Add(new PhoneTransferFile(
                        Guid.NewGuid().ToString("N")[..8],
                        raw,
                        info.Name,
                        info.Length));
                    changed = true;
                }
            }

            if (changed)
            {
                _onlineUrl = string.Empty;
                StateChanged?.Invoke();
            }
        }

        /// <summary>Startet den lokalen WLAN-HTTP-Server explizit erst nach Nutzerwahl.</summary>
        public void StartLocalServer()
        {
            _onlineUrl = string.Empty;
            EnsureServerStarted();
            StateChanged?.Invoke();
        }

        public void RemoveFile(string id)
        {
            bool empty;
            lock (_lock)
            {
                _files.RemoveAll(f => f.Id == id);
                empty = _files.Count == 0;
            }

            _onlineUrl = string.Empty;
            if (empty)
                StopServer();
            else
                StateChanged?.Invoke();
        }

        public void ClearReceivedFiles()
        {
            lock (_lock)
                _receivedFiles.Clear();
            StateChanged?.Invoke();
        }

        public void ClearAll()
        {
            lock (_lock)
                _files.Clear();

            _onlineUrl = string.Empty;
            if (!_isReceiveMode)
                StopServer();
            else
                StateChanged?.Invoke();
        }

        /// <summary>
        /// Lädt eine einzelne Datei explizit auf Wunsch temporär (1 Std.) hoch,
        /// damit der QR-Code auch außerhalb des lokalen WLANs funktioniert.
        /// </summary>
        public async Task<bool> UploadTemporaryAsync(PhoneTransferFile file, CancellationToken ct = default)
        {
            try
            {
                if (!File.Exists(file.FilePath)) return false;

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent("fileupload"), "reqtype");
                form.Add(new StringContent("1h"), "time");

                await using var fs = new FileStream(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
                using var fileContent = new StreamContent(fs);
                form.Add(fileContent, "fileToUpload", file.FileName);

                using var response = await SharedHttp.PostAsync("https://litterbox.catbox.moe/resources/internals/api.php", form, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return false;

                string result = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
                if (!Uri.TryCreate(result, UriKind.Absolute, out var uploadedUri) ||
                    uploadedUri.Scheme != Uri.UriSchemeHttps)
                    return false;

                _onlineUrl = uploadedUri.AbsoluteUri;
                StateChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return false;
            }
        }

        /// <summary>
        /// Packt mehrere Dateien in ein temporäres ZIP-Archiv und lädt es temporär (1 Std.) hoch,
        /// damit auch Mehrfach-Transfers ohne lokales Netzwerk über einen einzigen QR-Link funktionieren.
        /// </summary>
        public async Task<bool> UploadMultipleAsZipAsync(IReadOnlyList<PhoneTransferFile> files, CancellationToken ct = default)
        {
            if (files.Count == 0) return false;
            if (files.Count == 1) return await UploadTemporaryAsync(files[0], ct).ConfigureAwait(false);

            string tempZip = Path.Combine(Path.GetTempPath(), $"WindowsNotch_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.zip");
            try
            {
                await Task.Run(() =>
                {
                    using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
                    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var f in files)
                    {
                        if (!File.Exists(f.FilePath)) continue;
                        string entryName = f.FileName;
                        if (!usedNames.Add(entryName))
                        {
                            string stem = Path.GetFileNameWithoutExtension(entryName);
                            string ext = Path.GetExtension(entryName);
                            int i = 2;
                            while (!usedNames.Add($"{stem} ({i}){ext}")) i++;
                            entryName = $"{stem} ({i}){ext}";
                        }
                        archive.CreateEntryFromFile(f.FilePath, entryName, CompressionLevel.Fastest);
                    }
                }, ct).ConfigureAwait(false);

                return await UploadTemporaryAsync(new PhoneTransferFile("zip", tempZip, Path.GetFileName(tempZip), new FileInfo(tempZip).Length), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return false;
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }

        public void SwitchToLocalMode()
        {
            if (string.IsNullOrEmpty(_onlineUrl)) return;
            _onlineUrl = string.Empty;
            StateChanged?.Invoke();
        }

        private void EnsureServerStarted()
        {
            if (_listener != null) return;

            try
            {
                _token = CreateShortToken();
                _downloadCount = 0;
                _lastDownloadedFile = string.Empty;

                var listener = new TcpListener(IPAddress.Any, 0);
                listener.Start();
                _port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _listener = listener;
                _cts = new CancellationTokenSource();

                string ip = GetLocalLanIpAddress();
                ShareUrl = $"http://{ip}:{_port}/{_token}";

                _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                StopServer();
            }
        }

        public void StopServer()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _cts?.Dispose();
            _cts = null;
            _listener = null;
            _port = 0;
            ShareUrl = string.Empty;
            StateChanged?.Invoke();
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    if (!await _clientGate.WaitAsync(0, ct).ConfigureAwait(false))
                    {
                        client.Dispose();
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleClientAsync(client, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            _clientGate.Release();
                        }
                    }, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        App.LogError(ex);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 30000;

                try
                {
                    using var stream = client.GetStream();
                    var (headerText, bodyPrefix) = await ReadHttpHeadersAsync(stream, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(headerText)) return;

                    var headerLines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    if (headerLines.Length == 0) return;

                    var parts = headerLines[0].Split(' ');
                    if (parts.Length < 2) return;

                    string method = parts[0].ToUpperInvariant();
                    string fullTarget = parts[1];
                    int qIdx = fullTarget.IndexOf('?');
                    string rawPath = (qIdx >= 0 ? fullTarget[..qIdx] : fullTarget).Trim('/');
                    string query = qIdx >= 0 ? fullTarget[(qIdx + 1)..] : string.Empty;

                    var segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length == 0 || !FixedTimeEqualsToken(segments[0], _token))
                    {
                        await SendTextResponseAsync(stream, 404, "Not Found", L.T("phonetransfer.http_not_found"), ct).ConfigureAwait(false);
                        return;
                    }

                    // Empfangen-Modus: Mobile Upload-Seite & POST-Upload
                    if (_isReceiveMode)
                    {
                        if (method == "POST" && segments.Length >= 2 && segments[1] == "upload")
                        {
                            long contentLength = ParseContentLength(headerLines);
                            string fileName = ParseQueryParam(query, "name") ?? $"upload_{DateTime.Now:HHmmss}.bin";
                            await HandleReceiveUploadAsync(stream, fileName, contentLength, bodyPrefix, ct).ConfigureAwait(false);
                            return;
                        }

                        if (method is "GET" or "HEAD")
                        {
                            string html = BuildReceiveHtml();
                            await SendHtmlResponseAsync(stream, html, method == "HEAD", ct).ConfigureAwait(false);
                            return;
                        }

                        await SendTextResponseAsync(stream, 405, "Method Not Allowed", "Method Not Allowed", ct).ConfigureAwait(false);
                        return;
                    }

                    if (method is not ("GET" or "HEAD"))
                    {
                        await SendTextResponseAsync(stream, 405, "Method Not Allowed", "Method Not Allowed", ct).ConfigureAwait(false);
                        return;
                    }

                    var snapshot = GetFiles();
                    if (snapshot.Count == 0)
                    {
                        await SendTextResponseAsync(stream, 404, "Not Found", L.T("phonetransfer.http_no_files"), ct).ConfigureAwait(false);
                        return;
                    }

                    // Einzeldatei direkt ausliefern oder spezifische Datei per ID
                    if (segments.Length == 1 && snapshot.Count == 1)
                    {
                        await SendFileAsync(stream, snapshot[0], method == "HEAD", ct).ConfigureAwait(false);
                        return;
                    }

                    if (segments.Length >= 2)
                    {
                        var target = snapshot.FirstOrDefault(f => string.Equals(f.Id, segments[1], StringComparison.Ordinal));
                        if (target != null)
                        {
                            await SendFileAsync(stream, target, method == "HEAD", ct).ConfigureAwait(false);
                            return;
                        }
                    }

                    // Mehrere Dateien: kompakte mobile Download-Seite ausliefern
                    if (segments.Length == 1)
                    {
                        string html = BuildIndexHtml(snapshot);
                        await SendHtmlResponseAsync(stream, html, method == "HEAD", ct).ConfigureAwait(false);
                        return;
                    }

                    await SendTextResponseAsync(stream, 404, "Not Found", L.T("phonetransfer.http_file_not_found"), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                {
                    // Client hat Verbindung abgebrochen – normal beim mobilen Browser
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                }
            }
        }

        private async Task SendFileAsync(NetworkStream stream, PhoneTransferFile file, bool headOnly, CancellationToken ct)
        {
            if (!File.Exists(file.FilePath))
            {
                await SendTextResponseAsync(stream, 404, "Not Found", L.T("phonetransfer.http_file_missing"), ct).ConfigureAwait(false);
                return;
            }

            var info = new FileInfo(file.FilePath);
            string asciiFallback = ToAsciiFileName(file.FileName);
            string utf8Encoded = Uri.EscapeDataString(file.FileName);

            var headers = new StringBuilder();
            headers.Append("HTTP/1.1 200 OK\r\n");
            headers.Append("Content-Type: application/octet-stream\r\n");
            headers.Append($"Content-Length: {info.Length}\r\n");
            headers.Append($"Content-Disposition: attachment; filename=\"{asciiFallback}\"; filename*=UTF-8''{utf8Encoded}\r\n");
            headers.Append("Cache-Control: no-store\r\n");
            headers.Append("Connection: close\r\n\r\n");

            byte[] headerBytes = Encoding.UTF8.GetBytes(headers.ToString());
            await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);

            if (!headOnly)
            {
                using var fs = new FileStream(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
                await fs.CopyToAsync(stream, 65536, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                Interlocked.Increment(ref _downloadCount);
                _lastDownloadedFile = file.FileName;
                StateChanged?.Invoke();
            }
        }

        private string BuildIndexHtml(IReadOnlyList<PhoneTransferFile> files)
        {
            string lang = LocalizationService.CurrentLanguage;
            var sb = new StringBuilder();
            sb.Append($"<!DOCTYPE html><html lang=\"{lang}\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append($"<title>{WebUtility.HtmlEncode(L.T("phonetransfer.web_title"))}</title>");
            sb.Append("<style>body{margin:0;padding:24px 16px;background:#000;color:#fff;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;}");
            sb.Append(".wrap{max-width:460px;margin:0 auto;}h1{font-size:20px;margin:0 0 6px;}p{color:#8e8e93;font-size:13px;margin:0 0 18px;}");
            sb.Append(".card{display:flex;align-items:center;justify-content:space-between;background:#111114;border-radius:14px;padding:14px 16px;margin-bottom:10px;text-decoration:none;color:#fff;}");
            sb.Append(".name{font-size:14px;font-weight:600;word-break:break-all;margin-right:12px;}.meta{font-size:12px;color:#8e8e93;margin-top:2px;}");
            sb.Append(".btn{background:#0a84ff;color:#fff;padding:8px 14px;border-radius:999px;font-size:13px;font-weight:600;white-space:nowrap;}</style></head><body><div class=\"wrap\">");
            sb.Append($"<h1>{WebUtility.HtmlEncode(L.T("phonetransfer.web_heading"))}</h1>");
            sb.Append($"<p>{WebUtility.HtmlEncode(L.F("phonetransfer.web_ready", files.Count))}</p>");

            string downloadLabel = WebUtility.HtmlEncode(L.T("phonetransfer.web_download"));
            foreach (var f in files)
            {
                string safeName = WebUtility.HtmlEncode(f.FileName);
                string sizeText = FormatFileSize(f.FileSize);
                sb.Append($"<a class=\"card\" href=\"/{_token}/{f.Id}\"><div><div class=\"name\">{safeName}</div><div class=\"meta\">{sizeText}</div></div><span class=\"btn\">{downloadLabel}</span></a>");
            }

            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        private string BuildReceiveHtml()
        {
            string lang = LocalizationService.CurrentLanguage;
            string title = WebUtility.HtmlEncode(L.T("phonetransfer.web_send_title"));
            string heading = WebUtility.HtmlEncode(L.T("phonetransfer.web_send_heading"));
            string hint = WebUtility.HtmlEncode(L.T("phonetransfer.web_send_hint"));
            string tapSelect = WebUtility.HtmlEncode(L.T("phonetransfer.web_tap_select"));
            string jsSending = System.Text.Json.JsonSerializer.Serialize(L.T("phonetransfer.web_sending"));
            string jsErr = System.Text.Json.JsonSerializer.Serialize(L.T("phonetransfer.web_send_error"));
            string jsSentOne = System.Text.Json.JsonSerializer.Serialize(L.T("phonetransfer.web_sent_one"));
            string jsSentMany = System.Text.Json.JsonSerializer.Serialize(L.T("phonetransfer.web_sent_many"));

            return $$"""
<!DOCTYPE html><html lang="{{lang}}"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{title}}</title>
<style>
body{margin:0;padding:28px 18px;background:#000;color:#fff;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;}
.wrap{max-width:440px;margin:0 auto;}
h1{font-size:20px;margin:0 0 6px;}
p{color:#8e8e93;font-size:13px;margin:0 0 20px;}
.drop{display:block;background:#111114;border:1px dashed #2c2c34;border-radius:16px;padding:28px 18px;text-align:center;cursor:pointer;}
.btn{display:inline-block;margin-top:14px;background:#fff;color:#000;padding:10px 20px;border-radius:999px;font-size:14px;font-weight:600;border:none;cursor:pointer;}
.status{margin-top:16px;font-size:13px;color:#30d158;min-height:20px;}
input[type=file]{display:none;}
</style></head><body><div class="wrap">
<h1>{{heading}}</h1>
<p>{{hint}}</p>
<label class="drop">
  <div>{{tapSelect}}</div>
  <input id="f" type="file" multiple>
</label>
<div id="st" class="status"></div>
<script>
const inp=document.getElementById('f'),st=document.getElementById('st');
const tSend={{jsSending}},tErr={{jsErr}},tOne={{jsSentOne}},tMany={{jsSentMany}};
inp.addEventListener('change',async()=>{
  const files=[...inp.files];if(!files.length)return;
  for(let i=0;i<files.length;i++){
    const file=files[i];
    st.style.color='#8e8e93';
    st.textContent=`${tSend} ${i+1}/${files.length}: ${file.name}…`;
    const r=await fetch('/{{_token}}/upload?name='+encodeURIComponent(file.name),{method:'POST',body:file});
    if(!r.ok){st.style.color='#ff453a';st.textContent=tErr+file.name;return;}
  }
  st.style.color='#30d158';
  st.textContent=files.length===1?tOne+files[0].name:files.length+tMany;
  inp.value='';
});
</script></div></body></html>
""";
        }

        private static async Task<(string Headers, byte[] BodyPrefix)> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            var mem = new MemoryStream();
            var buf = new byte[2048];
            while (mem.Length < 32768)
            {
                int read = await stream.ReadAsync(buf, timeoutCts.Token).ConfigureAwait(false);
                if (read <= 0) break;
                mem.Write(buf, 0, read);

                var data = mem.GetBuffer();
                int len = (int)mem.Length;
                for (int i = 0; i <= len - 4; i++)
                {
                    if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                    {
                        string headers = Encoding.ASCII.GetString(data, 0, i);
                        int prefixLen = len - (i + 4);
                        byte[] prefix = prefixLen > 0 ? data[(i + 4)..len] : Array.Empty<byte>();
                        return (headers, prefix);
                    }
                }
            }
            return (string.Empty, Array.Empty<byte>());
        }

        private static long ParseContentLength(string[] headerLines)
        {
            foreach (var line in headerLines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(line["Content-Length:".Length..].Trim(), out long len) && len >= 0)
                        return len;
                }
            }
            return -1;
        }

        private static string? ParseQueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(pair[..eq], key, StringComparison.OrdinalIgnoreCase))
                {
                    try { return Uri.UnescapeDataString(pair[(eq + 1)..]); }
                    catch (UriFormatException) { return null; }
                }
            }
            return null;
        }

        internal static string SanitizeUploadFileName(string? rawFileName)
        {
            string trimmed = (rawFileName ?? string.Empty).Replace('\\', '/');
            int slash = trimmed.LastIndexOf('/');
            string baseName = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
            baseName = Path.GetFileName(baseName);

            foreach (char c in Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(c, '_');

            baseName = baseName.Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(baseName) || baseName is "." or "..")
                return $"upload_{DateTime.Now:HHmmss}.bin";

            string stem = Path.GetFileNameWithoutExtension(baseName);
            if (ReservedDeviceNames.Contains(stem))
                baseName = "_" + baseName;

            return baseName;
        }

        internal static bool IsPathInsideDirectory(string candidatePath, string rootDirectory)
        {
            string fullRoot = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
            string fullCandidate = Path.GetFullPath(candidatePath);
            return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }

        private async Task HandleReceiveUploadAsync(NetworkStream stream, string rawFileName, long contentLength, byte[] bodyPrefix, CancellationToken ct)
        {
            if (contentLength < 0 || contentLength > 512L * 1024 * 1024)
            {
                await SendTextResponseAsync(stream, 400, "Bad Request", L.T("phonetransfer.http_invalid_size"), ct).ConfigureAwait(false);
                return;
            }

            Directory.CreateDirectory(ReceiveFolder);
            string safeName = SanitizeUploadFileName(rawFileName);
            string destPath = Path.Combine(ReceiveFolder, safeName);
            string stem = Path.GetFileNameWithoutExtension(safeName);
            string ext = Path.GetExtension(safeName);
            int suffix = 1;
            while (File.Exists(destPath))
                destPath = Path.Combine(ReceiveFolder, $"{stem} ({suffix++}){ext}");

            if (!IsPathInsideDirectory(destPath, ReceiveFolder))
            {
                await SendTextResponseAsync(stream, 400, "Bad Request", L.T("phonetransfer.http_not_found"), ct).ConfigureAwait(false);
                return;
            }

            long remaining = contentLength;
            await using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                if (bodyPrefix.Length > 0)
                {
                    int take = (int)Math.Min(bodyPrefix.Length, remaining);
                    await fs.WriteAsync(bodyPrefix.AsMemory(0, take), ct).ConfigureAwait(false);
                    remaining -= take;
                }

                byte[] buf = new byte[65536];
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    int read = await stream.ReadAsync(buf.AsMemory(0, toRead), ct).ConfigureAwait(false);
                    if (read <= 0) break;
                    await fs.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                    remaining -= read;
                }
            }

            if (remaining > 0)
            {
                try { File.Delete(destPath); } catch { }
                await SendTextResponseAsync(stream, 400, "Bad Request", L.T("phonetransfer.http_incomplete_upload"), ct).ConfigureAwait(false);
                return;
            }

            var info = new FileInfo(destPath);
            lock (_lock)
            {
                _receivedFiles.Insert(0, new PhoneTransferFile(
                    Guid.NewGuid().ToString("N")[..8],
                    destPath,
                    info.Name,
                    info.Length));
            }

            await SendTextResponseAsync(stream, 200, "OK", "OK", ct).ConfigureAwait(false);
            StateChanged?.Invoke();
        }

        private static async Task SendHtmlResponseAsync(NetworkStream stream, string html, bool headOnly, CancellationToken ct)
        {
            byte[] body = Encoding.UTF8.GetBytes(html);
            string header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
            if (!headOnly)
                await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }

        private static async Task SendTextResponseAsync(NetworkStream stream, int code, string status, string text, CancellationToken ct)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            string header = $"HTTP/1.1 {code} {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }

        /// <summary>Erzeugt eine gestochen scharfe QR-Code-Bitmap für WPF.</summary>
        public static BitmapSource GenerateQrBitmap(string content, int size = 180)
        {
            var hints = new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
                [EncodeHintType.MARGIN] = 1,
                [EncodeHintType.CHARACTER_SET] = "UTF-8"
            };

            BitMatrix matrix = new QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, size, size, hints);
            int w = matrix.Width;
            int h = matrix.Height;
            int stride = w * 4;
            byte[] pixels = new byte[h * stride];

            for (int y = 0; y < h; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte val = matrix[x, y] ? (byte)0x00 : (byte)0xFF;
                    int idx = rowOffset + x * 4;
                    pixels[idx] = val;
                    pixels[idx + 1] = val;
                    pixels[idx + 2] = val;
                    pixels[idx + 3] = 0xFF;
                }
            }

            var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
            wb.Freeze();
            return wb;
        }

        public static string GetLocalLanIpAddress()
        {
            try
            {
                var candidates = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                              && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                              && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    .ThenByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet);

                foreach (var ni in candidates)
                {
                    var props = ni.GetIPProperties();
                    bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] b = ua.Address.GetAddressBytes();
                        if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;
                        if (hasGateway) return ua.Address.ToString();
                    }
                }

                // Fallback ohne Gateway-Prüfung
                foreach (var ni in candidates)
                {
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] b = ua.Address.GetAddressBytes();
                        if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;
                        return ua.Address.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }

            return "127.0.0.1";
        }

        public static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        private static bool FixedTimeEqualsToken(string candidate, string expected)
        {
            if (string.IsNullOrEmpty(expected)) return false;
            byte[] a = Encoding.UTF8.GetBytes(candidate);
            byte[] b = Encoding.UTF8.GetBytes(expected);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static string CreateShortToken()
        {
            Span<byte> buf = stackalloc byte[8];
            RandomNumberGenerator.Fill(buf);
            return Convert.ToHexString(buf).ToLowerInvariant();
        }

        private static string ToAsciiFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(c >= 32 && c < 127 && c is not ('"' or '\\' or ';') ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "download";
        }

        public void Dispose()
        {
            StopServer();
        }
    }
}
