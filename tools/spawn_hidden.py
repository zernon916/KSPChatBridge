"""Spawn a process with NO window, detached from the caller (start this with pythonw.exe via WMI so nothing
pops up on Luke's desktop or steals focus).  pythonw tools/spawn_hidden.py <stdout> <stderr> <prog> [args...]"""
import subprocess
import sys

out, err, cmd = sys.argv[1], sys.argv[2], sys.argv[3:]
flags = 0x08000000 | 0x00000008 | 0x00000200  # CREATE_NO_WINDOW | DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
p = subprocess.Popen(cmd, stdout=open(out, "w"), stderr=open(err, "w"), stdin=subprocess.DEVNULL,
                     creationflags=flags & ~0x00000008, env=dict(__import__("os").environ, PYTHONIOENCODING="utf-8"))
open(out + ".pid", "w").write(str(p.pid))
