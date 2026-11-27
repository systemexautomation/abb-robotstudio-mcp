/**
 * ABB RobotStudio MCP Server — Safety Guard
 * v3.0.0 — Pre-flight safety checks before simulation or execution
 *
 * RULE: NEVER run simulation or start program if there are RAPID errors.
 */

import { bridge } from "./bridge-client.js";
import { rwsControllerState, rwsExecutionState, rwsRecentEvents } from "./rws-client.js";

export interface SafetyCheckResult {
  safe: boolean;
  reason?: string;
  details: {
    controllerState?: string;
    executionState?: string;
    simulationState?: string;
    errorCount?: number;
    errors?: Array<{
      title: string;
      body: string;
      timestamp: string;
      type: string;
    }>;
  };
}

/**
 * Check safety before starting RobotStudio simulation.
 * Blocks if RAPID errors exist in the controller event log.
 */
export async function checkSimulationSafety(): Promise<SafetyCheckResult> {
  const details: SafetyCheckResult["details"] = {};

  try {
    // Check controller status
    const status = (await bridge("/controller/status", "GET", undefined, 10000)) as any;
    details.simulationState = status?.simulationState;
    details.controllerState = status?.systemState;

    // Check for RAPID errors
    const errors = (await bridge("/rapid/errors?count=20", "GET", undefined, 15000)) as any;
    if (errors?.errors && Array.isArray(errors.errors)) {
      const errorList = errors.errors.filter(
        (e: any) =>
          e.type === "Error" &&
          !e.title?.includes("Backup step ready") &&
          !e.title?.includes("Motors ON") &&
          !e.title?.includes("Motors OFF") &&
          !e.title?.includes("Automatic mode")
      );
      details.errorCount = errorList.length;
      details.errors = errorList.slice(0, 20);

      if (errorList.length > 0) {
        const titles = errorList.map((e: any) => e.title).join("; ");
        return {
          safe: false,
          reason: `${errorList.length} RAPID error(s) detected: ${titles}`,
          details,
        };
      }
    }

    return { safe: true, reason: "No RAPID errors — safe to start", details };
  } catch (e: any) {
    return {
      safe: false,
      reason: `Safety check unavailable: ${e.message}`,
      details,
    };
  }
}

/**
 * Check safety before starting program on a real controller via RWS.
 * More thorough — checks controller state, execution state, and event log.
 */
export async function checkExecutionSafety(): Promise<SafetyCheckResult> {
  const details: SafetyCheckResult["details"] = {};

  try {
    // Check controller state (the motors must be on to start)
    details.controllerState = await rwsControllerState();
    if (!details.controllerState) {
      return { safe: false, reason: "Could not read the controller state", details };
    }
    if (details.controllerState !== "motoron") {
      return {
        safe: false,
        reason: `Controller is in "${details.controllerState}": the motors must be on to start`,
        details,
      };
    }

    // Check execution state
    details.executionState = (await rwsExecutionState()).state;
    if (!details.executionState) {
      return { safe: false, reason: "Could not read the RAPID execution state", details };
    }

    if (details.executionState === "running") {
      return {
        safe: false,
        reason: "Program is already running on the controller",
        details,
      };
    }

    // Check the 10 newest event log entries for errors (message type 3)
    const recent = await rwsRecentEvents(10);
    const errorEntries = recent.filter((e) => e.type === 3);
    details.errorCount = errorEntries.length;
    details.errors = errorEntries.map((e) => ({
      title: e.title, body: e.description, timestamp: e.timestamp, type: "Error",
    }));

    if (errorEntries.length > 0) {
      return {
        safe: false,
        reason: `${errorEntries.length} error(s) among the 10 newest event log entries: ` +
          errorEntries.map((e) => `${e.title} (${e.timestamp})`).join("; "),
        details,
      };
    }

    return {
      safe: true,
      reason: recent.length > 0
        ? "Controller ready for execution"
        : "Controller ready for execution (event log empty or not read)",
      details,
    };
  } catch (e: any) {
    return {
      safe: false,
      reason: `Execution safety check unavailable: ${e.message}`,
      details,
    };
  }
}

/**
 * Validate and sanitize controller filesystem path to prevent path traversal.
 * Allowed roots: $HOME, $TEMP, or relative path without .. traversal.
 */
export function validateControllerPath(path: string): string {
  if (!path || typeof path !== "string") {
    throw new Error("Controller path must be a non-empty string");
  }
  const trimmed = path.trim();
  if (trimmed.includes("..") || trimmed.includes("\\")) {
    throw new Error(`Invalid path "${path}" — directory traversal is strictly forbidden`);
  }
  // Disallow hidden attempts to access system internal roots unless explicitly permitted
  const upper = trimmed.toUpperCase();
  if (upper.startsWith("$SYSTEM") || upper.startsWith("/SYSTEM")) {
    throw new Error(`Access to $SYSTEM root is forbidden for safety: "${path}"`);
  }
  return trimmed;
}
