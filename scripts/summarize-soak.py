"""Validate a completed synthetic soak and summarize measured, per-process data."""
import json
from pathlib import Path
import statistics
import sys

rows=[json.loads(line) for line in Path(sys.argv[1]).read_text(encoding="utf-8-sig").splitlines()]
completion=rows[-1]
if completion.get("complete") is not True or completion["durationSeconds"]<1800:
    raise RuntimeError("A completed 30-minute wall-clock run is required")
summary={"durationSeconds":completion["durationSeconds"],"hardwarePlayback":False,"backends":{}}
for backend in ("rust","csharp"):
    samples=[row for row in rows if row.get("backend")==backend]
    if len(samples)<170 or any(row["sources"]!=10 for row in samples):
        raise RuntimeError(f"Missing samples or disconnected sources: {backend}")
    last=samples[-1]
    if last["receivedPackets"]<completion["durationSeconds"]*10*45:
        raise RuntimeError(f"Insufficient sustained PCM packet delivery: {backend}")
    if any(a["receivedPackets"]>=b["receivedPackets"] for a,b in zip(samples,samples[1:])):
        raise RuntimeError(f"Packet delivery stalled: {backend}")
    values=[s for s in samples if s["elapsedSeconds"]>30]
    cpu=sorted(s["cpuCorePercent"] for s in values)
    growth=completion["memoryGrowthBytes"][backend]
    if growth>16*1024*1024:
        raise RuntimeError(f"Private memory grew by more than 16 MiB after warm-up: {backend}")
    if backend=="rust" and (last["underruns"] or last["droppedFrames"] or last["latencyTrims"]):
        raise RuntimeError("Rust encountered buffer starvation or discarded frames")
    summary["backends"][backend]={
        "samples":len(samples),"sources":10,"receivedPackets":last["receivedPackets"],
        "cpuCorePercentMedian":statistics.median(cpu),"cpuCorePercentP95":cpu[int(len(cpu)*.95)],
        "privateBytesFinal":last["privateBytes"],"privateBytesGrowth":growth,
        "underruns":last["underruns"],"droppedFrames":last["droppedFrames"],"latencyTrims":last["latencyTrims"],
        "sampledBufferMinMs":min(s["minBufferMs"] for s in samples),"sampledBufferMaxMs":max(s["maxBufferMs"] for s in samples),
        "maxRenderWorkMs":last["render"]["maxRenderWorkMs"],"lateRenderCalls":last["render"]["lateRenderCalls"]}
print(json.dumps(summary,indent=2))
