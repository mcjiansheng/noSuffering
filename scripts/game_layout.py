"""Resolve only actual game layouts; ambiguous installations require an explicit path."""
import hashlib
import json
from pathlib import Path

ASSEMBLIES = ('sts2.dll', 'GodotSharp.dll', '0Harmony.dll')


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def resolve(game_dir, assembly_dir=None, platform=None):
    game = Path(game_dir).expanduser().resolve()
    if not game.is_dir():
        raise ValueError(f'Game directory does not exist: {game}')
    apps = [game] if game.suffix == '.app' else sorted(game.glob('*.app'))
    if game.name == 'Resources' and game.parent.name == 'Contents':
        apps = [game.parent.parent]
    candidates = []
    if assembly_dir:
        assembly = Path(assembly_dir).expanduser().resolve()
    else:
        candidates.extend(game.glob('data_sts2_windows_*'))
        candidates.extend(game.glob('data_sts2_macos*'))
        for app in apps:
            candidates.extend((app / 'Contents' / 'Resources').glob('data_sts2_macos*'))
        candidates = sorted(set(p.resolve() for p in candidates if p.is_dir()))
        if platform:
            prefix = 'data_sts2_windows_' if platform == 'windows' else 'data_sts2_macos'
            candidates = [p for p in candidates if p.name.startswith(prefix)]
        if len(candidates) != 1:
            raise ValueError(f'Expected one assembly directory, found {len(candidates)}; specify --assembly-dir explicitly.')
        assembly = candidates[0]
    detected = ('macos' if assembly.name.startswith('data_sts2_macos') else
                'windows' if assembly.name.startswith('data_sts2_windows_') else None)
    if platform and detected and platform != detected:
        raise ValueError(f'--platform {platform} conflicts with actual assembly directory {assembly.name}')
    platform = platform or detected
    if platform not in ('windows', 'macos'):
        raise ValueError('Cannot identify platform; specify --platform windows or macos.')
    for name in (*ASSEMBLIES, 'sts2.runtimeconfig.json'):
        if not (assembly / name).is_file():
            raise ValueError(f'Missing required game file: {assembly / name}')
    if platform == 'macos':
        app = next((p for p in assembly.parents if p.suffix == '.app'), None)
        if app is None:
            if len(apps) != 1:
                raise ValueError('Mac deployment requires one actual .app; specify --game-dir to that .app.')
            app = apps[0]
        resources = app / 'Contents' / 'Resources'
        mods = app / 'Contents' / 'MacOS' / 'mods'
        if not resources.is_dir() or not mods.parent.is_dir():
            raise ValueError(f'Incomplete macOS application layout: {app}')
    else:
        resources = game
        mods = game / 'mods'
    runtime = json.loads((assembly / 'sts2.runtimeconfig.json').read_text(encoding='utf-8-sig'))
    release_path = resources / 'release_info.json'
    if not release_path.is_file():
        release_path = game / 'release_info.json'
    release = json.loads(release_path.read_text(encoding='utf-8-sig')) if release_path.is_file() else None
    return {'platform': platform, 'game_dir': str(game), 'assembly_dir': str(assembly),
            'mods_dir': str(mods), 'release': release, 'runtime': runtime,
            'assemblies': [{'name': name, 'sha256': sha256(assembly / name)} for name in ASSEMBLIES]}
