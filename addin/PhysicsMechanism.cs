using System;
using System.Collections.Generic;
using System.Linq;
using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using Newtonsoft.Json.Linq;

namespace ClaudeBridge
{
    // Physics properties of station objects, station physics settings, and creation of simple
    // mechanisms (links + prismatic/rotational joints) from existing Parts.
    public partial class ClaudeBridgeAddIn
    {
        // Object by exact name or by path "A/B/C" (each segment an exact child name, any object type).
        // A plain name must be unique in the station.
        private static ProjectObject ResolveObject(Station station, string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath)) throw new Exception("Object name or path required");
            if (nameOrPath.Contains("/"))
            {
                ProjectObject cur = station;
                foreach (var seg in nameOrPath.Split('/'))
                {
                    ProjectObject next = null;
                    foreach (var c in ChildrenOf(cur))
                        if (string.Equals(c.Name, seg, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
                    cur = next ?? throw new Exception("'" + seg + "' not found under '" + (ReferenceEquals(cur, station) ? "(station)" : cur.Name) + "' (path '" + nameOrPath + "')");
                }
                return cur;
            }
            var found = new List<ProjectObject>();
            void Find(ProjectObject parent, int depth)
            {
                foreach (var c in ChildrenOf(parent))
                {
                    if (string.Equals(c.Name, nameOrPath, StringComparison.OrdinalIgnoreCase)) found.Add(c);
                    if (c is Part) continue;
                    if (depth < MaxMaxDepth) Find(c, depth + 1);
                }
            }
            Find(station, 0);
            if (found.Count == 0) throw new Exception("No object named '" + nameOrPath + "'");
            if (found.Count > 1) throw new Exception("Ambiguous name '" + nameOrPath + "' (" + found.Count + " matches): use its path, e.g. 'Parent/" + nameOrPath + "'");
            return found[0];
        }

        private static PhysicsMotionControl ParseMotionControl(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "none": case "disabled": return PhysicsMotionControl.None;
                case "static": case "fixed": return PhysicsMotionControl.Static;
                case "kinematic": return PhysicsMotionControl.Kinematic;
                case "dynamic": return PhysicsMotionControl.Dynamic;
                default: throw new Exception("motionControl must be None, Static, Kinematic or Dynamic");
            }
        }

        private static object DescribePhysics(ProjectObject po, string path)
        {
            var d = new Dictionary<string, object> { ["name"] = po.Name, ["path"] = path, ["type"] = po.GetType().Name };
            if (po is GraphicComponent gc) d["motionControl"] = gc.PhysicsMotionControl.ToString();
            if (po is Part p)
            {
                var m = p.GetPhysicsMaterial();
                d["material"] = new
                {
                    m.SurfaceRoughness, m.SurfaceViscosity, m.SurfaceAdhesion, m.SurfaceAdhesionOverlap,
                    m.Density, m.Damping, m.Restitution, m.PoissonsRatio, m.YoungsModulus
                };
                d["useOriginalGeometry"] = p.GetPhysicsCollisionProperties().UseOriginalGeometry;
            }
            return d;
        }

        // GET /physics/get?name=<name or path>
        private object GetPhysics(System.Collections.Specialized.NameValueCollection query)
        {
            var station = GetActiveStation();
            var name = query["name"];
            var po = ResolveObject(station, name);
            var s = station.PhysicsSimulationProperties;
            return new
            {
                station = new { gravity_m_s2 = s.Gravity, floor = s.Floor, floorHeight_mm = s.FloorHeight * 1000.0 },
                obj = DescribePhysics(po, name)
            };
        }

        // POST /physics/set
        // Body: {"objects":["Box3_Box","SC_PusherTool/Pusher"],"motionControl":"Dynamic",
        //        "material":{"SurfaceRoughness":0.5,"Restitution":0.1,...},"useOriginalGeometry":false}
        // motionControl applies to any GraphicComponent; material and useOriginalGeometry to Parts only.
        // Material fields not given keep their current value. One undo step.
        private object SetPhysics(string body)
        {
            var station = GetActiveStation();
            var req = JObject.Parse(body ?? "{}");
            var names = (req["objects"] as JArray)?.Select(t => (string)t).ToList();
            if (names == null || names.Count == 0) throw new Exception("Required: objects (names or paths)");
            var targets = names.Select(n => Tuple.Create(ResolveObject(station, n), n)).ToList();
            PhysicsMotionControl? mc = req["motionControl"] != null ? ParseMotionControl((string)req["motionControl"]) : (PhysicsMotionControl?)null;
            var mat = req["material"] as JObject;
            bool? orig = req["useOriginalGeometry"] != null ? (bool)req["useOriginalGeometry"] : (bool?)null;
            if (mc == null && mat == null && orig == null) throw new Exception("Nothing to set: give motionControl, material or useOriginalGeometry");

            foreach (var t in targets)
            {
                if (mc != null && !(t.Item1 is GraphicComponent)) throw new Exception("'" + t.Item2 + "' is not a GraphicComponent");
                if ((mat != null || orig != null) && !(t.Item1 is Part)) throw new Exception("'" + t.Item2 + "': material and collision geometry need a Part");
            }

            var undo = Project.UndoContext;
            undo?.BeginUndoStep("ClaudeBridge: physics");
            try
            {
                foreach (var t in targets)
                {
                    if (mc != null) ((GraphicComponent)t.Item1).PhysicsMotionControl = mc.Value;
                    if (t.Item1 is Part p)
                    {
                        if (mat != null)
                        {
                            object m = p.GetPhysicsMaterial();   // boxed struct, fields set by reflection
                            foreach (var prop in mat.Properties())
                            {
                                var f = typeof(PhysicsMaterial).GetField(prop.Name);
                                if (f == null) throw new Exception("Unknown material field '" + prop.Name + "'");
                                f.SetValue(m, (double)prop.Value);
                            }
                            p.SetPhysicsMaterial((PhysicsMaterial)m);
                        }
                        if (orig != null)
                        {
                            var cp = p.GetPhysicsCollisionProperties();
                            cp.UseOriginalGeometry = orig.Value;
                            p.SetPhysicsCollisionProperties(cp);
                        }
                    }
                }
            }
            finally
            {
                undo?.EndUndoStep();
            }
            return new
            {
                success = true,
                objects = targets.Select(t => DescribePhysics(t.Item1, t.Item2)).ToList(),
                note = "One undo step (Ctrl+Z). Station not saved."
            };
        }

        // GET|POST /physics/settings   Body: {"gravity":9.81,"floor":true,"floorHeight":0}  (m/s2, mm)
        private object PhysicsSettings(string method, string body)
        {
            var station = GetActiveStation();
            if (method == "POST")
            {
                var req = JObject.Parse(body ?? "{}");
                var s = station.PhysicsSimulationProperties;
                if (req["gravity"] != null) s.Gravity = (double)req["gravity"];
                if (req["floor"] != null) s.Floor = (bool)req["floor"];
                if (req["floorHeight"] != null) s.FloorHeight = (double)req["floorHeight"] / 1000.0;
                var undo = Project.UndoContext;
                undo?.BeginUndoStep("ClaudeBridge: physics settings");
                try { station.PhysicsSimulationProperties = s; }
                finally { undo?.EndUndoStep(); }
            }
            var r = station.PhysicsSimulationProperties;
            return new { gravity_m_s2 = r.Gravity, floor = r.Floor, floorHeight_mm = r.FloorHeight * 1000.0 };
        }

        private static Vector3 Mm(JToken t, string what)
        {
            var a = t as JArray;
            if (a == null || a.Count != 3) throw new Exception(what + " must be [x,y,z] in mm");
            return new Vector3((double)a[0] / 1000.0, (double)a[1] / 1000.0, (double)a[2] / 1000.0);
        }

        // POST /mechanism/create
        // Body: {"name":"Pusher_Mech","type":"Other","parent":"SC_PusherTool",
        //        "links":[{"name":"L_Base","part":"SC_PusherTool/Base"},{"name":"L_Pusher","part":"SC_PusherTool/Pusher"}],
        //        "baseLink":"L_Base",
        //        "joints":[{"name":"J_Push","parent":"L_Base","child":"L_Pusher","type":"Prismatic",
        //                   "axisStart":[x,y,z],"axisEnd":[x,y,z],"min":0,"max":198.2}],
        //        "poses":{"Retracted":[0],"Extended":[198.2]},
        //        "attachToFlangeOf":"IRB460_110_240__01","mount":false}
        // Axis points in mm, station frame (pose of the parts when the call is made). Prismatic limits in
        // mm, rotational in degrees; poses use the same units. type: Other (default), Tool, ExternalAxis.
        // The new mechanism is added to "parent" (SmartComponent path; default the station). Link parts
        // still present after compilation are reported, not deleted. One undo step.
        private object CreateMechanism(string body)
        {
            var station = GetActiveStation();
            var req = JObject.Parse(body ?? "{}");
            var name = (string)req["name"];
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("Required: name, links, baseLink, joints");
            var typeStr = ((string)req["type"] ?? "Other").Trim();
            MechanismType mtype;
            switch (typeStr.ToLowerInvariant())
            {
                case "other": case "device": mtype = MechanismType.Other; break;
                case "tool": mtype = MechanismType.Tool; break;
                case "externalaxis": mtype = MechanismType.ExternalAxis; break;
                default: throw new Exception("type must be Other, Tool or ExternalAxis");
            }
            var links = (req["links"] as JArray) ?? throw new Exception("Required: links");
            var joints = (req["joints"] as JArray) ?? throw new Exception("Required: joints");
            var baseLink = (string)req["baseLink"] ?? throw new Exception("Required: baseLink");

            var parentPath = (string)req["parent"];
            var container = string.IsNullOrWhiteSpace(parentPath) ? (IHasGraphicComponents)station
                : ResolveObject(station, parentPath) as IHasGraphicComponents ?? throw new Exception("parent '" + parentPath + "' cannot hold graphic components");

            var linkParts = new List<Tuple<string, GraphicComponent>>();
            foreach (JObject l in links)
            {
                var ln = (string)l["name"];
                var pp = (string)l["part"];
                var gc = ResolveObject(station, pp) as GraphicComponent ?? throw new Exception("link part '" + pp + "' is not a GraphicComponent");
                linkParts.Add(Tuple.Create(ln, gc));
            }

            var jointTypes = new List<JointType>();
            Mechanism mech;
            var undo = Project.UndoContext;
            undo?.BeginUndoStep("ClaudeBridge: create mechanism " + name);
            try
            {
                var mb = new MechanismBuilder(mtype) { Name = name, ModelName = name };
                foreach (var lp in linkParts) mb.AddLink(lp.Item1, lp.Item2);
                mb.BaseLink = baseLink;
                foreach (JObject j in joints)
                {
                    var jn = (string)j["name"];
                    var jt = ((string)j["type"] ?? "Prismatic").ToLowerInvariant() == "rotational" ? JointType.Rotational : JointType.Prismatic;
                    jointTypes.Add(jt);
                    mb.AddJoint(jn, (string)j["parent"], (string)j["child"], Mm(j["axisStart"], "axisStart"), Mm(j["axisEnd"], "axisEnd"), jt, true);
                    double k = jt == JointType.Prismatic ? 0.001 : Math.PI / 180.0;
                    if (j["min"] != null && j["max"] != null) mb.SetJointLimit(jn, (double)j["min"] * k, (double)j["max"] * k);
                }
                mech = mb.CompileMechanism();
                mech.Name = name;
                container.GraphicComponents.Add(mech);

                if (req["poses"] is JObject poses)
                {
                    var dict = new Dictionary<string, double[]>();
                    foreach (var p in poses.Properties())
                    {
                        var vals = ((JArray)p.Value).Select(v => (double)v).ToArray();
                        if (vals.Length != jointTypes.Count) throw new Exception("pose '" + p.Name + "' needs " + jointTypes.Count + " values");
                        dict[p.Name] = vals.Select((v, i) => v * (jointTypes[i] == JointType.Prismatic ? 0.001 : Math.PI / 180.0)).ToArray();
                    }
                    mech.SetUserDefinedJointPositions(dict);
                }

                var robotName = (string)req["attachToFlangeOf"];
                if (!string.IsNullOrWhiteSpace(robotName))
                {
                    var robot = ResolveObject(station, robotName) as Mechanism ?? throw new Exception("'" + robotName + "' is not a mechanism");
                    bool mount = req["mount"] != null && (bool)req["mount"];
                    if (!robot.AttachToFlange(mech, mount)) throw new Exception("AttachToFlange refused");
                }
            }
            finally
            {
                undo?.EndUndoStep();
            }

            return new
            {
                success = true,
                name = mech.Name,
                type = mech.GetType().Name,
                joints = mech.GetJointTypes().Select(t => t.ToString()).ToArray(),
                jointValues = mech.GetJointValues(),
                poses = mech.GetUserDefinedJointPositions()?.Keys.ToArray(),
                linkPartsStillInStation = linkParts.Where(lp => lp.Item2.Parent != null).Select(lp => lp.Item2.Name).ToArray(),
                note = "One undo step (Ctrl+Z). Station not saved."
            };
        }

        // POST /mechanism/joints   Body: {"name":"SC_PusherTool/Pusher_Mech","values":[198.2]}  (mm / deg)
        // Optional, applied first as one undo step:
        //   "limits":[[min,max], null, ...]   one entry per joint, null = unchanged
        //   "poses":{"Extended":[205], "Old":null}   added or replaced, null = removed
        // Without "values": returns the current joint values. Moving the joints is not an undo step (like jogging).
        private object MechanismJoints(string body)
        {
            var station = GetActiveStation();
            var req = JObject.Parse(body ?? "{}");
            var name = (string)req["name"];
            var mech = ResolveObject(station, name) as Mechanism ?? throw new Exception("'" + name + "' is not a mechanism");
            var types = mech.GetJointTypes();
            Func<int, double> k = i => types[i] == JointType.Prismatic ? 1000.0 : 180.0 / Math.PI;
            var limits = req["limits"] as JArray;
            var newPoses = req["poses"] as JObject;
            if (limits != null || newPoses != null)
            {
                if (limits != null && limits.Count > types.Length) throw new Exception("limits: " + limits.Count + " entries for " + types.Length + " joints");
                var undo = Project.UndoContext;
                undo?.BeginUndoStep("ClaudeBridge: mechanism limits/poses " + mech.Name);
                try
                {
                    if (limits != null)
                        for (int i = 0; i < limits.Count; i++)
                        {
                            if (limits[i] == null || limits[i].Type == JTokenType.Null) continue;
                            var mm = (JArray)limits[i];
                            double lo = (double)mm[0], hi = (double)mm[1];
                            if (lo > hi) throw new Exception("limits[" + i + "]: min > max");
                            if (!mech.SetJointLimits(i, lo / k(i), hi / k(i))) throw new Exception("SetJointLimits refused for joint " + (i + 1));
                        }
                    if (newPoses != null)
                    {
                        var dict = mech.GetUserDefinedJointPositions() ?? new Dictionary<string, double[]>();
                        dict = new Dictionary<string, double[]>(dict);
                        foreach (var p in newPoses.Properties())
                        {
                            if (p.Value.Type == JTokenType.Null) { dict.Remove(p.Name); continue; }
                            var pv = ((JArray)p.Value).Select(t => (double)t).ToArray();
                            if (pv.Length != types.Length) throw new Exception("pose '" + p.Name + "' needs " + types.Length + " values");
                            dict[p.Name] = pv.Select((x, i) => x / k(i)).ToArray();
                        }
                        mech.SetUserDefinedJointPositions(dict);
                    }
                }
                finally
                {
                    undo?.EndUndoStep();
                }
            }
            if (req["values"] is JArray vals)
            {
                var v = vals.Select((t, i) => (double)t / k(i)).ToArray();
                if (!mech.SetJointValues(v)) throw new Exception("SetJointValues refused (outside limits?)");
            }
            var cur = mech.GetJointValues();
            mech.GetJointLimits(out var mins, out var maxs);
            return new
            {
                name = mech.Name,
                joints = types.Select((t, i) => new { type = t.ToString(), value = cur[i] * k(i), min = mins[i] * k(i), max = maxs[i] * k(i) }).ToArray(),
                poses = mech.GetUserDefinedJointPositions()?.ToDictionary(p => p.Key, p => p.Value.Select((x, i) => x * k(i)).ToArray())
            };
        }
    }
}
