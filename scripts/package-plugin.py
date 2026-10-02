"""Package only plugin-owned assemblies; Jellyfin supplies its own dependencies."""
import hashlib
from pathlib import Path
import zipfile

repo = Path(__file__).resolve().parents[1]
source = repo / 'src/Jellyfin.Plugin.RetroBridge/bin/Release/net10.0'
output = repo / 'artifacts'
output.mkdir(exist_ok=True)
archive = output / 'RetroBridge-0.1.0.zip'
with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED) as package:
    for name in ['Jellyfin.Plugin.RetroBridge.dll', 'RetroBridge.Domain.dll']:
        data = (source / name).read_bytes()
        info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        package.writestr(info, data)
checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
(archive.with_suffix('.zip.sha256')).write_text(f'{checksum}  {archive.name}\n')
print(archive)
