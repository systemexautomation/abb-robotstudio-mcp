using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using Newtonsoft.Json.Linq;
using VirtualController = ABB.Robotics.RobotStudio.Stations.VirtualController;

namespace ClaudeBridge
{
    // Write access to the Station Logic: SmartComponents, their signals, I/O connections and
    // property bindings. Everything in one POST is wrapped in a single undo step and rolled back
    // when an operation fails.
    public partial class ClaudeBridgeAddIn
    {
        private static string LibraryDir()
        {
            var bin = Path.GetDirectoryName(typeof(Station).Assembly.Location);
            return Path.Combine(Path.GetDirectoryName(bin) ?? bin, "ABB Library", "Components");
        }

        // GET /station/logic/library — built-in SmartComponents that "create"+"library" can instantiate
        private object GetSmartComponentLibrary()
        {
            var dir = LibraryDir();
            var names = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.rslib").Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n).ToList()
                : new List<string>();
            return new { directory = dir, count = names.Count, components = names };
        }

        // Path of a SmartComponent below the station ("A/B"), "" or "(Station Logic)" = the station itself.
        private static ProjectObject LogicContainer(Station station, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "(Station Logic)") return station;
            ProjectObject cur = station;
            foreach (var seg in path.Split('/'))
            {
                ProjectObject next = null;
                foreach (var c in ChildrenOf(cur))
                    if (c is SmartComponent && string.Equals(c.Name, seg, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
                if (next == null)
                    throw new Exception("SmartComponent '" + seg + "' not found under '" + (ReferenceEquals(cur, station) ? "(Station Logic)" : cur.Name) + "' (path '" + path + "')");
                cur = next;
            }
            return cur;
        }

        // Object by name or path anywhere in the station (does not descend into Parts: they are huge).
        private static ProjectObject FindLogicObject(Station station, string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath)) return null;
            if (nameOrPath.Contains("/"))
            {
                try { return LogicContainer(station, nameOrPath); } catch { }
            }
            var queue = new Queue<ProjectObject>();
            queue.Enqueue(station);
            int visited = 0;
            while (queue.Count > 0 && visited++ < 100000)
            {
                var cur = queue.Dequeue();
                foreach (var c in ChildrenOf(cur))
                {
                    if (string.Equals(c.Name, nameOrPath, StringComparison.OrdinalIgnoreCase)) return c;
                    if (c is Part) continue;
                    queue.Enqueue(c);
                }
            }
            return null;
        }

        // Endpoint of a connection/binding as written by the Station Logic: the container itself,
        // one of its children, the station, or (at station level) a virtual controller.
        private static ProjectObject LogicEndpoint(Station station, ProjectObject container, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "(Station Logic)" || name == "(station)") return station;
            if (name == "(self)" || (container is SmartComponent && string.Equals(container.Name, name, StringComparison.OrdinalIgnoreCase)))
                return container;
            foreach (var c in ChildrenOf(container))
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            if (ReferenceEquals(container, station))
                foreach (VirtualController vc in station.VirtualControllers)
                    if (string.Equals(vc.Name, name, StringComparison.OrdinalIgnoreCase)) return vc;
            throw new Exception("Endpoint '" + name + "' not found in '" + (ReferenceEquals(container, station) ? "(Station Logic)" : container.Name) + "'");
        }

        private static IOSignalCollection SignalsOf(Station station, ProjectObject container)
            => container is SmartComponent sc ? sc.IOSignals : station.IOSignals;

        private static IOConnectionCollection ConnectionsOf(Station station, ProjectObject container)
            => container is SmartComponent sc ? sc.Connections : station.Connections;

        private static PropertyBindingCollection BindingsOf(Station station, ProjectObject container)
            => container is SmartComponent sc ? sc.Bindings : station.Bindings;

        private static void CheckSignal(Station station, ProjectObject endpoint, string signal)
        {
            IOSignalCollection coll = null;
            if (endpoint is SmartComponent sc) coll = sc.IOSignals;
            else if (ReferenceEquals(endpoint, station)) coll = station.IOSignals;
            if (coll != null && !coll.Contains(signal))
                throw new Exception("Signal '" + signal + "' does not exist on '" + (ReferenceEquals(endpoint, station) ? "(Station Logic)" : endpoint.Name) + "'");
        }

        private static void CheckProperty(ProjectObject endpoint, string property)
        {
            if (endpoint is SmartComponent sc && !sc.Properties.Contains(property))
                throw new Exception("Property '" + property + "' does not exist on '" + endpoint.Name + "'");
        }

        private static double UnitFactor(string unit)
        {
            switch ((unit ?? "").ToLowerInvariant())
            {
                case "mm": return 0.001;
                case "deg": return Math.PI / 180.0;
                default: return 1.0; // m, rad, s, raw
            }
        }

        private static object ConvertPropertyValue(Station station, DynamicProperty p, JToken v, string unit)
        {
            var t = p.ValueType;
            double f = UnitFactor(unit);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (t == typeof(bool))
                return v.Type == JTokenType.Boolean ? (bool)v : bool.Parse(v.ToString());
            if (t == typeof(int)) return Convert.ToInt32(v.ToString(), inv);
            if (t == typeof(long)) return Convert.ToInt64(v.ToString(), inv);
            if (t == typeof(double)) return Convert.ToDouble(v.ToString(), inv) * f;
            if (t == typeof(string)) return v.Type == JTokenType.Null ? "" : v.ToString();
            if (t == typeof(ABB.Robotics.Math.Vector3))
            {
                double[] n;
                if (v is JArray a) n = a.Select(x => Convert.ToDouble(x.ToString(), inv)).ToArray();
                else n = v.ToString().Trim('[', ']', ' ').Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToDouble(x, inv)).ToArray();
                if (n.Length != 3) throw new Exception("A Vector3 needs 3 numbers");
                return new ABB.Robotics.Math.Vector3(n[0] * f, n[1] * f, n[2] * f);
            }
            if (t.IsEnum)
                return int.TryParse(v.ToString(), out var iv) ? Enum.ToObject(t, iv) : Enum.Parse(t, v.ToString(), true);
            if (typeof(ProjectObject).IsAssignableFrom(t) || t.IsInterface)
            {
                if (v.Type == JTokenType.Null || string.IsNullOrWhiteSpace(v.ToString())) return null;
                var o = FindLogicObject(station, v.ToString());
                if (o == null) throw new Exception("Object '" + v + "' not found for property '" + p.Name + "'");
                if (!t.IsInstanceOfType(o)) throw new Exception("Object '" + v + "' is a " + o.GetType().Name + ", property '" + p.Name + "' needs " + t.Name);
                return o;
            }
            throw new Exception("Unsupported property type " + t.Name + " for '" + p.Name + "'");
        }

        // Name of a connection endpoint as the Station Logic lists it, without needing the object to exist
        private static string EndpointName(Station station, ProjectObject container, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "(Station Logic)" || name == "(station)") return "(Station Logic)";
            if (name == "(self)") return ReferenceEquals(container, station) ? "(Station Logic)" : container.Name;
            return name;
        }

        private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string LogicPath(ProjectObject po)
        {
            var parts = new List<string>();
            for (var cur = po; cur != null && !(cur is Station); cur = cur.Parent)
                if (cur is SmartComponent) parts.Insert(0, cur.Name);
            return string.Join("/", parts);
        }

        // POST /station/logic/edit
        // {"ops":[ {"op":"create","parent":"","name":"X","library":"Source"|"copyOf":"Box Manager/BoxCopy"},
        //          {"op":"setProperty","path":"X","property":"Position","value":[1,2,3],"unit":"mm"},
        //          {"op":"addSignal","path":"X","name":"Go","type":"DigitalInput","autoReset":false},
        //          {"op":"connect","container":"","source":"A","sourceSignal":"s","target":"B","targetSignal":"t"},
        //          {"op":"bind","container":"","source":"A","sourceProperty":"p","target":"B","targetProperty":"q"},
        //          {"op":"delete|rename|removeSignal|disconnect|unbind|setVisible", ...} ]}
        private object EditStationLogic(string body)
        {
            var station = GetActiveStation();
            var req = JObject.Parse(body ?? "{}");
            var ops = req["ops"] as JArray;
            if (ops == null || ops.Count == 0) throw new Exception("Required: ops (non-empty array)");

            var results = new List<object>();
            var created = new List<GraphicComponent>();
            var undo = Project.UndoContext;
            undo.BeginUndoStep("ClaudeBridge: edit station logic");
            bool stepOpen = true;
            int index = -1;
            try
            {
                foreach (JObject op in ops)
                {
                    index++;
                    results.Add(ApplyLogicOp(station, op, created));
                }
                undo.EndUndoStep();
                stepOpen = false;
            }
            catch (Exception ex)
            {
                if (stepOpen)
                {
                    try { undo.CancelUndoStep(CancelUndoStepType.Rollback); } catch { }
                }
                // The undo rollback does not reliably remove new components: remove them explicitly.
                int removed = 0;
                for (int i = created.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var g = created[i];
                        // children of an already removed parent are gone with it
                        if (g.Parent != null) { RemoveLogicComponent(station, g); removed++; }
                    }
                    catch { }
                }
                var inner = ex is AggregateException ae && ae.InnerException != null ? ae.InnerException : ex;
                throw new Exception("Operation " + index + " (" + ((ops[index] as JObject)?["op"] ?? "?") + ") failed: " + inner.Message
                    + ". Undo rollback requested; " + removed + " component(s) created by this call were removed. "
                    + "Changes to pre-existing components may remain: check with rs_get_station_logic or press Ctrl+Z.");
            }
            return new { success = true, applied = results.Count, results, note = "One undo step (Ctrl+Z). Station not saved." };
        }

        // Detach a component from its parent collection, then dispose it (Delete() needs a null parent).
        private static void RemoveLogicComponent(Station station, GraphicComponent g)
        {
            var holder = (g.Parent as IHasGraphicComponents) ?? station;
            holder.GraphicComponents.Remove(g);
            try { g.Delete(); } catch { }
        }

        private object ApplyLogicOp(Station station, JObject op, List<GraphicComponent> created)
        {
            string kind = (string)op["op"];
            switch (kind)
            {
                case "create":
                {
                    var parent = LogicContainer(station, (string)op["parent"]);
                    var name = (string)op["name"];
                    if (string.IsNullOrWhiteSpace(name)) throw new Exception("create: name required");
                    var hg = parent as IHasGraphicComponents ?? throw new Exception("create: parent cannot hold components");
                    var coll = hg.GraphicComponents;
                    if (coll.Contains(name)) throw new Exception("create: '" + name + "' already exists in this parent");
                    GraphicComponent gc;
                    var library = (string)op["library"];
                    var copyOf = (string)op["copyOf"];
                    if (!string.IsNullOrEmpty(library))
                    {
                        var file = Path.Combine(LibraryDir(), library + ".rslib");
                        var arg = File.Exists(file) ? file : library;
                        var task = coll.AddLibraryComponentAsync(arg);
                        // The continuation of the async load needs the UI thread, which we occupy:
                        // pump messages while waiting instead of blocking.
                        var deadline = DateTime.UtcNow.AddSeconds(25);
                        while (!task.IsCompleted && DateTime.UtcNow < deadline)
                        {
                            System.Windows.Forms.Application.DoEvents();
                            System.Threading.Thread.Sleep(5);
                        }
                        if (!task.IsCompleted) throw new Exception("create: library component '" + library + "' did not load within 25 s");
                        gc = task.GetAwaiter().GetResult();
                    }
                    else if (!string.IsNullOrEmpty(copyOf))
                    {
                        var src = FindLogicObject(station, copyOf) as GraphicComponent ?? throw new Exception("create: copyOf '" + copyOf + "' not found");
                        gc = src.Copy() as GraphicComponent ?? throw new Exception("create: copy failed");
                        if (!coll.Contains(gc)) coll.Add(gc);
                    }
                    else
                    {
                        gc = new SmartComponent();
                        gc.Name = name;
                        coll.Add(gc);
                    }
                    gc.Name = name;
                    created.Add(gc);
                    return new { op = kind, path = LogicPath(parent) is var pp && pp.Length > 0 ? pp + "/" + gc.Name : gc.Name, type = gc.GetType().Name };
                }
                case "delete":
                {
                    var o = FindLogicObject(station, (string)op["path"]) as GraphicComponent ?? throw new Exception("delete: '" + (string)op["path"] + "' not found");
                    var n = o.Name;
                    RemoveLogicComponent(station, o);
                    return new { op = kind, deleted = n };
                }
                case "rename":
                {
                    var o = FindLogicObject(station, (string)op["path"]) ?? throw new Exception("rename: '" + (string)op["path"] + "' not found");
                    o.Name = (string)op["newName"];
                    return new { op = kind, name = o.Name };
                }
                case "setVisible":
                {
                    var o = FindLogicObject(station, (string)op["path"] ?? (string)op["name"]) as GraphicComponent ?? throw new Exception("setVisible: object not found");
                    o.Visible = (bool)op["visible"];
                    return new { op = kind, name = o.Name, visible = o.Visible };
                }
                case "setProperty":
                {
                    var sc = LogicContainer(station, (string)op["path"]) as SmartComponent ?? throw new Exception("setProperty: path must be a SmartComponent");
                    var pname = (string)op["property"];
                    if (!sc.Properties.Contains(pname)) throw new Exception("setProperty: '" + sc.Name + "' has no property '" + pname + "'");
                    var p = sc.Properties[pname];
                    var value = ConvertPropertyValue(station, p, op["value"] ?? JValue.CreateNull(), (string)op["unit"]);
                    var info = p.TrySetValue(value);
                    if (!info.Success) throw new Exception("setProperty: " + pname + " rejected (" + info.Result + (string.IsNullOrEmpty(info.ErrorMessage) ? "" : ": " + info.ErrorMessage) + ")");
                    return new { op = kind, path = LogicPath(sc), property = pname, displayValue = p.DisplayValue };
                }
                case "addProperty":
                {
                    var sc = LogicContainer(station, (string)op["path"]) as SmartComponent ?? throw new Exception("addProperty: path must be a SmartComponent");
                    var pname = (string)op["name"];
                    if (string.IsNullOrWhiteSpace(pname)) throw new Exception("addProperty: name required");
                    if (sc.Properties.Contains(pname)) throw new Exception("addProperty: '" + pname + "' already exists");
                    var typeName = (string)op["type"] ?? "System.Double";
                    var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Double"] = "System.Double", ["Boolean"] = "System.Boolean", ["Int32"] = "System.Int32", ["String"] = "System.String",
                        ["Vector3"] = "ABB.Robotics.Math.Vector3",
                        ["GraphicComponent"] = "ABB.Robotics.RobotStudio.Stations.GraphicComponent",
                        ["IHasTransform"] = "ABB.Robotics.RobotStudio.Stations.IHasTransform",
                        ["IAttachableChild"] = "ABB.Robotics.RobotStudio.Stations.IAttachableChild",
                        ["ProjectObject"] = "ABB.Robotics.RobotStudio.ProjectObject",
                        ["Part"] = "ABB.Robotics.RobotStudio.Stations.Part"
                    };
                    var full = aliases.TryGetValue(typeName, out var mapped) ? mapped : typeName;
                    var p = new DynamicProperty(pname, full);
                    sc.Properties.Add(p);
                    if (p.ValueType == null) { sc.Properties.Remove(pname); throw new Exception("addProperty: unknown type '" + typeName + "'"); }
                    if (op["description"] != null) p.Description = (string)op["description"];
                    if (op["value"] != null)
                    {
                        var info = p.TrySetValue(ConvertPropertyValue(station, p, op["value"], (string)op["unit"]));
                        if (!info.Success) throw new Exception("addProperty: initial value rejected (" + info.Result + ")");
                    }
                    return new { op = kind, path = LogicPath(sc), property = pname, type = p.ValueType.Name };
                }
                case "removeProperty":
                {
                    var sc = LogicContainer(station, (string)op["path"]) as SmartComponent ?? throw new Exception("removeProperty: path must be a SmartComponent");
                    var pname = (string)op["name"];
                    if (!sc.Properties.Contains(pname)) throw new Exception("removeProperty: '" + pname + "' not found");
                    sc.Properties.Remove(pname);
                    return new { op = kind, removed = pname };
                }
                case "addSignal":
                {
                    var container = LogicContainer(station, (string)op["path"]);
                    var name = (string)op["name"];
                    var coll = SignalsOf(station, container);
                    if (coll.Contains(name)) throw new Exception("addSignal: '" + name + "' already exists");
                    var type = (IOSignalType)Enum.Parse(typeof(IOSignalType), (string)op["type"] ?? "DigitalInput", true);
                    var sig = new IOSignal(name, type);
                    if (op["autoReset"] != null) sig.AutoReset = (bool)op["autoReset"];
                    coll.Add(sig);
                    return new { op = kind, signal = name, type = type.ToString() };
                }
                case "removeSignal":
                {
                    var container = LogicContainer(station, (string)op["path"]);
                    var name = (string)op["name"];
                    var coll = SignalsOf(station, container);
                    if (!coll.Contains(name)) throw new Exception("removeSignal: '" + name + "' not found");
                    coll.Remove(name);
                    return new { op = kind, removed = name };
                }
                case "connect":
                {
                    var container = LogicContainer(station, (string)op["container"]);
                    var src = LogicEndpoint(station, container, (string)op["source"]);
                    var tgt = LogicEndpoint(station, container, (string)op["target"]);
                    var ss = (string)op["sourceSignal"]; var ts = (string)op["targetSignal"];
                    CheckSignal(station, src, ss); CheckSignal(station, tgt, ts);
                    var conns = ConnectionsOf(station, container);
                    foreach (IOConnection c in conns)
                        if (ReferenceEquals(c.SourceObject, src) && c.SourceSignal == ss && ReferenceEquals(c.TargetObject, tgt) && c.TargetSignal == ts)
                            return new { op = kind, note = "already connected" };
                    var allow = op["allowCycle"] != null && (bool)op["allowCycle"];
                    conns.Add(new IOConnection(src, ss, tgt, ts, allow));
                    return new { op = kind, connection = ObjName(src, station) + "." + ss + " -> " + ObjName(tgt, station) + "." + ts };
                }
                case "disconnect":
                {
                    var container = LogicContainer(station, (string)op["container"]);
                    var srcName = EndpointName(station, container, (string)op["source"]);
                    var tgtName = EndpointName(station, container, (string)op["target"]);
                    var ss = (string)op["sourceSignal"]; var ts = (string)op["targetSignal"];
                    var conns = ConnectionsOf(station, container);
                    var found = conns.Cast<IOConnection>().FirstOrDefault(c => SameName(ObjName(c.SourceObject, station) ?? c.SourceObjectName, srcName) && c.SourceSignal == ss && SameName(ObjName(c.TargetObject, station) ?? c.TargetObjectName, tgtName) && c.TargetSignal == ts)
                        ?? throw new Exception("disconnect: connection not found");
                    conns.Remove(found);
                    return new { op = kind, removed = true };
                }
                case "bind":
                {
                    var container = LogicContainer(station, (string)op["container"]);
                    var src = LogicEndpoint(station, container, (string)op["source"]);
                    var tgt = LogicEndpoint(station, container, (string)op["target"]);
                    var sp = (string)op["sourceProperty"]; var tp = (string)op["targetProperty"];
                    CheckProperty(src, sp); CheckProperty(tgt, tp);
                    var binds = BindingsOf(station, container);
                    foreach (PropertyBinding b in binds)
                        if (ReferenceEquals(b.SourceObject, src) && b.SourceProperty == sp && ReferenceEquals(b.TargetObject, tgt) && b.TargetProperty == tp)
                            return new { op = kind, note = "already bound" };
                    var allow = op["allowCycle"] != null && (bool)op["allowCycle"];
                    binds.Add(new PropertyBinding(src, sp, tgt, tp, allow));
                    return new { op = kind, binding = ObjName(src, station) + "." + sp + " -> " + ObjName(tgt, station) + "." + tp };
                }
                case "unbind":
                {
                    var container = LogicContainer(station, (string)op["container"]);
                    var srcName = EndpointName(station, container, (string)op["source"]);
                    var tgtName = EndpointName(station, container, (string)op["target"]);
                    var sp = (string)op["sourceProperty"]; var tp = (string)op["targetProperty"];
                    var binds = BindingsOf(station, container);
                    var found = binds.Cast<PropertyBinding>().FirstOrDefault(b => SameName(ObjName(b.SourceObject, station) ?? b.SourceObjectName, srcName) && b.SourceProperty == sp && SameName(ObjName(b.TargetObject, station) ?? b.TargetObjectName, tgtName) && b.TargetProperty == tp)
                        ?? throw new Exception("unbind: binding not found");
                    binds.Remove(found);
                    return new { op = kind, removed = true };
                }
                default:
                    throw new Exception("Unknown op '" + kind + "'");
            }
        }
    }
}
