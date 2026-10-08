#!/usr/bin/env python3
"""Download an owned Steam game branch into its independent NoSuffering lab slot."""
import argparse
import os
from pathlib import Path
import subprocess
from lab import slot

ROOT = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--platform', required=True, choices=('windows', 'macos', 'both'))
parser.add_argument('--branch', required=True, choices=('public-beta', 'public'))
parser.add_argument('--downloader', required=True, help='Path to official DepotDownloader 3.4.0 or later.')
parser.add_argument('--interactive-login', action='store_true', help='Enter Steam login name here; DepotDownloader privately prompts for the password in the terminal.')
args = parser.parse_args()
# Authentication stays in the downloader; passwords/tokens never appear in script arguments.
base = slot(args.platform, args.branch)
target = base / 'game'
if target.resolve() != target.absolute() or (base / 'prepared.json').exists():
    parser.error('Refusing a linked or already prepared game slot; choose a clean download slot.')
auth = ROOT / '.tools' / 'steam-download'
auth.mkdir(parents=True, exist_ok=True)
if os.name != 'nt':
    auth.chmod(0o700)
command = [str(Path(args.downloader).expanduser().resolve()), '-app', '2868840', '-branch', args.branch,
           '-loginid', '3142868840', '-dir', str(target)]
if args.platform == 'both':
    command += ['-depot', '2868841', '2868842', '-all-platforms']
else:
    command += ['-depot', '2868841' if args.platform == 'windows' else '2868842', '-os', args.platform]
if args.interactive_login:
    account = input('Steam login name (password is entered only in the downloader): ').strip()
    if not account:
        parser.error('A Steam login name is required for interactive login.')
    command += ['-username', account]
else:
    command += ['-qr']
raise SystemExit(subprocess.call(command, cwd=auth))
