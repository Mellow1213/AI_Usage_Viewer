#!/usr/bin/env python3
"""Run the fixture app to completion and preserve the primary failure."""
import argparse
import json
import platform
from pathlib import Path
import re
import shutil
import signal
import subprocess
import tempfile
import time


def collect_crashes(directory, output, pid, started):
    """Copy only reports for this invocation, never unrelated user crashes."""
    copied = []
    if directory is None or not directory.exists():
        return copied
    for source in directory.iterdir():
        if not source.name.startswith('AIUsageWidget') or source.suffix not in {'.ips', '.crash'}:
            continue
        try:
            if source.stat().st_mtime < started - 1:
                continue
            text = source.read_text(errors='replace')
            if not (re.search(r'"pid"\s*:\s*' + str(pid) + r'\b', text)
                    or re.search(r'^Process:.*\[' + str(pid) + r'\]', text, re.MULTILINE)):
                continue
            shutil.copy2(source, output / source.name)
            copied.append(source.name)
        except OSError:
            continue  # CrashReporter may still be writing the file.
    return copied


def run(command, output, timeout=180, crash_directory=None):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    for name in ('smoke.json', 'progress.json', 'console.log', 'process.json'):
        if (output / name).exists():
            raise ValueError(f'Use a fresh output directory; {name} already exists')
    started_wall, started = time.time(), time.monotonic()
    report = {'passed': False, 'system': platform.platform(), 'architecture': platform.machine(),
              'timedOut': False}
    with (output / 'console.log').open('w') as log:
        try:
            app = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
        except OSError as error:
            report['error'] = f'Cannot launch fixture app: {error}'
        else:
            report['pid'] = app.pid
            try:
                app.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                report['timedOut'] = True
                app.terminate()
                try:
                    app.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    app.kill()
                    app.wait()
            finally:
                # Only this owned child is terminated, including on interruption.
                if app.poll() is None:
                    app.kill()
                    app.wait()
            report['returnCode'] = app.returncode
            if app.returncode < 0:
                report['signal'] = signal.Signals(-app.returncode).name
            if report['timedOut']:
                report['error'] = f'Fixture app exceeded {timeout:g}s'
            elif app.returncode != 0:
                report['error'] = f'Fixture app exited with {app.returncode}'
            if app.returncode != 0 and crash_directory is not None:
                for _ in range(6):
                    reports = collect_crashes(Path(crash_directory), output, app.pid, started_wall)
                    if reports:
                        report['crashReports'] = reports
                        break
                    time.sleep(1)

    # Missing/invalid JSON is secondary when the process already failed.
    for name, key in [('progress.json', 'lastProgress'), ('smoke.json', 'smoke')]:
        path = output / name
        if path.exists():
            try:
                report[key] = json.loads(path.read_text())
            except (OSError, ValueError) as error:
                report.setdefault('error', f'Invalid {name}: {error}')
        elif key == 'smoke':
            report.setdefault('error', 'Fixture app exited without smoke.json')
    smoke = report.get('smoke')
    if not isinstance(smoke, dict) or smoke.get('passed') is not True:
        report.setdefault('error', 'WebKit fixture assertions failed')
    report['passed'] = 'error' not in report
    report['elapsedSeconds'] = time.monotonic() - started
    (output / 'process.json').write_text(json.dumps(report, indent=2) + '\n')
    print((output / 'console.log').read_text(errors='replace'), end='', flush=True)
    print(json.dumps(report, indent=2), flush=True)
    return 0 if report['passed'] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('app', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--timeout', type=float, default=180)
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error('--timeout must be positive')
    app, output = args.app.resolve(), args.output.resolve()
    crashes = Path.home() / 'Library/Logs/DiagnosticReports' if platform.system() == 'Darwin' else None
    with tempfile.TemporaryDirectory(prefix='aiusage-ui-fixture-') as data:
        return run([str(app / 'Contents/MacOS/AIUsageWidget'), '--fixture', '--data-dir', data,
                    '--smoke-test', str(output), '--verify-memory'], output, args.timeout, crashes)


if __name__ == '__main__':
    raise SystemExit(main())
