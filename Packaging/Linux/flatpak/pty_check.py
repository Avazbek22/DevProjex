"""PTY consumer with bounded terminal-query replies and a readable screen log."""
import codecs
import fcntl
import os
from pathlib import Path
import pty
import re
import select
import struct
import subprocess
import termios
import time
import pyte


class Terminal:
    def __init__(self, command, folder):
        self.command_line = command
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 52, 180, 0, 0))
        self.process = subprocess.Popen(command, stdin=slave, stdout=slave, stderr=slave,
                                        env={**os.environ, 'TERM': 'xterm-256color', 'COLORTERM': 'truecolor'})
        os.close(slave)
        self.folder = Path(folder)
        self.folder.mkdir(parents=True, exist_ok=True)
        self.raw = (self.folder / 'terminal.raw').open('wb')
        self.screen = pyte.Screen(180, 52)
        self.stream = pyte.Stream(self.screen)
        self.decoder = codecs.getincrementaldecoder('utf-8')('replace')
        self.pending = b''

    def pump(self, duration=.2):
        deadline = time.monotonic() + duration
        while time.monotonic() < deadline:
            ready, _, _ = select.select([self.master], [], [], max(0, deadline - time.monotonic()))
            if not ready:
                break
            try:
                data = os.read(self.master, 65536)
            except OSError:
                break
            if not data:
                break
            self.raw.write(data)
            self.raw.flush()
            self.stream.feed(self.decoder.decode(data))
            self.pending = (self.pending + data)[-8192:]
            queries = [(b'\x1b[6n', b'\x1b[1;1R'), (b'\x1b[?6n', b'\x1b[?1;1R'),
                       (b'\x1b[18t', b'\x1b[8;52;180t'), (b'\x1b[c', b'\x1b[?1;2c'),
                       (b'\x1b[>c', b'\x1b[>0;276;0c')]
            for query, reply in queries:
                if query in self.pending:
                    os.write(self.master, reply)
                    self.pending = self.pending.replace(query, b'')
            for match in list(re.finditer(rb'\x1b\](10|11);\?(?:\x07|\x1b\\)', self.pending)):
                os.write(self.master, b'\x1b]' + match[1] + b';rgb:1111/1111/1111\x1b\\')
                self.pending = self.pending.replace(match[0], b'')
        return '\n'.join(self.screen.display)

    def wait(self, predicate, label, timeout=30):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            text = self.pump()
            if predicate(text):
                self.snapshot(label)
                return text
            if self.process.poll() is not None:
                break
        self.snapshot(label + '-failed')
        raise AssertionError('Terminal did not reach ' + label)

    def command(self, text):
        os.write(self.master, (':' + text + '\r').encode())
        self.pump(.5)

    def snapshot(self, name):
        (self.folder / (name + '.txt')).write_text('\n'.join(self.screen.display))

    def close(self):
        if self.process.poll() is None:
            self.command('quit')
            self.snapshot('exit-confirmation')
            os.write(self.master, b'\r')
            self.pump(.5)
            deadline = time.monotonic() + 10
            while self.process.poll() is None and time.monotonic() < deadline:
                self.pump()
            if self.process.poll() is None:
                self.snapshot('exit-failed')
                self.process.terminate()
                self.process.wait(timeout=10)
                listing = subprocess.check_output(['flatpak', 'ps', '--columns=instance,application'], text=True)
                for line in listing.splitlines():
                    if 'io.github.Avazbek22.DevProjex' not in line:
                        continue
                    instance = line.split()[0]
                    command = subprocess.run(['flatpak', 'enter', instance, 'sh', '-c', 'cat /proc/2/cmdline'], capture_output=True)
                    if self.command_line[4].encode() in command.stdout.split(b'\x00'):
                        subprocess.run(['flatpak', 'kill', instance], check=True)
                raise AssertionError('Terminal did not exit after confirmation; see exit-failed.txt')
        self.raw.close()
        os.close(self.master)
