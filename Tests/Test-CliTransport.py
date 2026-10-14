"""Opt-in native CLI/launcher smoke test; leaves existing editors running.

Run from the game project, or pass --project. --launch permits the CLI to own a
new editor; closing its stdin then closes only that owned editor. This test does
not switch scenes, enter Play, import assets or retire any workaround.
"""
import argparse
import json
import os
from pathlib import Path
import queue
import re
import subprocess
import threading
import time


class Client:
    def __init__(self, command, cwd):
        self.process = subprocess.Popen(command, cwd=cwd, stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                        text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW)
        self.lines = queue.Queue()
        self.errors = []
        def pump(stream, sink):
            for line in stream:
                sink(line.rstrip())
        self.output_reader = threading.Thread(target=pump, args=(self.process.stdout, self.lines.put), daemon=True)
        self.output_reader.start()
        threading.Thread(target=pump, args=(self.process.stderr, self.errors.append), daemon=True).start()
        self.next_id = 0

    def send(self, message):
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def request(self, method, params=None, timeout=75):
        self.next_id += 1
        ident = self.next_id
        self.send(dict(jsonrpc="2.0", id=ident, method=method, params=params or {}))
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                line = self.lines.get(timeout=min(0.5, max(0.01, deadline - time.monotonic())))
            except queue.Empty:
                if self.process.poll() is not None:
                    raise RuntimeError(f"CLI exited {self.process.returncode}: {'; '.join(self.errors)}")
                continue
            reply = json.loads(line)
            if reply.get("id") == ident:
                if "error" in reply:
                    raise RuntimeError(str(reply["error"]))
                return reply["result"]
        raise TimeoutError(f"{method} timed out: {'; '.join(self.errors)}")

    def initialize(self):
        result = self.request("initialize", dict(protocolVersion="2025-11-25", capabilities={},
                              clientInfo=dict(name="sbox-mcp-plus-cli-smoke", version="1")))
        self.send(dict(jsonrpc="2.0", method="notifications/initialized"))
        return result

    def tool(self, name, arguments=None):
        result = self.request("tools/call", dict(name=name, arguments=arguments or {}))
        if result.get("isError"):
            raise RuntimeError(str(result))
        if "structuredContent" in result:
            return result["structuredContent"]
        texts = [block["text"] for block in result.get("content", []) if block["type"] == "text"]
        return json.loads("\n".join(texts))

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                # Kill only this CLI/launcher, never an enumerated editor PID.
                self.process.kill()
                self.process.wait(timeout=15)
                raise TimeoutError("CLI did not close after stdin EOF")
        self.output_reader.join(timeout=5)
        if self.output_reader.is_alive():
            raise TimeoutError("CLI stdout did not close")
        while not self.lines.empty():
            # Shutdown is still part of the MCP stream, not a place for shell output.
            json.loads(self.lines.get_nowait())
        if self.process.returncode != 0:
            raise RuntimeError(f"CLI shutdown exited {self.process.returncode}: {'; '.join(self.errors)}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine", required=True)
    parser.add_argument("--project", default=os.getcwd())
    parser.add_argument("--pwsh", default="pwsh")
    parser.add_argument("--launch", action="store_true")
    args = parser.parse_args()
    cli = str(Path(args.engine) / "bin/managed/sbox-cli.exe")
    project = str(Path(args.project).resolve())
    launcher = str(Path(__file__).resolve().parents[1] / "Scripts/Connect-Sbox.ps1")
    owner = None
    client = None
    try:
        if args.launch:
            owner = Client([cli, "--project", project, "--startup-timeout", "60"], project)
            owner.initialize()
        client = Client([args.pwsh, "-NoProfile", "-File", launcher, "-EngineDirectory", args.engine], project)
        init = client.initialize()
        print(json.dumps(dict(handshake=init.get("_meta", {}))), flush=True)
        status = client.tool("editor_status")
        deadline = time.monotonic() + 90
        while status.get("IsCompiling", status.get("isCompiling", False)) and time.monotonic() < deadline:
            time.sleep(1)
            status = client.tool("editor_status")
        print(json.dumps(dict(editor_status=status)), flush=True)
        if status.get("IsCompiling", status.get("isCompiling", False)):
            raise TimeoutError("Native compilation did not settle within 90 seconds")
        success = status.get("LastCompileSucceeded", status.get("lastCompileSucceeded"))
        if success is not True:
            print(json.dumps(dict(compile_status=client.tool("compile_status"))), flush=True)
            raise RuntimeError("Native compilation was not successful")
        discovery = client.tool("search_tools", dict(query="x_"))
        names = {tool["name"] for tool in discovery["tools"] if tool["name"].startswith("x_")}
        expected = set()
        for source in Path(__file__).resolve().parents[1].joinpath("Editor").glob("*.cs"):
            expected.update(re.findall(r'\[McpTool(?:\.ReadOnly)?\(\s*"([^"]+)"', source.read_text(encoding="utf-8-sig")))
        if not expected or not expected.issubset(names):
            raise RuntimeError(f"Missing loaded tools: {sorted(expected - names)}")
        print(json.dumps(dict(extras=sorted(names), expected_count=len(expected))), flush=True)
        print(json.dumps(dict(x_editor_status=client.tool("x_editor_status"))), flush=True)
    finally:
        try:
            if client:
                client.close()
        finally:
            if owner:
                owner.close()
    print("PASS native CLI handshake, relay, compilation, complete discovery and protocol-clean EOF", flush=True)


if __name__ == "__main__":
    main()
