"""Export the reviewed local native fixes, including pinned dependency adjustments."""
from pathlib import Path
import json,subprocess,sys
root=Path(sys.argv[1]).resolve(); out=Path(__file__).parent
def git(folder,*args): return subprocess.check_output(['git','-C',str(folder),*args])
manifest={'BaseCommit':git(root,'rev-parse','HEAD').decode().strip(),'Patches':[]}
entries=[('.', 'service-v0.27.8.patch'),('thirdparty/protobuf','protobuf-build.patch'),
         ('thirdparty/imgui','imgui-tabs.patch'),('thirdparty/PS3EYEDriver','pseye-frame-api.patch'),
         ('thirdparty/libstem_gamepad','gamepad-init.patch')]
for directory,filename in entries:
    folder=root/directory
    args=['diff','--binary']+(['--','.',':(exclude)thirdparty'] if directory=='.' else [])
    data=git(folder,*args); (out/filename).write_bytes(data)
    manifest['Patches'].append({'Directory':directory,'File':filename,'BaseCommit':git(folder,'rev-parse','HEAD').decode().strip()})
(out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
