#!/usr/bin/env node

/**
 * ABB RobotStudio MCP Server — Combined SDK + RWS
 * - SDK tools (rs_*): Control RobotStudio app via ClaudeBridge Add-In (TcpListener)
 * - RWS tools (rws_*): Control the robot controller via Robot Web Services
 *
 * v3.0.0 — Safety-first: RAPID validation, simulation guard, runtime URL switching
 */

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

import { BRIDGE_URL, getRwsUrl, setRwsUrl, LARGE_MODULE_THRESHOLD } from "./config.js";
import { bridge } from "./bridge-client.js";
import {
  rws, rwsExtract, rwsExtractAll, rwsPostForm,
  resetRwsSession, withWriteAccess, withRapidWriteAccess, rwsPostAction, RwsError,
} from "./rws-client.js";
import { validateRapidCode } from "./rapid-validator.js";
import { checkSimulationSafety, checkExecutionSafety } from "./safety-guard.js";

// ── Helpers ──────────────────────────────────────────────────

function ok(data: any) {
  return { content: [{ type: "text" as const, text: JSON.stringify(data, null, 2) }] };
}

function err(e: any, context?: string) {
  const message = e?.message ?? String(e ?? "Unknown error");
  const prefix = context ? `[${context}] ` : "";
  return {
    content: [{ type: "text" as const, text: JSON.stringify({ error: prefix + message }, null, 2) }],
    isError: true,
  };
}

// The add-in decodes queries with Uri.UnescapeDataString, which does not turn "+" into a space.
// URLSearchParams encodes a literal "+" as %2B, so every remaining "+" is a space.
function toBridgeQuery(q: URLSearchParams): string {
  return q.toString().replace(/\+/g, "%20");
}

// ── RAPID Validation Helper ──────────────────────────────────

function validateOrReject(code: string, skipValidation?: boolean): { valid: true } | { valid: false; error: any } {
  if (skipValidation) return { valid: true };
  const v = validateRapidCode(code);
  if (!v.valid) {
    return {
      valid: false,
      error: {
        error: `RAPID syntax validation failed (${v.errors.length} error(s))`,
        validationErrors: v.errors,
        warnings: v.warnings,
        lineCount: v.lineCount,
        hint: "Set skipValidation=true to bypass this check",
      },
    };
  }
  return { valid: true };
}

// ── Large Module Upload via RWS File Service ─────────────────

async function uploadLargeModule(task: string, module: string, code: string): Promise<any> {
  // Validate module name to prevent path traversal
  if (module.includes("..") || !/^[a-zA-Z0-9_\-]+$/.test(module)) {
    throw new Error(`Invalid module name "${module}" — must contain only [a-zA-Z0-9_-] and no ".."`);
  }
  // Temporary file directly in $HOME (the file service does not create missing directories)
  const remoteFile = `$HOME/_claude_upload_${module}.modx`;
  const fileUrl = `/fileservice/${encodeURIComponent(remoteFile)}`;

  // Upload the module text to the controller filesystem
  await rws("PUT", fileUrl, code, "text/plain");
  try {
    // Load it in the task, replacing the module of the same name
    // (mastership on RobotWare 6 / 7, control station write access on RobotWare 8)
    const r = await withRapidWriteAccess(() => loadModule(task, remoteFile, true));
    return { success: true, message: `Module ${module} loaded via RWS file upload (${r.status})` };
  } finally {
    // Clean up the temp file
    await rws("DELETE", fileUrl).catch(() => {});
  }
}

// RAPID task actions, RWS 2 route first (RobotWare 8), then RWS 1 (RobotWare 6 / 7).
// The caller holds the write access.
function loadModule(task: string, modulepath: string, replace: boolean) {
  return rwsPostAction(`/rw/rapid/tasks/${task}/loadmod`, `/rw/rapid/tasks/${task}?action=loadmod`,
    { modulepath, replace: replace ? "true" : "false" });
}

function unloadModule(task: string, module: string) {
  return rwsPostAction(`/rw/rapid/tasks/${task}/unloadmod`, `/rw/rapid/tasks/${task}?action=unloadmod`,
    { module });
}

function setSymbolValue(task: string, module: string, variable: string, value: string) {
  const sym = `/rw/rapid/symbol/RAPID/${task}/${module}/${variable}/data`;
  return rwsPostAction(sym, `${sym}?action=set`, { value });
}

// ═══════════════════════════════════════════════════════════════
// ██  MCP SERVER                                                ██
// ═══════════════════════════════════════════════════════════════

const server = new McpServer({
  name: "abb-robotstudio",
  version: "3.0.0",
});

// ═══════════════════════════════════════════════════════════════
// ██  SDK TOOLS (rs_*) — RobotStudio via Add-In                ██
// ═══════════════════════════════════════════════════════════════

server.tool("rs_ping", "Check if RobotStudio ClaudeBridge Add-In is running and connected", {},
  async () => { try {
    return ok(await bridge("/ping", "GET", undefined, 5000));
  } catch (e: any) {
    return err(new Error(`Cannot connect to ClaudeBridge at ${BRIDGE_URL}. Is RobotStudio running?`));
  }}
);

// ── Station ──────────────────────────────────────────────────

server.tool("rs_get_station", "Get active RobotStudio station info (name, controllers)", {},
  async () => { try { return ok(await bridge("/station")); } catch (e: any) { return err(e); } }
);

server.tool("rs_get_station_logic", "Read-only view of the Station Logic: station-level I/O signals, I/O connections and property bindings; every SmartComponent with its properties, signals, internal connections/bindings and child components; the RAPID work objects (wobjdata) of every task, with user and object frames as placed in the station; and the station frames (Paths & Targets > Frames). Poses: global position (mm) and Euler ZYX (deg).",
  {
    component: z.string().optional().describe("Only SmartComponents whose name contains this (case-insensitive). Station Logic is always returned"),
    properties: z.boolean().optional().describe("Include SmartComponent properties (default: true)"),
    frames: z.boolean().optional().describe("Include work objects and station frames (default: true)"),
    frameFilter: z.string().optional().describe("Only work objects and frames whose name contains this, e.g. 'Frame_Place_'"),
  },
  async ({ component, properties, frames, frameFilter }) => { try {
    const q = new URLSearchParams();
    if (component) q.set("component", component);
    if (properties === false) q.set("properties", "false");
    if (frames === false) q.set("frames", "false");
    if (frameFilter) q.set("frameFilter", frameFilter);
    const qs = toBridgeQuery(q);
    return ok(await bridge(`/station/logic${qs ? "?" + qs : ""}`, "GET", undefined, 30000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_get_station_objects", "List objects in the station (robots, tools, parts, smart components) with their tree path. Use nameFilter to search by name and limit to cap the result size. Set bbox=true for global position and bounding box (mm). Response reports matchedCount and truncated.",
  {
    bbox: z.boolean().optional().describe("Include global position and bounding box in mm (default: false)"),
    nameFilter: z.string().optional().describe("Case-insensitive substring the object name must contain"),
    limit: z.number().int().min(1).max(2000).optional().describe("Max objects returned (default: 200)"),
    maxDepth: z.number().int().min(0).max(20).optional().describe("Max tree depth to search (default: 6)"),
  },
  async ({ bbox, nameFilter, limit, maxDepth }) => { try {
    const q = new URLSearchParams();
    if (bbox) q.set("bbox", "true");
    if (nameFilter) q.set("nameFilter", nameFilter);
    if (limit !== undefined) q.set("limit", String(limit));
    if (maxDepth !== undefined) q.set("maxDepth", String(maxDepth));
    const qs = toBridgeQuery(q);
    return ok(await bridge(`/station/objects${qs ? "?" + qs : ""}`, "GET", undefined, 30000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_get_object_parts", "Inspect the internal elements of one station object: child components, parts and their bodies, each with name, global position and bounding box (mm). Bodies also report face count, volume and surface area. Use it to tell sub-elements apart (e.g. several pallets inside one layout part). Read-only.",
  {
    name: z.string().describe("Object name: exact match (case-insensitive) is preferred, otherwise a unique substring. A path 'Parent/Child' selects one object when the name is not unique"),
    maxDepth: z.number().int().min(0).max(20).optional().describe("Max depth of children to describe (default: 3)"),
    limit: z.number().int().min(1).max(2000).optional().describe("Max elements described / items listed (default: 200)"),
    mode: z.enum(["tree", "bodies", "clusters"]).optional().describe(
      "tree (default): children and bodies. bodies: one global box per body of a Part. " +
      "clusters: bodies whose boxes touch (within clusterTolerance) are merged into one item — use it for flattened CAD imports"),
    offset: z.number().int().min(0).optional().describe("bodies/clusters: skip the first N items of the list (paging)"),
    region: z.array(z.number()).length(6).optional().describe("bodies/clusters: [minX,minY,minZ,maxX,maxY,maxZ] in mm; only bodies intersecting it are kept"),
    clusterTolerance: z.number().min(0).max(1000).optional().describe("clusters: max gap in mm between merged boxes (default: 1)"),
    footprints: z.array(z.string()).optional().describe("bodies/clusters: XY sizes to look for, e.g. ['812.8x863.6'] (mm, orientation-free). Returns best candidates per footprint instead of the full list"),
    sizeTolerance: z.number().min(0).optional().describe("Max per-side deviation in mm for a footprint match (default: 25)"),
    candidates: z.number().int().min(1).max(50).optional().describe("Candidates returned per footprint (default: 5)"),
    source: z.enum(["auto", "mesh", "faces"]).optional().describe(
      "bodies/clusters geometry: mesh = display tessellation (fast), faces = exact B-rep (slow, computed in slices: " +
      "if status is 'incomplete', call again to continue). auto (default) = mesh if available. Boxes are cached per Part"),
    timeBudget: z.number().int().min(1).max(240).optional().describe("faces source: seconds of work per call (default: 20)"),
  },
  async ({ name, maxDepth, limit, mode, offset, region, clusterTolerance, footprints, sizeTolerance, candidates, source, timeBudget }) => { try {
    const q = new URLSearchParams({ name });
    if (maxDepth !== undefined) q.set("maxDepth", String(maxDepth));
    if (limit !== undefined) q.set("limit", String(limit));
    if (mode) q.set("mode", mode);
    if (offset !== undefined) q.set("offset", String(offset));
    if (region) q.set("region", region.join(","));
    if (clusterTolerance !== undefined) q.set("clusterTolerance", String(clusterTolerance));
    if (footprints?.length) q.set("footprints", footprints.join(";"));
    if (sizeTolerance !== undefined) q.set("sizeTolerance", String(sizeTolerance));
    if (candidates !== undefined) q.set("candidates", String(candidates));
    if (source) q.set("source", source);
    if (timeBudget !== undefined) q.set("timeBudget", String(timeBudget));
    // Analysing every body of a large imported Part can take minutes (add-in allows 300 s)
    return ok(await bridge(`/station/object/parts?${toBridgeQuery(q)}`, "GET", undefined, 310000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_create_part_from_body", "MODIFIES THE STATION. Copy one body of a Part (index from rs_get_object_parts mode=bodies) into a new Part whose frame is on a lower corner of the body: X along the long side, Z up, body in +X/+Y. Geometry stays in place; the source body is not removed. One undo step (Ctrl+Z in RobotStudio). Does not save the station.",
  {
    sourcePart: z.string().describe("Exact name of the source Part, or its path 'Parent/Part'"),
    bodyIndex: z.number().int().min(0).describe("Body index as reported by rs_get_object_parts (firstBodyIndices, source=mesh)"),
    name: z.string().min(1).describe("Name of the new Part (must not already exist)"),
  },
  async ({ sourcePart, bodyIndex, name }) => { try {
    return ok(await bridge("/station/part/from-body", "POST", { sourcePart, bodyIndex, name }, 60000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_remove_part_bodies", "MODIFIES THE STATION. Remove bodies from a Part (indices from rs_get_object_parts mode=bodies, source=mesh). All-or-nothing: with expectFootprints, every body must match one footprint or nothing is removed. One undo step (Ctrl+Z). Body indices change afterwards. Does not save the station.",
  {
    sourcePart: z.string().describe("Exact name of the Part, or its path 'Parent/Part'"),
    bodyIndices: z.array(z.number().int().min(0)).min(1).describe("Body indices to remove"),
    expectFootprints: z.array(z.string()).optional().describe("Safety check: XY sizes each body must match, e.g. ['965.2x965.2'] (mm, orientation-free)"),
    sizeTolerance: z.number().min(0).optional().describe("Tolerance in mm for expectFootprints (default: 1)"),
  },
  async ({ sourcePart, bodyIndices, expectFootprints, sizeTolerance }) => { try {
    return ok(await bridge("/station/part/remove-bodies", "POST", { sourcePart, bodyIndices, expectFootprints, sizeTolerance }, 60000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_set_part_frame", "MODIFIES THE STATION. Move a Part's local frame to a new pose without moving its geometry (bodies keep their global pose). Pose in station coordinates: origin in mm, xAxis and zAxis as perpendicular direction vectors; Y = Z x X. One undo step (Ctrl+Z). Does not save the station.",
  {
    name: z.string().describe("Exact name of the Part, or its path 'Parent/Part'"),
    origin: z.array(z.number()).length(3).describe("New frame origin [x,y,z] in mm, station coordinates"),
    xAxis: z.array(z.number()).length(3).describe("New X axis direction [x,y,z], station coordinates"),
    zAxis: z.array(z.number()).length(3).describe("New Z axis direction [x,y,z], perpendicular to xAxis"),
  },
  async ({ name, origin, xAxis, zAxis }) => { try {
    return ok(await bridge("/station/part/set-frame", "POST", { name, origin, xAxis, zAxis }, 60000));
  } catch (e: any) { return err(e); } }
);

const solidShape = z.enum(["box", "cylinder", "cone", "sphere", "pyramid"]);
const solidDims = {
  shape: solidShape.describe("Solid type: box, cylinder, cone, sphere or pyramid"),
  size: z.array(z.number().positive()).length(3).optional().describe("box: [length (X), width (Y), height (Z)] in mm"),
  radius: z.number().positive().optional().describe("cylinder, cone (base radius) or sphere: radius in mm"),
  height: z.number().positive().optional().describe("cylinder, cone or pyramid: height in mm"),
  topRadius: z.number().min(0).optional().describe("cone: top radius in mm (frustum); omit for a pointed cone"),
  baseSize: z.number().positive().optional().describe("pyramid: distance from the base centre to a base corner in mm (a 4-sided pyramid of baseSize 80 has a square base of side 113.1)"),
  sides: z.number().int().min(3).optional().describe("pyramid: number of base sides (default 4)"),
};

server.tool("rs_create_solid_part", "MODIFIES THE STATION. Create a new Part holding one simple solid (box, cylinder, cone, sphere or pyramid), dimensions in mm. The Part frame is the given pose (default: station origin, axes aligned). The solid is built in that frame: box = corner at the origin, extending along +X/+Y/+Z; cylinder/cone/pyramid = base centre at the origin, height along +Z; sphere = centre at the origin. The result reports the real bounding box and whether the extents match. One undo step (Ctrl+Z). Does not save the station.",
  {
    name: z.string().min(1).describe("Name of the new Part (must not already exist)"),
    ...solidDims,
    origin: z.array(z.number()).length(3).optional().describe("Frame origin [x,y,z] in mm, station coordinates (default [0,0,0])"),
    xAxis: z.array(z.number()).length(3).optional().describe("Frame X axis direction (default [1,0,0])"),
    zAxis: z.array(z.number()).length(3).optional().describe("Frame Z axis direction, perpendicular to xAxis (default [0,0,1])"),
  },
  async (args) => { try {
    return ok(await bridge("/station/part/create-solid", "POST", args, 60000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_modify_solid_part", "MODIFIES THE STATION. Replace the geometry of an existing Part by a new simple solid (box, cylinder, cone, sphere or pyramid, dimensions in mm), keeping its name, frame and references (SmartComponent properties, parents). The solid is built in the Part's current frame with the same conventions as rs_create_solid_part. Refuses a Part with several bodies (imported CAD) unless force=true. One undo step (Ctrl+Z). Does not save the station.",
  {
    name: z.string().min(1).describe("Exact name of the existing Part"),
    ...solidDims,
    force: z.boolean().optional().describe("Replace even when the Part has several bodies (default false)"),
  },
  async (args) => { try {
    return ok(await bridge("/station/part/modify-solid", "POST", args, 60000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_transform_part", "MODIFIES THE STATION. Move a whole Part rigidly (frame AND geometry together, unlike rs_set_part_frame): first rotate it by rotateZ degrees about the vertical axis through pivot (default: the Part frame origin), then translate it. Use it to turn a Part by 90 degrees. Read the exact result with rs_get_object_parts mode=bodies source=faces. One undo step (Ctrl+Z). Does not save the station.",
  {
    name: z.string().min(1).describe("Exact name of the Part"),
    rotateZ: z.number().optional().describe("Rotation in degrees about the vertical axis (counter-clockwise seen from above)"),
    pivot: z.array(z.number()).length(2).optional().describe("Rotation centre [x,y] in mm, station coordinates (default: the Part frame origin)"),
    translate: z.array(z.number()).length(3).optional().describe("Translation [dx,dy,dz] in mm applied after the rotation"),
  },
  async (args) => { try {
    return ok(await bridge("/station/part/transform", "POST", args, 60000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_get_smartcomponent_library", "List the built-in SmartComponents (Source, Sink, LinearMover2, Attacher, LogicGate, Timer...) that rs_edit_station_logic can create with op=create + library.", {},
// ── Physics & mechanisms ────────────────────────────────────────────────────

server.tool("rs_get_physics", "Read the physics properties of one station object (name, or path 'Parent/Child' when the name is not unique): motion control (None, Static, Kinematic, Dynamic), and for a Part its physics material and collision geometry setting. Also returns the station physics settings (gravity, floor). Read-only.",
  { name: z.string().min(1).describe("Object name, or path 'Parent/Child'") },
  async ({ name }) => { try {
    return ok(await bridge(`/physics/get?name=${encodeURIComponent(name)}`));
  } catch (e: any) { return err(e); } }
);

const physicsMaterial = z.object({
  SurfaceRoughness: z.number().optional().describe("Friction coefficient of the surface"),
  SurfaceViscosity: z.number().optional(),
  SurfaceAdhesion: z.number().optional(),
  SurfaceAdhesionOverlap: z.number().optional(),
  Density: z.number().optional().describe("kg/m3"),
  Damping: z.number().optional(),
  Restitution: z.number().optional().describe("Bounce, 0..1"),
  PoissonsRatio: z.number().optional(),
  YoungsModulus: z.number().optional(),
}).strict();

server.tool("rs_set_physics", "MODIFIES THE STATION. Set physics properties of station objects (names, or paths 'Parent/Child'): motionControl (None = no physics, Static = fixed obstacle, Kinematic = moved by the simulation and pushes dynamic objects, Dynamic = moved by gravity and contacts), and for Parts the physics material (fields not given keep their value) and useOriginalGeometry (exact geometry instead of the simplified collision shape). One undo step (Ctrl+Z). Does not save the station.",
  {
    objects: z.array(z.string().min(1)).min(1).describe("Object names or paths"),
    motionControl: z.enum(["None", "Static", "Kinematic", "Dynamic"]).optional(),
    material: physicsMaterial.optional(),
    useOriginalGeometry: z.boolean().optional(),
  },
  async (args) => { try {
    return ok(await bridge("/physics/set", "POST", args, 60000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_physics_settings", "Read the station physics settings (gravity in m/s2, floor on/off and height in mm). With any argument given: MODIFIES THE STATION (one undo step, not saved).",
  {
    gravity: z.number().optional().describe("m/s2"),
    floor: z.boolean().optional(),
    floorHeight: z.number().optional().describe("mm"),
  },
  async (args) => { try {
    const set = Object.keys(args).length > 0;
    return ok(await bridge("/physics/settings", set ? "POST" : "GET", set ? args : undefined));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_create_mechanism", "MODIFIES THE STATION. Build a mechanism from existing Parts (each link = one Part, given by name or path) with prismatic or rotational joints, like RobotStudio's Create Mechanism. Joint axes are 2 points in mm, station coordinates, at the current pose of the parts. Limits and poses in mm (prismatic) or degrees (rotational). Optional named poses (for PoseMover), parent SmartComponent, and attachment to a robot flange (mount=false keeps the current position). Link parts still present after compilation are reported, not deleted. One undo step (Ctrl+Z). Does not save the station.",
  {
    name: z.string().min(1),
    type: z.enum(["Other", "Tool", "ExternalAxis"]).optional().describe("Default Other (device)"),
    parent: z.string().optional().describe("SmartComponent (name or path) that will hold the mechanism; default the station"),
    links: z.array(z.object({ name: z.string().min(1), part: z.string().min(1) })).min(2),
    baseLink: z.string().min(1),
    joints: z.array(z.object({
      name: z.string().min(1),
      parent: z.string().min(1).describe("Parent link name"),
      child: z.string().min(1).describe("Child link name"),
      type: z.enum(["Prismatic", "Rotational"]),
      axisStart: z.array(z.number()).length(3),
      axisEnd: z.array(z.number()).length(3),
      min: z.number().optional(),
      max: z.number().optional(),
    })).min(1),
    poses: z.record(z.string(), z.array(z.number())).optional().describe("Named poses, e.g. {\"Retracted\":[0],\"Extended\":[198.2]}"),
    attachToFlangeOf: z.string().optional().describe("Robot mechanism name (or path) to attach the new mechanism to"),
    mount: z.boolean().optional().describe("true = move the mechanism onto the flange; false (default) = keep its position"),
  },
  async (args) => { try {
    return ok(await bridge("/mechanism/create", "POST", args, 120000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_mechanism_joints", "Read the joint values, limits and named poses of a mechanism (name or path). With limits / poses: MODIFIES the mechanism (one undo step, station not saved): limits = one [min,max] per joint (mm / degrees), null keeps a joint; poses = named joint values added or replaced, null removes a pose. With values: MOVES the mechanism to these joint values (mm / degrees), like jogging; not an undo step.",
  {
    name: z.string().min(1),
    values: z.array(z.number()).optional(),
    limits: z.array(z.tuple([z.number(), z.number()]).nullable()).optional(),
    poses: z.record(z.string(), z.array(z.number()).nullable()).optional(),
  },
  async (args) => { try {
    return ok(await bridge("/mechanism/joints", "POST", args, 60000));
  } catch (e: any) { return err(e); } }
);

  async () => { try { return ok(await bridge("/station/logic/library")); } catch (e: any) { return err(e); } }
);

const logicOp = z.object({
  op: z.enum(["create", "delete", "rename", "setVisible", "setProperty", "addProperty", "removeProperty", "addSignal", "removeSignal", "connect", "disconnect", "bind", "unbind"]),
}).passthrough();

server.tool("rs_edit_station_logic",
  "MODIFIES THE STATION. Edit the Station Logic with a list of operations executed in order, as ONE undo step (Ctrl+Z). If one operation fails the call stops, the components it created are removed, but changes already made to pre-existing components may remain (check with rs_get_station_logic). Does not save the station. " +
  "Paths are SmartComponent names separated by '/' (e.g. 'Box Manager/BoxCopy'); '' = the station itself. Operations: " +
  "{op:'create', parent?:path, name, library?:'Source'|... (see rs_get_smartcomponent_library) | copyOf?:path (copy of an existing component, keeps its properties) } (neither = empty SmartComponent); " +
  "{op:'delete', path}; {op:'rename', path, newName}; {op:'setVisible', name, visible}; " +
  "{op:'setProperty', path, property, value, unit?:'mm'|'deg'|'m'|'rad'|'s'} (unit converts to SI for Double/Vector3; value for object properties = name or path of the object; Vector3 = [x,y,z]); " +
  "{op:'addProperty', path, name, type:'Double'|'Boolean'|'Int32'|'String'|'Vector3'|'GraphicComponent'|'IHasTransform'|'IAttachableChild'|'ProjectObject'|'Part'|full type name, value?, description?}; {op:'removeProperty', path, name}; {op:'addSignal', path?, name, type:'DigitalInput'|'DigitalOutput'|'AnalogInput'|'AnalogOutput'|'DigitalGroupInput'|'DigitalGroupOutput', autoReset?}; {op:'removeSignal', path?, name}; " +
  "{op:'connect'|'disconnect', container?:path, source, sourceSignal, target, targetSignal, allowCycle?} (source/target = child name, '(self)' for the container, '(Station Logic)', or a controller name at station level); " +
  "{op:'bind'|'unbind', container?:path, source, sourceProperty, target, targetProperty}. Read the result with rs_get_station_logic.",
  { ops: z.array(logicOp).min(1).describe("Operations, executed in order") },
  async ({ ops }) => { try {
    return ok(await bridge("/station/logic/edit", "POST", { ops }, 60000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_list_config_signals", "List the I/O signals defined in the live controller configuration (EIO_SIGNAL), with type, access and device.",
  { nameFilter: z.string().optional().describe("Case-insensitive substring of the signal name") },
  async ({ nameFilter }) => { try {
    const q = nameFilter ? `?nameFilter=${encodeURIComponent(nameFilter)}` : "";
    return ok(await bridge(`/controller/config/signals${q}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_create_config_signals",
  "MODIFIES THE CONTROLLER CONFIGURATION. Create I/O signals (EIO_SIGNAL) in the live configuration of the virtual controller. Only missing signals are created; existing ones are never changed. A controller restart is usually required afterwards.",
  {
    signals: z.array(z.object({
      name: z.string(),
      type: z.enum(["DI", "DO", "AI", "AO", "GI", "GO"]).optional().describe("Default DO"),
      access: z.string().optional().describe("Default 'All'"),
    })).min(1),
  },
  async ({ signals }) => { try {
    return ok(await bridge("/controller/config/signals", "POST", { signals }, 60000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_save_station", "Save the active station to disk", {},
  async () => { try { return ok(await bridge("/station/save", "POST")); } catch (e: any) { return err(e); } }
);

// ── RAPID via SDK ────────────────────────────────────────────

server.tool("rs_get_tasks", "List RAPID tasks with module counts (SDK)", {},
  async () => { try { return ok(await bridge("/rapid/tasks")); } catch (e: any) { return err(e); } }
);

server.tool("rs_get_modules", "List RAPID modules in a task (SDK)",
  { task: z.string().describe("Task name, e.g. 'T_ROB1'") },
  async ({ task }) => { try {
    return ok(await bridge(`/rapid/modules?task=${encodeURIComponent(task)}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_read_module", "Read full RAPID source code of a module (SDK)",
  { task: z.string(), module: z.string().describe("e.g. 'module_MAIN', 'module_EGM', 'Wizard_Params'") },
  async ({ task, module }) => { try {
    return ok(await bridge(`/rapid/module/text?task=${encodeURIComponent(task)}&module=${encodeURIComponent(module)}`, "GET", undefined, 30000));
  } catch (e: any) { return err(e); } }
);

// ── rs_write_module (ENHANCED: RAPID validation + large-module fallback) ──

server.tool("rs_write_module", "Write RAPID module source code into running controller (SDK). Auto-validates RAPID syntax and uses file upload for large modules.",
  {
    task: z.string(),
    module: z.string(),
    code: z.string().describe("Full RAPID code (MODULE ... ENDMODULE)"),
    replace: z.boolean().optional().describe("Replace existing modules (default: true)"),
    skipValidation: z.boolean().optional().describe("Skip RAPID syntax validation (default: false)"),
    useFileUpload: z.boolean().optional().describe("Force file-based upload via RWS (for very large modules)"),
  },
  async ({ task, module, code, replace, skipValidation, useFileUpload }) => { try {
    // 1. Validate RAPID syntax
    const v = validateOrReject(code, skipValidation);
    if (!v.valid) return err(new Error(JSON.stringify(v.error, null, 2)), "RAPID Validation");

    // 2. Choose upload method
    const isLarge = code.length > LARGE_MODULE_THRESHOLD;
    if (useFileUpload || isLarge) {
      try {
        return ok(await uploadLargeModule(task, module, code));
      } catch (fileErr: any) {
        // Fallback: try SDK bridge if file upload fails
        if (!useFileUpload) {
          return ok(await bridge(
            `/rapid/module/text?task=${encodeURIComponent(task)}&module=${encodeURIComponent(module)}`,
            "POST", { code, replace }, 30000
          ));
        }
        throw fileErr;
      }
    }

    // 3. Standard SDK bridge upload
    return ok(await bridge(
      `/rapid/module/text?task=${encodeURIComponent(task)}&module=${encodeURIComponent(module)}`,
      "POST", { code, replace }, 30000
    ));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_read_variable", "Read a RAPID variable initial value (SDK)",
  { task: z.string(), name: z.string() },
  async ({ task, name }) => { try {
    return ok(await bridge(`/rapid/variable?task=${encodeURIComponent(task)}&name=${encodeURIComponent(name)}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_write_variable", "Set a RAPID variable initial value (SDK)",
  { task: z.string(), name: z.string(), value: z.string() },
  async ({ task, name, value }) => { try {
    return ok(await bridge(`/rapid/variable?task=${encodeURIComponent(task)}&name=${encodeURIComponent(name)}`, "POST", { value }));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_list_variables", "List RAPID variables with optional type/module filter (SDK)",
  { task: z.string().optional().describe("Task name (default: T_ROB1)"),
    typeFilter: z.string().optional().describe("Filter by data type e.g. 'robtarget', 'tooldata', 'wobjdata', 'num'"),
    module: z.string().optional().describe("Filter by module name") },
  async ({ task, typeFilter, module }) => { try {
    let p = `/rapid/variables?task=${encodeURIComponent(task || "T_ROB1")}`;
    if (typeFilter) p += `&typeFilter=${encodeURIComponent(typeFilter)}`;
    if (module) p += `&module=${encodeURIComponent(module)}`;
    return ok(await bridge(p));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_get_execution_errors", "Read controller event log for errors and warnings (SDK)",
  { count: z.number().optional().describe("Max messages to return (default: 10, max: 100)") },
  async ({ count }) => { try {
    return ok(await bridge(`/rapid/errors?count=${count || 10}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_get_screenshot", "Capture 3D view screenshot as base64 PNG (SDK)",
  { width: z.number().optional().describe("Width in pixels (default: 1280, max: 3840)"),
    height: z.number().optional().describe("Height in pixels (default: 720, max: 2160)") },
  async ({ width, height }) => { try {
    const result = await bridge("/screenshot", "POST", {
      width: width || 1280, height: height || 720,
    }, 20000) as any;
    return {
      content: [
        { type: "image" as const, data: result.imageBase64, mimeType: result.mimeType || "image/png" },
        { type: "text" as const, text: JSON.stringify({
          width: result.width, height: result.height, timestamp: result.timestamp
        }, null, 2) },
      ],
    };
  } catch (e: any) { return err(e); } }
);

// ── Simulation ───────────────────────────────────────────────

server.tool("rs_controller_status", "Get controller state via SDK (systemState, runMode, simulation)", {},
  async () => { try { return ok(await bridge("/controller/status")); } catch (e: any) { return err(e); } }
);

// ── rs_start_simulation (ENHANCED: safety guard) ──

server.tool("rs_start_simulation", "Start RobotStudio simulation (Play). Blocks if RAPID errors detected — use force=true to override.",
  {
    force: z.boolean().optional().describe("Skip safety check and force start even with errors (default: false)"),
  },
  async ({ force }) => { try {
    if (!force) {
      const safety = await checkSimulationSafety();
      if (!safety.safe) {
        return err(
          new Error(`Safety check blocked simulation: ${safety.reason}\nHint: Set force=true to override`),
          "SafetyGuard"
        );
      }
    }
    return ok(await bridge("/simulation/start", "POST"));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_stop_simulation", "Stop RobotStudio simulation", {},
  async () => { try { return ok(await bridge("/simulation/stop", "POST")); } catch (e: any) { return err(e); } }
);

server.tool("rs_pause_simulation", "Pause RobotStudio simulation", {},
  async () => { try { return ok(await bridge("/simulation/pause", "POST")); } catch (e: any) { return err(e); } }
);

server.tool("rs_reset_simulation", "Reset simulation to start position (stops simulation and resets PP)", {},
  async () => { try { return ok(await bridge("/simulation/reset", "POST")); } catch (e: any) { return err(e); } }
);

server.tool("rs_simulation_status",
  "Get simulation state, time, speed multiplier and fullSpeed (As fast as possible)", {},
  async () => { try { return ok(await bridge("/simulation/status")); } catch (e: any) { return err(e); } }
);

server.tool("rs_set_sim_speed",
  "Set the simulation speed: a multiplier, or 'As fast as possible' (fullSpeed). " +
  "Setting only 'speed' leaves 'As fast as possible'.",
  {
    speed: z.number().positive().optional()
      .describe("Speed multiplier (1.0 = real time)"),
    fullSpeed: z.boolean().optional()
      .describe("true = As fast as possible; false = use the multiplier"),
  },
  async ({ speed, fullSpeed }) => { try {
    if (speed === undefined && fullSpeed === undefined)
      throw new Error("Provide speed and/or fullSpeed");
    return ok(await bridge("/simulation/speed", "POST", { speed, fullSpeed }));
  } catch (e: any) { return err(e); } }
);

// ── Paths & Targets ─────────────────────────────────────────

server.tool("rs_get_paths", "List all robot paths in the station", {},
  async () => { try { return ok(await bridge("/paths")); } catch (e: any) { return err(e); } }
);

server.tool("rs_get_path_targets", "Get targets/waypoints in a path",
  { path: z.string() },
  async ({ path }) => { try {
    return ok(await bridge(`/paths/targets?path=${encodeURIComponent(path)}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_create_path", "Create a new robot path",
  { name: z.string() },
  async ({ name }) => { try {
    return ok(await bridge("/paths/create", "POST", { name }));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_create_target", "Create a new robot target at coordinates",
  { name: z.string(), x: z.number().describe("mm"), y: z.number().describe("mm"), z: z.number().describe("mm") },
  async ({ name, x, y, z: zp }) => { try {
    return ok(await bridge("/targets/create", "POST", { name, x, y, z: zp }));
  } catch (e: any) { return err(e); } }
);

// ── Config Files & Collision ─────────────────────────────────

server.tool("rs_read_config", "Read controller config file (SYS.cfg, EIO.cfg, SIO.cfg, MOC.cfg)",
  { name: z.string().describe("e.g. 'SIO.cfg'") },
  async ({ name }) => { try {
    return ok(await bridge(`/config/read?name=${encodeURIComponent(name)}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_write_config", "Write controller config file",
  { name: z.string(), content: z.string() },
  async ({ name, content }) => { try {
    return ok(await bridge(`/config/write?name=${encodeURIComponent(name)}`, "POST", { content }));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_check_collisions", "Run collision check on station objects", {},
  async () => { try { return ok(await bridge("/collision/check")); } catch (e: any) { return err(e); } }
);

server.tool("rs_create_collision_set", "MODIFIES THE STATION. Create an active RobotStudio collision set between two groups of existing graphic objects (names of Parts, Groups or Mechanisms, e.g. the robot against the cell). Colliding objects are highlighted in the graphics window. Objects created during the simulation cannot be put in a set. One undo step (Ctrl+Z). Does not save the station.",
  {
    name: z.string().min(1).describe("Name of the collision set"),
    first: z.array(z.string().min(1)).min(1).describe("Names of the objects of the first group"),
    second: z.array(z.string().min(1)).min(1).describe("Names of the objects of the second group"),
    color: z.array(z.number().int().min(0).max(255)).length(3).optional().describe("Highlight color [r,g,b] (default red)"),
    nearMiss: z.number().min(0).optional().describe("Near-miss distance in mm (default 0 = collisions only)"),
    replace: z.boolean().optional().describe("Replace an existing set of the same name (default false)"),
  },
  async (args) => { try {
    return ok(await bridge("/collision/set", "POST", args, 30000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_get_position", "Get robot position via SDK", {},
  async () => { try { return ok(await bridge("/robot/position")); } catch (e: any) { return err(e); } }
);

server.tool("rs_get_io_signals", "List I/O signals via SDK", {},
  async () => { try { return ok(await bridge("/io/signals")); } catch (e: any) { return err(e); } }
);

server.tool("rs_read_io_signal", "Read individual IO signal state and value via SDK",
  { name: z.string().describe("Signal name, e.g. 'di1', 'do1'") },
  async ({ name }) => { try {
    return ok(await bridge(`/io/signal?name=${encodeURIComponent(name)}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_write_io_signal", "Set individual IO signal value via SDK",
  {
    name: z.string().describe("Signal name, e.g. 'di1', 'do1'"),
    value: z.number().describe("Signal value (e.g. 1 or 0)"),
  },
  async ({ name, value }) => { try {
    return ok(await bridge(`/io/signal?name=${encodeURIComponent(name)}`, "POST", { value }));
  } catch (e: any) { return err(e); } }
);

// ── NEW: RAPID Validation ────────────────────────────────────

server.tool("rs_validate_rapid", "Validate RAPID source code syntax (block balance: IF/ENDIF, PROC/ENDPROC, etc.)",
  { code: z.string().describe("Full RAPID source code to validate") },
  async ({ code }) => { try {
    return ok(validateRapidCode(code));
  } catch (e: any) { return err(e); } }
);

// ── NEW: Safety Check ────────────────────────────────────────

server.tool("rs_check_safety", "Check if it is safe to start simulation (no RAPID errors, controller ready)",
  {},
  async () => { try {
    return ok(await checkSimulationSafety());
  } catch (e: any) { return err(e); } }
);

// ═══════════════════════════════════════════════════════════════
// ██  RWS TOOLS (rws_*) — Robot Controller via Web Services   ██
// ═══════════════════════════════════════════════════════════════

// ── NEW: Runtime URL Switching ───────────────────────────────

server.tool("rws_set_controller_url", "Switch RWS target controller at runtime (virtual ↔ real, no restart needed)",
  {
    url: z.string().describe("New controller URL, e.g. 'http://localhost:80' (virtual) or 'http://192.168.125.1:80' (real)"),
  },
  async ({ url }) => { try {
    const previous = getRwsUrl();
    setRwsUrl(url);
    resetRwsSession(); // force fresh auth for new controller
    const current = getRwsUrl();
    return ok({ previous, current, message: `Controller URL changed: ${previous} → ${current}` });
  } catch (e: any) { return err(e); } }
);

// ── Controller ───────────────────────────────────────────────

server.tool("rws_controller_status", "Get controller state via RWS (opmode, motors, RAPID execution)", {},
  async () => { try {
    const [panel, opmode, exec] = await Promise.all([
      rws("/rw/panel/ctrl-state").catch(() => rws("/rw/panel/ctrlstate")), // ctrl-state on RobotWare 8
      rws("/rw/panel/opmode"),
      rws("/rw/rapid/execution"),
    ]);
    return ok({
      controllerState: rwsExtract(panel.body, "ctrlstate") || rwsExtract(panel.body, "ctrl-state"),
      operatingMode: rwsExtract(opmode.body, "opmode"),
      executionState: rwsExtract(exec.body, "ctrlexecstate"),
      cycle: rwsExtract(exec.body, "cycle"),
    });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_set_motors", "Turn motors on/off via RWS (real controller command)",
  { state: z.enum(["on", "off"]) },
  async ({ state }) => { try {
    const r = await rws("POST", "/rw/panel/ctrlstate?action=setctrlstate",
      `ctrl-state=motor${state}`, "application/x-www-form-urlencoded");
    return ok({ result: r.status === 204 ? `Motors ${state}` : r.body });
  } catch (e: any) { return err(e); } }
);

// ── RAPID Execution (RWS) ────────────────────────────────────

// ── rws_start_program (ENHANCED: safety guard) ──

server.tool("rws_start_program", "Start RAPID execution on the controller via RWS. Blocks if errors detected — use force=true to override.",
  {
    mode: z.enum(["continue", "reset"]).optional(),
    force: z.boolean().optional().describe("Skip safety check and force start even with errors (default: false)"),
  },
  async ({ mode, force }) => { try {
    if (!force) {
      const safety = await checkExecutionSafety();
      if (!safety.safe) {
        return err(
          new Error(`Safety check blocked execution: ${safety.reason}\nHint: Set force=true to override`),
          "SafetyGuard"
        );
      }
    }
    const r = await rws("POST", "/rw/rapid/execution?action=start",
      `regain=continue&execmode=${mode || "continue"}&cycle=forever&condition=none&stopatbp=disabled&alltaskbytsp=false`,
      "application/x-www-form-urlencoded");
    return ok({ result: r.status === 204 ? "Program started" : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_stop_program", "Stop RAPID execution via RWS", {},
  async () => { try {
    const r = await rws("POST", "/rw/rapid/execution?action=stop",
      "stopmode=stop&usetsp=normal", "application/x-www-form-urlencoded");
    return ok({ result: r.status === 204 ? "Program stopped" : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_reset_pp", "Reset RAPID program pointer via RWS", {},
  async () => { try {
    const r = await rws("POST", "/rw/rapid/execution?action=resetpp");
    return ok({ result: r.status === 204 ? "PP reset" : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_execution_state", "Get RAPID execution state via RWS", {},
  async () => { try {
    const r = await rws("/rw/rapid/execution");
    return ok({ state: rwsExtract(r.body, "ctrlexecstate"), cycle: rwsExtract(r.body, "cycle") });
  } catch (e: any) { return err(e); } }
);

// ── RAPID Tasks & Modules (RWS) ─────────────────────────────

server.tool("rws_get_tasks", "List RAPID tasks via RWS", {},
  async () => { try {
    const r = await rws("/rw/rapid/tasks");
    const names = rwsExtractAll(r.body, "name");
    const excstates = rwsExtractAll(r.body, "excstate");
    return ok(names.map((n, i) => ({ name: n, executionState: excstates[i] ?? "?" })));
  } catch (e: any) { return err(e); } }
);

server.tool("rws_get_modules", "List RAPID modules in a task via RWS",
  { task: z.string() },
  async ({ task }) => { try {
    const r = await rws(`/rw/rapid/tasks/${task}/modules`);
    const names = rwsExtractAll(r.body, "name");
    const types = rwsExtractAll(r.body, "type");
    return ok(names.map((n, i) => ({ name: n, type: types[i] ?? "?" })));
  } catch (e: any) { return err(e); } }
);

server.tool("rws_read_module", "Read RAPID module text via RWS",
  { task: z.string(), module: z.string() },
  async ({ task, module }) => { try {
    const r = await rws(`/rw/rapid/tasks/${task}/modules/${module}/text`);
    let text = rwsExtract(r.body, "module-text");
    if (!text) {
      // RobotWare 7 answers with metadata only: the module text sits in a temporary file
      // on the controller (e.g. /TEMP/pusres.123) that must be downloaded separately.
      const filePath = rwsExtract(r.body, "file-path");
      const m = /^\/?(TEMP|HOME|SYSTEM)\/(.+)$/i.exec(filePath);
      if (m) {
        const file = await rws(`/fileservice/${encodeURIComponent(`$${m[1]!.toUpperCase()}/${m[2]}`)}`);
        text = file.body;
      }
    }
    return ok({ task, module, text: text || r.body });
  } catch (e: any) { return err(e); } }
);

// ── rws_write_module (ENHANCED: RAPID validation) ──

server.tool("rws_write_module", "MODIFIES THE RAPID PROGRAM. Write RAPID module text via RWS: the text is uploaded to a temporary file in $HOME and loaded in the task, replacing the module of the same name. Write access: mastership (RobotWare 6 / 7) or remote control station (RobotWare 8; refused while another control station, e.g. the FlexPendant, holds write access). Validates RAPID syntax before upload. The module file the system loads at reset (SYS.cfg) is not changed.",
  {
    task: z.string(), module: z.string(), code: z.string(),
    skipValidation: z.boolean().optional().describe("Skip RAPID syntax validation (default: false)"),
  },
  async ({ task, module, code, skipValidation }) => { try {
    // Validate RAPID syntax first
    const v = validateOrReject(code, skipValidation);
    if (!v.valid) return err(new Error(JSON.stringify(v.error, null, 2)), "RAPID Validation");
    const declared = code.match(/^\s*MODULE\s+(\w+)/im)?.[1];
    if (declared && declared.toLowerCase() !== module.toLowerCase()) {
      return err(new Error(`The code declares MODULE ${declared}, not ${module}`), "RAPID Validation");
    }
    return ok(await uploadLargeModule(task, module, code));
  } catch (e: any) { return err(e); } }
);

// ── NEW: RWS Load/Unload Module from File ──────────────────

function validateControllerPath(filepath: string): string {
  // Block path traversal
  if (filepath.includes("..")) {
    throw new Error(`Path traversal blocked: ".." not allowed in "${filepath}"`);
  }
  // Must start with $HOME, $SYSTEM, or $TEMP followed by alphanumeric/underscore/hyphen path segments
  // Dot allowed only in filenames (not as segment), slash as separator
  if (!/^\$(?:HOME|SYSTEM|TEMP)(?:\/[a-zA-Z0-9_\-]+)*\/[a-zA-Z0-9_\-\.]+$/i.test(filepath)) {
    throw new Error(
      `Invalid controller path: "${filepath}". Must start with $HOME, $SYSTEM, or $TEMP.`
    );
  }
  return filepath;
}

server.tool("rws_load_module_from_file", "MODIFIES THE RAPID PROGRAM. Load a RAPID module from a file on the controller filesystem via RWS. Write access: mastership (RobotWare 6 / 7) or remote control station (RobotWare 8; refused while another control station, e.g. the FlexPendant, holds write access).",
  {
    task: z.string().describe("Task name, e.g. 'T_ROB1'"),
    filepath: z.string().describe("Path on controller, e.g. '$HOME/my_module.mod'"),
    replace: z.boolean().optional().describe("Replace existing module (default: true)"),
  },
  async ({ task, filepath, replace }) => { try {
    const safePath = validateControllerPath(filepath);
    const r = await withRapidWriteAccess(() => loadModule(task, safePath, replace !== false));
    return ok({ result: r.status < 300 ? `Module loaded from ${safePath}` : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_unload_module", "MODIFIES THE RAPID PROGRAM. Unload (delete) a RAPID module from a task via RWS. Write access: mastership (RobotWare 6 / 7) or remote control station (RobotWare 8; refused while another control station, e.g. the FlexPendant, holds write access).",
  {
    task: z.string().describe("Task name, e.g. 'T_ROB1'"),
    module: z.string().describe("Module name to unload"),
  },
  async ({ task, module }) => { try {
    const r = await withRapidWriteAccess(() => unloadModule(task, module));
    return ok({ result: r.status < 300 ? `Module ${module} unloaded` : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_save_program", "Save entire RAPID program to a directory on the controller via RWS",
  {
    task: z.string().describe("Task name, e.g. 'T_ROB1'"),
    path: z.string().describe("Directory path on controller, e.g. '$HOME/mybackup'"),
  },
  async ({ task, path: savePath }) => { try {
    const safePath = validateControllerPath(savePath + "/program");
    const r = await rwsPostForm(`/rw/rapid/tasks/${task}/program?action=save`, { path: safePath });
    return ok({ result: r.status === 204 ? `Program saved to ${safePath}` : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_load_program", "Load a RAPID program from the controller filesystem via RWS",
  {
    task: z.string().describe("Task name, e.g. 'T_ROB1'"),
    progpath: z.string().describe("Program file path, e.g. '$HOME/myprog.pgf'"),
    loadmode: z.enum(["add", "replace"]).optional().describe("Load mode (default: replace)"),
  },
  async ({ task, progpath, loadmode }) => { try {
    const safePath = validateControllerPath(progpath);
    const r = await rwsPostForm(`/rw/rapid/tasks/${task}/program?action=loadprog`, {
      progpath: safePath,
      loadmode: loadmode || "replace",
    });
    return ok({ result: r.status === 204 ? `Program loaded from ${safePath}` : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_restart_controller", "Restart the robot controller via RWS (warm-start, resets RAPID)",
  {
    mode: z.enum(["warm", "cold"]).optional().describe("Restart mode: warm (keep system params) or cold (full restart). Default: warm."),
  },
  async ({ mode }) => { try {
    const restartMode = mode || "warm";
    const r = await rws("POST", `/rw/panel/ctrlstate?action=${restartMode}start`);
    return ok({ result: r.status === 204 ? `Controller ${restartMode}-started` : r.body });
  } catch (e: any) { return err(e); } }
);

// ── RAPID Variables (RWS) ────────────────────────────────────

server.tool("rws_read_variable", "Read a RAPID variable's live runtime value via RWS",
  { task: z.string(), module: z.string(), variable: z.string() },
  async ({ task, module, variable }) => { try {
    const r = await rws(`/rw/rapid/symbol/RAPID/${task}/${module}/${variable}/data`);
    return ok({ variable, value: rwsExtract(r.body, "value") || r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_write_variable", "MODIFIES RAPID DATA. Write a RAPID variable's live value via RWS. Write access: mastership (RobotWare 6 / 7) or remote control station (RobotWare 8; refused while another control station, e.g. the FlexPendant, holds write access).",
  { task: z.string(), module: z.string(), variable: z.string(), value: z.string() },
  async ({ task, module, variable, value }) => { try {
    const r = await withRapidWriteAccess(() => setSymbolValue(task, module, variable, value));
    return ok({ result: r.status < 300 ? `${variable} = ${value}` : r.body });
  } catch (e: any) { return err(e); } }
);

// ── Robot Position (RWS) ────────────────────────────────────

server.tool("rws_get_position", "Get live robot TCP position via RWS",
  { mechUnit: z.string().optional().describe("Default: ROB_1") },
  async ({ mechUnit }) => { try {
    const mu = mechUnit || "ROB_1";
    const [tcp, jt] = await Promise.all([
      rws(`/rw/motionsystem/mechunits/${mu}/robtarget`),
      rws(`/rw/motionsystem/mechunits/${mu}/jointtarget`),
    ]);
    return ok({
      tcp: { x: rwsExtract(tcp.body, "x"), y: rwsExtract(tcp.body, "y"), z: rwsExtract(tcp.body, "z"),
             q1: rwsExtract(tcp.body, "q1"), q2: rwsExtract(tcp.body, "q2"),
             q3: rwsExtract(tcp.body, "q3"), q4: rwsExtract(tcp.body, "q4") },
      joints: { rax_1: rwsExtract(jt.body, "rax_1"), rax_2: rwsExtract(jt.body, "rax_2"),
                rax_3: rwsExtract(jt.body, "rax_3"), rax_4: rwsExtract(jt.body, "rax_4"),
                rax_5: rwsExtract(jt.body, "rax_5"), rax_6: rwsExtract(jt.body, "rax_6") },
    });
  } catch (e: any) { return err(e); } }
);

// ── I/O Signals (RWS) ───────────────────────────────────────

server.tool("rws_get_io_signals", "List all I/O signals via RWS", {},
  async () => { try {
    const r = await rws("/rw/iosystem/signals");
    const names = rwsExtractAll(r.body, "name");
    const types = rwsExtractAll(r.body, "type");
    const vals = rwsExtractAll(r.body, "lvalue");
    return ok(names.map((n, i) => ({ name: n, type: types[i], value: vals[i] })));
  } catch (e: any) { return err(e); } }
);

server.tool("rws_read_io", "Read a single I/O signal via RWS",
  { signal: z.string() },
  async ({ signal }) => { try {
    const r = await rws(`/rw/iosystem/signals/${signal}`);
    return ok({ signal, type: rwsExtract(r.body, "type"), value: rwsExtract(r.body, "lvalue") });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_write_io", "Set an I/O signal value via RWS",
  { signal: z.string(), value: z.number() },
  async ({ signal, value }) => { try {
    const r = await rws("POST", `/rw/iosystem/signals/${signal}/set-value`,
      `lvalue=${value}`, "application/x-www-form-urlencoded;v=2.0");
    return ok({ result: r.status === 204 ? `${signal} = ${value}` : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_create_signals", "MODIFIES THE CONTROLLER CONFIGURATION. Create I/O signals (EIO_SIGNAL instances) through RWS, registering as a remote control station for write access (RobotWare 8). Only missing signals are created; existing ones are left unchanged. The controller must be restarted before the new signals can be used.",
  {
    signals: z.array(z.object({
      name: z.string().regex(/^[A-Za-z][A-Za-z0-9_]{0,31}$/).describe("Signal name"),
      type: z.enum(["DI", "DO", "AI", "AO", "GI", "GO"]).optional().describe("Signal type (default DO)"),
      access: z.string().optional().describe("Access level (default All)"),
      device: z.string().optional().describe("I/O device (default none: simulated signal)"),
    })).min(1),
  },
  async ({ signals }) => { try {
    const path = (n: string) => `/rw/cfg/EIO/EIO_SIGNAL/instances/${encodeURIComponent(n)}`;
    const exists = async (n: string) => {
      try { await rws(path(n)); return true; }
      catch (e) { if (e instanceof RwsError && e.status === 404) return false; throw e; }
    };
    const created: string[] = [], existing: string[] = [];
    await withWriteAccess(async () => {
      for (const s of signals) {
        if (await exists(s.name)) { existing.push(s.name); continue; }
        await rwsPostForm("/rw/cfg/EIO/EIO_SIGNAL/instances/create-default", { name: s.name });
        const attrs: Record<string, string> = {
          SignalType: `[${s.type ?? "DO"},1]`,
          Access: `[${s.access ?? "All"},1]`,
        };
        if (s.device) attrs.Device = `[${s.device},1]`;
        await rwsPostForm(path(s.name), attrs);
        created.push(s.name);
      }
    });
    return ok({ created, existing, note: created.length ? "Restart the controller to use the new signals." : "Nothing created." });
  } catch (e: any) { return err(e); } }
);

// ── Speed Override (RWS) ────────────────────────────────────

server.tool("rws_get_speed_override", "Get speed override percentage via RWS", {},
  async () => { try {
    const r = await rws("/rw/panel/speedratio");
    return ok({ speedRatio: rwsExtract(r.body, "speedratio") });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_set_speed_override", "Set speed override (0-100%) via RWS",
  { speed: z.number().min(0).max(100) },
  async ({ speed }) => { try {
    const r = await rws("POST", "/rw/panel/speedratio?action=setspeedratio",
      `speed-ratio=${speed}`, "application/x-www-form-urlencoded");
    return ok({ result: r.status === 204 ? `Speed ${speed}%` : r.body });
  } catch (e: any) { return err(e); } }
);

// ── Event Log (RWS) ─────────────────────────────────────────

server.tool("rws_event_log", "Get controller event log via RWS",
  { count: z.number().optional().describe("Default: 10") },
  async ({ count }) => { try {
    const r = await rws(`/rw/elog/0?lang=en&count=${count || 10}`);
    const titles = rwsExtractAll(r.body, "title");
    const bodies = rwsExtractAll(r.body, "body");
    const timestamps = rwsExtractAll(r.body, "timestamp");
    const types = rwsExtractAll(r.body, "msgtype");
    const entries = titles.map((t, i) => ({
      title: t, body: bodies[i] ?? "", timestamp: timestamps[i] ?? "", type: types[i] ?? ""
    }));
    return ok(entries.length > 0 ? entries : { raw: r.body });
  } catch (e: any) { return err(e); } }
);

// ── Controller Files (RWS) ──────────────────────────────────

server.tool("rws_list_files", "List files on controller filesystem via RWS",
  { path: z.string().optional().describe("Default: $HOME") },
  async ({ path }) => { try {
    const safePath = validateControllerPath(path || "$HOME");
    const r = await rws(`/fileservice/${encodeURIComponent(safePath)}`);
    return ok({ files: r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_read_file", "Read a file from controller filesystem via RWS",
  { path: z.string() },
  async ({ path }) => { try {
    const safePath = validateControllerPath(path);
    const r = await rws(`/fileservice/${encodeURIComponent(safePath)}`);
    return ok({ content: r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_write_file", "Write a file to controller filesystem via RWS",
  { path: z.string(), content: z.string() },
  async ({ path, content }) => { try {
    const safePath = validateControllerPath(path);
    const r = await rws("PUT", `/fileservice/${encodeURIComponent(safePath)}`, content, "text/plain");
    return ok({ result: r.status <= 204 ? `Written to ${safePath}` : r.body });
  } catch (e: any) { return err(e); } }
);

// ── Mastership (RWS) ────────────────────────────────────────

server.tool("rws_request_mastership", "Request mastership (needed for writes)",
  { domain: z.enum(["rapid", "cfg", "motion"]).optional() },
  async ({ domain }) => { try {
    const r = await rws("POST", `/rw/mastership/${domain || "rapid"}?action=request`);
    return ok({ result: r.status === 204 ? "Mastership acquired" : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_release_mastership", "Release mastership",
  { domain: z.enum(["rapid", "cfg", "motion"]).optional() },
  async ({ domain }) => { try {
    const r = await rws("POST", `/rw/mastership/${domain || "rapid"}?action=release`);
    return ok({ result: r.status === 204 ? "Mastership released" : r.body });
  } catch (e: any) { return err(e); } }
);

// ═══════════════════════════════════════════════════════════════
// ██  START SERVER                                              ██
// ═══════════════════════════════════════════════════════════════

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("ABB RobotStudio MCP Server v3.0.0 (Safety-First Edition)");
  console.error("  SDK Bridge: " + BRIDGE_URL + " (TcpListener, no admin rights)");
  console.error("  RWS:        " + getRwsUrl() + " (runtime-switchable via rws_set_controller_url)");
  console.error("  Safety:     RAPID validation + pre-simulation error guard");
  console.error("  Tools:      rs_* (42) + rws_* (30) = 72 total");
  console.error("  RULES:      NEVER run simulation if RAPID errors exist");
}

main().catch((err) => {
  console.error("Fatal:", err);
  process.exit(1);
});
