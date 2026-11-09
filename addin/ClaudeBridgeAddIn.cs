using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Environment;
using ABB.Robotics.RobotStudio.Stations;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.RapidDomain;
using ABB.Robotics.Controllers.Discovery;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtualController = ABB.Robotics.RobotStudio.Stations.VirtualController;

namespace ClaudeBridge
{
    public partial class ClaudeBridgeAddIn
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private const int Port = 58080;
        private static readonly Queue<Action> _uiQueue = new Queue<Action>();
        private Thread _serverThread;
        private bool _shuttingDown;

        // ── Add-In Entry Point ───────────────────────────────────

        public static void AddinMain()
        {
            var addIn = new ClaudeBridgeAddIn();
            addIn.Start();
            Logger.AddMessage(new LogMessage("ClaudeBridge HTTP server started on port " + Port + " (TcpListener)"));
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();

            // Process queued UI-thread actions on the Idle event
            UIEnvironment.Idle += OnIdle;

            // Start server on background thread
            _serverThread = new Thread(() => ListenLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "ClaudeBridge-Server"
            };
            _serverThread.Start();
        }

        public void Shutdown()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            UIEnvironment.Idle -= OnIdle;

            Logger.AddMessage(new LogMessage("ClaudeBridge HTTP server stopped"));
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            lock (_uiQueue)
            {
                while (_uiQueue.Count > 0)
                {
                    var action = _uiQueue.Dequeue();
                    try { action(); }
                    catch { /* handled in RunOnUIThread */ }
                }
            }
        }

        // Marshal to UI thread via the Idle event queue
        private T RunOnUIThread<T>(Func<T> func, int timeoutSeconds = 30)
        {
            T result = default;
            Exception error = null;
            var done = new ManualResetEventSlim(false);

            lock (_uiQueue)
            {
                _uiQueue.Enqueue(() =>
                {
                    try { result = func(); }
                    catch (Exception ex) { error = ex; }
                    finally { done.Set(); }
                });
            }

            if (!done.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                throw new TimeoutException("UI thread did not respond within " + timeoutSeconds + " seconds");

            if (error != null)
                throw new Exception(error.Message, error);
            return result;
        }

        // ── TcpListener Server Loop ─────────────────────────────

        private void ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_listener.Pending())
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch (SocketException) when (ct.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Logger.AddMessage(new LogMessage("ClaudeBridge error: " + ex.Message));
                }
            }
        }

        // ── Manual HTTP Parser ──────────────────────────────────

        private void HandleClient(TcpClient client)
        {
            string method = "GET";
            string path = "/";
            string body = null;
            string origin = null;
            var query = new NameValueCollection();
            int statusCode = 200;

            try
            {
                client.NoDelay = true;
                client.LingerState = new LingerOption(true, 1);
                using (client)
                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = 30000;
                    stream.WriteTimeout = 30000;

                    // Read until \r\n\r\n (header terminator)
                    var buffer = new byte[8192];
                    var headerBuffer = new StringBuilder();
                    int totalRead = 0;
                    bool headersComplete = false;
                    int contentLength = 0;

                    int lastBytesRead = 0;
                    while (totalRead < 65536 && !headersComplete)
                    {
                        lastBytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (lastBytesRead == 0) break;
                        headerBuffer.Append(Encoding.ASCII.GetString(buffer, 0, lastBytesRead));
                        totalRead += lastBytesRead;

                        var text = headerBuffer.ToString();
                        int headerEnd = text.IndexOf("\r\n\r\n");
                        if (headerEnd >= 0)
                        {
                            headersComplete = true;
                            // Parse headers
                            var headerSection = text.Substring(0, headerEnd);
                            var lines = headerSection.Split(new[] { "\r\n" }, StringSplitOptions.None);

                            // Request line: METHOD /path?query HTTP/1.1
                            if (lines.Length > 0)
                            {
                                var parts = lines[0].Split(' ');
                                if (parts.Length >= 2)
                                {
                                    method = parts[0].ToUpper();
                                    var fullPath = parts[1];
                                    int qIdx = fullPath.IndexOf('?');
                                    if (qIdx >= 0)
                                    {
                                        path = fullPath.Substring(0, qIdx);
                                        ParseQueryString(fullPath.Substring(qIdx + 1), query);
                                    }
                                    else
                                    {
                                        path = fullPath;
                                    }
                                }
                            }

                            // Parse header lines
                            for (int i = 1; i < lines.Length; i++)
                            {
                                var colonIdx = lines[i].IndexOf(':');
                                if (colonIdx > 0)
                                {
                                    var key = lines[i].Substring(0, colonIdx).Trim();
                                    var val = lines[i].Substring(colonIdx + 1).Trim();
                                    if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                                        int.TryParse(val, out contentLength);
                                    else if (key.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                                        origin = val;
                                }
                            }

                            // Read body: headerEnd+4 is where body starts in accumulated text
                            // Body bytes in the last buffer read start at offset: lastBytesRead - (totalRead - headerEnd - 4)
                            if (contentLength > 0)
                            {
                                var headerLen = headerEnd + 4; // includes \r\n\r\n
                                var bodyInLastRead = totalRead - headerLen;
                                var bodyBytes = new byte[contentLength];
                                int bodyPos = 0;

                                if (bodyInLastRead > 0)
                                {
                                    int bufferBodyStart = lastBytesRead - bodyInLastRead;
                                    int copyCount = Math.Min(bodyInLastRead, contentLength);
                                    Array.Copy(buffer, bufferBodyStart, bodyBytes, 0, copyCount);
                                    bodyPos = copyCount;
                                }

                                while (bodyPos < contentLength)
                                {
                                    int n = stream.Read(bodyBytes, bodyPos, contentLength - bodyPos);
                                    if (n == 0) break;
                                    bodyPos += n;
                                }
                                body = Encoding.UTF8.GetString(bodyBytes, 0, bodyPos);
                            }
                        }
                    }

                    path = path.TrimEnd('/').ToLower();
                    if (string.IsNullOrEmpty(path)) path = "/";

                    // Handle CORS preflight
                    if (method == "OPTIONS")
                    {
                        SendResponse(client, 200, new { status = "ok" }, origin);
                        return;
                    }

                    // Route to handler on UI thread
                    object result;
                    try
                    {
                        // Geometry analysis of large imported parts can take minutes
                        int uiTimeout = path == "/station/object/parts" ? 300 : 30;
                        result = RunOnUIThread(() =>
                            Route(path, method, body, query), uiTimeout);
                    }
                    catch (Exception ex)
                    {
                        result = new { error = ex.Message };
                        statusCode = 500;
                    }

                    SendResponse(client, statusCode, result, origin);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    SendResponse(client, 500, new { error = "Internal server error: " + ex.Message }, origin);
                }
                catch { /* best effort */ }
            }
        }

        private static void ParseQueryString(string queryString, NameValueCollection query)
        {
            if (string.IsNullOrEmpty(queryString)) return;
            foreach (var pair in queryString.Split('&'))
            {
                var eqIdx = pair.IndexOf('=');
                if (eqIdx > 0)
                    query[Uri.UnescapeDataString(pair.Substring(0, eqIdx))] =
                        Uri.UnescapeDataString(pair.Substring(eqIdx + 1));
                else
                    query[Uri.UnescapeDataString(pair)] = "";
            }
        }

        private static void SendResponse(TcpClient client, int statusCode, object data, string origin = null)
        {
            try
            {
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                var statusText = statusCode == 200 ? "OK" :
                                 statusCode == 400 ? "Bad Request" :
                                 statusCode == 500 ? "Internal Server Error" : "OK";

                var sb = new StringBuilder();
                sb.Append("HTTP/1.1 " + statusCode + " " + statusText + "\r\n");
                sb.Append("Content-Type: application/json; charset=utf-8\r\n");

                // Dynamic CORS: echo Origin back only if localhost (exact host comparison)
                if (!string.IsNullOrEmpty(origin))
                {
                    bool isLocalhost = false;
                    try
                    {
                        var originUri = new Uri(origin);
                        var host = originUri.Host.ToLowerInvariant();
                        var scheme = originUri.Scheme.ToLowerInvariant();
                        isLocalhost = (host == "localhost" || host == "127.0.0.1" || host == "[::1]")
                                      && (scheme == "http" || scheme == "https" || scheme == "tauri");
                    }
                    catch (UriFormatException) { /* malformed origin — reject */ }
                    if (isLocalhost)
                    {
                        sb.Append("Access-Control-Allow-Origin: " + origin + "\r\n");
                        sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
                        sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
                    }
                }
                sb.Append("Content-Length: " + jsonBytes.Length + "\r\n");
                sb.Append("Connection: close\r\n");
                sb.Append("\r\n");

                var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
                var stream = client.GetStream();
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(jsonBytes, 0, jsonBytes.Length);
                stream.Flush();
            }
            catch { /* client may have disconnected */ }
        }

        // ── Route Dispatcher ─────────────────────────────────────

        private object Route(string path, string method, string body,
            NameValueCollection query)
        {
            switch (path)
            {
                case "/ping":
                    var station = Station.ActiveStation;
                    return new
                    {
                        status = "ok",
                        server = "ClaudeBridge",
                        port = Port,
                        transport = "TcpListener",
                        stationOpen = station != null,
                        stationName = station?.Name ?? "(none)"
                    };

                // ── Station ───────────────────────────────────
                case "/station":              return GetStation();
                case "/station/objects":      return GetStationObjects(query);
                case "/station/object/parts": return GetObjectParts(query);
                case "/station/part/from-body":
                    if (method != "POST") throw new Exception("POST required");
                    return CreatePartFromBody(body);
                case "/station/part/remove-bodies":
                    if (method != "POST") throw new Exception("POST required");
                    return RemoveBodiesFromPart(body);
                case "/station/part/set-frame":
                    if (method != "POST") throw new Exception("POST required");
                    return SetPartFrame(body);
                case "/station/logic":        return GetStationLogic(query);
                case "/station/logic/edit":
                    if (method != "POST") throw new Exception("POST required");
                    return EditStationLogic(body);
                case "/station/logic/library": return GetSmartComponentLibrary();
                case "/station/save":         return SaveStation();

                // ── RAPID ─────────────────────────────────────
                case "/rapid/tasks":          return GetRapidTasks();
                case "/rapid/modules":        return GetRapidModules(query["task"]);
                case "/rapid/module/text":
                    return method == "GET"
                        ? GetModuleText(query["task"], query["module"])
                        : SetModuleText(query["task"], query["module"], body);
                case "/rapid/variable":
                    return method == "GET"
                        ? GetRapidVariable(query["task"], query["name"])
                        : SetRapidVariable(query["task"], query["name"], body);
                case "/rapid/variables":
                    return ListRapidVariables(query);
                case "/rapid/errors":
                    return GetExecutionErrors(query["count"]);

                // ── Controller ────────────────────────────────
                case "/controller/status":    return GetControllerStatus();
                case "/controller/config/signals":
                    return method == "GET" ? ListConfigSignals(query) : CreateConfigSignals(body);

                // ── Simulation ────────────────────────────────
                case "/simulation/start":     return StartSimulation();
                case "/simulation/stop":      return StopSimulation();
                case "/simulation/pause":     return PauseSimulation();
                case "/simulation/reset":     return ResetSimulation();
                case "/simulation/status":    return GetSimulationStatus();
                case "/simulation/speed":
                    return method == "GET" ? GetSimulationSpeed() : SetSimulationSpeed(body);

                // ── Position ──────────────────────────────────
                case "/robot/position":       return GetRobotPosition();

                // ── I/O ───────────────────────────────────────
                case "/io/signals":           return GetIOSignals();
                case "/io/signal":
                    return method == "GET"
                        ? GetIOSignal(query["name"])
                        : SetIOSignal(query["name"], body);

                // ── Paths & Targets ───────────────────────────
                case "/paths":                return GetPaths();
                case "/paths/targets":        return GetPathTargets(query["path"]);
                case "/paths/create":         return CreatePath(body);
                case "/targets/create":       return CreateTarget(body);

                // ── Config Files ──────────────────────────────
                case "/config/read":          return ReadConfigFile(query["name"]);
                case "/config/write":         return WriteConfigFile(query["name"], body);

                // ── Collision Detection ───────────────────────
                case "/collision/check":      return CheckCollisions();

                // ── Screenshot ────────────────────────────────
                case "/screenshot":           return TakeScreenshot(body);

                default:
                    return new { error = "Unknown endpoint: " + path };
            }
        }

        // ── Helpers ──────────────────────────────────────────

        private Station GetActiveStation()
        {
            var s = Station.ActiveStation;
            if (s == null) throw new Exception("No active station in RobotStudio");
            return s;
        }

        private VirtualController GetController()
        {
            var station = GetActiveStation();
            if (station.VirtualControllers.Count == 0)
                throw new Exception("No controller in station");
            return station.VirtualControllers[0];
        }

        private Controller ConnectToController()
        {
            var rsCtrl = GetController();
            var scanner = new NetworkScanner();
            scanner.Scan();

            ControllerInfo ctrlInfo = null;
            try
            {
                var sysId = new Guid(rsCtrl.SystemId);
                ctrlInfo = scanner.Find(sysId);
            }
            catch { }

            if (ctrlInfo == null)
            {
                var allControllers = scanner.GetControllers();
                ctrlInfo = allControllers.FirstOrDefault(c =>
                    c.Name.Equals(rsCtrl.Name, StringComparison.OrdinalIgnoreCase));
                if (ctrlInfo == null)
                {
                    var names = string.Join(", ", allControllers.Select(c => c.Name + " (" + c.SystemId + ")"));
                    throw new Exception("Controller '" + rsCtrl.Name + "' not found. Available: [" + names + "]");
                }
            }

            try
            {
                var controller = Controller.Connect(ctrlInfo, ConnectionType.Standalone, false);
                controller.Logon(UserInfo.DefaultUser);
                return controller;
            }
            catch (ObjectDisposedException)
            {
                throw new Exception("Controller connection was disposed. The virtual controller may be restarting. Try again in a moment.");
            }
        }

        private RsTask FindTask(VirtualController ctrl, string taskName)
        {
            var allTasks = ctrl.Tasks.Cast<RsTask>()
                .Concat(ctrl.NonMotionTasks.Cast<RsTask>())
                .ToList();

            if (string.IsNullOrEmpty(taskName))
                return allTasks.FirstOrDefault()
                    ?? throw new Exception("No tasks found");

            return allTasks.FirstOrDefault(t =>
                    t.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception("Task '" + taskName + "' not found. Available: " +
                    string.Join(", ", allTasks.Select(t => t.Name)));
        }

        private RsPathProcedure FindPath(ProjectObject parent, string pathName)
        {
            foreach (ProjectObject child in parent.Children)
            {
                if (child is RsPathProcedure pp &&
                    pp.Name.Equals(pathName, StringComparison.OrdinalIgnoreCase))
                    return pp;
                var found = FindPath(child, pathName);
                if (found != null) return found;
            }
            return null;
        }

        // ── Station Endpoints ────────────────────────────────

        private object GetStation()
        {
            var station = GetActiveStation();
            var ctrls = new List<object>();
            foreach (VirtualController c in station.VirtualControllers)
            {
                ctrls.Add(new
                {
                    name = c.Name,
                    systemId = c.SystemId,
                    systemState = c.SystemState.ToString(),
                    systemPath = c.SystemPath
                });
            }
            return new { name = station.Name, controllers = ctrls };
        }

        // Limits keep responses well under the MCP client's 1 MB cap.
        private const int DefaultObjectLimit = 200;
        private const int MaxObjectLimit = 2000;
        private const int DefaultMaxDepth = 6;
        private const int MaxMaxDepth = 20;

        private static int ParseIntQuery(NameValueCollection query, string key, int def, int min, int max)
        {
            int v;
            if (!int.TryParse(query[key], out v)) v = def;
            return Math.Max(min, Math.Min(max, v));
        }

        private static object Vec3Mm(ABB.Robotics.Math.Vector3 v)
        {
            return new
            {
                x_mm = Math.Round(v.x * 1000, 1),
                y_mm = Math.Round(v.y * 1000, 1),
                z_mm = Math.Round(v.z * 1000, 1)
            };
        }

        private static object BBoxMm(ABB.Robotics.Math.BoundingBox bb)
        {
            if (!bb.IsValid()) return null;
            var min = bb.min; var max = bb.max;
            return new
            {
                minX_mm = Math.Round(min.x * 1000, 1),
                minY_mm = Math.Round(min.y * 1000, 1),
                minZ_mm = Math.Round(min.z * 1000, 1),
                maxX_mm = Math.Round(max.x * 1000, 1),
                maxY_mm = Math.Round(max.y * 1000, 1),
                maxZ_mm = Math.Round(max.z * 1000, 1),
                sizeX_mm = Math.Round((max.x - min.x) * 1000, 1),
                sizeY_mm = Math.Round((max.y - min.y) * 1000, 1),
                sizeZ_mm = Math.Round((max.z - min.z) * 1000, 1)
            };
        }

        private static ABB.Robotics.Math.BoundingBox Union(ABB.Robotics.Math.BoundingBox a, ABB.Robotics.Math.BoundingBox b)
        {
            if (!a.IsValid()) return b;
            if (!b.IsValid()) return a;
            var r = a;
            r.min = new ABB.Robotics.Math.Vector3(Math.Min(a.min.x, b.min.x), Math.Min(a.min.y, b.min.y), Math.Min(a.min.z, b.min.z));
            r.max = new ABB.Robotics.Math.Vector3(Math.Max(a.max.x, b.max.x), Math.Max(a.max.y, b.max.y), Math.Max(a.max.z, b.max.z));
            return r;
        }

        // Adds global position + bounding box of a GraphicComponent to obj (best effort).
        private static void AddGeometryInfo(GraphicComponent gc, Dictionary<string, object> obj)
        {
            try { obj["position"] = Vec3Mm(gc.Transform.GlobalMatrix.Translation); } catch { }
            try
            {
                var bb = BBoxMm(gc.GetBoundingBox(true));
                if (bb != null) obj["boundingBox"] = bb;
            }
            catch { }
        }

        private static IEnumerable<ProjectObject> ChildrenOf(ProjectObject po)
        {
            // Children covers most objects; GraphicComponents covers groups/smart components.
            var seen = new HashSet<ProjectObject>();
            foreach (ProjectObject c in po.Children)
                if (seen.Add(c)) yield return c;
            if (po is IHasGraphicComponents hg)
                foreach (GraphicComponent c in hg.GraphicComponents)
                    if (seen.Add(c)) yield return c;
        }

        // GET /station/objects?nameFilter=&limit=&maxDepth=&bbox=true
        private object GetStationObjects(NameValueCollection query)
        {
            var station = GetActiveStation();
            var filter = query["nameFilter"];
            bool bbox = query["bbox"] == "true";
            int limit = ParseIntQuery(query, "limit", DefaultObjectLimit, 1, MaxObjectLimit);
            int maxDepth = ParseIntQuery(query, "maxDepth", DefaultMaxDepth, 0, MaxMaxDepth);

            var objects = new List<object>();
            int matched = 0;

            void Walk(ProjectObject parent, string parentPath, int depth)
            {
                foreach (var child in ChildrenOf(parent))
                {
                    var name = child.Name ?? "";
                    var path = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;
                    bool isMatch = string.IsNullOrEmpty(filter)
                        || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isMatch)
                    {
                        matched++;
                        if (objects.Count < limit)
                        {
                            var obj = new Dictionary<string, object>
                            {
                                ["name"] = name,
                                ["type"] = child.GetType().Name,
                                ["depth"] = depth,
                                ["path"] = path
                            };
                            if (bbox && child is GraphicComponent gc) AddGeometryInfo(gc, obj);
                            objects.Add(obj);
                        }
                    }
                    if (depth < maxDepth)
                        Walk(child, path, depth + 1);
                }
            }

            Walk(station, "", 0);
            return new
            {
                stationName = station.Name,
                nameFilter = filter,
                maxDepth,
                limit,
                matchedCount = matched,
                returnedCount = objects.Count,
                truncated = matched > objects.Count,
                objects
            };
        }

        // GET /station/object/parts?name=&maxDepth=&limit=
        private object GetObjectParts(NameValueCollection query)
        {
            var station = GetActiveStation();
            var name = query["name"];
            if (string.IsNullOrWhiteSpace(name))
                throw new Exception("Parameter 'name' is required");
            int limit = ParseIntQuery(query, "limit", DefaultObjectLimit, 1, MaxObjectLimit);
            int maxDepth = ParseIntQuery(query, "maxDepth", 3, 0, MaxMaxDepth);

            // Locate target: exact (case-insensitive) match wins, else unique substring match.
            var exact = new List<Tuple<ProjectObject, string>>();
            var partial = new List<Tuple<ProjectObject, string>>();
            void Find(ProjectObject parent, string parentPath, int depth)
            {
                foreach (var child in ChildrenOf(parent))
                {
                    var n = child.Name ?? "";
                    var path = string.IsNullOrEmpty(parentPath) ? n : parentPath + "/" + n;
                    if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) exact.Add(Tuple.Create(child, path));
                    else if (n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) partial.Add(Tuple.Create(child, path));
                    if (depth < MaxMaxDepth) Find(child, path, depth + 1);
                }
            }
            Find(station, "", 0);

            var candidates = exact.Count > 0 ? exact : partial;
            if (candidates.Count == 0)
                throw new Exception("No object whose name matches '" + name + "'");
            if (candidates.Count > 1)
                return new
                {
                    error = "Ambiguous name '" + name + "': " + candidates.Count + " matches. Use the exact name.",
                    candidates = candidates.Take(50).Select(c => new { name = c.Item1.Name, type = c.Item1.GetType().Name, path = c.Item2 })
                };

            var target = candidates[0].Item1;
            var info = new Dictionary<string, object>
            {
                ["name"] = target.Name,
                ["type"] = target.GetType().Name,
                ["path"] = candidates[0].Item2
            };

            var mode = (query["mode"] ?? "tree").ToLowerInvariant();
            if (mode == "bodies" || mode == "clusters")
            {
                if (!(target is Part ap))
                    throw new Exception("mode=" + mode + " requires a Part; '" + target.Name + "' is " + target.GetType().Name);
                return AnalyzePartBodies(ap, info, mode, query);
            }
            if (mode != "tree")
                throw new Exception("Unknown mode '" + mode + "'. Use tree, bodies or clusters.");
            if (target is GraphicComponent tgc) AddGeometryInfo(tgc, info);

            int count = 0;
            bool truncated = false;

            List<object> DescribeBodies(Part part)
            {
                var list = new List<object>();
                foreach (Body body in part.Bodies)
                {
                    if (count >= limit) { truncated = true; break; }
                    count++;
                    var b = new Dictionary<string, object>
                    {
                        ["name"] = body.Name,
                        ["kind"] = "Body",
                        ["bodyType"] = body.BodyType.ToString()
                    };
                    try
                    {
                        var bb = ABB.Robotics.Math.BoundingBox.Empty;
                        int faces = 0;
                        foreach (Face f in body.Faces) { faces++; bb = Union(bb, f.GetBoundingBox(true)); }
                        b["faceCount"] = faces;
                        var bbo = BBoxMm(bb);
                        if (bbo != null) b["boundingBox"] = bbo;
                    }
                    catch { }
                    try { b["volume_dm3"] = Math.Round(body.Volume * 1000, 3); } catch { }
                    try { b["surfaceArea_m2"] = Math.Round(body.SurfaceArea, 4); } catch { }
                    list.Add(b);
                }
                return list;
            }

            List<object> Describe(ProjectObject parent, int depth)
            {
                var list = new List<object>();
                foreach (var child in ChildrenOf(parent))
                {
                    if (child is Body) continue; // bodies are listed via Part.Bodies
                    if (count >= limit) { truncated = true; break; }
                    count++;
                    var o = new Dictionary<string, object>
                    {
                        ["name"] = child.Name,
                        ["kind"] = child.GetType().Name,
                        ["depth"] = depth
                    };
                    if (child is GraphicComponent gc) AddGeometryInfo(gc, o);
                    if (child is Part p)
                    {
                        try { o["bodyCount"] = p.Bodies.Count; } catch { }
                        var bodies = DescribeBodies(p);
                        if (bodies.Count > 0) o["bodies"] = bodies;
                    }
                    if (depth < maxDepth)
                    {
                        var sub = Describe(child, depth + 1);
                        if (sub.Count > 0) o["children"] = sub;
                    }
                    list.Add(o);
                }
                return list;
            }

            if (target is Part tp)
            {
                try { info["bodyCount"] = tp.Bodies.Count; } catch { }
                info["bodies"] = DescribeBodies(tp);
            }
            info["children"] = Describe(target, 0);
            info["itemCount"] = count;
            info["truncated"] = truncated;
            info["note"] = "Coordinates are global, in mm. Body bounding boxes are computed from their faces.";
            return info;
        }

        // ── Body analysis of a flattened Part (mode=bodies|clusters) ──────────
        // Query (lengths in mm):
        //   region=minX,minY,minZ,maxX,maxY,maxZ  keep only bodies whose bbox intersects it
        //   clusterTolerance=1                    max gap between bboxes merged into one cluster
        //   footprints=812.8x863.6;711.2x965.2    XY sizes to look for (orientation-free)
        //   sizeTolerance=25                      max per-side deviation to call it a match
        //   candidates=5                          best candidates returned per footprint
        //   limit=200&offset=0                    paging of the full item list (no footprints)

        private class BoxItem
        {
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ; // metres, global
            public int BodyCount, FaceCount;
            public double Volume; // m3, sum of body volumes
            public List<int> BodyIndices = new List<int>();
        }

        private static double ParseDoubleQuery(NameValueCollection query, string key, double def)
        {
            double v;
            return double.TryParse(query[key], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static object BoxItemMm(BoxItem b, int index)
        {
            double sx = (b.MaxX - b.MinX) * 1000, sy = (b.MaxY - b.MinY) * 1000, sz = (b.MaxZ - b.MinZ) * 1000;
            return new
            {
                index,
                bodyCount = b.BodyCount,
                faceCount = b.FaceCount,
                volume_dm3 = Math.Round(b.Volume * 1000, 3),
                center = new
                {
                    x_mm = Math.Round((b.MinX + b.MaxX) * 500, 1),
                    y_mm = Math.Round((b.MinY + b.MaxY) * 500, 1),
                    z_mm = Math.Round((b.MinZ + b.MaxZ) * 500, 1)
                },
                boundingBox = new
                {
                    minX_mm = Math.Round(b.MinX * 1000, 1), minY_mm = Math.Round(b.MinY * 1000, 1), minZ_mm = Math.Round(b.MinZ * 1000, 1),
                    maxX_mm = Math.Round(b.MaxX * 1000, 1), maxY_mm = Math.Round(b.MaxY * 1000, 1), maxZ_mm = Math.Round(b.MaxZ * 1000, 1),
                    sizeX_mm = Math.Round(sx, 1), sizeY_mm = Math.Round(sy, 1), sizeZ_mm = Math.Round(sz, 1)
                },
                firstBodyIndices = b.BodyIndices.Take(10)
            };
        }

        // ── Per-Part cache of body boxes ─────────────────────────────────────
        // source=mesh : display tessellation (Part.Mesh → MeshPart.Bodies → MeshBody.GetBoundingBox), fast.
        // source=faces: exact B-rep faces (Body.Faces → Face.GetBoundingBox(true)), slow on big imports,
        //               so it runs in time-budgeted slices and resumes on the next call.
        // source=auto : mesh if available, else faces.

        private class BodyBoxCache
        {
            public Part Part;
            public string Source;
            public List<BoxItem> Boxes = new List<BoxItem>(); // one slot per body (null = no geometry)
            public int NextIndex;
            public bool Done;
            public object FrameCheck;
            public int PartBodyCount; // Part.Bodies.Count when built: cache is stale if it changes
        }

        private static readonly Dictionary<Part, BodyBoxCache> _bodyBoxCache = new Dictionary<Part, BodyBoxCache>();

        private static BoxItem ToBoxItem(ABB.Robotics.Math.BoundingBox bb, int index, int faces, double volume)
        {
            var it = new BoxItem
            {
                MinX = bb.min.x, MinY = bb.min.y, MinZ = bb.min.z,
                MaxX = bb.max.x, MaxY = bb.max.y, MaxZ = bb.max.z,
                BodyCount = 1, FaceCount = faces, Volume = volume
            };
            it.BodyIndices.Add(index);
            return it;
        }

        private static double CornerDistance(ABB.Robotics.Math.BoundingBox a, ABB.Robotics.Math.BoundingBox b)
        {
            return Math.Abs(a.min.x - b.min.x) + Math.Abs(a.min.y - b.min.y) + Math.Abs(a.min.z - b.min.z) +
                   Math.Abs(a.max.x - b.max.x) + Math.Abs(a.max.y - b.max.y) + Math.Abs(a.max.z - b.max.z);
        }

        private BodyBoxCache GetBodyBoxes(Part part, string source, int timeBudgetSeconds)
        {
            BodyBoxCache c;
            int currentBodyCount = part.Bodies.Count;
            if (_bodyBoxCache.TryGetValue(part, out c) && c.PartBodyCount == currentBodyCount && (source == "auto" || source == c.Source))
            {
                if (c.Done) return c;
            }
            else
            {
                c = null;
                if (source == "auto" || source == "mesh")
                    c = TryBuildFromMesh(part);
                if (c == null)
                {
                    if (source == "mesh") throw new Exception("No display mesh available for '" + part.Name + "'. Use source=faces.");
                    c = new BodyBoxCache { Part = part, Source = "faces" };
                    int n = part.Bodies.Count;
                    for (int i = 0; i < n; i++) c.Boxes.Add(null);
                }
                c.PartBodyCount = currentBodyCount;
                _bodyBoxCache[part] = c;
                if (c.Done) return c;
            }

            // Faces: resume where the previous slice stopped
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int idx = 0;
            foreach (Body body in part.Bodies)
            {
                if (idx >= c.NextIndex)
                {
                    if (sw.Elapsed.TotalSeconds >= timeBudgetSeconds) break;
                    var bb = ABB.Robotics.Math.BoundingBox.Empty;
                    int faces = 0;
                    try { foreach (Face f in body.Faces) { faces++; bb = Union(bb, f.GetBoundingBox(true)); } } catch { }
                    double vol = 0;
                    try { vol = body.Volume; } catch { }
                    if (idx < c.Boxes.Count) c.Boxes[idx] = bb.IsValid() ? ToBoxItem(bb, idx, faces, vol) : null;
                    c.NextIndex = idx + 1;
                }
                idx++;
            }
            c.Done = c.NextIndex >= c.Boxes.Count;
            return c;
        }

        private BodyBoxCache TryBuildFromMesh(Part part)
        {
            MeshPart mp = null;
            try
            {
                var mesh = part.Mesh;
                if (mesh == null) return null;
                foreach (var lvl in new[] { DetailLevels.Coarse, DetailLevels.Medium, DetailLevels.Fine, DetailLevels.Default })
                {
                    try { mp = mesh[lvl]; } catch { mp = null; }
                    if (mp != null && mp.Bodies != null && mp.Bodies.Count > 0) break;
                    mp = null;
                }
            }
            catch { return null; }
            if (mp == null) return null;

            // Mesh coordinates may be local to the Part: pick the frame that matches the Part's global box
            var raw = new List<ABB.Robotics.Math.BoundingBox>();
            var rawUnion = ABB.Robotics.Math.BoundingBox.Empty;
            var faceCounts = new List<int>();
            foreach (MeshBody mb in mp.Bodies)
            {
                var bb = ABB.Robotics.Math.BoundingBox.Empty;
                try { bb = mb.GetBoundingBox(); } catch { }
                raw.Add(bb);
                int fc = 0; try { fc = mb.Faces.Count; } catch { }
                faceCounts.Add(fc);
                if (bb.IsValid()) rawUnion = Union(rawUnion, bb);
            }
            if (!rawUnion.IsValid()) return null;

            var global = part.GetBoundingBox(true);
            var m = part.Transform.GlobalMatrix;
            var transformedUnion = rawUnion.TransformAffine(m);
            double dRaw = CornerDistance(rawUnion, global), dTrans = CornerDistance(transformedUnion, global);
            bool useTransform = dTrans < dRaw;

            var c = new BodyBoxCache
            {
                Part = part,
                Source = "mesh",
                FrameCheck = new
                {
                    meshBodyCount = raw.Count,
                    appliedPartTransform = useTransform,
                    deviationFromPartBox_mm = Math.Round(Math.Min(dRaw, dTrans) * 1000, 1),
                    note = "Sum of the 6 corner deviations between the union of mesh boxes and Part.GetBoundingBox(true). Near 0 = frame is right."
                }
            };
            for (int i = 0; i < raw.Count; i++)
            {
                var bb = raw[i];
                if (!bb.IsValid()) { c.Boxes.Add(null); continue; }
                if (useTransform) bb = bb.TransformAffine(m);
                c.Boxes.Add(ToBoxItem(bb, i, faceCounts[i], 0));
            }
            c.NextIndex = c.Boxes.Count;
            c.Done = true;
            return c;
        }

        private object AnalyzePartBodies(Part part, Dictionary<string, object> info, string mode, NameValueCollection query)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int limit = ParseIntQuery(query, "limit", DefaultObjectLimit, 1, MaxObjectLimit);
            int offset = ParseIntQuery(query, "offset", 0, 0, int.MaxValue);
            int perFootprint = ParseIntQuery(query, "candidates", 5, 1, 50);
            double clusterTol = ParseDoubleQuery(query, "clusterTolerance", 1) / 1000.0;
            double sizeTol = ParseDoubleQuery(query, "sizeTolerance", 25);

            double[] region = null;
            if (!string.IsNullOrWhiteSpace(query["region"]))
            {
                var parts = query["region"].Split(',');
                if (parts.Length != 6) throw new Exception("region must be minX,minY,minZ,maxX,maxY,maxZ (mm)");
                region = parts.Select(p => double.Parse(p.Trim(), System.Globalization.CultureInfo.InvariantCulture) / 1000.0).ToArray();
            }

            // 1. Global bounding box of every body (cached per Part; see GetBodyBoxes)
            var cache = GetBodyBoxes(part, (query["source"] ?? "auto").ToLowerInvariant(),
                                     ParseIntQuery(query, "timeBudget", 20, 1, 240));
            info["geometrySource"] = cache.Source;
            if (cache.FrameCheck != null) info["meshFrameCheck"] = cache.FrameCheck;
            if (!cache.Done)
            {
                info["status"] = "incomplete";
                info["progress"] = new { processed = cache.NextIndex, total = cache.Boxes.Count };
                info["hint"] = "Body boxes are computed in slices to keep RobotStudio responsive. Call again with the same name to continue; progress is kept.";
                info["timing_s"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
                return info;
            }

            var items = new List<BoxItem>();
            int skippedNoGeometry = 0, outsideRegion = 0;
            foreach (var cb in cache.Boxes)
            {
                if (cb == null) { skippedNoGeometry++; continue; }
                if (region != null && (cb.MaxX < region[0] || cb.MinX > region[3] || cb.MaxY < region[1] ||
                                       cb.MinY > region[4] || cb.MaxZ < region[2] || cb.MinZ > region[5]))
                { outsideRegion++; continue; }
                // Copy: clustering mutates/aggregates items, the cache must stay intact
                var it = new BoxItem
                {
                    MinX = cb.MinX, MinY = cb.MinY, MinZ = cb.MinZ, MaxX = cb.MaxX, MaxY = cb.MaxY, MaxZ = cb.MaxZ,
                    BodyCount = 1, FaceCount = cb.FaceCount, Volume = cb.Volume
                };
                it.BodyIndices.AddRange(cb.BodyIndices);
                items.Add(it);
            }
            double bboxSeconds = sw.Elapsed.TotalSeconds;

            // 2. Optional clustering: union-find over bboxes closer than clusterTol (sweep on X)
            if (mode == "clusters" && items.Count > 1)
            {
                int n = items.Count;
                var parent = Enumerable.Range(0, n).ToArray();
                int FindRoot(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
                var order = Enumerable.Range(0, n).OrderBy(i => items[i].MinX).ToArray();
                var active = new List<int>();
                foreach (var i in order)
                {
                    var a = items[i];
                    active.RemoveAll(j => items[j].MaxX + clusterTol < a.MinX);
                    foreach (var j in active)
                    {
                        var b = items[j];
                        if (a.MinY <= b.MaxY + clusterTol && b.MinY <= a.MaxY + clusterTol &&
                            a.MinZ <= b.MaxZ + clusterTol && b.MinZ <= a.MaxZ + clusterTol)
                        {
                            int ra = FindRoot(i), rb = FindRoot(j);
                            if (ra != rb) parent[ra] = rb;
                        }
                    }
                    active.Add(i);
                }
                var groups = new Dictionary<int, BoxItem>();
                for (int i = 0; i < n; i++)
                {
                    var r = FindRoot(i); var a = items[i];
                    if (!groups.TryGetValue(r, out var g))
                    {
                        g = new BoxItem { MinX = a.MinX, MinY = a.MinY, MinZ = a.MinZ, MaxX = a.MaxX, MaxY = a.MaxY, MaxZ = a.MaxZ };
                        groups[r] = g;
                    }
                    g.MinX = Math.Min(g.MinX, a.MinX); g.MinY = Math.Min(g.MinY, a.MinY); g.MinZ = Math.Min(g.MinZ, a.MinZ);
                    g.MaxX = Math.Max(g.MaxX, a.MaxX); g.MaxY = Math.Max(g.MaxY, a.MaxY); g.MaxZ = Math.Max(g.MaxZ, a.MaxZ);
                    g.BodyCount += a.BodyCount; g.FaceCount += a.FaceCount; g.Volume += a.Volume;
                    g.BodyIndices.AddRange(a.BodyIndices);
                }
                items = groups.Values.ToList();
            }

            // Largest first: makes the paged list readable
            items = items.OrderByDescending(b => (b.MaxX - b.MinX) * (b.MaxY - b.MinY)).ToList();

            info["mode"] = mode;
            info["bodyCount"] = cache.Boxes.Count;
            info["bodiesWithoutGeometry"] = skippedNoGeometry;
            if (region != null) info["bodiesOutsideRegion"] = outsideRegion;
            info["itemCount"] = items.Count;
            if (mode == "clusters") info["clusterTolerance_mm"] = clusterTol * 1000;

            // 3. Footprint search, or plain paged list
            var footprintsArg = query["footprints"];
            if (!string.IsNullOrWhiteSpace(footprintsArg))
            {
                var results = new List<object>();
                foreach (var fp in footprintsArg.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var dims = fp.ToLowerInvariant().Split('x');
                    if (dims.Length != 2) throw new Exception("footprint '" + fp + "' must be WIDTHxLENGTH in mm");
                    double d1 = double.Parse(dims[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    double d2 = double.Parse(dims[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    double tSmall = Math.Min(d1, d2), tLarge = Math.Max(d1, d2);

                    var scored = items.Select((b, idx) =>
                    {
                        double sx = (b.MaxX - b.MinX) * 1000, sy = (b.MaxY - b.MinY) * 1000;
                        double err = Math.Max(Math.Abs(Math.Min(sx, sy) - tSmall), Math.Abs(Math.Max(sx, sy) - tLarge));
                        return new { idx, b, err };
                    }).OrderBy(s => s.err).Take(perFootprint).ToList();

                    results.Add(new
                    {
                        footprint_mm = fp,
                        matchCount = items.Count(b =>
                        {
                            double sx = (b.MaxX - b.MinX) * 1000, sy = (b.MaxY - b.MinY) * 1000;
                            return Math.Max(Math.Abs(Math.Min(sx, sy) - tSmall), Math.Abs(Math.Max(sx, sy) - tLarge)) <= sizeTol;
                        }),
                        candidates = scored.Select(s => new
                        {
                            footprintError_mm = Math.Round(s.err, 1),
                            withinTolerance = s.err <= sizeTol,
                            item = BoxItemMm(s.b, s.idx)
                        })
                    });
                }
                info["sizeTolerance_mm"] = sizeTol;
                info["footprintSearch"] = results;
            }
            else
            {
                info["offset"] = offset;
                info["returnedCount"] = Math.Max(0, Math.Min(limit, items.Count - offset));
                info["truncated"] = offset + limit < items.Count;
                info["items"] = items.Skip(offset).Take(limit).Select((b, k) => BoxItemMm(b, offset + k)).ToList();
            }

            info["timing_s"] = new { bodyBoxes = Math.Round(bboxSeconds, 1), total = Math.Round(sw.Elapsed.TotalSeconds, 1) };
            info["note"] = "Global coordinates in mm, axis-aligned boxes. Items sorted by XY area (largest first); 'index' refers to that order, firstBodyIndices to Part.Bodies order.";
            return info;
        }

        // ── POST /station/part/from-body ──────────────────────────────────────
        // Body: {"sourcePart":"...","bodyIndex":8,"name":"Palette_28x38"}
        // Copies one body (index as reported by rs_get_object_parts mode=bodies, source=mesh) into a new
        // Part. The new Part's frame is set on a lower corner of the body's box: X along the long side,
        // Z up, Y completing a right-handed frame, the box lying in +X/+Y. Geometry stays in place.
        // Wrapped in one undo step (Ctrl+Z in RobotStudio removes it).
        private object CreatePartFromBody(string body)
        {
            var station = GetActiveStation();
            var req = JObject.Parse(body ?? "{}");
            var sourceName = (string)req["sourcePart"];
            var newName = (string)req["name"];
            var idxToken = req["bodyIndex"];
            if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(newName) || idxToken == null)
                throw new Exception("Required: sourcePart, bodyIndex, name");
            int bodyIndex = (int)idxToken;

            if (station.GraphicComponents.Contains(newName))
                throw new Exception("An object named '" + newName + "' already exists in the station");

            // Source Part: exact (case-insensitive) name among all station objects
            Part part = null;
            void Find(ProjectObject parent, int depth)
            {
                foreach (var child in ChildrenOf(parent))
                {
                    if (part != null) return;
                    if (child is Part p && (p.Name ?? "").Equals(sourceName, StringComparison.OrdinalIgnoreCase)) { part = p; return; }
                    if (depth < MaxMaxDepth) Find(child, depth + 1);
                }
            }
            Find(station, 0);
            if (part == null) throw new Exception("No Part named '" + sourceName + "'");

            // Expected box (global) from the mesh cache, same indexing as rs_get_object_parts
            var cache = GetBodyBoxes(part, "mesh", 20);
            if (bodyIndex < 0 || bodyIndex >= cache.Boxes.Count || cache.Boxes[bodyIndex] == null)
                throw new Exception("bodyIndex " + bodyIndex + " has no geometry (0.." + (cache.Boxes.Count - 1) + ")");
            var box = cache.Boxes[bodyIndex];

            // Resolve the B-rep Body behind that mesh body
            Body src = null;
            var mp = GetMeshPart(part);
            try { src = mp?.Bodies[bodyIndex].Body; } catch { }
            if (src == null) src = part.Bodies[bodyIndex];

            // Safety: the Body's exact box must match the mesh box before we copy anything
            var exact = ABB.Robotics.Math.BoundingBox.Empty;
            foreach (Face f in src.Faces) exact = Union(exact, f.GetBoundingBox(true));
            if (!exact.IsValid()) throw new Exception("Body " + bodyIndex + " has no exact geometry");
            double dev = new[] {
                Math.Abs(exact.min.x - box.MinX), Math.Abs(exact.min.y - box.MinY), Math.Abs(exact.min.z - box.MinZ),
                Math.Abs(exact.max.x - box.MaxX), Math.Abs(exact.max.y - box.MaxY), Math.Abs(exact.max.z - box.MaxZ)
            }.Max() * 1000;
            if (dev > 5)
                throw new Exception("Body/mesh mismatch for index " + bodyIndex + " (" + Math.Round(dev, 1) + " mm). Nothing created.");

            // Target frame on a lower corner of the exact box
            double sx = exact.max.x - exact.min.x, sy = exact.max.y - exact.min.y;
            ABB.Robotics.Math.Vector3 xAxis, yAxis, origin;
            var zAxis = new ABB.Robotics.Math.Vector3(0, 0, 1);
            // Square within 1 mm (float noise): keep X along station X
            if (sx >= sy - 0.001)
            {
                xAxis = new ABB.Robotics.Math.Vector3(1, 0, 0);
                yAxis = new ABB.Robotics.Math.Vector3(0, 1, 0);
                origin = new ABB.Robotics.Math.Vector3(exact.min.x, exact.min.y, exact.min.z);
            }
            else
            {
                // Long side along station Y: X=+Y, Y=-X, corner at (maxX, minY) keeps the box in +X/+Y
                xAxis = new ABB.Robotics.Math.Vector3(0, 1, 0);
                yAxis = new ABB.Robotics.Math.Vector3(-1, 0, 0);
                origin = new ABB.Robotics.Math.Vector3(exact.max.x, exact.min.y, exact.min.z);
            }
            var frame = new ABB.Robotics.Math.Matrix4(xAxis, yAxis, zAxis, origin);

            var bodyGlobal = src.Transform.GlobalMatrix;
            var undo = Project.UndoContext;
            undo?.BeginUndoStep("ClaudeBridge: create " + newName);
            Part created;
            try
            {
                var copy = (Body)src.Copy();
                created = new Part { Name = newName };
                station.GraphicComponents.Add(created);
                created.Bodies.Add(copy);
                created.Transform.GlobalMatrix = frame;
                copy.Transform.GlobalMatrix = bodyGlobal; // keep geometry where it was
            }
            finally
            {
                undo?.EndUndoStep();
            }

            // Verify: new part's global box must coincide with the source body's box
            var nb = created.GetBoundingBox(true);
            double placeDev = nb.IsValid() ? new[] {
                Math.Abs(nb.min.x - exact.min.x), Math.Abs(nb.min.y - exact.min.y), Math.Abs(nb.min.z - exact.min.z),
                Math.Abs(nb.max.x - exact.max.x), Math.Abs(nb.max.y - exact.max.y), Math.Abs(nb.max.z - exact.max.z)
            }.Max() * 1000 : double.NaN;

            var g = created.Transform.GlobalMatrix;
            return new
            {
                success = true,
                name = created.Name,
                sourcePart = part.Name,
                bodyIndex,
                frame = new
                {
                    origin = Vec3Mm(g.Translation),
                    xAxis = new { x = Math.Round(g.x.x, 4), y = Math.Round(g.x.y, 4), z = Math.Round(g.x.z, 4) },
                    yAxis = new { x = Math.Round(g.y.x, 4), y = Math.Round(g.y.y, 4), z = Math.Round(g.y.z, 4) },
                    zAxis = new { x = Math.Round(g.z.x, 4), y = Math.Round(g.z.y, 4), z = Math.Round(g.z.z, 4) }
                },
                size_mm = new
                {
                    alongX = Math.Round(Math.Max(sx, sy) * 1000, 1),
                    alongY = Math.Round(Math.Min(sx, sy) * 1000, 1),
                    alongZ = Math.Round((exact.max.z - exact.min.z) * 1000, 1)
                },
                boundingBox = BBoxMm(nb),
                placementDeviation_mm = Math.Round(placeDev, 2),
                note = "The original body is still part of the source Part (not removed). Undo with Ctrl+Z in RobotStudio."
            };
        }

        private static MeshPart GetMeshPart(Part part)
        {
            try
            {
                var mesh = part.Mesh;
                if (mesh == null) return null;
                foreach (var lvl in new[] { DetailLevels.Coarse, DetailLevels.Medium, DetailLevels.Fine, DetailLevels.Default })
                {
                    MeshPart mp = null;
                    try { mp = mesh[lvl]; } catch { }
                    if (mp != null && mp.Bodies != null && mp.Bodies.Count > 0) return mp;
                }
            }
            catch { }
            return null;
        }

        // ── POST /station/part/remove-bodies ──────────────────────────────────
        // Body: {"sourcePart":"...","bodyIndices":[0,1],"expectFootprints":["965.2x965.2"],"sizeTolerance":1}
        // Removes bodies (indices as reported by rs_get_object_parts, source=mesh) from a Part.
        // All-or-nothing: if any index is invalid or (when expectFootprints is given) its XY box does not
        // match one of the footprints, nothing is removed. One undo step.
        private object RemoveBodiesFromPart(string body)
        {
            var station = GetActiveStation();
            var req = JObject.Parse(body ?? "{}");
            var sourceName = (string)req["sourcePart"];
            var indices = req["bodyIndices"]?.Select(t => (int)t).Distinct().OrderBy(i => i).ToList();
            if (string.IsNullOrWhiteSpace(sourceName) || indices == null || indices.Count == 0)
                throw new Exception("Required: sourcePart, bodyIndices");
            double sizeTol = req["sizeTolerance"] != null ? (double)req["sizeTolerance"] : 1.0;
            var footprints = (req["expectFootprints"] as JArray)?.Select(t =>
            {
                var d = ((string)t).ToLowerInvariant().Split('x');
                double a = double.Parse(d[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                double b = double.Parse(d[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                return new[] { Math.Min(a, b), Math.Max(a, b) };
            }).ToList();

            Part part = null;
            void Find(ProjectObject parent, int depth)
            {
                foreach (var child in ChildrenOf(parent))
                {
                    if (part != null) return;
                    if (child is Part p && (p.Name ?? "").Equals(sourceName, StringComparison.OrdinalIgnoreCase)) { part = p; return; }
                    if (depth < MaxMaxDepth) Find(child, depth + 1);
                }
            }
            Find(station, 0);
            if (part == null) throw new Exception("No Part named '" + sourceName + "'");

            var cache = GetBodyBoxes(part, "mesh", 20);
            var mp = GetMeshPart(part);
            int countBefore = part.Bodies.Count;

            // Resolve and check everything before touching the station
            var toRemove = new List<Body>();
            var report = new List<object>();
            foreach (var idx in indices)
            {
                if (idx < 0 || idx >= cache.Boxes.Count || cache.Boxes[idx] == null)
                    throw new Exception("bodyIndex " + idx + " has no geometry. Nothing removed.");
                var box = cache.Boxes[idx];
                double sx = (box.MaxX - box.MinX) * 1000, sy = (box.MaxY - box.MinY) * 1000;
                double small = Math.Min(sx, sy), large = Math.Max(sx, sy);
                if (footprints != null && !footprints.Any(f => Math.Abs(small - f[0]) <= sizeTol && Math.Abs(large - f[1]) <= sizeTol))
                    throw new Exception("bodyIndex " + idx + " is " + Math.Round(sx, 1) + " x " + Math.Round(sy, 1) +
                                        " mm, not one of the expected footprints. Nothing removed.");
                Body b = null;
                try { b = mp?.Bodies[idx].Body; } catch { }
                if (b == null) throw new Exception("bodyIndex " + idx + ": no B-rep body behind the mesh body. Nothing removed.");
                toRemove.Add(b);
                report.Add(new { bodyIndex = idx, sizeX_mm = Math.Round(sx, 1), sizeY_mm = Math.Round(sy, 1),
                                 minX_mm = Math.Round(box.MinX * 1000, 1), minY_mm = Math.Round(box.MinY * 1000, 1), minZ_mm = Math.Round(box.MinZ * 1000, 1) });
            }

            var undo = Project.UndoContext;
            undo.BeginUndoStep("ClaudeBridge: remove " + toRemove.Count + " bodies from " + part.Name);
            int removed = 0;
            try
            {
                foreach (var b in toRemove)
                    if (part.Bodies.Remove(b)) removed++;
            }
            finally
            {
                undo.EndUndoStep();
            }

            _bodyBoxCache.Remove(part); // indices changed
            return new
            {
                success = removed == toRemove.Count,
                sourcePart = part.Name,
                requested = toRemove.Count,
                removed,
                bodyCountBefore = countBefore,
                bodyCountAfter = part.Bodies.Count,
                bodies = report,
                note = "Body indices of this Part have changed: re-run rs_get_object_parts before using indices again. Undo with Ctrl+Z. Station not saved."
            };
        }

        // ── GET /station/logic?component=&properties=true&frames=true&frameFilter= ─
        // Read-only view of the Station Logic (station-level signals, I/O connections, property
        // bindings), of every SmartComponent (properties, signals, internal connections/bindings,
        // child components) and of the station frames (Paths & Targets > Frames).

        private static string ObjName(ProjectObject po, Station station)
        {
            if (po == null) return null;
            if (ReferenceEquals(po, station)) return "(Station Logic)";
            return string.IsNullOrEmpty(po.Name) ? "(" + po.GetType().Name + ")" : po.Name;
        }

        private static string ValueText(object v)
        {
            if (v == null) return null;
            if (v is ProjectObject po) return po.Name + " (" + po.GetType().Name + ")";
            if (v is double d) return Math.Round(d, 6).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            return s != null && s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }

        private static List<object> DescribeSignals(IOSignalCollection signals)
        {
            var list = new List<object>();
            foreach (IOSignal s in signals)
                list.Add(new { name = s.Name, type = s.SignalType.ToString(), isInput = s.IsInput, value = ValueText(s.Value), autoReset = s.AutoReset });
            return list;
        }

        private static List<object> DescribeConnections(IOConnectionCollection conns, Station station)
        {
            var list = new List<object>();
            foreach (IOConnection c in conns)
                list.Add(new
                {
                    source = ObjName(c.SourceObject, station) ?? c.SourceObjectName,
                    sourceSignal = c.SourceSignal,
                    target = ObjName(c.TargetObject, station) ?? c.TargetObjectName,
                    targetSignal = c.TargetSignal,
                    allowCycle = c.AllowCycle
                });
            return list;
        }

        private static List<object> DescribeBindings(PropertyBindingCollection bindings, Station station)
        {
            var list = new List<object>();
            foreach (PropertyBinding b in bindings)
                list.Add(new
                {
                    source = ObjName(b.SourceObject, station) ?? b.SourceObjectName,
                    sourceProperty = b.SourceProperty,
                    target = ObjName(b.TargetObject, station) ?? b.TargetObjectName,
                    targetProperty = b.TargetProperty
                });
            return list;
        }

        private object GetStationLogic(NameValueCollection query)
        {
            var station = GetActiveStation();
            var componentFilter = query["component"];
            bool withProps = query["properties"] != "false";
            bool withFrames = query["frames"] != "false";
            var frameFilter = query["frameFilter"];

            var components = new List<object>();
            void Walk(ProjectObject parent, string parentPath, int depth)
            {
                foreach (var child in ChildrenOf(parent))
                {
                    if (!(child is SmartComponent sc)) { continue; }
                    var path = string.IsNullOrEmpty(parentPath) ? sc.Name : parentPath + "/" + sc.Name;
                    bool include = string.IsNullOrEmpty(componentFilter)
                        || (sc.Name ?? "").IndexOf(componentFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (include)
                    {
                        var o = new Dictionary<string, object>
                        {
                            ["name"] = sc.Name,
                            ["path"] = path,
                            ["typeName"] = sc.TypeDisplayName,
                            ["description"] = sc.Description
                        };
                        if (!string.IsNullOrEmpty(sc.CodeBehindLocation)) o["codeBehind"] = sc.CodeBehindLocation;
                        if (withProps)
                        {
                            var props = new List<object>();
                            foreach (DynamicProperty p in sc.Properties)
                                props.Add(new { name = p.Name, type = p.ValueType?.Name, value = p.DisplayValue ?? ValueText(p.Value), readOnly = p.ReadOnly });
                            o["properties"] = props;
                        }
                        o["signals"] = DescribeSignals(sc.IOSignals);
                        if (sc.Connections.Count > 0) o["connections"] = DescribeConnections(sc.Connections, station);
                        if (sc.Bindings.Count > 0) o["bindings"] = DescribeBindings(sc.Bindings, station);
                        var kids = new List<string>();
                        foreach (GraphicComponent g in sc.GraphicComponents) kids.Add(g.Name + " (" + g.GetType().Name + ")");
                        if (kids.Count > 0) o["children"] = kids;
                        components.Add(o);
                    }
                    if (depth < 8) Walk(sc, path, depth + 1); // nested smart components
                }
            }
            Walk(station, "", 0);

            var result = new Dictionary<string, object>
            {
                ["stationName"] = station.Name,
                ["stationLogic"] = new
                {
                    signals = DescribeSignals(station.IOSignals),
                    connections = DescribeConnections(station.Connections, station),
                    bindings = DescribeBindings(station.Bindings, station)
                },
                ["smartComponents"] = components
            };

            if (withFrames)
            {
                // RAPID work objects (wobjdata), as RobotStudio places them in the station
                object Pose(Transform t)
                {
                    try
                    {
                        var m = t.GlobalMatrix;
                        var e = m.EulerZYX;
                        return new
                        {
                            position = Vec3Mm(m.Translation),
                            eulerZYX_deg = new
                            {
                                rx = Math.Round(e.x * 180 / Math.PI, 3),
                                ry = Math.Round(e.y * 180 / Math.PI, 3),
                                rz = Math.Round(e.z * 180 / Math.PI, 3)
                            },
                            xAxis = new { x = Math.Round(m.x.x, 4), y = Math.Round(m.x.y, 4), z = Math.Round(m.x.z, 4) }
                        };
                    }
                    catch { return null; }
                }
                var wobjs = new List<object>();
                foreach (VirtualController vc in station.VirtualControllers)
                {
                    var tasks = vc.Tasks.Cast<RsTask>().Concat(vc.NonMotionTasks.Cast<RsTask>());
                    foreach (var task in tasks)
                        foreach (var d in task.DataDeclarations)
                        {
                            if (!(d is RsWorkObject wo)) continue;
                            if (!string.IsNullOrEmpty(frameFilter) && (wo.Name ?? "").IndexOf(frameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            wobjs.Add(new
                            {
                                name = wo.Name,
                                controller = vc.Name,
                                task = task.Name,
                                module = wo.ModuleName,
                                robotHold = wo.RobotHold,
                                userFrameProgrammed = wo.UserFrameProgrammed,
                                userFrameMechanicalUnit = wo.UserFrameMechanicalUnit,
                                userFrame = Pose(wo.UserFrame),
                                objectFrame = Pose(wo.ObjectFrame)
                            });
                        }
                }
                result["workObjects"] = wobjs;

                var frames = new List<object>();
                foreach (Frame f in station.Frames)
                {
                    if (!string.IsNullOrEmpty(frameFilter) && (f.Name ?? "").IndexOf(frameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var fo = new Dictionary<string, object> { ["name"] = f.Name };
                    try
                    {
                        var m = f.Transform.GlobalMatrix;
                        fo["position"] = Vec3Mm(m.Translation);
                        var e = m.EulerZYX;
                        fo["eulerZYX_deg"] = new
                        {
                            rx = Math.Round(e.x * 180 / Math.PI, 3),
                            ry = Math.Round(e.y * 180 / Math.PI, 3),
                            rz = Math.Round(e.z * 180 / Math.PI, 3)
                        };
                    }
                    catch { }
                    frames.Add(fo);
                }
                result["stationFrames"] = frames;
            }
            return result;
        }

        // ── POST /station/part/set-frame ──────────────────────────────────────
        // Body: {"name":"Palette_28x38","origin":[x,y,z],"xAxis":[x,y,z],"zAxis":[x,y,z]}  (mm, station frame)
        // Moves the Part's local frame to the given pose WITHOUT moving its geometry (bodies are
        // re-placed at their previous global pose). Y = Z x X. One undo step.
        private object SetPartFrame(string body)
        {
            var station = GetActiveStation();
            var req = JObject.Parse(body ?? "{}");
            var name = (string)req["name"];
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("Required: name, origin, xAxis, zAxis");

            ABB.Robotics.Math.Vector3 Vec(string key, double scale)
            {
                var a = req[key] as JArray;
                if (a == null || a.Count != 3) throw new Exception(key + " must be [x,y,z]");
                return new ABB.Robotics.Math.Vector3((double)a[0] * scale, (double)a[1] * scale, (double)a[2] * scale);
            }
            var origin = Vec("origin", 0.001);
            var xAxis = Vec("xAxis", 1).Normalized();
            var zAxis = Vec("zAxis", 1).Normalized();
            if (Math.Abs(xAxis.Dot(zAxis)) > 1e-6)
                throw new Exception("xAxis and zAxis must be perpendicular");
            var yAxis = zAxis.Cross(xAxis);
            var frame = new ABB.Robotics.Math.Matrix4(xAxis, yAxis, zAxis, origin);

            Part part = null;
            void Find(ProjectObject parent, int depth)
            {
                foreach (var child in ChildrenOf(parent))
                {
                    if (part != null) return;
                    if (child is Part p && (p.Name ?? "").Equals(name, StringComparison.OrdinalIgnoreCase)) { part = p; return; }
                    if (depth < MaxMaxDepth) Find(child, depth + 1);
                }
            }
            Find(station, 0);
            if (part == null) throw new Exception("No Part named '" + name + "'");

            var before = part.GetBoundingBox(true);
            var bodies = part.Bodies.Cast<Body>().ToList();
            var poses = bodies.Select(b => b.Transform.GlobalMatrix).ToList();

            var undo = Project.UndoContext;
            undo.BeginUndoStep("ClaudeBridge: set frame of " + part.Name);
            try
            {
                part.Transform.GlobalMatrix = frame;
                for (int i = 0; i < bodies.Count; i++)
                    bodies[i].Transform.GlobalMatrix = poses[i]; // geometry stays where it was
            }
            finally
            {
                undo.EndUndoStep();
            }

            var after = part.GetBoundingBox(true);
            double dev = (before.IsValid() && after.IsValid()) ? new[] {
                Math.Abs(after.min.x - before.min.x), Math.Abs(after.min.y - before.min.y), Math.Abs(after.min.z - before.min.z),
                Math.Abs(after.max.x - before.max.x), Math.Abs(after.max.y - before.max.y), Math.Abs(after.max.z - before.max.z)
            }.Max() * 1000 : double.NaN;

            var g = part.Transform.GlobalMatrix;
            var e = g.EulerZYX;
            return new
            {
                success = true,
                name = part.Name,
                frame = new
                {
                    origin = Vec3Mm(g.Translation),
                    xAxis = new { x = Math.Round(g.x.x, 4), y = Math.Round(g.x.y, 4), z = Math.Round(g.x.z, 4) },
                    yAxis = new { x = Math.Round(g.y.x, 4), y = Math.Round(g.y.y, 4), z = Math.Round(g.y.z, 4) },
                    zAxis = new { x = Math.Round(g.z.x, 4), y = Math.Round(g.z.y, 4), z = Math.Round(g.z.z, 4) },
                    eulerZYX_deg = new { rx = Math.Round(e.x * 180 / Math.PI, 3), ry = Math.Round(e.y * 180 / Math.PI, 3), rz = Math.Round(e.z * 180 / Math.PI, 3) }
                },
                bodyCount = bodies.Count,
                boundingBox = BBoxMm(after),
                geometryDeviation_mm = Math.Round(dev, 2),
                note = "Geometry is unchanged if geometryDeviation_mm is ~0. Undo with Ctrl+Z. Station not saved."
            };
        }

        private object SaveStation()
        {
            var station = GetActiveStation();
            station.Save();
            return new { success = true, message = "Station '" + station.Name + "' saved" };
        }

        // ── RAPID Endpoints ──────────────────────────────────

        private object GetRapidTasks()
        {
            var ctrl = GetController();
            var tasks = new List<object>();

            void AddTasks(IEnumerable<RsTask> taskList, string category)
            {
                foreach (var task in taskList)
                {
                    var children = new List<string>();
                    foreach (ProjectObject child in task.Children)
                    {
                        children.Add(child.Name + " (" + child.GetType().Name + ")");
                        foreach (ProjectObject gc in child.Children)
                            children.Add("  " + gc.Name + " (" + gc.GetType().Name + ")");
                    }
                    tasks.Add(new
                    {
                        name = task.Name,
                        category,
                        isMotionTask = task.IsMotionTask,
                        pathProcedures = task.PathProcedures.Count,
                        dataDeclarations = task.DataDeclarations.Count,
                        children
                    });
                }
            }

            AddTasks(ctrl.Tasks.Cast<RsTask>(), "Motion");
            AddTasks(ctrl.NonMotionTasks.Cast<RsTask>(), "NonMotion");
            return tasks;
        }

        private object GetRapidModules(string taskName)
        {
            using (var controller = ConnectToController())
            {
                var task = controller.Rapid.GetTask(taskName ?? "T_ROB1");
                var modules = new List<object>();
                foreach (Module mod in task.GetModules())
                {
                    modules.Add(new
                    {
                        name = mod.Name,
                        type = mod.Type.ToString(),
                        isSystem = mod.IsSystem
                    });
                }
                return modules;
            }
        }

        private object GetModuleText(string taskName, string moduleName)
        {
            string step = "start";
            try
            {
                step = "ConnectToController";
                using (var controller = ConnectToController())
                {
                    step = "GetTask(" + taskName + ")";
                    var task = controller.Rapid.GetTask(taskName ?? "T_ROB1");

                    step = "GetModule(" + moduleName + ")";
                    object module = null;
                    bool isSystem = false;
                    try
                    {
                        module = task.GetModule(moduleName);
                        if (module == null)
                            isSystem = true; // treat as system module
                        else
                            isSystem = TryGetStringProperty(module, "IsSystem") == "True";
                    }
                    catch
                    {
                        // System modules may throw on GetModule — treat as system
                        isSystem = true;
                    }

                    var localDir = Path.Combine(Path.GetTempPath(), "claude_rapid");
                    Directory.CreateDirectory(localDir);
                    var localFile = Path.Combine(localDir, moduleName + ".mod");

                    if (!isSystem)
                    {
                        bool found = false;

                        // Strategy 1: SaveProgramToFile (needs program loaded)
                        step = "SaveProgramToFile";
                        try
                        {
                            var remoteDir = "$HOME/_claude_tmp_prog";
                            task.SaveProgramToFile(remoteDir);
                            step = "FindInSavedProgram";
                            var files = controller.FileSystem.GetFilesAndDirectories(remoteDir + "/*");
                            string foundRemote = null;
                            foreach (var f in files)
                            {
                                var fname = Path.GetFileNameWithoutExtension(f.Name);
                                if (fname.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                                { foundRemote = remoteDir + "/" + f.Name; break; }
                            }
                            if (foundRemote != null)
                            {
                                step = "GetFile(" + foundRemote + ")";
                                controller.FileSystem.GetFile(foundRemote, localFile, true);
                                string text = File.ReadAllText(localFile);
                                try { controller.FileSystem.RemoveDirectory(remoteDir, true); } catch { }
                                try { File.Delete(localFile); } catch { }
                                return new { task = taskName, module = moduleName, type = "program", text };
                            }
                            try { controller.FileSystem.RemoveDirectory(remoteDir, true); } catch { }
                        }
                        catch (Exception saveEx)
                        {
                            Logger.AddMessage(new LogMessage("ClaudeBridge: SaveProgramToFile failed: " + saveEx.Message));
                        }

                        // Strategy 2: Try module.SaveToFile via reflection
                        if (!found && module != null)
                        {
                            step = "ModuleSaveToFile";
                            try
                            {
                                var saveMethod = module.GetType().GetMethod("SaveToFile", new[] { typeof(string) });
                                if (saveMethod != null)
                                {
                                    saveMethod.Invoke(module, new object[] { "$HOME" });
                                    var saveCtrl = GetController();
                                    var savedFile = Path.Combine(saveCtrl.SystemPath, "HOME", moduleName + ".sysx");
                                    if (File.Exists(savedFile))
                                    {
                                        string text = File.ReadAllText(savedFile);
                                        try { File.Delete(savedFile); } catch { }
                                        return new { task = taskName, module = moduleName, type = "program", text };
                                    }
                                }
                            }
                            catch (Exception modEx)
                            {
                                Logger.AddMessage(new LogMessage("ClaudeBridge: ModuleSaveToFile failed: " + modEx.Message));
                            }
                        }

                        // Strategy 3: Search VC disk recursively
                        step = "SearchVCDisk";
                        var diskCtrl = GetController();
                        var searchRoot = Path.Combine(diskCtrl.SystemPath, "HOME");
                        if (Directory.Exists(searchRoot))
                        {
                            // Several copies may exist in sub-folders: take the most recent one
                            var newest = new[] { "*.mod", "*.modx", "*.sys", "*.sysx", "*.pgf" }
                                .SelectMany(p => Directory.GetFiles(searchRoot, p, SearchOption.AllDirectories))
                                .Where(f => Path.GetFileNameWithoutExtension(f).Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(f => File.GetLastWriteTime(f))
                                .FirstOrDefault();
                            if (newest != null)
                            {
                                return new
                                {
                                    task = taskName, module = moduleName, type = "program",
                                    text = File.ReadAllText(newest),
                                    source = "disk: " + newest,
                                    sourceModified = File.GetLastWriteTime(newest).ToString("yyyy-MM-dd HH:mm:ss"),
                                    note = "Read from the virtual controller's disk copy; it may be older than the module loaded in the controller."
                                };
                            }
                        }
                        // Strategy 4: Controller filesystem search
                        step = "ControllerFS";
                        try
                        {
                            var ctrlFS = GetController();
                            var searchPaths = new[] { "$HOME", "$SYSTEM", "$SYSTEM/RAPID", "$HOME/_claude_tmp_prog" };
                            foreach (var sp in searchPaths)
                            {
                                try
                                {
                                    var entries = controller.FileSystem.GetFilesAndDirectories(sp + "/*");
                                    foreach (var entry in entries)
                                    {
                                        if (Path.GetFileNameWithoutExtension(entry.Name).Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            step = "GetFile(" + sp + "/" + entry.Name + ")";
                                            controller.FileSystem.GetFile(sp + "/" + entry.Name, localFile, true);
                                            string text = File.ReadAllText(localFile);
                                            try { File.Delete(localFile); } catch { }
                                            return new { task = taskName, module = moduleName, type = "program", text };
                                        }
                                    }
                                }
                                catch { /* path may not exist */ }
                            }
                        }
                        catch (Exception fsEx)
                        {
                            Logger.AddMessage(new LogMessage("ClaudeBridge: ControllerFS search failed: " + fsEx.Message));
                        }

                        // Strategy 5: RWS REST API (reads directly from controller memory)
                        step = "RWS";
                        try
                        {
                            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                            {
                                var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes("Default User:robotics"));
                                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
                                var rwsUrl = "http://localhost:80/rw/rapid/tasks/" + Uri.EscapeDataString(taskName ?? "T_ROB1")
                                    + "/modules/" + Uri.EscapeDataString(moduleName) + "/text";
                                var resp = http.GetAsync(rwsUrl).Result;
                                if (resp.IsSuccessStatusCode)
                                {
                                    var xml = resp.Content.ReadAsStringAsync().Result;
                                    // Extract text from RWS XML response
                                    var m = System.Text.RegularExpressions.Regex.Match(xml,
                                        @"class=""module-text""[^>]*>([^<]*)<", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                    if (m.Success && m.Groups[1].Value.Trim().Length > 0)
                                    {
                                        return new { task = taskName, module = moduleName, type = "program", text = m.Groups[1].Value.Trim() };
                                    }
                                }
                            }
                        }
                        catch (Exception rwsEx)
                        {
                            Logger.AddMessage(new LogMessage("ClaudeBridge: RWS fallback failed: " + rwsEx.Message));
                        }

                        throw new Exception("Cannot read module '" + moduleName + "'. Tried: SaveProgramToFile, ModuleSaveToFile, disk scan, controller FS, RWS API. VC path: " + searchRoot);
                    }

                    step = "SearchSystemModule";
                    string[] searchDirs = { "$HOME", "$SYSTEM", "$SYSTEM/RAPID" };

                    foreach (var dir in searchDirs)
                    {
                        try
                        {
                            var files = controller.FileSystem.GetFilesAndDirectories(dir + "/*");
                            foreach (var f in files)
                            {
                                var fname = Path.GetFileNameWithoutExtension(f.Name);
                                if (fname.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                                {
                                    step = "GetFile(" + dir + "/" + f.Name + ")";
                                    controller.FileSystem.GetFile(dir + "/" + f.Name, localFile, true);
                                    string text = File.ReadAllText(localFile);
                                    try { File.Delete(localFile); } catch { }
                                    return new { task = taskName, module = moduleName, type = "system", text };
                                }
                            }
                        }
                        catch { }
                    }

                    step = "SaveToFile to $HOME";
                    if (module != null)
                    {
                        var saveMethod = module.GetType().GetMethod("SaveToFile", new[] { typeof(string) });
                        if (saveMethod != null)
                            saveMethod.Invoke(module, new object[] { "$HOME" });
                        else
                            throw new Exception("SaveToFile method not found on " + module.GetType().FullName);
                        step = "ReadFromDisk";
                        var rsCtrl = GetController();
                        var savedFile = Path.Combine(rsCtrl.SystemPath, "HOME", moduleName + ".sysx");
                        if (File.Exists(savedFile))
                        {
                            string sysText = File.ReadAllText(savedFile);
                            try { File.Delete(savedFile); } catch { }
                            return new { task = taskName, module = moduleName, type = "system", text = sysText };
                        }
                    }

                    // Last resort: read directly from VC disk
                    var ctrl = GetController();
                    var homePath = Path.Combine(ctrl.SystemPath, "HOME");
                    if (Directory.Exists(homePath))
                    {
                        foreach (var f in Directory.GetFiles(homePath, "*.*"))
                        {
                            if (Path.GetFileNameWithoutExtension(f).Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                            {
                                string text = File.ReadAllText(f);
                                return new { task = taskName, module = moduleName, type = "system", text };
                            }
                        }
                    }
                    throw new Exception("Could not read module '" + moduleName + "' — tried SaveToFile and disk scan in " + homePath);
                }
            }
            catch (Exception ex)
            {
                throw new Exception("GetModuleText failed at step '" + step + "': " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private object SetModuleText(string taskName, string moduleName, string body)
        {
            var data = JObject.Parse(body);
            var code = data["code"]?.ToString();
            if (string.IsNullOrEmpty(code))
                throw new Exception("Missing 'code' in request body");

            // BOM-free UTF-8 + CRLF normalization
            var normalizedCode = code.Replace("\r\n", "\n").Replace("\n", "\r\n");

            using (var controller = ConnectToController())
            {
                var task = controller.Rapid.GetTask(taskName ?? "T_ROB1");

                // Clean up old modules before loading new one
                bool replace = data["replace"] == null || data["replace"].Value<bool>();
                if (replace)
                {
                    foreach (Module existingMod in task.GetModules())
                    {
                        var mName = existingMod.Name;
                        if (mName.Equals("BASE", StringComparison.OrdinalIgnoreCase) ||
                            mName.Equals("user", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!existingMod.IsSystem && !mName.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                        {
                            try { task.DeleteModule(mName); } catch { }
                        }
                    }
                }

                var rsCtrl = GetController();
                var vcHome = Path.Combine(rsCtrl.SystemPath, "HOME");
                var tmpFile = Path.Combine(vcHome, "_claude_tmp_" + moduleName + ".mod");
                try
                {
                    File.WriteAllText(tmpFile, normalizedCode, new UTF8Encoding(false));
                    using (var mastership = Mastership.Request(controller))
                    {
                        try { task.DeleteModule(moduleName); } catch { }
                        task.LoadModuleFromFile(tmpFile, RapidLoadMode.Add);
                    }
                    return new { success = true, message = "Module " + moduleName + " updated" };
                }
                finally
                {
                    try { File.Delete(tmpFile); } catch { }
                }
            }
        }

        private object GetRapidVariable(string taskName, string varName)
        {
            var ctrl = GetController();
            var task = FindTask(ctrl, taskName);

            foreach (RsDataDeclaration decl in task.DataDeclarations)
            {
                if (decl.Name.Equals(varName, StringComparison.OrdinalIgnoreCase))
                {
                    var result = new Dictionary<string, object>
                    {
                        ["name"] = decl.Name,
                        ["dataType"] = decl.DataType,
                        ["moduleName"] = decl.ModuleName,
                        ["storageType"] = decl.StorageType.ToString(),
                        ["local"] = decl.Local
                    };

                    var initExpr = TryGetStringProperty(decl, "InitialExpression");
                    if (initExpr != null) result["value"] = initExpr;

                    return result;
                }
            }
            throw new Exception("Variable '" + varName + "' not found in task '" + taskName + "'");
        }

        private object SetRapidVariable(string taskName, string varName, string body)
        {
            var data = JObject.Parse(body);
            var value = data["value"]?.ToString();
            if (value == null) throw new Exception("Missing 'value' in request body");

            var ctrl = GetController();
            var task = FindTask(ctrl, taskName);

            foreach (RsDataDeclaration decl in task.DataDeclarations)
            {
                if (decl.Name.Equals(varName, StringComparison.OrdinalIgnoreCase))
                {
                    if (TrySetStringProperty(decl, "InitialExpression", value))
                        return new { success = true, message = "Variable " + varName + " set to " + value };
                    throw new Exception("Cannot set InitialExpression on this declaration type");
                }
            }
            throw new Exception("Variable '" + varName + "' not found");
        }

        private object ListRapidVariables(NameValueCollection query)
        {
            var ctrl = GetController();
            var taskName = query["task"] ?? "T_ROB1";
            var typeFilter = query["typeFilter"]?.ToLower();
            var moduleFilter = query["module"];

            var task = FindTask(ctrl, taskName);
            var variables = new List<object>();

            foreach (RsDataDeclaration decl in task.DataDeclarations)
            {
                if (!string.IsNullOrEmpty(moduleFilter) &&
                    !decl.ModuleName.Equals(moduleFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(typeFilter) &&
                    !decl.DataType.ToLower().Contains(typeFilter))
                    continue;

                var varInfo = new Dictionary<string, object>
                {
                    ["name"] = decl.Name,
                    ["dataType"] = decl.DataType,
                    ["moduleName"] = decl.ModuleName,
                    ["storageType"] = decl.StorageType.ToString(),
                    ["local"] = decl.Local
                };

                var initExpr = TryGetStringProperty(decl, "InitialExpression");
                if (initExpr != null) varInfo["initialValue"] = initExpr;

                variables.Add(varInfo);
            }

            return new { task = taskName, variableCount = variables.Count, variables };
        }

        private object GetExecutionErrors(string countStr)
        {
            int maxCount = 10;
            if (!string.IsNullOrEmpty(countStr)) int.TryParse(countStr, out maxCount);
            if (maxCount > 100) maxCount = 100;

            var errors = new List<object>();
            try
            {
                using (var controller = ConnectToController())
                {
                    var categories = controller.EventLog.GetCategories();
                    int collected = 0;
                    foreach (var cat in categories)
                    {
                        var catName = TryGetStringProperty(cat, "Name") ?? "?";
                        var messages = TryGetProperty(cat, "Messages") as System.Collections.IEnumerable;
                        if (messages != null)
                        {
                            foreach (var msg in messages)
                            {
                                if (collected >= maxCount) break;
                                errors.Add(new
                                {
                                    category = catName,
                                    title = TryGetStringProperty(msg, "Title") ?? "",
                                    body = TryGetStringProperty(msg, "Body") ?? "",
                                    timestamp = (TryGetProperty(msg, "Timestamp") as DateTime?)?.ToString("o") ?? "",
                                    type = TryGetStringProperty(msg, "Type") ?? ""
                                });
                                collected++;
                            }
                        }
                        if (collected >= maxCount) break;
                    }
                }
            }
            catch (Exception ex)
            {
                return new { error = "Event log not available: " + ex.Message };
            }

            return new { errors, count = errors.Count };
        }

        // ── Controller Status ────────────────────────────────

        private object GetControllerStatus()
        {
            var ctrl = GetController();
            return new
            {
                name = ctrl.Name,
                systemId = ctrl.SystemId,
                systemState = ctrl.SystemState.ToString(),
                systemPath = ctrl.SystemPath,
                runMode = TryGetStringProperty(ctrl, "RunMode") ?? "unknown",
                simulationState = Simulator.State.ToString(),
                simulationSpeed = Simulator.SimulationSpeed
            };
        }

        // ── Simulation Endpoints ─────────────────────────────

        private object StartSimulation()
        {
            Simulator.StartAsync();
            return new { success = true, message = "Simulation started", state = Simulator.State.ToString() };
        }

        private object StopSimulation()
        {
            Simulator.StopAsync();
            return new { success = true, message = "Simulation stopped", state = Simulator.State.ToString() };
        }

        private object PauseSimulation()
        {
            Simulator.PauseAsync();
            return new { success = true, message = "Simulation paused", state = Simulator.State.ToString() };
        }

        private object ResetSimulation()
        {
            Simulator.StopAsync();

            // Try to reset Program Pointer via RWS-style action on controller
            try
            {
                using (var controller = ConnectToController())
                {
                    using (Mastership.Request(controller))
                    {
                        // Attempt PP reset via reflection (API varies by SDK version)
                        var rapid = controller.Rapid;
                        var resetMethod = rapid.GetType().GetMethod("ResetProgramPointer")
                                       ?? rapid.GetType().GetMethod("ResetPP");
                        if (resetMethod != null)
                        {
                            resetMethod.Invoke(rapid, null);
                        }
                        else
                        {
                            // Fallback: try execution reset via known pattern
                            Logger.AddMessage(new LogMessage("ClaudeBridge: ResetProgramPointer not available in this SDK version"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.AddMessage(new LogMessage("ClaudeBridge PP reset note: " + ex.Message));
            }

            return new { success = true, message = "Simulation stopped and PP reset attempted", state = Simulator.State.ToString() };
        }

        private object GetSimulationStatus()
        {
            return new
            {
                state = Simulator.State.ToString(),
                currentTime = Simulator.CurrentTime,
                simulationSpeed = Simulator.SimulationSpeed
            };
        }

        private object GetSimulationSpeed()
        {
            return new { simulationSpeed = Simulator.SimulationSpeed };
        }

        private object SetSimulationSpeed(string body)
        {
            var data = JObject.Parse(body);
            var speed = data["speed"]?.Value<double>() ?? 1.0;
            Simulator.SimulationSpeed = speed;
            return new { success = true, message = "Simulation speed set to " + speed };
        }

        // ── Position Endpoint ────────────────────────────────

        private object GetRobotPosition()
        {
            var station = GetActiveStation();
            var mechanisms = new List<object>();

            foreach (ProjectObject child in station.Children)
            {
                if (child is Mechanism mech)
                {
                    var jointValues = new List<double>();
                    try
                    {
                        var jvProp = mech.GetType().GetProperty("JointValues");
                        if (jvProp != null)
                        {
                            var jv = jvProp.GetValue(mech) as double[];
                            if (jv != null) jointValues.AddRange(jv);
                        }
                    }
                    catch { /* ignore */ }

                    double fx = 0, fy = 0, fz = 0;
                    try
                    {
                        var flange = mech.GetFlange(0);
                        var tProp = flange.GetType().GetProperty("Transform")
                                 ?? flange.GetType().GetProperty("Matrix");
                        if (tProp != null)
                        {
                            var mat = tProp.GetValue(flange);
                            fx = (double)(mat.GetType().GetProperty("X")?.GetValue(mat) ?? 0.0);
                            fy = (double)(mat.GetType().GetProperty("Y")?.GetValue(mat) ?? 0.0);
                            fz = (double)(mat.GetType().GetProperty("Z")?.GetValue(mat) ?? 0.0);
                        }
                    }
                    catch { /* ignore */ }

                    mechanisms.Add(new
                    {
                        name = mech.Name,
                        type = mech.GetType().Name,
                        flangeX = fx, flangeY = fy, flangeZ = fz,
                        joints = jointValues
                    });
                }
            }

            if (mechanisms.Count == 0)
            {
                var ctrl = GetController();
                foreach (RsMechanicalUnit mu in ctrl.MechanicalUnits)
                {
                    mechanisms.Add(new { name = mu.Name, type = mu.GetType().Name });
                }
            }

            return mechanisms;
        }

        // ── I/O Signal Endpoints ─────────────────────────────

        private object GetIOSignals()
        {
            var signals = new List<object>();

            try
            {
                using (var controller = ConnectToController())
                {
                    System.Collections.IEnumerable signalList = null;
                    try
                    {
                        // Try GetSignals with one int arg first
                        var method = controller.IOSystem.GetType().GetMethod("GetSignals", new[] { typeof(int) });
                        if (method != null)
                            signalList = method.Invoke(controller.IOSystem, new object[] { 0 }) as System.Collections.IEnumerable;
                    }
                    catch { }
                    if (signalList == null)
                    {
                        try
                        {
                            // Try GetSignals via reflection without args
                            var method = controller.IOSystem.GetType().GetMethod("GetSignals", new Type[0]);
                            if (method != null)
                                signalList = method.Invoke(controller.IOSystem, null) as System.Collections.IEnumerable;
                        }
                        catch { }
                    }
                    if (signalList == null)
                    {
                        try
                        {
                            // Last resort: try GetAllSignals / ListSignals
                            var method = controller.IOSystem.GetType().GetMethod("GetAllSignals")
                                      ?? controller.IOSystem.GetType().GetMethod("ListSignals");
                            if (method != null)
                                signalList = method.Invoke(controller.IOSystem, null) as System.Collections.IEnumerable;
                        }
                        catch { }
                    }

                    if (signalList != null)
                    {
                        int count = 0;
                        foreach (var signal in signalList)
                        {
                            if (count >= 500) break;
                            signals.Add(new
                            {
                                name = TryGetStringProperty(signal, "Name"),
                                type = TryGetStringProperty(signal, "Type"),
                                value = TryGetStringProperty(signal, "Value")
                            });
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new { message = "IO signals not accessible via controller: " + ex.Message + ". Try starting the virtual controller first." };
            }

            if (signals.Count == 0)
                return new { message = "No IO signals found. Ensure the virtual controller is started." };

            return signals;
        }

        private object GetIOSignal(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new Exception("Signal name required. Use /io/signals to list available signals.");

            try
            {
                using (var controller = ConnectToController())
                {
                    var signal = TryGetProperty(controller.IOSystem, name)
                              ?? TryGetPropertyViaMethod(controller.IOSystem, "GetSignal", name);
                    if (signal == null)
                        throw new Exception("Signal '" + name + "' not found");
                    return new
                    {
                        name = TryGetStringProperty(signal, "Name"),
                        type = TryGetStringProperty(signal, "Type"),
                        value = TryGetStringProperty(signal, "Value")
                    };
                }
            }
            catch (Exception ex) when (!ex.Message.StartsWith("Signal '"))
            {
                throw new Exception("Cannot read IO signal: " + ex.Message + ". Ensure virtual controller is running.");
            }
        }

        private object SetIOSignal(string name, string body)
        {
            if (string.IsNullOrEmpty(name))
                throw new Exception("Signal name required. Use /io/signals to list available signals.");

            var data = JObject.Parse(body);
            var valueObj = data["value"];
            if (valueObj == null) throw new Exception("Missing 'value' in request body");

            try
            {
                using (var controller = ConnectToController())
                {
                    using (Mastership.Request(controller))
                    {
                        var signal = TryGetProperty(controller.IOSystem, name)
                                  ?? TryGetPropertyViaMethod(controller.IOSystem, "GetSignal", name);
                        if (signal == null)
                            throw new Exception("Signal '" + name + "' not found");

                        var prevValue = TryGetStringProperty(signal, "Value");
                        var setProp = signal.GetType().GetProperty("Value");
                        if (setProp != null && setProp.CanWrite)
                        {
                            var targetType = setProp.PropertyType;
                            if (targetType == typeof(float) || targetType == typeof(double))
                                setProp.SetValue(signal, Convert.ChangeType(valueObj.Value<double>(), targetType));
                            else
                                setProp.SetValue(signal, valueObj.Value<double>());
                        }

                        return new
                        {
                            success = true,
                            message = "Signal " + name + " set to " + valueObj.Value<double>(),
                            previousValue = prevValue,
                            newValue = TryGetStringProperty(signal, "Value")
                        };
                    }
                }
            }
            catch (Exception ex) when (!ex.Message.StartsWith("Signal '"))
            {
                throw new Exception("Cannot set IO signal: " + ex.Message + ". Ensure virtual controller is running.");
            }
        }

        // ── Path & Target Endpoints ──────────────────────────

        private object GetPaths()
        {
            var station = GetActiveStation();
            var paths = new List<object>();
            CollectPaths(station, paths);
            return paths;
        }

        private void CollectPaths(ProjectObject parent, List<object> paths)
        {
            foreach (ProjectObject child in parent.Children)
            {
                if (child is RsPathProcedure pp)
                {
                    paths.Add(new
                    {
                        name = pp.Name,
                        instructionCount = pp.Instructions.Count
                    });
                }
                CollectPaths(child, paths);
            }
        }

        private object GetPathTargets(string pathName)
        {
            var station = GetActiveStation();
            var path = FindPath(station, pathName);
            if (path == null) throw new Exception("Path '" + pathName + "' not found");

            var targets = new List<object>();
            foreach (RsInstruction instr in path.Instructions)
            {
                if (instr is RsMoveInstruction move)
                {
                    var targetObj = TryGetProperty(move, "Target")
                                ?? TryGetProperty(move, "RobTarget")
                                ?? TryGetProperty(move, "ToPoint");

                    var instrName = TryGetStringProperty(move, "InstructionName")
                                 ?? TryGetStringProperty(move, "Name")
                                 ?? move.GetType().Name;

                    if (targetObj != null)
                    {
                        targets.Add(new
                        {
                            name = TryGetStringProperty(targetObj, "Name") ?? "?",
                            instruction = instrName,
                            type = targetObj.GetType().Name
                        });
                    }
                    else
                    {
                        targets.Add(new { name = move.Name, instruction = instrName });
                    }
                }
            }
            return targets;
        }

        private object CreatePath(string body)
        {
            var data = JObject.Parse(body);
            var name = data["name"]?.ToString() ?? "NewPath";
            var ctrl = GetController();
            var task = ctrl.Tasks.Count > 0 ? ctrl.Tasks[0] : throw new Exception("No tasks");

            var path = new RsPathProcedure(name);
            task.PathProcedures.Add(path);
            return new { success = true, message = "Path '" + name + "' created" };
        }

        private object CreateTarget(string body)
        {
            var data = JObject.Parse(body);
            var name = data["name"]?.ToString() ?? "Target";
            var x = data["x"]?.Value<double>() ?? 0;
            var y = data["y"]?.Value<double>() ?? 0;
            var z = data["z"]?.Value<double>() ?? 0;

            var ctrl = GetController();
            var task = ctrl.Tasks.Count > 0 ? ctrl.Tasks[0] : throw new Exception("No tasks");

            var target = new RsRobTarget();
            target.Name = name;

            var transProp = target.GetType().GetProperty("Transform")
                         ?? target.GetType().GetProperty("Frame");
            if (transProp != null && transProp.CanWrite)
            {
                var frame = new ABB.Robotics.Math.Matrix4(
                    ABB.Robotics.Math.Vector3.XVector,
                    ABB.Robotics.Math.Vector3.YVector,
                    ABB.Robotics.Math.Vector3.ZVector,
                    new ABB.Robotics.Math.Vector3(x, y, z)
                );
                transProp.SetValue(target, frame);
            }
            task.DataDeclarations.Add(target);
            return new { success = true, message = "Target '" + name + "' at (" + x + ", " + y + ", " + z + ")" };
        }

        // ── Config File Endpoints ────────────────────────────

        private object ReadConfigFile(string cfgName)
        {
            var ctrl = GetController();
            var sysPath = ctrl.SystemPath;
            if (string.IsNullOrEmpty(sysPath))
                throw new Exception("Cannot determine system directory");

            var candidates = new[]
            {
                Path.Combine(sysPath, "SYSPAR", cfgName ?? "SYS.cfg"),
                Path.Combine(sysPath, cfgName ?? "SYS.cfg"),
            };

            foreach (var cfgPath in candidates)
            {
                if (File.Exists(cfgPath))
                    return new { path = cfgPath, content = File.ReadAllText(cfgPath) };
            }
            throw new Exception("Config file '" + cfgName + "' not found in " + sysPath);
        }

        private object WriteConfigFile(string cfgName, string body)
        {
            var data = JObject.Parse(body);
            var content = data["content"]?.ToString();
            if (string.IsNullOrEmpty(content))
                throw new Exception("Missing 'content' in request body");

            var ctrl = GetController();
            var sysPath = ctrl.SystemPath;

            // Try SYSPAR first, fallback to root
            var cfgPath = Path.Combine(sysPath, "SYSPAR", cfgName ?? "SYS.cfg");
            var dir = Path.GetDirectoryName(cfgPath);
            if (!Directory.Exists(dir))
            {
                cfgPath = Path.Combine(sysPath, cfgName ?? "SYS.cfg");
            }

            File.WriteAllText(cfgPath, content);
            return new { success = true, message = "Config file " + cfgName + " written to " + cfgPath };
        }

        // ── Collision Detection ──────────────────────────────

        private object CheckCollisions()
        {
            var station = GetActiveStation();
            try
            {
                CollisionDetector.CheckCollisions(station);
                return new { success = true, message = "Collision check executed — no collisions detected" };
            }
            catch (Exception ex)
            {
                return new { success = false, message = "Collision detected: " + ex.Message };
            }
        }

        // ── Screenshot ───────────────────────────────────────

        private object TakeScreenshot(string body)
        {
            GetActiveStation(); // ensure station exists
            int width = 1280, height = 720;
            string savePath = null;

            if (!string.IsNullOrEmpty(body))
            {
                var data = JObject.Parse(body);
                if (data["width"] != null) width = Math.Min(data["width"].Value<int>(), 3840);
                if (data["height"] != null) height = Math.Min(data["height"].Value<int>(), 2160);
                if (data["savePath"] != null) savePath = data["savePath"].ToString();
            }

            string imageBase64 = null;

            // Route runs on UI thread via _uiQueue/OnIdle, so we can call
            // RobotStudio APIs directly without queuing another action.
            // (Queuing to _uiQueue from inside Route would deadlock.)
            try
            {
                TakeScreenshotInternal(width, height, out imageBase64);
            }
            catch (Exception ex)
            {
                throw new Exception("Screenshot failed: " + ex.Message, ex);
            }

            if (savePath != null)
            {
                // Validate savePath is within allowed directories
                var fullPath = Path.GetFullPath(savePath);
                var allowedBase = Path.GetFullPath(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RobotStudio"));
                Directory.CreateDirectory(allowedBase); // ensure base exists
                // Prevent path traversal: ensure base+separator match or exact base match
                var allowedWithSep = allowedBase.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? allowedBase
                    : allowedBase + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(allowedWithSep, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(fullPath, allowedBase, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("savePath must be within " + allowedBase);
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(fullPath, Convert.FromBase64String(imageBase64));
                return new
                {
                    imageBase64, width, height,
                    mimeType = "image/png",
                    savedPath = fullPath,
                    timestamp = DateTime.UtcNow.ToString("o")
                };
            }

            return new
            {
                imageBase64, width, height,
                mimeType = "image/png",
                savedPath = (string)null,
                timestamp = DateTime.UtcNow.ToString("o")
            };
        }

        private void TakeScreenshotInternal(int width, int height, out string imageBase64)
        {
            imageBase64 = null;

            // Search all loaded assemblies for GraphicControl type
            Type gcType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    gcType = asm.GetType("ABB.Robotics.RobotStudio.GraphicControl");
                    if (gcType == null)
                        gcType = asm.GetTypes().FirstOrDefault(t => t.Name == "GraphicControl");
                }
                catch { }
                if (gcType != null) break;
            }
            if (gcType == null)
                throw new Exception("GraphicControl type not found in any loaded assembly. Available types with 'Graphic': " +
                    string.Join(", ", AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                        .Where(t => t.Name.Contains("Graphic") || t.Name.Contains("Screen"))
                        .Take(10).Select(t => t.FullName)));

            var acProp = gcType.GetProperty("ActiveGraphicControl");
            if (acProp == null) throw new Exception("ActiveGraphicControl property not found");
            object gc = acProp.GetValue(null);
            if (gc == null) throw new Exception("No active graphic control — open a station with 3D view");

            var ssMethod = gc.GetType().GetMethod("ScreenShot", new Type[] { typeof(int), typeof(int) });
            if (ssMethod == null) throw new Exception("ScreenShot method not found on " + gc.GetType().FullName);

            using (var bitmap = ssMethod.Invoke(gc, new object[] { width, height }) as Bitmap)
            {
                if (bitmap == null) throw new Exception("ScreenShot returned null");
                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    imageBase64 = Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        // ── Reflection Helpers ───────────────────────────────

        private ProjectObject FindObjectByName(ProjectObject parent, string name)
        {
            foreach (ProjectObject child in parent.Children)
            {
                if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return child;
                var found = FindObjectByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private string TryGetStringProperty(object obj, string propName)
        {
            var prop = obj.GetType().GetProperty(propName);
            if (prop != null && prop.CanRead)
            {
                try { return prop.GetValue(obj)?.ToString(); }
                catch { return null; }
            }
            return null;
        }

        private bool TrySetStringProperty(object obj, string propName, string value)
        {
            var prop = obj.GetType().GetProperty(propName);
            if (prop != null && prop.CanWrite)
            {
                try { prop.SetValue(obj, value); return true; }
                catch { return false; }
            }
            return false;
        }

        private object TryGetProperty(object obj, string propName)
        {
            var prop = obj.GetType().GetProperty(propName);
            if (prop != null && prop.CanRead)
            {
                try { return prop.GetValue(obj); }
                catch { return null; }
            }
            return null;
        }

        private double GetDoubleProp(object obj, string propName)
        {
            var val = TryGetProperty(obj, propName);
            if (val == null) return 0.0;
            try { return Convert.ToDouble(val); }
            catch { return 0.0; }
        }

        private object TryGetPropertyViaMethod(object obj, string methodName, params object[] args)
        {
            var method = obj.GetType().GetMethod(methodName);
            if (method != null)
            {
                try { return method.Invoke(obj, args); }
                catch { return null; }
            }
            return null;
        }
    }
}
