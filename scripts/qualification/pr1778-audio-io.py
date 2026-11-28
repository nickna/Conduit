"""Host counters for the PR 1778 provider-storage crossover diagnostic."""
import json
import os
import sys
import time
from pathlib import Path


def read(path):
    try:
        return path.read_text()
    except (OSError, UnicodeError):
        return ""


def storage_env(proc):
    entries = (proc / "environ").read_bytes().split(b"\x00")
    return {key: next((val[len(key)+1:].decode() for val in entries
                       if val.startswith(key.encode() + b"=")), None)
            for key in ("ASPNETCORE_TEMP", "TMPDIR")}


def proc_sample(proc):
    stat = read(proc / "stat")
    if not stat:
        return None
    fields = stat.rsplit(")", 1)[1].split()
    io = {key: int(value) for key, value in
          (line.split(":") for line in read(proc / "io").splitlines())}
    return [int(fields[11]), int(fields[12]), *(io.get(k) for k in IO_KEYS)]


def psi(path):
    return [int(line.rsplit("total=", 1)[1]) for line in read(path).splitlines()]


def counters():
    disk = {}
    for line in read(Path("/proc/diskstats")).splitlines():
        f = line.split()
        if len(f) >= 14 and f[2] in DEVICES:
            disk[f[2]] = [int(f[i]) for i in (5, 9, 12, 13)]
    mem = {f[0][:-1]: int(f[1]) for f in
           (line.split() for line in read(Path("/proc/meminfo")).splitlines())
           if f[0] in ("Dirty:", "Writeback:")}
    return {"t": time.monotonic_ns(), "proc": [proc_sample(p) for p in PROCS],
            "lo": [int(read(LO / k)) for k in ("rx_bytes", "tx_bytes")],
            "disk": disk, "cpu": list(map(int, read(Path("/proc/stat")).splitlines()[0].split()[1:9])),
            "pressure": [psi(Path("/proc/pressure") / k) for k in ("cpu", "io")], "mem_kib": mem}


out, mode, *pids = sys.argv[1:]
PROCS = [Path("/proc") / pid for pid in pids]
IO_KEYS = ["rchar", "wchar", "read_bytes", "write_bytes", "cancelled_write_bytes"]
LO = Path("/sys/class/net/lo/statistics")
DEVICES = {p.name for p in Path("/sys/block").iterdir() if not p.name.startswith(("ram", "loop"))}
started = time.monotonic()
cpu_start = time.process_time_ns()
placement = [storage_env(p) for p in PROCS[:2]]
assert placement[0]["ASPNETCORE_TEMP"] is None
assert placement[1]["ASPNETCORE_TEMP"] == (os.environ["PR1778_PROVIDER_TMP"] if mode == "memory" else None)
samples = []
while time.monotonic() - started <= 610:
    item = counters()
    samples.append(item)
    if item["proc"][2] is None or not PROCS[2].exists():
        break
    time.sleep(0.05)
else:
    raise RuntimeError("Client outlived diagnostic deadline")
assert len(samples) >= 2 and all(samples[0]["proc"]), "Missed client window"
result = {"mode": mode, "pids": pids, "processes": ["gateway", "provider", "client"],
          "tempEnvironment": placement, "procFields": ["utime", "stime", *IO_KEYS],
          "diskFields": ["sectorsRead", "sectorsWritten", "busyMs", "weightedBusyMs"],
          "clock_ticks": os.sysconf("SC_CLK_TCK"), "cpuNs": time.process_time_ns() - cpu_start,
          "samples": samples}
Path(out).write_text(json.dumps(result, separators=(",", ":")) + "\n")

