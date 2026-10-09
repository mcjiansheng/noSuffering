#!/usr/bin/env python3
"""Run two actual native offline ENet processes in separate copied lab slots."""
import argparse
import datetime
import json
import os
import plistlib
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
from game_layout import resolve, sha256
from lab import executable, override_text

ROOT = Path(__file__).resolve().parent.parent


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def copy_game(source, destination):
    # Hard links copy the large immutable native resources cheaply. Every file
    # changed below is unlinked first; the original game and live UI lab retain
    # their own override, mods and settings files.
    def copy_file(src, dst):
        try:
            os.link(src, dst)
        except OSError:
            shutil.copy2(src, dst)
        return dst
    shutil.copytree(source, destination, copy_function=copy_file, symlinks=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--platform', choices=('macos', 'windows'), required=True)
    parser.add_argument('--branch', choices=('public-beta', 'public'), default='public-beta')
    parser.add_argument('--headless', action='store_true')
    parser.add_argument('--seed-reload', action='store_true', help='Exit both peers, continue the saved run in fresh processes, and verify the first reroll seed advances.')
    parser.add_argument('--timeout', type=int, default=600)
    args = parser.parse_args()
    host = 'windows' if os.name == 'nt' else 'macos' if sys.platform == 'darwin' else None
    if args.platform != host:
        raise ValueError('Actual native multiplayer probe must run on its matching host OS.')
    base = ROOT / '.tools' / 'game-lab' / f'{args.platform}-{args.branch}'
    prepared = json.loads((base / 'prepared.json').read_text(encoding='utf-8'))
    source = Path(prepared['game_dir'])
    source_assembly = Path(prepared['assembly_dir'])
    if source.resolve() != source.absolute() or not source_assembly.is_relative_to(source):
        raise ValueError('Unsafe prepared game paths.')
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
    output = ROOT / 'artifacts' / args.platform / args.branch / f'mp-{stamp}'
    if output.resolve() != output.absolute():
        raise ValueError("Artifact root must have a canonical project path.")
    output.mkdir(parents=True, exist_ok=False)
    write_json(output / ".mp-probe-session.json", {"platform": args.platform, "branch": args.branch, "stamp": stamp})
    slots = ROOT / '.tools' / 'game-lab' / 'mp' / args.platform / args.branch / stamp
    data = Path(os.environ['APPDATA']) if args.platform == 'windows' else Path.home() / 'Library' / 'Application Support'
    expected_suffix = f'NoSufferingLab/{args.platform}-{args.branch}'
    if prepared['user_dir_suffix'] != expected_suffix:
        raise ValueError('Source user profile is outside the exact existing lab hierarchy.')
    original_user = data / expected_suffix
    if original_user.resolve() != original_user.absolute():
        raise ValueError('Source lab user directory contains a symlink.')
    stage = ROOT / 'artifacts' / args.platform / args.branch / 'NoSuffering'
    if not (stage / 'NoSuffering.dll').is_file():
        raise ValueError('Build the current native branch before the multiplayer probe.')
    processes = []
    streams = []
    record = {'scope': 'two actual native localhost ENet processes; direct fixture entries; no Steam multiplayer claim',
              'platform': args.platform, 'branch': args.branch, 'multiplayer': 'running', 'peers': {}}
    try:
        for role in ('host', 'client'):
            game = slots / role / 'game'
            copy_game(source, game)
            layout = resolve(game, game / source_assembly.relative_to(source), args.platform)
            native = executable(layout)
            # macOS may report a different pathname for an executable sharing a
            # vnode with another hard link, confusing native mod/user isolation.
            # Give each role its own unchanged executable bytes and identity.
            if native.stat().st_nlink > 1:
                original_native = source / native.relative_to(game)
                native.unlink()
                shutil.copy2(original_native, native)
            if args.platform == 'macos':
                info = native.parent.parent / 'Info.plist'
                metadata = plistlib.loads(info.read_bytes())
                metadata['CFBundleIdentifier'] = f'com.megacrit.SlayTheSpire2.NoSufferingLab.{stamp}.{role}'
                info.unlink()  # Break immutable-resource hard link before editing.
                info.write_bytes(plistlib.dumps(metadata))
            override = native.parent / 'override.cfg'
            override.unlink(missing_ok=True)
            suffix = f'NoSufferingLab/mp/{args.platform}-{args.branch}/{stamp}/{role}'
            override.write_text(override_text(suffix), encoding='utf-8')
            user = data / suffix
            if user.resolve() != user.absolute():
                raise ValueError("MP user directory contains a symlink.")
            if user.exists():
                raise ValueError('Refusing to reuse an existing multiplayer user directory.')
            shutil.copytree(original_user, user)
            identity = '1' if role == 'host' else '2'
            account = user / 'default' / identity
            template = user / 'default' / '1'
            if not account.exists():
                shutil.copytree(template, account)
            for settings in user.rglob('settings.save'):
                value = json.loads(settings.read_text(encoding='utf-8-sig'))
                value['mod_settings'] = dict(value.get('mod_settings') or {}, mods_enabled=True)
                value['seen_ea_disclaimer'] = True
                settings.write_text(json.dumps(value, ensure_ascii=False), encoding='utf-8')
            mod = Path(layout['mods_dir']) / 'NoSuffering'
            if mod.exists():
                shutil.rmtree(mod)
            shutil.copytree(stage, mod)
            command = [str(native), '--log-file', str(output / f'{role}-engine.log'), '--max-fps', '30', '--force-steam=off',
                       '--fastmp=host_standard' if role == 'host' else '--fastmp=join', '--clientId=1' if role == 'host' else '--clientId=2']
            if args.headless:
                command.append('--headless')
            command.extend(['--', '--ns-lab-probe', '--ns-mp-probe', f'--ns-mp-role={role}', f'--ns-mp-root={output}'])
            if args.seed_reload:
                command.append('--ns-mp-seed-stage=first')
            record['peers'][role] = {'command': command, 'user_data': str(user), 'game': str(game), 'mod_sha256': sha256(mod / 'NoSuffering.dll'), 'native_sha256': sha256(native)}
            stream = (output / f'{role}-stdout.log').open('w', encoding='utf-8')
            streams.append(stream)
            process = subprocess.Popen(command, cwd=native.parent, stdout=stream, stderr=subprocess.STDOUT)
            processes.append((role, process))
            if role == 'host':
                # Poll actual native log evidence, never open the client against a
                # presumed host or change the host firewall/listener policy.
                until = time.monotonic() + 60
                while time.monotonic() < until:
                    log = output / 'host-engine.log'
                    text = log.read_text(encoding='utf-8', errors='replace') if log.exists() else ''
                    if 'MP_PROBE_BIND 127.0.0.1' in text:
                        break
                    if process.poll() is not None:
                        raise ValueError('Host exited before the loopback listener started.')
                    time.sleep(0.2)
                else:
                    raise ValueError('Host did not prove loopback-only native binding; client was not launched.')
        until = time.monotonic() + args.timeout
        while any(process.poll() is None for _, process in processes):
            if time.monotonic() >= until:
                raise ValueError('Native two-peer probe timed out.')
            time.sleep(0.5)
        passed = True
        for role, process in processes:
            report = output / f'report-{role}.json'
            results = json.loads(report.read_text(encoding='utf-8')) if report.is_file() else None
            record['peers'][role].update(exit_code=process.returncode, report=results)
            logs = '\n'.join((output / f'{role}-{kind}.log').read_text(encoding='utf-8', errors='replace') for kind in ('stdout', 'engine'))
            paths = re.findall(r'LAB user_data=([^\r\n]+)', logs)
            isolated = bool(paths) and all(p.strip().replace('\\', '/') == record['peers'][role]['user_data'].replace('\\', '/') for p in paths)
            record['peers'][role]['isolated_user_data'] = isolated
            passed = passed and bool(results) and isolated and process.returncode == 0 and all(v.startswith('PASS') for v in results['results'].values())
        if passed and args.seed_reload:
            # Preserve first-lifetime evidence, then launch the exact same isolated
            # profiles/apps through native saved-run host/load and client/join.
            for stream in streams:
                stream.close()
            streams.clear()
            processes.clear()
            for role in ('host', 'client'):
                peer = record['peers'][role]
                peer['first_lifetime'] = {k: peer[k] for k in ('exit_code', 'report', 'isolated_user_data')}
                for kind in ('stdout', 'engine'):
                    (output / f'{role}-{kind}.log').rename(output / f'{role}-{kind}-first.log')
                (output / f'report-{role}.json').rename(output / f'report-{role}-first.json')
                command = [('--fastmp=load' if role == 'host' else value) if value.startswith('--fastmp=') else
                           '--ns-mp-seed-stage=resume' if value == '--ns-mp-seed-stage=first' else value for value in peer['command']]
                peer['resume_command'] = command
                stream = (output / f'{role}-stdout.log').open('w', encoding='utf-8')
                streams.append(stream)
                process = subprocess.Popen(command, cwd=Path(command[0]).parent, stdout=stream, stderr=subprocess.STDOUT)
                processes.append((role, process))
                if role == 'host':
                    until = time.monotonic() + 60
                    while time.monotonic() < until:
                        log = output / 'host-engine.log'
                        text = log.read_text(encoding='utf-8', errors='replace') if log.exists() else ''
                        if 'MP_PROBE_BIND 127.0.0.1' in text:
                            break
                        if process.poll() is not None:
                            raise ValueError('Resumed host exited before loopback binding.')
                        time.sleep(0.2)
                    else:
                        raise ValueError('Resumed host did not prove loopback-only binding.')
            until = time.monotonic() + args.timeout
            while any(process.poll() is None for _, process in processes):
                if time.monotonic() >= until:
                    raise ValueError('Fresh-process native continuation timed out.')
                time.sleep(0.5)
            for role, process in processes:
                peer = record['peers'][role]
                report = output / f'report-{role}.json'
                result = json.loads(report.read_text(encoding='utf-8')) if report.is_file() else None
                logs = '\n'.join((output / f'{role}-{kind}.log').read_text(encoding='utf-8', errors='replace') for kind in ('stdout', 'engine'))
                paths = re.findall(r'LAB user_data=([^\r\n]+)', logs)
                isolated = bool(paths) and all(p.strip().replace('\\', '/') == peer['user_data'].replace('\\', '/') for p in paths)
                peer.update(exit_code=process.returncode, report=result, isolated_user_data=isolated)
                passed = passed and bool(result) and isolated and process.returncode == 0 and all(v.startswith('PASS') for v in result['results'].values())
        record['multiplayer'] = 'native_enet_smoke_passed' if passed else 'native_enet_smoke_failed'
    except Exception as error:
        record.update(multiplayer='native_enet_smoke_incomplete', error=str(error))
    finally:
        for _, process in processes:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
        for stream in streams:
            stream.close()
        write_json(output / 'run-record.json', record)
    print(json.dumps({'record': str(output / 'run-record.json'), 'multiplayer': record['multiplayer']}, indent=2))
    return 0 if record['multiplayer'] == 'native_enet_smoke_passed' else 1


if __name__ == '__main__':
    sys.exit(main())
