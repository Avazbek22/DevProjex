"""Record the actual server byte streams without altering the MCP transport."""
import json
from pathlib import Path
import signal
import subprocess
import sys
import threading

folder = Path(sys.argv[1])
with (folder / 'stdout.jsonl').open('wb') as stdout, (folder / 'stderr.log').open('wb') as stderr:
    process = subprocess.Popen(sys.argv[2:], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    def stop(signum, frame):
        process.terminate()

    signal.signal(signal.SIGTERM, stop)

    def input_stream():
        try:
            for line in sys.stdin.buffer:
                process.stdin.write(line)
                process.stdin.flush()
        except BrokenPipeError:
            pass
        finally:
            process.stdin.close()

    def error_stream():
        for line in process.stderr:
            stderr.write(line)
            stderr.flush()

    threading.Thread(target=input_stream, daemon=True).start()
    errors = threading.Thread(target=error_stream, daemon=True)
    errors.start()
    for line in process.stdout:
        stdout.write(line)
        stdout.flush()
        sys.stdout.buffer.write(line)
        sys.stdout.buffer.flush()
    result = process.wait(timeout=10)
    errors.join(timeout=5)
if result:
    raise SystemExit(result)
lines = (folder / 'stdout.jsonl').read_text().splitlines()
if not lines or any(json.loads(line).get('jsonrpc') != '2.0' for line in lines):
    raise SystemExit('Server stdout contains non-protocol output')
