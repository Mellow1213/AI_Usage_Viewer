#!/usr/bin/env python3
"""Measure the app's whole macOS process coalition using isolated fixture data."""
import argparse
import ctypes
import json
import os
from pathlib import Path
import subprocess
import time


# Read-only instrumentation, not shipped in the app. XNU's proc_info_private.h
# defines PROC_PIDCOALITIONINFO (20) as two coalition IDs and three reserved words.
LIBPROC = ctypes.CDLL('/usr/lib/libproc.dylib', use_errno=True)
LIBPROC.proc_pidinfo.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_int]


def coalition(pid):
    values = (ctypes.c_uint64 * 5)()
    count = LIBPROC.proc_pidinfo(pid, 20, 0, values, ctypes.sizeof(values))
    return values[0] if count == ctypes.sizeof(values) else None


def members(pid):
    identity = coalition(pid)
    if identity is None:
        raise RuntimeError('Cannot read the launched app process coalition')
    rows = subprocess.check_output(['ps', '-axo', 'pid=,comm='], text=True).splitlines()
    owned = []
    for row in rows:
        process_id, command = row.strip().split(maxsplit=1)
        if coalition(int(process_id)) == identity:
            owned.append((int(process_id), command))
    if identity == coalition(os.getpid()) or not any(member == pid and command.endswith('/AIUsageWidget') for member, command in owned):
        raise RuntimeError(f'The fixture app does not have its own process coalition: {owned}')
    return owned


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('app', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    app, output = args.app.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    log = (output / 'launch.log').open('w')
    launcher = subprocess.Popen(['open', '-n', '-W', str(app), '--args', '--fixture',
        '--data-dir', str(output / 'data'), '--memory-benchmark', str(output)], stdout=log, stderr=log)
    samples, phase = [], None
    started = time.monotonic()
    phase_file, result_file = output / 'phase.json', output / 'memory-benchmark.json'
    try:
        while time.monotonic() - started < 180:
            if result_file.exists():
                break
            if phase_file.exists():
                current = json.loads(phase_file.read_text())
                phase = current['phase']
                owned = members(current['pid'])
                sample_file = output / f'sample-{len(samples):03}.json'
                command = ['/usr/bin/footprint', '--noCategories', '-j', str(sample_file)]
                for pid, _ in owned:
                    command += ['-p', str(pid)]
                subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, check=True)
                sample = json.loads(sample_file.read_text())
                if sample.get('errors'):
                    raise RuntimeError(f'Memory sample failed: {sample["errors"]}')
                # Recheck the phase after sampling so transitions do not mix modes.
                if phase_file.exists() and json.loads(phase_file.read_text())['phase'] == phase:
                    samples.append({'phase': phase, 'elapsedSeconds': time.monotonic() - started,
                        'webLoaded': current['webLoaded'], 'footprintBytes': sample['total footprint'],
                        'processes': sample['processes']})
            elif launcher.poll() is not None:
                raise RuntimeError('The app exited before starting its benchmark')
            time.sleep(1)
        if not result_file.exists():
            raise TimeoutError('The fixture memory benchmark did not finish')
        result = json.loads(result_file.read_text())
        summaries = {}
        for name in dict.fromkeys(item['phase'] for item in samples):
            selected = [item for item in samples if item['phase'] == name]
            summaries[name] = {'samples': len(selected),
                'meanMiB': sum(item['footprintBytes'] for item in selected) / len(selected) / 1048576,
                'processCounts': sorted(set(len(item['processes']) for item in selected)),
                'webLoaded': selected[0]['webLoaded']}
        report = {'app': str(app), 'unit': 'bytes', 'metric': 'physical footprint of app resource coalition',
            'fixture': True, 'result': result, 'phases': summaries, 'samples': samples}
        (output / 'measurement.json').write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps({'phases': summaries, 'result': result}, indent=2), flush=True)
        if not result['passed']:
            raise RuntimeError('The app failed its reopen checks')
        launcher.wait(timeout=10)
    finally:
        # The child app quits itself. Only stop the owned launcher on failure;
        # never terminate other AI Usage instances or a shared CLI.
        if launcher.poll() is None:
            launcher.terminate()
        log.close()


if __name__ == '__main__':
    main()
