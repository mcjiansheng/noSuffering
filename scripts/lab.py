#!/usr/bin/env python3
"""Prepare and launch an isolated copy of the actual Windows or macOS game."""
import argparse
import datetime
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile

from build import ensure_game_closed
from game_layout import resolve, sha256

ROOT = Path(__file__).resolve().parent.parent
LAB = ROOT / '.tools' / 'game-lab'


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def slot(platform, branch):
    if not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9._-]*', branch):
        raise ValueError('Branch must be a simple label, for example public-beta or public.')
    path = LAB / f'{platform}-{branch}'
    if path.resolve() != path.absolute():
        raise ValueError('Refusing a lab directory reached through a symlink.')
    return path


def executable(layout):
    if layout['platform'] == 'macos':
        app = next(p for p in Path(layout['mods_dir']).parents if p.suffix == '.app')
        with (app / 'Contents' / 'Info.plist').open('rb') as stream:
            name = plistlib.load(stream)['CFBundleExecutable']
        if Path(name).name != name:
            raise ValueError('Invalid CFBundleExecutable in game Info.plist.')
        result = app / 'Contents' / 'MacOS' / name
    else:
        # The actual game export embeds its PCK in this executable.
        result = Path(layout['game_dir']) / 'SlayTheSpire2.exe'
    if not result.is_file() or result.is_symlink():
        raise ValueError(f'Missing native game executable or executable is a symlink: {result}')
    return result


def override_text(suffix):
    return ('[application]\nconfig/use_custom_user_dir=true\n'
            f'config/custom_user_dir_name="{suffix}"\n')


def confined(layout, native, game):
    if game.resolve() != game.absolute():
        raise ValueError('Refusing a lab game directory reached through a symlink.')
    for path in (native, Path(layout['assembly_dir']), Path(layout['mods_dir'])):
        if not path.resolve().is_relative_to(game):
            raise ValueError(f'Lab game path escapes its independent directory: {path}')


def enable_mods(platform, suffix, output):
    if platform == 'windows':
        appdata = os.environ.get('APPDATA')
        if not appdata:
            raise ValueError('APPDATA is unavailable; cannot identify the native isolated settings directory.')
        data = Path(appdata).resolve()
    else:
        data = (Path.home() / 'Library' / 'Application Support').resolve()
    settings = data / suffix / 'default' / '1' / 'settings.save'
    if settings.resolve() != settings.absolute():
        raise ValueError('Refusing lab settings reached through a symlink.')
    if not settings.is_file():
        raise ValueError('Native lab settings do not exist. First run without --enable-mods to let the game '
                         'create settings; that bootstrap has no mod-load success. Then rerun with --enable-mods.')
    original = settings.read_bytes()
    value = json.loads(original.decode('utf-8-sig'))
    if not isinstance(value, dict) or 'mod_settings' not in value:
        raise ValueError('Native settings JSON has no mod_settings field; refusing to guess its schema.')
    mod_settings = value['mod_settings']
    if mod_settings is None:
        value['mod_settings'] = {'mods_enabled': True, 'mod_list': []}
    elif isinstance(mod_settings, dict):
        mod_settings['mods_enabled'] = True
    else:
        raise ValueError('Native mod_settings is neither an object nor null.')
    backup = output / 'settings-before-enable-mods.save'
    backup.write_bytes(original)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=settings.parent,
                                         prefix='settings.save.lab-', delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(value, stream, ensure_ascii=False, indent=2)
            stream.write('\n')
        if settings.read_bytes() != original:
            raise ValueError('Native lab settings changed during the update; refusing replacement.')
        temporary.replace(settings)
    finally:
        if temporary:
            temporary.unlink(missing_ok=True)
    return {'settings': str(settings), 'settings_backup': str(backup)}


def prepare(args):
    source = resolve(args.game_dir, args.assembly_dir, args.platform)
    base = slot(source['platform'], args.branch)
    game = base / 'game'
    marker = base / 'prepared.json'
    if marker.exists():
        raise ValueError(f'Lab is already prepared: {base}; use run or a different branch label.')
    source_path = Path(source['game_dir'])
    source_assembly = Path(source['assembly_dir'])
    if not source_assembly.is_relative_to(source_path):
        raise ValueError('Selected assembly directory must be inside the source game directory.')
    assembly_relative = source_assembly.relative_to(source_path)
    destination_assembly = game / assembly_relative
    if args.in_place:
        if source_path != game.resolve() or not game.is_dir():
            raise ValueError('--in-place requires a clean independent download at the managed lab game directory.')
        if any(p.is_dir() and p.name.lower() == 'mods' and any(p.iterdir()) for p in game.rglob('*')):
            raise ValueError('Independent download contains existing mods; prepare a clean game download.')
    else:
        if game.exists():
            raise ValueError(f'Destination already exists: {game}; do not overwrite a lab or incomplete download.')
        if game.resolve().is_relative_to(source_path):
            raise ValueError('Lab destination must not be inside the source game.')
        def ignore(directory, names):
            return [name for name in names if name.lower() in
                    ('mods', 'steamapps', 'userdata', 'appcache', 'shadercache', '.depotdownloader',
                     'override.cfg', 'steam_appid.txt')]
        # Dereference copies so lab mutations cannot follow links back to the installation.
        if source_path.suffix == '.app':
            game.mkdir(parents=True)
            shutil.copytree(source_path, game / source_path.name, ignore=ignore)
            destination_assembly = game / source_path.name / assembly_relative
        else:
            shutil.copytree(source_path, game, ignore=ignore)
    layout = resolve(game, destination_assembly, source['platform'])
    native = executable(layout)
    confined(layout, native, game)
    if layout['platform'] == 'macos':
        # Steam depot downloads do not restore the executable mode on this Mac.
        native.chmod(native.stat().st_mode | 0o111)
    suffix = f"NoSufferingLab/{source['platform']}-{args.branch}"
    override = native.parent / 'override.cfg'
    if override.exists() or override.is_symlink():
        raise ValueError(f'Refusing to replace existing settings: {override}')
    override.write_text(override_text(suffix), encoding='utf-8')
    record = dict(layout, branch=args.branch, executable=str(native), executable_sha256=sha256(native),
                  user_dir_suffix=suffix, override=str(override), source=str(source_path),
                  prepared_at=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  load='not_executed', singleplayer='not_executed', multiplayer='not_executed')
    write_json(marker, record)
    print(json.dumps({'game_dir': str(game), 'mods_dir': layout['mods_dir'], 'record': str(marker)}, indent=2))


def run(args):
    if not args.platform:
        raise ValueError('run requires --platform windows or macos.')
    host = 'windows' if os.name == 'nt' else 'macos' if sys.platform == 'darwin' else None
    if args.platform != host:
        raise ValueError(f'Cannot launch {args.platform} native game on this host.')
    base = slot(args.platform, args.branch)
    record = json.loads((base / 'prepared.json').read_text(encoding='utf-8'))
    game = base / 'game'
    layout = resolve(game, record['assembly_dir'], args.platform)
    native = executable(layout)
    confined(layout, native, game)
    suffix = f'NoSufferingLab/{args.platform}-{args.branch}'
    if (record['executable'] != str(native) or record['assemblies'] != layout['assemblies'] or
            record['executable_sha256'] != sha256(native) or record['user_dir_suffix'] != suffix):
        raise ValueError('Prepared game identity changed; use a fresh lab slot.')
    override = native.parent / 'override.cfg'
    if override.is_symlink() or override.read_text(encoding='utf-8') != override_text(suffix):
        raise ValueError('Lab user-data override changed; refusing to launch.')
    mods = Path(layout['mods_dir'])
    if mods.is_symlink() or any(p.is_symlink() for p in mods.rglob('*')):
        raise ValueError('Refusing to launch with linked mods.')
    if not (mods / 'NoSuffering' / 'NoSuffering.dll').is_file():
        raise ValueError('Build and deploy NoSuffering into this lab before running the load probe.')
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    output = ROOT / 'artifacts' / args.platform / args.branch / ('lab-' + stamp)
    output.mkdir(parents=True)
    settings_update = {}
    if args.enable_mods:
        ensure_game_closed(layout)
        settings_update = enable_mods(args.platform, suffix, output)
    command = [str(native), '--log-file', str(output / 'engine.log'), '--max-fps', '30']
    if args.headless:
        command.append('--headless')
    if args.quit_after:
        command.extend(['--quit-after', str(args.quit_after)])
    # Native CommandLineHelper reads GetCmdlineArgs; our probe reads GetCmdlineUserArgs.
    command.extend(['--force-steam=off', '--', '--ns-lab-probe'])
    if args.gameplay_probe:
        command.append('--ns-gameplay-probe')
    if args.continue_probe:
        command.append('--ns-gameplay-continue-probe')
    if args.neow_probe:
        command.append('--ns-neow-probe')
    if args.rollback_probe:
        command.append('--ns-rollback-probe')
    if args.expansion_probe:
        command.append('--ns-expansion-probe')
    result = dict(record, command=command, load='running', singleplayer='not_executed',
                  multiplayer='not_executed', mod_sha256=sha256(mods / 'NoSuffering' / 'NoSuffering.dll'),
                  **settings_update)
    write_json(output / 'run-record.json', result)
    try:
        with (output / 'stdout.log').open('w', encoding='utf-8') as stream:
            completed = subprocess.run(command, cwd=native.parent, stdout=stream, stderr=subprocess.STDOUT)
        logs = '\n'.join(p.read_text(encoding='utf-8', errors='replace') for p in output.glob('*.log'))
        paths = re.findall(r'LAB user_data=([^\r\n]+)', logs)
        verified = bool(paths) and all(p.strip().replace('\\', '/').rstrip('/').endswith('/' + suffix) for p in paths)
        initialized = bool(re.search(r'\[NoSuffering\] \S+ initialized; game=', logs))
        offline = 'Steam initialization skipped' in logs
        if args.gameplay_probe or args.continue_probe or args.neow_probe or args.rollback_probe or args.expansion_probe:
            marker = 'EXPANSION_PROBE_RESULTS' if args.expansion_probe else 'ROLLBACK_PROBE_RESULTS' if args.rollback_probe else 'NEOW_PROBE_RESULTS' if args.neow_probe else 'GAMEPLAY_PROBE_RESULTS' if args.gameplay_probe else 'CONTINUE_PROBE_RESULTS'
            reports = re.findall(r'\[NoSuffering\] ' + marker + r' (\{[^\r\n]+\})', logs)
            if reports:
                report = json.loads(reports[-1])
                checks = report['results']
                passed = bool(checks) and all(value.startswith('PASS') for value in checks.values())
                result.update(singleplayer='smoke_passed' if passed else 'smoke_failed',
                              singleplayer_scope=report['scope'], singleplayer_checks=checks, acceptance='not_executed')
                if args.neow_probe or args.rollback_probe or args.expansion_probe:
                    result['singleplayer_state'] = report['state']
            else:
                result.update(singleplayer='smoke_incomplete', acceptance='not_executed')
        normal_exit = completed.returncode == 0 or ((args.gameplay_probe or args.continue_probe or args.neow_probe or args.rollback_probe or args.expansion_probe) and bool(reports))
        result.update(exit_code=completed.returncode, observed_user_data=paths,
                      isolated_user_data='passed' if verified else 'failed',
                      offline='passed' if offline else 'unverified',
                      load='passed' if verified and initialized and offline and normal_exit else 'failed')
    except (OSError, KeyboardInterrupt) as error:
        result.update(load='failed', error=str(error))
        raise
    finally:
        write_json(output / 'run-record.json', result)
    print(json.dumps({'record': str(output / 'run-record.json'), 'load': result['load']}, indent=2))
    if result.get('singleplayer') in ('smoke_failed', 'smoke_incomplete'):
        raise ValueError('Native singleplayer smoke failed or did not complete; inspect recorded results.')
    if result['load'] != 'passed':
        raise ValueError('Lab load probe failed; inspect recorded engine/stdout logs. Gameplay remains unexecuted.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('prepare', 'run'))
    parser.add_argument('--game-dir', default=os.environ.get('STS2_INSTALL_DIR'))
    parser.add_argument('--assembly-dir', help='Actual architecture assembly directory when the native game contains more than one.')
    parser.add_argument('--platform', choices=('windows', 'macos'))
    parser.add_argument('--branch', default='public-beta')
    parser.add_argument('--in-place', action='store_true', help='Prepare an independent download already in the managed lab slot.')
    parser.add_argument('--headless', action='store_true')
    parser.add_argument('--enable-mods', action='store_true', help='Enable mods only in existing native lab settings, with backup.')
    parser.add_argument('--continue-probe', action='store_true', help='Verify native disk continuation in a fresh process.')
    parser.add_argument('--gameplay-probe', action='store_true', help='Run the opt-in native gameplay diagnostic; does not imply acceptance.')
    parser.add_argument('--neow-probe', action='store_true', help='Verify act-one Neow reward reroll, claim and native Proceed button.')
    parser.add_argument('--expansion-probe', action='store_true', help='Verify personal rerolls, native shop relic behavior and act-three boss HP.')
    parser.add_argument('--rollback-probe', action='store_true', help='Verify native map rollback input and independent settings.')
    parser.add_argument('--quit-after', type=int, default=300, help='Native frame limit; 0 runs until closed.')
    args = parser.parse_args()
    if args.action == 'prepare' and not args.game_dir:
        parser.error('prepare requires --game-dir or STS2_INSTALL_DIR.')
    if sum((args.gameplay_probe, args.continue_probe, args.neow_probe, args.rollback_probe, args.expansion_probe)) > 1:
        parser.error('Choose one gameplay, continue, Neow or rollback probe per process.')
    if args.quit_after < 0:
        parser.error('--quit-after must be nonnegative.')
    try:
        (prepare if args.action == 'prepare' else run)(args)
    except (OSError, ValueError, KeyError, StopIteration) as error:
        print(f'NoSuffering lab: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
