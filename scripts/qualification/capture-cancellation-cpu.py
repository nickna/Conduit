"""Sample only the owned Gateway process between the capture markers."""
import argparse
import json
import os
from pathlib import Path
import select
import subprocess
import time


def wait_for_ack(process, fd):
    deadline = time.monotonic() + 12
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"perf exited before enabling events ({process.returncode})")
        if select.select([fd], [], [], 0.2)[0]:
            response = os.read(fd, 128)
            if response.strip(b"\0\t\r\n ") == b"ack":
                return
            raise RuntimeError(f"Unexpected perf control receipt: {response!r}")
    raise TimeoutError("perf did not acknowledge enable")


def record(perf, pid, prefix):
    paths = {name: Path(f"{prefix}.{name}") for name in ("ctl", "ack", "data", "ready", "stop")}
    for key in ("ctl", "ack"):
        os.mkfifo(paths[key], 0o600)
    ctl = os.open(paths["ctl"], os.O_RDWR | os.O_NONBLOCK)
    ack = os.open(paths["ack"], os.O_RDWR | os.O_NONBLOCK)
    command = ["sudo", "-n", "timeout", "--signal=INT", "140", perf, "record", "--event", "cpu-clock", "--freq", "199",
               "--call-graph", "fp", "--clockid", "mono", "--delay=-1", "--no-buildid-cache",
               "--pid", str(pid), "--control", f"fifo:{paths['ctl']},{paths['ack']}",
               "--output", str(paths["data"])]
    try:
        with Path(f"{prefix}.perf.log").open("wb") as stderr, subprocess.Popen(
                command, stdin=subprocess.DEVNULL, stdout=stderr, stderr=stderr) as process:
            try:
                os.write(ctl, b"enable\n")
                wait_for_ack(process, ack)
                start = time.monotonic_ns()
                paths["ready"].write_text(f"{start}\n", encoding="ascii")
                deadline = time.monotonic() + 120
                while not paths["stop"].exists():
                    if process.poll() is not None:
                        raise RuntimeError(f"perf exited prematurely ({process.returncode})")
                    if time.monotonic() > deadline:
                        raise TimeoutError("The measured cancellation window did not signal completion")
                    time.sleep(0.02)
                stop = time.monotonic_ns()
            finally:
                if process.poll() is None:
                    os.write(ctl, b"stop\n")
                if process.wait(timeout=20):
                    raise RuntimeError(f"perf record failed. Inspect {prefix}.perf.log")
    finally:
        os.close(ctl)
        os.close(ack)
        for key in ("ctl", "ack"):
            paths[key].unlink(missing_ok=True)
        if paths["data"].is_file():
            subprocess.run(["sudo", "-n", "chmod", "0644", paths["data"]], check=True)
    symbol_map = Path(f"/tmp/perf-{pid}.map")
    if not symbol_map.is_file() or not symbol_map.stat().st_size:
        raise RuntimeError(f"Missing managed perf map for owned Gateway PID {pid}")
    Path(f"{prefix}.map").write_bytes(symbol_map.read_bytes())
    with Path(f"{prefix}.stacks.txt").open("wb") as output:
        subprocess.run([perf, "script", "--ns", "-i", paths["data"],
                        "-F", "comm,pid,tid,time,event,period,ip,sym,dso"],
                       stdout=output, check=True)
    with Path(f"{prefix}.report.txt").open("wb") as output:
        subprocess.run([perf, "report", "--stdio", "--no-children", "-n", "-i", paths["data"],
                        "--sort", "comm,dso,symbol", "--percent-limit", "0.5"],
                       stdout=output, check=True)
    if not Path(f"{prefix}.stacks.txt").stat().st_size:
        raise RuntimeError("No on-CPU samples were captured")
    Path(f"{prefix}.json").write_text(json.dumps({
        "pid": pid, "event": "cpu-clock", "frequencyHz": 199,
        "clock": "CLOCK_MONOTONIC", "enableAckNanoseconds": start,
        "stopSignalObservedNanoseconds": stop,
        "scope": "Separately instrumented cancellation window. This is not a qualification sample.",
    }, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("perf")
    cli.add_argument("pid", type=int)
    cli.add_argument("prefix", type=Path)
    args = cli.parse_args()
    record(args.perf, args.pid, args.prefix)
