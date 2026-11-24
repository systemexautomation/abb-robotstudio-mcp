using System;
using System.Collections.Generic;
using System.Linq;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using Newtonsoft.Json.Linq;

namespace ClaudeBridge
{
    // Creation and modification of simple solid Parts (box, cylinder, cone, sphere, pyramid).
    public partial class ClaudeBridgeAddIn
    {
        // ── POST /station/part/create-solid ───────────────────────────────────
        // Body: {"name":"Box3_Box_new","shape":"box","size":[238,231,132],
        //        "origin":[x,y,z],"xAxis":[1,0,0],"zAxis":[0,0,1]}      (mm, station frame)
        // shape: box (size=[l,w,h]), cylinder (radius,height), cone (radius,height[,topRadius]),
        //        sphere (radius), pyramid (baseSize,height,sides).
        // The Part frame is the given pose (default: station origin, axes aligned). The solid is built
        // in that frame: box = corner at the origin and the box in +X/+Y/+Z; cylinder/cone/pyramid = base
        // centre at the origin and height along +Z; sphere = centre at the origin. One undo step.
        private object CreateSolidPart(string body)
        {
            var station = GetActiveStation();
            var req = JObject.Parse(body ?? "{}");
            var name = (string)req["name"];
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("Required: name, shape and its dimensions");
            if (station.GraphicComponents.Contains(name))
                throw new Exception("An object named '" + name + "' already exists in the station");

            var frame = FrameFromRequest(req);
            var solid = BuildSolid(req, frame, out var expected);

            Part created;
            var undo = Project.UndoContext;
            undo?.BeginUndoStep("ClaudeBridge: create solid " + name);
            try
            {
                var placed = solid.Transform.GlobalMatrix;
                created = new Part { Name = name };
                station.GraphicComponents.Add(created);
                created.Bodies.Add(solid);
                created.Transform.GlobalMatrix = frame;
                solid.Transform.GlobalMatrix = placed;
            }
            finally
            {
                undo?.EndUndoStep();
            }
            return DescribeSolidPart(created, expected, "created");
        }

        // ── POST /station/part/modify-solid ───────────────────────────────────
        // Body: {"name":"Box3_Box","shape":"box","size":[238,231,132]}  (+ optional "force":true)
        // Replaces the geometry of an existing single-body Part by a new solid, keeping the Part's name,
        // frame and references. The new solid is built in the Part's current frame (same conventions as
        // create-solid). Refuses a Part with several bodies unless force is true. One undo step.
        private object ModifySolidPart(string body)
        {
            var station = GetActiveStation();
            var req = JObject.Parse(body ?? "{}");
            var name = (string)req["name"];
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("Required: name, shape and its dimensions");
            bool force = req["force"] != null && (bool)req["force"];

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

            var oldBodies = part.Bodies.Cast<Body>().ToList();
            if (oldBodies.Count != 1 && !force)
                throw new Exception("Part '" + part.Name + "' has " + oldBodies.Count + " bodies; refusing to replace its geometry (pass force=true to override). Nothing changed.");

            var before = part.GetBoundingBox(true);
            var frame = part.Transform.GlobalMatrix;
            var solid = BuildSolid(req, frame, out var expected);

            var undo = Project.UndoContext;
            undo?.BeginUndoStep("ClaudeBridge: modify solid " + part.Name);
            try
            {
                var placed = solid.Transform.GlobalMatrix;
                foreach (var b in oldBodies) part.Bodies.Remove(b);
                part.Bodies.Add(solid);
                solid.Transform.GlobalMatrix = placed;
            }
            finally
            {
                undo?.EndUndoStep();
            }
            var res = DescribeSolidPart(part, expected, "modified");
            return new
            {
                success = true,
                result = res,
                previousBoundingBox = before.IsValid() ? BBoxMm(before) : null,
                removedBodies = oldBodies.Count
            };
        }

        // ── POST /station/part/transform ──────────────────────────────────────
        // Body: {"name":"Box2_Tote","rotateZ":90,"pivot":[x,y],"translate":[dx,dy,dz]}  (mm, degrees, station frame)
        // Moves the WHOLE Part rigidly (frame and geometry together): first a rotation of rotateZ degrees
        // about the vertical axis through pivot (default: the Part frame origin), then the translation.
        // One undo step.
        private object TransformPart(string body)
        {
            var station = GetActiveStation();
            var req = JObject.Parse(body ?? "{}");
            var name = (string)req["name"];
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("Required: name and rotateZ and/or translate");
            double rot = req["rotateZ"] != null && req["rotateZ"].Type != JTokenType.Null ? (double)req["rotateZ"] : 0;
            var tr = req["translate"] as JArray;
            if (tr != null && tr.Count != 3) throw new Exception("translate must be [dx,dy,dz] in mm");
            if (rot == 0 && tr == null) throw new Exception("Nothing to do: give rotateZ and/or translate");

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

            var g = part.Transform.GlobalMatrix;
            double px = g.Translation.x, py = g.Translation.y;
            var pv = req["pivot"] as JArray;
            if (pv != null)
            {
                if (pv.Count != 2) throw new Exception("pivot must be [x,y] in mm");
                px = (double)pv[0] * 0.001;
                py = (double)pv[1] * 0.001;
            }
            double a = rot * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
            ABB.Robotics.Math.Vector3 R(ABB.Robotics.Math.Vector4 v) =>
                new ABB.Robotics.Math.Vector3(v.x * c - v.y * s, v.x * s + v.y * c, v.z);
            var origin = g.Translation;
            double ox = px + (origin.x - px) * c - (origin.y - py) * s;
            double oy = py + (origin.x - px) * s + (origin.y - py) * c;
            double dx = tr != null ? (double)tr[0] * 0.001 : 0, dy = tr != null ? (double)tr[1] * 0.001 : 0, dz = tr != null ? (double)tr[2] * 0.001 : 0;
            var newOrigin = new ABB.Robotics.Math.Vector3(ox + dx, oy + dy, origin.z + dz);
            var frame = new ABB.Robotics.Math.Matrix4(R(g.x), R(g.y), R(g.z), newOrigin);

            var before = part.GetBoundingBox(true);
            var undo = Project.UndoContext;
            undo?.BeginUndoStep("ClaudeBridge: transform " + part.Name);
            try
            {
                part.Transform.GlobalMatrix = frame;
            }
            finally
            {
                undo?.EndUndoStep();
            }
            var res = DescribeSolidPart(part, null, "transformed");
            return new
            {
                success = true,
                result = res,
                previousBoundingBox = before.IsValid() ? BBoxMm(before) : null,
                note = "The Part moved as a whole (frame and geometry). Read exact sizes with rs_get_object_parts mode=bodies source=faces."
            };
        }

        private static ABB.Robotics.Math.Matrix4 FrameFromRequest(JObject req)
        {
            ABB.Robotics.Math.Vector3 Vec(string key, double[] def, double scale)
            {
                var a = req[key] as JArray;
                if (a == null) return new ABB.Robotics.Math.Vector3(def[0] * scale, def[1] * scale, def[2] * scale);
                if (a.Count != 3) throw new Exception(key + " must be [x,y,z]");
                return new ABB.Robotics.Math.Vector3((double)a[0] * scale, (double)a[1] * scale, (double)a[2] * scale);
            }
            var origin = Vec("origin", new[] { 0.0, 0, 0 }, 0.001);
            var xAxis = Vec("xAxis", new[] { 1.0, 0, 0 }, 1).Normalized();
            var zAxis = Vec("zAxis", new[] { 0.0, 0, 1 }, 1).Normalized();
            if (Math.Abs(xAxis.Dot(zAxis)) > 1e-6) throw new Exception("xAxis and zAxis must be perpendicular");
            return new ABB.Robotics.Math.Matrix4(xAxis, zAxis.Cross(xAxis), zAxis, origin);
        }

        // Builds the solid in the given frame (metres inside the SDK). expected = sorted extents in mm.
        private static Body BuildSolid(JObject req, ABB.Robotics.Math.Matrix4 frame, out double[] expected)
        {
            var shape = ((string)req["shape"] ?? "").ToLowerInvariant();
            double Num(string key)
            {
                var t = req[key];
                if (t == null || t.Type == JTokenType.Null) throw new Exception("Required for " + shape + ": " + key);
                double v = (double)t;
                if (!(v > 0)) throw new Exception(key + " must be > 0");
                return v;
            }
            Body b;
            switch (shape)
            {
                case "box":
                {
                    var s = req["size"] as JArray;
                    if (s == null || s.Count != 3) throw new Exception("box needs size [length,width,height] in mm");
                    double l = (double)s[0], w = (double)s[1], h = (double)s[2];
                    if (!(l > 0 && w > 0 && h > 0)) throw new Exception("size values must be > 0");
                    b = Body.CreateSolidBox(frame, new ABB.Robotics.Math.Vector3(l * 0.001, w * 0.001, h * 0.001));
                    expected = new[] { l, w, h };
                    break;
                }
                case "cylinder":
                {
                    double r = Num("radius"), h = Num("height");
                    b = Body.CreateSolidCylinder(frame, r * 0.001, h * 0.001);
                    expected = new[] { 2 * r, 2 * r, h };
                    break;
                }
                case "cone":
                {
                    double r = Num("radius"), h = Num("height");
                    double top = req["topRadius"] != null && req["topRadius"].Type != JTokenType.Null ? (double)req["topRadius"] : -1;
                    if (top >= 0)
                    {
                        b = Body.CreateSolidCone(frame, r * 0.001, top * 0.001, h * 0.001);
                        expected = new[] { 2 * Math.Max(r, top), 2 * Math.Max(r, top), h };
                    }
                    else
                    {
                        b = Body.CreateSolidCone(frame, r * 0.001, h * 0.001);
                        expected = new[] { 2 * r, 2 * r, h };
                    }
                    break;
                }
                case "sphere":
                {
                    double r = Num("radius");
                    b = Body.CreateSolidSphere(frame.Translation, r * 0.001);
                    expected = new[] { 2 * r, 2 * r, 2 * r };
                    break;
                }
                case "pyramid":
                {
                    double baseSize = Num("baseSize"), h = Num("height");
                    int sides = req["sides"] != null ? (int)req["sides"] : 4;
                    if (sides < 3) throw new Exception("sides must be >= 3");
                    b = Body.CreateSolidPyramid(frame, baseSize * 0.001, h * 0.001, sides);
                    expected = null; // baseSize = base centre to corner distance (checked 2026-11-19: 80 gives a 113.1 square)
                    break;
                }
                default:
                    throw new Exception("shape must be one of: box, cylinder, cone, sphere, pyramid");
            }
            if (expected != null) Array.Sort(expected);
            return b;
        }

        private object DescribeSolidPart(Part part, double[] expected, string action)
        {
            var bb = part.GetBoundingBox(true);
            double[] sizes = bb.IsValid()
                ? new[] { (bb.max.x - bb.min.x) * 1000, (bb.max.y - bb.min.y) * 1000, (bb.max.z - bb.min.z) * 1000 }
                : null;
            bool? matches = null;
            if (sizes != null && expected != null)
            {
                var s = (double[])sizes.Clone();
                Array.Sort(s);
                matches = s.Zip(expected, (a, e) => Math.Abs(a - e) < 0.5).All(x => x);
            }
            var g = part.Transform.GlobalMatrix;
            return new
            {
                success = true,
                action,
                name = part.Name,
                bodyCount = part.Bodies.Count,
                frame = new
                {
                    origin = Vec3Mm(g.Translation),
                    xAxis = new { x = Math.Round(g.x.x, 4), y = Math.Round(g.x.y, 4), z = Math.Round(g.x.z, 4) },
                    zAxis = new { x = Math.Round(g.z.x, 4), y = Math.Round(g.z.y, 4), z = Math.Round(g.z.z, 4) }
                },
                boundingBox = bb.IsValid() ? BBoxMm(bb) : null,
                expectedExtentsSorted_mm = expected,
                extentsMatchExpected = matches,
                note = "Undo with Ctrl+Z in RobotStudio. The station is not saved."
            };
        }
    }
}
