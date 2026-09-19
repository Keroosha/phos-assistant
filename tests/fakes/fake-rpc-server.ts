// Scripted fake OMP RPC server for Phos wire-contract tests.
//
// Reads newline-delimited JSON commands on stdin and emits canned frames on
// stdout. Behaviour is selected via the PHOS_FAKE_SCENARIO env var:
//   - basic       (default): respond to every command; negotiate ack.
//   - chunks      : get_state is split across rpc_chunk frames.
//   - chunk_corrupt: get_state emits a chunk with a wrong byteLength.
//   - events      : prompt emits agent_start + text_delta + terminal agent_end.
//   - prompt_result: prompt returns agentInvoked:false + prompt_result.
//   - host        : prompt emits a host_tool_call for tg_send_message.
//   - uri         : prompt emits a host_uri_request (tg:// read).
//
// host_tool_result / host_uri_result frames received on stdin are echoed back as
// `{type:"echo", frame:<...>}` so tests can observe what the client wrote.
// negotiate_protocol is acknowledged and then signalled via `protocol_negotiated`.

import { createInterface } from "node:readline";
import { stdin, stdout } from "node:process";

function writeFrame(obj: unknown): void {
  stdout.write(JSON.stringify(obj) + "\n");
}

const scenario = process.env.PHOS_FAKE_SCENARIO || "basic";

// The ready frame advertises protocol v1 + v2 and the transport limits.
writeFrame({
  type: "ready",
  protocolVersion: 1,
  supportedProtocolVersions: [1, 2],
  maxFrameBytes: 1048576,
  maxReassembledFrameBytes: 67108864,
});

function str(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function respond(id: string | undefined, command: string, data?: unknown, error?: string, code?: string): void {
  const frame: Record<string, unknown> = { id, type: "response", command };
  if (error !== undefined) {
    frame.success = false;
    frame.error = error;
    if (code !== undefined) frame.code = code;
  } else {
    frame.success = true;
    if (data !== undefined) frame.data = data;
  }
  writeFrame(frame);
}

function respondViaChunks(id: string, command: string, data: unknown, chunkCount: number): void {
  const json = JSON.stringify({ id, type: "response", command, success: true, data });
  const bytes = Buffer.from(json, "utf8");
  const chunkSize = Math.max(1, Math.ceil(bytes.length / chunkCount));
  const chunkId = "rpc-" + id;
  // Mirrors omp's RpcFrameEncoder: byteLength is the FULL reassembled frame
  // size, identical in every chunk of the sequence.
  const byteLength = bytes.length;
  for (let i = 0; i < chunkCount; i++) {
    const slice = bytes.subarray(i * chunkSize, Math.min((i + 1) * chunkSize, bytes.length));
    writeFrame({
      type: "rpc_chunk",
      chunkId,
      index: i,
      count: chunkCount,
      byteLength,
      data: slice.toString("base64"),
    });
  }
}

function respondCorruptChunk(id: string, command: string, data: unknown): void {
  const json = JSON.stringify({ id, type: "response", command, success: true, data });
  const bytes = Buffer.from(json, "utf8");
  const chunkSize = Math.max(1, Math.ceil(bytes.length / 2));
  const chunkId = "rpc-corrupt-" + id;
  // Declares a total one byte larger than the payload actually sums to; the
  // reassembler must reject the sequence at completion.
  const byteLength = bytes.length + 1;
  for (let i = 0; i < 2; i++) {
    const slice = bytes.subarray(i * chunkSize, Math.min((i + 1) * chunkSize, bytes.length));
    writeFrame({
      type: "rpc_chunk",
      chunkId,
      index: i,
      count: 2,
      byteLength,
      data: slice.toString("base64"),
    });
  }
}

const rl = createInterface({ input: stdin, crlfDelay: Infinity });

rl.on("line", (line) => {
  if (!line.trim()) return;
  let parsed: unknown;
  try {
    parsed = JSON.parse(line);
  } catch {
    writeFrame({ type: "response", command: "parse", success: false, error: "parse error" });
    return;
  }
  if (typeof parsed !== "object" || parsed === null) return;
  const msg = parsed as Record<string, unknown>;

  const type = str(msg.type);
  const id = str(msg.id);
  if (type === undefined) return;

  if (type === "negotiate_protocol") {
    respond(id, "negotiate_protocol", {});
    writeFrame({ type: "protocol_negotiated", version: msg.protocolVersion });
    return;
  }

  if (type === "host_tool_result" || type === "host_uri_result") {
    writeFrame({ type: "echo", frame: msg });
    return;
  }

  if (type === "get_state") {
    const stateData = {
      sessionId: "sess-123",
      sessionFile: "/tmp/sess-123.jsonl",
      isStreaming: false,
      queuedMessageCount: 0,
      messageCount: 5,
    };
    if (scenario === "chunks") {
      respondViaChunks(id ?? "req", "get_state", stateData, 3);
    } else if (scenario === "chunk_corrupt") {
      respondCorruptChunk(id ?? "req", "get_state", stateData);
    } else {
      const respondState = (): void => respond(id, "get_state", stateData);
      if (id === "slow") {
        setTimeout(respondState, 200);
      } else {
        respondState();
      }
    }
    return;
  }

  if (type === "set_host_tools") {
    const tools = Array.isArray(msg.tools) ? msg.tools : [];
    const toolNames = tools
      .map((t) => (typeof t === "object" && t !== null && "name" in t ? (t as { name?: unknown }).name : undefined))
      .filter((n): n is string => typeof n === "string");
    respond(id, "set_host_tools", { toolNames });
    return;
  }

  if (type === "set_host_uri_schemes") {
    const schemes = Array.isArray(msg.schemes) ? msg.schemes : [];
    const schemeNames = schemes
      .map((s) => (typeof s === "object" && s !== null && "scheme" in s ? (s as { scheme?: unknown }).scheme : undefined))
      .filter((n): n is string => typeof n === "string");
    respond(id, "set_host_uri_schemes", { schemes: schemeNames });
    return;
  }

  if (type === "get_last_assistant_text") {
    respond(id, "get_last_assistant_text", { text: "hello" });
    return;
  }

  if (type === "get_available_commands") {
    respond(id, "get_available_commands", { commands: [] });
    return;
  }

  if (type === "abort") {
    respond(id, "abort", {});
    return;
  }

  if (type === "bash") {
    respond(id, "bash", undefined, "bash command rejected", "bash_error");
    return;
  }

  if (type === "prompt") {
    respond(id, "prompt", { agentInvoked: true });
    if (scenario === "events") {
      writeFrame({ type: "agent_start" });
      writeFrame({ type: "message_update", assistantMessageEvent: { type: "text_delta", delta: "Hello " } });
      writeFrame({ type: "message_update", assistantMessageEvent: { type: "text_delta", delta: "world!" } });
      writeFrame({ type: "agent_end", messages: [], isTerminal: true });
    } else if (scenario === "prompt_result") {
      writeFrame({ type: "prompt_result", id, agentInvoked: false });
    } else if (scenario === "host") {
      writeFrame({
        type: "host_tool_call",
        id: "host_1",
        toolCallId: "toolu_1",
        toolName: "tg_send_message",
        arguments: { chat_id: 1, text: "hi" },
      });
    } else if (scenario === "uri") {
      writeFrame({ type: "host_uri_request", id: "uri_1", operation: "read", url: "tg://message/1/2" });
    }
    return;
  }

  // Unknown command -> failure.
  respond(id, type, undefined, "unknown command " + type, "unknown_command");
});
