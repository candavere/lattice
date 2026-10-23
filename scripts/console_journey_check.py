#!/usr/bin/env python3
import os, pty, sys, time, select, struct, fcntl, termios, subprocess

BINARY = os.environ.get("LATTICE_BIN", "Cli/bin/Release/net10.0/lattice")
if not os.path.isabs(BINARY):
    BINARY = os.path.join(os.getcwd(), BINARY)

def run():
    master, slave = pty.openpty()
    winsize = struct.pack('HHHH', 32, 100, 0, 0)
    fcntl.ioctl(slave, termios.TIOCSWINSZ, winsize)
    env = dict(os.environ); env['TERM']='xterm-256color'
    pid = os.fork()
    if pid == 0:
        os.setsid()
        os.dup2(slave,0); os.dup2(slave,1); os.dup2(slave,2)
        os.chdir("/tmp")
        os.close(master); os.close(slave)
        os.execvpe(BINARY, [BINARY], env)
    os.close(slave)
    buf = bytearray()
    def drain(t):
        end=time.time()+t
        while time.time()<end:
            r,_,_=select.select([master],[],[],0.1)
            if r:
                try: d=os.read(master,4096)
                except OSError: d=b''
                if not d: break
                buf.extend(d)
    def send(s):
        os.write(master, s.encode())
    steps = [
        ("sleep",1.5),("text","/generate"),("sleep",0.5),("key","Enter"),("sleep",1.0),
        ("key","Enter"),("sleep",0.3),("text","1"),("sleep",0.3),("key","Enter"),("sleep",0.3),
        ("key","Enter"),("sleep",4.0),("key","Tab"),("sleep",1.0),("text","s"),("sleep",2.0),
        ("key","Esc"),("sleep",0.5),("key","Esc"),("sleep",0.5),("text","/exit"),("sleep",0.3),
        ("key","Enter"),("sleep",1.0),
    ]
    keymap={"Enter":"\r","Tab":"\t","Esc":"\x1b"}
    for kind,val in steps:
        if kind=="sleep": time.sleep(val); drain(0.0)
        elif kind=="text": send(val); drain(0.2)
        elif kind=="key": send(keymap[val]); drain(0.2)
    drain(2.0)
    try: os.kill(pid, 15)
    except ProcessLookupError: pass
    try: os.waitpid(pid,0)
    except ChildProcessError: pass
    text=buf.decode('utf-8','replace')
    ok = ('saved ' in text) and ('missing required' not in text or text.count('missing required')<=1)
    # saved must appear
    if 'saved ' not in text:
        print("FAIL: no save confirmation"); return 1
    print("PASS: journey completed; save confirmed")
    return 0

if __name__=='__main__':
    sys.exit(run())
