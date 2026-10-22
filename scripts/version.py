"""The project version, read from Directory.Build.props (the single source of truth)."""
from pathlib import Path
import re

REPO = Path(__file__).resolve().parents[1]
VERSION = re.search(r'<Version>([^<]+)</Version>', (REPO / 'Directory.Build.props').read_text()).group(1)
PLUGIN_ARCHIVE = REPO / f'artifacts/RetroBridge-{VERSION}.zip'

if __name__ == '__main__':
    print(VERSION)
