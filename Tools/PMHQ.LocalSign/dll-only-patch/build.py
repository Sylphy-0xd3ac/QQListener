"""Build and merge the DLL using Zig and Python pefile."""
from pathlib import Path
import argparse
import shutil
import subprocess
import sys

HERE=Path(__file__).resolve().parent
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--original',type=Path,required=True,help='Official unmodified PMHQ v9.0.6 Windows x64 pmhq.dll')
parser.add_argument('--zig',default='zig')
parser.add_argument('--output-dir',type=Path,default=HERE/'build')
parser.add_argument('--local-pipe',action='store_true',default=True,help='Build the manager-free local pipe runtime (default)')
parser.add_argument('--legacy-compute',dest='local_pipe',action='store_false',help='Reproduce the historical compute-only patch; not used by the plugin')
args=parser.parse_args()
original=args.original.resolve()
zig=shutil.which(args.zig)
if not zig:raise SystemExit('Zig executable not found')
lib=Path(zig).resolve().parent/'lib'
if not lib.is_dir():raise SystemExit('Zig lib directory not found next to the executable')
out=args.output_dir.resolve();out.mkdir(parents=True,exist_ok=True)
command=[zig,'cc','-target','x86_64-windows-gnu','-O2','-Wall','-Wextra','-fno-builtin','-fno-stack-protector','-nostdlib','-shared',str(HERE/('local_pipe.c' if args.local_pipe else 'payload.c')),'-lkernel32','-lbcrypt','-Wl,--entry,PMHQLocalSignBoot','-Wl,--image-base,0x190000000','-o',str(out/'payload.dll')]
for directory in ('include','libc/include/x86_64-windows-gnu','libc/include/generic-mingw','libc/include/x86_64-windows-any','libc/include/any-windows-any'):
    command.extend(['-isystem',str(lib/directory)])
subprocess.run(command,check=True)
subprocess.run([sys.executable,str(HERE/'merge_dll.py'),'--original',str(original),'--payload',str(out/'payload.dll'),'--output',str(out/'pmhq.dll')],check=True)
print('Runtime replacement file:',out/'pmhq.dll')
