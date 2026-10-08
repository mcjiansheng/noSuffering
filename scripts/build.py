#!/usr/bin/env python3
"""Portable build, inspection and deployment using actual installed game assemblies."""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import zipfile

from game_layout import resolve, sha256

ROOT = Path(__file__).resolve().parent.parent


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def output_dir(layout, branch):
    if not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9._-]*', branch):
        raise ValueError('Branch must be a simple path label, for example public-beta or public.')
    return ROOT / 'artifacts' / layout['platform'] / branch


def build(args, layout):
    target = output_dir(layout, args.branch)
    stage = target / 'NoSuffering'
    record = dict(layout, branch=args.branch, build='not_executed', load='not_executed',
                  singleplayer='not_executed', multiplayer='not_executed')
    version = json.loads((ROOT / 'NoSuffering.json').read_text(encoding='utf-8-sig'))['version']
    archive = target / f'NoSuffering-{version}.zip'
    # Invalidate previous deliverables before attempting a rebuild.
    if stage.exists():
        shutil.rmtree(stage)
    archive.unlink(missing_ok=True)
    write_json(target / 'build-record.json', record)
    runtime_tfm = layout['runtime'].get('runtimeOptions', {}).get('tfm')
    if not runtime_tfm or not re.fullmatch(r'net\d+\.\d+', runtime_tfm):
        raise ValueError('Actual game runtimeconfig must declare a supported netX.Y target framework.')
    dotnet = args.dotnet
    local = ROOT / '.tools' / 'dotnet' / ('dotnet.exe' if os.name == 'nt' else 'dotnet')
    if dotnet == 'dotnet' and local.is_file():
        dotnet = str(local)
    key = f"{layout['platform']}/{args.branch}"
    intermediate = ROOT / 'obj' / key
    binary = ROOT / 'bin' / key
    command = [dotnet, 'build', str(ROOT / 'NoSuffering.csproj'), '-c', 'Release', '--nologo',
               f"-p:Sts2AssemblyDir={layout['assembly_dir']}", f'-p:Sts2Branch={args.branch}', f'-p:TargetFramework={runtime_tfm}',
               f'-p:BaseIntermediateOutputPath={intermediate}{os.sep}',
               f'-p:MSBuildProjectExtensionsPath={intermediate}{os.sep}',
               f'-p:BaseOutputPath={binary}{os.sep}']
    try:
        subprocess.run(command, cwd=ROOT, check=True, env=dict(os.environ, DOTNET_CLI_HOME=str(ROOT / '.tools' / 'dotnet-home'), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1'))
        stage.mkdir(parents=True)
        shutil.copy2(binary / 'Release' / runtime_tfm / 'NoSuffering.dll', stage)
        for name in ('NoSuffering.json', 'README.md', 'THIRD_PARTY_NOTICES.md'):
            if (ROOT / name).is_file():
                shutil.copy2(ROOT / name, stage)
        if (ROOT / 'docs').is_dir():
            shutil.copytree(ROOT / 'docs', stage / 'docs')
            # The native loader scans nested JSON files as mod manifests.
            # Preserve evidence as text inside the installed mod only.
            for evidence in (stage / 'docs').rglob('*.json'):
                evidence.rename(evidence.with_suffix('.txt'))
        record.update(build='passed', version=version, mod_sha256=sha256(stage / 'NoSuffering.dll'))
        write_json(stage / 'build-record.txt', record)
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as package:
            for file in sorted(stage.rglob('*')):
                if file.is_file():
                    package.write(file, Path('NoSuffering') / file.relative_to(stage))
    except (OSError, subprocess.CalledProcessError) as error:
        record.update(build='failed', error=str(error))
        write_json(target / 'build-record.json', record)
        raise
    write_json(target / 'build-record.json', record)
    print(json.dumps({'artifact': str(archive), 'record': str(target / 'build-record.json'),
                      'mod_sha256': record['mod_sha256']}, indent=2))


def ensure_game_closed(layout):
    if os.name == 'nt':
        result = subprocess.run(['tasklist', '/FO', 'CSV', '/NH'], check=True, capture_output=True, text=True, errors='replace')
        if re.search(r'(?im)^"SlayTheSpire2\.exe"', result.stdout):
            raise ValueError('Close the game before deploying NoSuffering.')
    else:
        result = subprocess.run(['ps', '-axo', 'comm='], check=True, capture_output=True, text=True, errors='replace')
        for line in result.stdout.splitlines():
            executable = Path(line.strip()).name.lower()
            if executable in ('slaythespire2', 'slaythespire2.exe', 'sts2') or 'slay the spire 2.app/contents/macos/' in line.lower():
                raise ValueError('Close the game before deploying NoSuffering.')


def deploy(args, layout):
    target = output_dir(layout, args.branch)
    stage = target / 'NoSuffering'
    record_path = stage / 'build-record.txt'
    if not record_path.is_file():
        raise ValueError('Build this platform and branch before deploying.')
    record = json.loads(record_path.read_text(encoding='utf-8'))
    if record.get('build') != 'passed' or record.get('assemblies') != layout['assemblies']:
        raise ValueError('Build record does not match the installed game assemblies; rebuild before deploying.')
    if record.get('mod_sha256') != sha256(stage / 'NoSuffering.dll'):
        raise ValueError('Staged mod DLL does not match the build record; rebuild before deploying.')
    ensure_game_closed(layout)
    mods = Path(layout['mods_dir'])
    destination = mods / 'NoSuffering'
    # Never traverse mod symlinks or overwrite an unrelated mod directory.
    if mods.is_symlink() or destination.is_symlink():
        raise ValueError('Refusing deployment through a mods or NoSuffering symlink.')
    if destination.exists():
        manifest = destination / 'NoSuffering.json'
        if not manifest.is_file():
            raise ValueError('Existing destination is not identifiable as NoSuffering; refusing replacement.')
        expected = json.loads((stage / 'NoSuffering.json').read_text(encoding='utf-8-sig'))
        installed = json.loads(manifest.read_text(encoding='utf-8-sig'))
        if installed.get('id') != expected.get('id') or not expected.get('id'):
            raise ValueError('Existing destination manifest is not NoSuffering; refusing replacement.')
        stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f')
        backup = target / ('deploy-backup-' + stamp)
        shutil.copytree(destination, backup, symlinks=True)
    mods.mkdir(parents=True, exist_ok=True)
    temporary = mods / ('NoSuffering.pending-' + str(os.getpid()))
    if temporary.exists():
        raise ValueError(f'Pending deployment already exists: {temporary}')
    shutil.copytree(stage, temporary)
    previous = mods / ('NoSuffering.previous-' + str(os.getpid()))
    if previous.exists():
        shutil.rmtree(temporary)
        raise ValueError(f'Previous deployment already exists: {previous}')
    try:
        ensure_game_closed(layout)
        if destination.exists():
            destination.rename(previous)
        try:
            temporary.rename(destination)
        except OSError:
            if previous.exists():
                previous.rename(destination)
            raise
        if previous.exists():
            shutil.rmtree(previous)
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)
    print(f'Installed: {destination}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('build', 'inspect', 'deploy'), nargs='?', default='build')
    parser.add_argument('--game-dir', default=os.environ.get('STS2_INSTALL_DIR'))
    parser.add_argument('--assembly-dir', default=os.environ.get('STS2_ASSEMBLY_DIR'))
    parser.add_argument('--platform', choices=('windows', 'macos'))
    parser.add_argument('--branch', default='public-beta')
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    if not args.game_dir:
        parser.error('Provide --game-dir or STS2_INSTALL_DIR.')
    try:
        layout = resolve(args.game_dir, args.assembly_dir, args.platform)
        if args.action == 'inspect':
            print(json.dumps(layout, ensure_ascii=False, indent=2))
        elif args.action == 'build':
            build(args, layout)
        else:
            deploy(args, layout)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f'NoSuffering: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
