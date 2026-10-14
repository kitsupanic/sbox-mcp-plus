"""Regression: an earlier successful build cannot certify an ongoing compile."""
import contextlib
import importlib.util
import io
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("cli_smoke", Path(__file__).with_name("Test-CliTransport.py"))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class PendingCompileClient:
    closed = False

    def __init__(self, command, cwd):
        pass

    def initialize(self):
        return {}

    def tool(self, name, arguments=None):
        if name != "editor_status":
            raise AssertionError("A pending compile must fail before discovery")
        return {"IsCompiling": True, "LastCompileSucceeded": True}

    def close(self):
        self.closed = True


class CompileEvidenceTests(unittest.TestCase):
    def test_previous_success_does_not_pass_pending_compile(self):
        client = PendingCompileClient([], ".")
        with patch.object(smoke, "Client", return_value=client), \
             patch.object(smoke.time, "monotonic", side_effect=[0.0, 91.0]), \
             patch("sys.argv", ["smoke", "--engine", "fixture-engine"]), \
             contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(TimeoutError, "compilation did not settle"):
                smoke.main()
        self.assertTrue(client.closed)


if __name__ == "__main__":
    unittest.main()
