using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.ConfigurationDomain;
using Newtonsoft.Json.Linq;
using RobotStudio.API.Internal;

namespace ClaudeBridge
{
    // Creation of I/O signals in the live controller configuration (EIO_SIGNAL) through the
    // controller SDK. Works on the running configuration, unlike the .cfg files on disk.
    public partial class ClaudeBridgeAddIn
    {
        // GET /controller/config/signals?nameFilter=
        private object ListConfigSignals(System.Collections.Specialized.NameValueCollection query)
        {
            var filter = query["nameFilter"];
            using (var controller = ConnectToController())
            {
                var type = controller.Configuration.ExternalIO["EIO_SIGNAL"];
                var list = new List<object>();
                foreach (var inst in type.GetInstances())
                {
                    var name = Convert.ToString(inst.GetAttribute("Name"));
                    if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    list.Add(new
                    {
                        name,
                        signalType = Convert.ToString(inst.GetAttribute("SignalType")),
                        access = SafeAttr(inst, "Access"),
                        device = SafeAttr(inst, "Device")
                    });
                }
                return new { count = list.Count, signals = list };
            }
        }

        private static string SafeAttr(Instance inst, string attr)
        {
            try { return Convert.ToString(inst.GetAttribute(attr)); } catch { return null; }
        }

        // Wait for a Task while keeping the UI thread's message loop alive (its continuations may need it).
        private static void PumpUntilDone(Task task, int seconds, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
            if (!task.IsCompleted) throw new Exception(what + " did not finish within " + seconds + " s");
            task.GetAwaiter().GetResult(); // rethrows the original exception
        }

        // POST /controller/config/signals  {"signals":[{"name":"doFormat1","type":"DO","access":"All"}]}
        // Creates missing signals only (existing ones are reported, never modified).
        // A restart of the controller is usually needed before the new signals are usable.
        //
        // RobotWare 8 has no mastership service. Two routes to obtain write access to the configuration:
        //  1. RobotStudio's own controller connection (the one the Controller tab uses), through
        //     ExecuteWithMastershipResource(CFG, ...);
        //  2. a separate SDK connection registered as a control station.
        private object CreateConfigSignals(string body)
        {
            var req = JObject.Parse(body ?? "{}");
            var arr = req["signals"] as JArray;
            if (arr == null || arr.Count == 0) throw new Exception("Required: signals (non-empty array)");

            var created = new List<string>();
            var existing = new List<string>();

            void Write(Controller controller)
            {
                var type = controller.Configuration.ExternalIO["EIO_SIGNAL"];
                var known = new HashSet<string>(type.GetInstances().Select(i => Convert.ToString(i.GetAttribute("Name"))), StringComparer.OrdinalIgnoreCase);
                foreach (JObject s in arr)
                {
                    var name = (string)s["name"];
                    var sigType = (string)s["type"] ?? "DO";
                    var access = (string)s["access"] ?? "All";
                    if (string.IsNullOrWhiteSpace(name)) throw new Exception("Each signal needs a name");
                    if (known.Contains(name) || created.Contains(name)) { if (!existing.Contains(name)) existing.Add(name); continue; }
                    var inst = type.Create(name);
                    inst.SetAttribute("SignalType", sigType);
                    if (!string.IsNullOrEmpty(access)) inst.SetAttribute("Access", access);
                    created.Add(name);
                }
            }

            string route = null;
            var errors = new List<string>();

            // Route 1: RobotStudio's own connection
            try
            {
                var vc = GetController();
                var conn = vc.ControllerConnection;
                var own = conn?.RobConnection as Controller;
                if (own == null) throw new Exception("RobotStudio's controller connection is not available (RobConnection = "
                    + (conn?.RobConnection?.GetType().Name ?? "null") + ")");
                var task = conn.ExecuteWithMastershipResource(ApiMastershipResource.CFG, () =>
                {
                    Write(own);
                    return Task.CompletedTask;
                });
                PumpUntilDone(task, 25, "Configuration write through RobotStudio's connection");
                route = "RobotStudio connection";
            }
            catch (Exception ex)
            {
                errors.Add("RobotStudio connection: " + (ex is AggregateException ae && ae.InnerException != null ? ae.InnerException.Message : ex.Message));
            }

            // Route 2: separate connection registered as a control station
            if (route == null && created.Count == 0)
            {
                try
                {
                    using (var controller = ConnectToController())
                    {
                        var station = controller.ControlStation;
                        try { station.Register("ClaudeBridge", Guid.NewGuid().ToString(), 0, true); }
                        catch (Exception ex) { errors.Add("Register control station: " + ex.Message); }
                        try { station.RequestWriteAccess(); }
                        catch (Exception ex) { throw new Exception("Write access refused: " + ex.Message); }
                        try { Write(controller); route = "control station"; }
                        finally { try { station.ReleaseWriteAccess(); } catch { } }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("Control station: " + ex.Message);
                }
            }

            if (route == null)
                throw new Exception("No route gave write access to the configuration. " + string.Join(" | ", errors.Select(e => e.Split('\n')[0])));

            return new
            {
                success = true,
                route,
                created,
                alreadyExisting = existing,
                note = created.Count > 0 ? "Restart the controller (warm start) to activate the new signals." : "Nothing to create."
            };
        }
    }
}
