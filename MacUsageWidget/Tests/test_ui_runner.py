import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import signal
import sys
import tempfile
import unittest


spec = importlib.util.spec_from_file_location('ui_runner', Path(__file__).with_name('run-ui-verification.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class UIRunnerTests(unittest.TestCase):
    def invoke(self, code, timeout=3):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / 'result'
            code = 'import json, os, pathlib, signal, sys, time\nout = pathlib.Path(sys.argv[1])\n' + code
            with contextlib.redirect_stdout(io.StringIO()):
                status = runner.run([sys.executable, '-c', code, str(output)], output, timeout)
            return status, json.loads((output / 'process.json').read_text())

    def test_success_requires_clean_exit_and_passing_result(self):
        status, result = self.invoke('(out / "smoke.json").write_text(json.dumps({"passed": True}))')
        self.assertEqual(status, 0)
        self.assertEqual(result['returnCode'], 0)
        self.assertTrue(result['passed'])

    @unittest.skipUnless(os.name == 'posix', 'POSIX signal return codes')
    def test_sigill_is_primary_even_without_smoke(self):
        status, result = self.invoke('(out / "progress.json").write_text(json.dumps({"phase": "hidden"}))\nos.kill(os.getpid(), signal.SIGILL)')
        self.assertEqual(status, 1)
        self.assertEqual(result['returnCode'], -signal.SIGILL)
        self.assertEqual(result['signal'], 'SIGILL')
        self.assertEqual(result['lastProgress']['phase'], 'hidden')
        self.assertIn('exited with', result['error'])

    def test_result_does_not_hide_later_process_failure(self):
        status, result = self.invoke('(out / "smoke.json").write_text(json.dumps({"passed": True}))\nsys.exit(7)')
        self.assertEqual(status, 1)
        self.assertEqual(result['returnCode'], 7)

    def test_missing_result_has_clear_error(self):
        status, result = self.invoke('pass')
        self.assertEqual(status, 1)
        self.assertIn('without smoke.json', result['error'])

    def test_failed_assertion_and_invalid_json_fail(self):
        for value in ['{"passed":false}', 'invalid', '[]', '{"passed":1}']:
            with self.subTest(value=value):
                status, _ = self.invoke(f'(out / "smoke.json").write_text({value!r})')
                self.assertEqual(status, 1)

    def test_timeout_cleans_up_child_even_after_result(self):
        status, result = self.invoke('(out / "smoke.json").write_text(json.dumps({"passed": True}))\ntime.sleep(60)', timeout=0.3)
        self.assertEqual(status, 1)
        self.assertTrue(result['timedOut'])
        self.assertIsNotNone(result['returnCode'])

    def test_stale_result_is_not_accepted(self):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder)
            (output / 'smoke.json').write_text('{"passed":true}')
            with self.assertRaisesRegex(ValueError, 'fresh output'):
                runner.run([sys.executable, '-c', 'pass'], output)

    def test_launch_failure_is_reported(self):
        with tempfile.TemporaryDirectory() as folder, contextlib.redirect_stdout(io.StringIO()):
            output = Path(folder)
            self.assertEqual(runner.run([str(output / 'absent')], output), 1)
            self.assertIn('Cannot launch', json.loads((output / 'process.json').read_text())['error'])

    def test_crash_collection_filters_pid_and_time(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            output = root / 'output'
            output.mkdir()
            (root / 'AIUsageWidget-current.ips').write_text('{"pid":1234}')
            (root / 'AIUsageWidget-other.ips').write_text('{"pid":12345}')
            old = root / 'AIUsageWidget-old.crash'
            old.write_text('Process: AIUsageWidget [1234]')
            os.utime(old, (0, 0))
            self.assertEqual(runner.collect_crashes(root, output, 1234, 10), ['AIUsageWidget-current.ips'])


if __name__ == '__main__':
    unittest.main()
