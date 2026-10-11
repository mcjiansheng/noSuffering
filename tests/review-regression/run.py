"""Run focused regressions in fresh native game/profile copies."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
from game_layout import resolve, sha256
from lab import executable, override_text


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def copy_link(src, dst):
    try:
        os.link(src, dst)
    except OSError:
        shutil.copy2(src, dst)
    return dst


def run(args):
    actual_platform = 'windows' if os.name == 'nt' else 'macos' if sys.platform == 'darwin' else ''
    if args.platform != actual_platform:
        raise ValueError('Run the native fixture on the matching OS.')
    label = args.platform + '-' + args.branch
    source_slot = ROOT / '.tools/game-lab' / label
    prepared = json.loads((source_slot / 'prepared.json').read_text())
    source = Path(prepared['game_dir'])
    source_assembly = Path(prepared['assembly_dir'])
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f')
    output = ROOT / 'artifacts/review-regression' / args.phase / (label + '-' + args.case + '-' + stamp)
    output.mkdir(parents=True)
    roles = ['host', 'client'] if args.case == 'network' else ['solo']
    data = Path(os.environ['APPDATA']) if args.platform == 'windows' else Path.home() / 'Library/Application Support'
    source_user = data / 'NoSufferingLab' / label
    product = ROOT / 'artifacts/review-regression' / args.phase / label / 'NoSuffering'
    fixture = ROOT / 'tests/review-regression/bin' / args.branch / 'Release/net9.0/ReviewRegression.dll'
    if not (product / 'NoSuffering.dll').is_file() or not fixture.is_file():
        raise ValueError('Build product snapshot and ReviewRegression for this target first.')
    session = {'phase': args.phase, 'case': args.case, 'platform': args.platform, 'branch': args.branch, 'peers': {}}
    for role in roles:
        game = ROOT / '.tools/game-lab/review' / args.platform / args.branch / stamp / role / 'game'
        shutil.copytree(source, game, copy_function=copy_link,
                        ignore=lambda directory, names: [n for n in names if n.lower() in ('mods', '.depotdownloader')])
        layout = resolve(game, game / source_assembly.relative_to(source), args.platform)
        native = executable(layout)
        if native.stat().st_nlink > 1:
            native.unlink()
            shutil.copy2(source / native.relative_to(game), native)
        if args.platform == 'macos':
            info = native.parent.parent / 'Info.plist'
            metadata = plistlib.loads(info.read_bytes())
            metadata['CFBundleIdentifier'] = f'com.megacrit.SlayTheSpire2.NoSufferingReview.{stamp}.{role}'
            info.unlink()
            info.write_bytes(plistlib.dumps(metadata))
        override = native.parent / 'override.cfg'
        override.unlink(missing_ok=True)
        suffix = f'NoSufferingLab/review/{label}/{stamp}/{role}'
        override.write_text(override_text(suffix))
        user = data / suffix
        if user.exists() or user.resolve() != user.absolute():
            raise ValueError('Review profile must be fresh and canonical.')
        user.mkdir(parents=True)
        shutil.copytree(source_user / 'default', user / 'default')
        account = user / 'default' / ('2' if role == 'client' else '1')
        if not account.exists():
            shutil.copytree(user / 'default/1', account)
        for settings in user.rglob('settings.save'):
            value = json.loads(settings.read_text(encoding='utf-8-sig'))
            value['mod_settings'] = dict(value.get('mod_settings') or {}, mods_enabled=True)
            value['seen_ea_disclaimer'] = True
            settings.write_text(json.dumps(value, ensure_ascii=False), encoding='utf-8')
        mods = Path(layout['mods_dir'])
        shutil.copytree(product, mods / 'NoSuffering')
        test_mod = mods / 'ReviewRegression'
        test_mod.mkdir()
        shutil.copy2(fixture, test_mod)
        shutil.copy2(ROOT / 'tests/review-regression/ReviewRegression.json', test_mod)
        command = [str(native), '--headless', '--max-fps', '60', '--force-steam=off']
        if args.case == 'network':
            command += ['--fastmp=host_standard' if role == 'host' else '--fastmp=join', '--clientId=1' if role == 'host' else '--clientId=2']
        command += ['--', '--ns-lab-probe', '--ns-review-probe', '--ns-review-case=' + args.case,
                    '--ns-review-role=' + role, '--ns-review-output=' + str(output)]
        if args.phase == 'fixed':
            command += ['--ns-review-fixed']
        session['peers'][role] = {'user_data': str(user), 'executable': str(native), 'command': command,
                                  'product_sha256': sha256(product / 'NoSuffering.dll'), 'fixture_sha256': sha256(fixture)}
    write_json(output / '.review-session.json', session)
    processes = {}
    streams = []
    def launch(role, resume=False):
        command = session['peers'][role]['command'][:]
        stage = role + ('-resume' if resume else '')
        if resume:
            command = ['--fastmp=load' if x == '--fastmp=host_standard' else x for x in command]
            command += ['--ns-review-network-stage=resume']
        command[1:1] = ['--log-file', str(output / (stage + '-engine.log'))]
        stream = (output / (stage + '-stdout.log')).open('w')
        streams.append(stream)
        process = subprocess.Popen(command, cwd=Path(command[0]).parent, stdout=stream, stderr=subprocess.STDOUT)
        processes[role] = process
        session['peers'][role]['pid' + ('_resume' if resume else '')] = process.pid
        return process
    def await_bound(process, stage):
        end = time.monotonic() + 60
        path = output / (stage + '-engine.log')
        while time.monotonic() < end:
            if path.exists() and 'REVIEW_LOOPBACK_BOUND' in path.read_text(errors='replace'):
                return
            if process.poll() is not None:
                raise RuntimeError('Host exited before loopback bind: ' + str(output))
            time.sleep(.1)
        raise TimeoutError('Host binding timed out: ' + str(output))
    try:
        for role in roles:
            process = launch(role)
            if role == 'host':
                await_bound(process, 'host')
        end = time.monotonic() + args.timeout
        restarted = False
        while any(p.poll() is None for p in processes.values()):
            if args.case == 'network' and not restarted and (output / 'network-restart-request').exists() and processes['host'].poll() is not None:
                if processes['host'].returncode != 0:
                    raise RuntimeError('First host failed')
                (output / 'report-host.json').rename(output / 'report-host-first.json')
                process = launch('host', resume=True)
                await_bound(process, 'host-resume')
                (output / 'network-host-restarted').write_text('ready')
                restarted = True
            if time.monotonic() >= end:
                raise TimeoutError('Review native processes timed out')
            time.sleep(.2)
        for role, process in processes.items():
            path = output / ('report-' + role + '.json')
            report = json.loads(path.read_text()) if path.exists() else None
            session['peers'][role]['exit_code'] = process.returncode
            session['peers'][role]['report'] = report
        session['passed'] = all(p['exit_code'] == 0 and p['report'] and p['report']['passed'] for p in session['peers'].values())
    except Exception as e:
        session['passed'] = False
        session['error'] = str(e)
    finally:
        for process in processes.values():
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
        for stream in streams:
            stream.close()
        write_json(output / 'run-record.json', session)
    print(json.dumps({'record': str(output / 'run-record.json'), 'passed': session.get('passed'), 'error': session.get('error')}), flush=True)
    return 0 if session.get('passed') else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--platform', choices=['macos', 'windows'], required=True)
    parser.add_argument('--branch', choices=['public-beta', 'public'], default='public-beta')
    parser.add_argument('--case', choices=['storage', 'config-ui', 'network'], required=True)
    parser.add_argument('--phase', choices=['baseline', 'fixed'], required=True)
    parser.add_argument('--timeout', type=int, default=300)
    raise SystemExit(run(parser.parse_args()))
