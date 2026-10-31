"""Apply version-pinned native fixes; never change installed binaries."""
from pathlib import Path
import json,subprocess,sys
root=Path(sys.argv[1]).resolve(); here=Path(__file__).parent
manifest=json.loads((here/'manifest.json').read_text())
for entry in manifest['Patches']:
    directory=root/entry['Directory']; patch=(here/entry['File']).resolve()
    def git(*args): return subprocess.run(['git','-C',str(directory),*args],capture_output=True)
    if git('rev-parse','HEAD').stdout.decode().strip()!=entry['BaseCommit']:
        raise SystemExit(f"Wrong source revision: {directory}; expected {entry['BaseCommit']}")
    check=git('apply','--check',str(patch))
    if check.returncode:
        if git('apply','--reverse','--check',str(patch)).returncode==0:
            print(f"Already applied: {entry['File']}"); continue
        raise SystemExit(check.stderr.decode(errors='replace'))
    result=git('apply',str(patch))
    if result.returncode: raise SystemExit(result.stderr.decode(errors='replace'))
    print(f"Applied: {entry['File']}")
